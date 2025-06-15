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
using BareProx.Models;
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
        var netappSnapmirrorService = scope.ServiceProvider.GetRequiredService<INetappSnapmirrorService>();

        // Step 1: Find all PolicyUuids in use
        var refs = await db.SnapMirrorRelations
            .Where(r => r.PolicyUuid != null && r.DestinationControllerId != 0)
            .Select(r => new { r.DestinationControllerId, r.PolicyUuid })
            .Distinct()
            .ToListAsync(ct);

        foreach (var pair in refs)
        {
            try
            {
                var fetchedPolicy = await netappSnapmirrorService.SnapMirrorPolicyGet(pair.DestinationControllerId, pair.PolicyUuid!);
                if (fetchedPolicy == null)
                    continue;

                var dbPolicy = await db.SnapMirrorPolicies
                    .Include(p => p.Retentions)
                    .FirstOrDefaultAsync(p => p.Uuid == pair.PolicyUuid, ct);

                if (dbPolicy == null)
                {
                    // Not present: Add new
                    db.SnapMirrorPolicies.Add(fetchedPolicy);
                    await db.SaveChangesAsync(ct);
                    continue;
                }

                // --- Compare fields, update if changed ---
                bool changed = false;
                if (dbPolicy.Name != fetchedPolicy.Name) { dbPolicy.Name = fetchedPolicy.Name; changed = true; }
                if (dbPolicy.Scope != fetchedPolicy.Scope) { dbPolicy.Scope = fetchedPolicy.Scope; changed = true; }
                if (dbPolicy.Type != fetchedPolicy.Type) { dbPolicy.Type = fetchedPolicy.Type; changed = true; }
                if (dbPolicy.NetworkCompressionEnabled != fetchedPolicy.NetworkCompressionEnabled) { dbPolicy.NetworkCompressionEnabled = fetchedPolicy.NetworkCompressionEnabled; changed = true; }
                if (dbPolicy.Throttle != fetchedPolicy.Throttle) { dbPolicy.Throttle = fetchedPolicy.Throttle; changed = true; }

                // --- Deep compare retentions ---
                // Simplest: Remove all and re-add if any difference (you can optimize if needed)
                if (!AreRetentionsEqual(dbPolicy.Retentions, fetchedPolicy.Retentions))
                {
                    dbPolicy.Retentions.Clear();
                    foreach (var ret in fetchedPolicy.Retentions)
                        dbPolicy.Retentions.Add(ret);
                    changed = true;
                }

                if (changed)
                    await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync SnapMirror policy for controller {ControllerId} and policy {PolicyUuid}", pair.DestinationControllerId, pair.PolicyUuid);
            }
        }
    }

    // Helper for deep comparing two retention lists
    private static bool AreRetentionsEqual(
        IList<SnapMirrorPolicyRetention> a,
        IList<SnapMirrorPolicyRetention> b)
    {
        if (a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Label != b[i].Label ||
                a[i].Count != b[i].Count ||
                a[i].Preserve != b[i].Preserve ||
                a[i].Warn != b[i].Warn ||
                a[i].Period != b[i].Period)
                return false;
        }
        return true;
    }


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
