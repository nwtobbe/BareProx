/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 *
 * Copyright (C) 2025 Tobias Modig
 *
 * This file is part of BareProx.
 *
 * BareProx is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * BareProx is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with BareProx. If not, see <https://www.gnu.org/licenses/>.
 */

using BareProx.Data;
using BareProx.Services;
using Microsoft.EntityFrameworkCore;


public class SnapMirrorSyncService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SnapMirrorSyncService> _logger;

    public SnapMirrorSyncService(IServiceProvider services, ILogger<SnapMirrorSyncService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

        var nextSelectedVolumeUpdate = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var netappService = scope.ServiceProvider.GetRequiredService<INetappService>();
                await netappService.SyncSnapMirrorRelationsAsync(stoppingToken);
                await EnsureSnapMirrorPoliciesAsync(stoppingToken);

                // Check if it's time to update volumes
                if (DateTime.UtcNow >= nextSelectedVolumeUpdate)
                {
                    await netappService.UpdateAllSelectedVolumesAsync(stoppingToken);
                    nextSelectedVolumeUpdate = DateTime.UtcNow.AddHours(1);
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing SnapMirror relations");
            }

            // --- Set Time!
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        } 
    }
    private async Task EnsureSnapMirrorPoliciesAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var netapp = scope.ServiceProvider.GetRequiredService<INetappService>();

        // Step 1: Find all PolicyUuids in use
        var refs = await db.SnapMirrorRelations
            .Where(r => r.PolicyUuid != null && r.DestinationControllerId != 0)
            .Select(r => new { r.DestinationControllerId, r.PolicyUuid })
            .Distinct()
            .ToListAsync(ct);

        foreach (var pair in refs)
        {
            if (await db.SnapMirrorPolicies.AnyAsync(p => p.Uuid == pair.PolicyUuid, ct))
                continue; // Already present

            try
            {
                // This method should fetch and map policy+retentions for a single controller+uuid
                var policy = await netapp.SnapMirrorPolicyGet(pair.DestinationControllerId, pair.PolicyUuid!);
                if (policy != null)
                {
                    db.SnapMirrorPolicies.Add(policy);
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync SnapMirror policy for controller {ControllerId} and policy {PolicyUuid}", pair.DestinationControllerId, pair.PolicyUuid);
            }
        } }


    }




