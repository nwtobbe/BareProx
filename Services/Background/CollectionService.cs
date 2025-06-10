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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;


public class CollectionService
    : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<CollectionService> _logger;

    public CollectionService(IServiceProvider services, ILogger<CollectionService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

        var nextSelectedVolumeUpdate = DateTime.UtcNow;
        var nextClusterStatusCheck = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var netappService = scope.ServiceProvider.GetRequiredService<INetappService>();
                var netappSnapmirrorService = scope.ServiceProvider.GetRequiredService<INetappSnapmirrorService>();

                await netappSnapmirrorService.SyncSnapMirrorRelationsAsync(stoppingToken);
                await EnsureSnapMirrorPoliciesAsync(stoppingToken);

                // Check if it's time to update volumes
                if (DateTime.UtcNow >= nextSelectedVolumeUpdate)
                {
                    await netappService.UpdateAllSelectedVolumesAsync(stoppingToken);
                    nextSelectedVolumeUpdate = DateTime.UtcNow.AddHours(1);
                }

                // Call health check every 5 minutes
                if (DateTime.UtcNow >= nextClusterStatusCheck)
                {
                    await CheckProxmoxClusterAndHostsStatusAsync(stoppingToken);
                    nextClusterStatusCheck = DateTime.UtcNow.AddMinutes(5);
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing SnapMirror relations or checking cluster status");
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
        var netappSnapmirrorService = scope.ServiceProvider.GetRequiredService<INetappSnapmirrorService>();

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
                var policy = await netappSnapmirrorService.SnapMirrorPolicyGet(pair.DestinationControllerId, pair.PolicyUuid!);
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

    private async Task CheckProxmoxClusterAndHostsStatusAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var proxmoxService = scope.ServiceProvider.GetRequiredService<ProxmoxService>();

        var clusters = await db.ProxmoxClusters
            .Include(c => c.Hosts)
            .ToListAsync(ct);

        foreach (var cluster in clusters)
        {
            try
            {
                // Single call covers both cluster & per-host up/down
                var (quorate, onlineCount, totalCount, hostStates, summary) =
                    await proxmoxService.GetClusterStatusAsync(cluster, ct);

                // Update cluster
                cluster.HasQuorum = quorate;
                cluster.OnlineHostCount = onlineCount;
                cluster.TotalHostCount = totalCount;
                cluster.LastStatus = summary;
                cluster.LastStatusMessage = summary;
                cluster.LastChecked = DateTime.UtcNow;

                // Update each host
                foreach (var host in cluster.Hosts)
                {
                    var key = host.Hostname ?? host.HostAddress;
                    if (hostStates.TryGetValue(key, out var isOnline))
                    {
                        host.IsOnline = isOnline;
                        host.LastStatus = isOnline ? "online" : "offline";
                        host.LastStatusMessage = summary;
                        host.LastChecked = DateTime.UtcNow;
                    }
                }

                // Save with SQLite retry
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        await db.SaveChangesAsync(ct);
                        break;
                    }
                    catch (DbUpdateException ex)
                        when (ex.InnerException is SqliteException se && se.SqliteErrorCode == 5)
                    {
                        if (i == 2) throw;
                        await Task.Delay(500, ct);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check status for cluster {ClusterId}", cluster.Id);
                cluster.LastStatus = "Error";
                cluster.LastStatusMessage = ex.Message;
                cluster.LastChecked = DateTime.UtcNow;

                // Retry save on error
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        await db.SaveChangesAsync(ct);
                        break;
                    }
                    catch (DbUpdateException ey)
                        when (ey.InnerException is SqliteException se && se.SqliteErrorCode == 5)
                    {
                        if (i == 2) throw;
                        await Task.Delay(500, ct);
                    }
                }
            }
        }
    }

}









