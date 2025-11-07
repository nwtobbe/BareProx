/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 *
 * Copyright (C) 2025 Tobias Modig
 *
 * This file is part of BareProx.
 *
 * BareProx is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * BareProx is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with BareProx. If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using BareProx.Data;
using BareProx.Models;
using BareProx.Services;
using BareProx.Services.Proxmox;
using BareProx.Services.Proxmox.Ops;
using BareProx.Services.Netapp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BareProx.Services.Background
{
    public sealed class CollectionService : BackgroundService, ICollectionService
    {
        private readonly IDbFactory _dbf;
        private readonly IQueryDbFactory _qdbf;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<CollectionService> _logger;

        public CollectionService(
            IDbFactory dbf,
            IQueryDbFactory qdbf,
            IServiceScopeFactory scopeFactory,
            ILogger<CollectionService> logger)
        {
            _dbf = dbf;
            _qdbf = qdbf;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        // Cadences
        private static readonly TimeSpan LoopSleep = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan ClusterStatusCadence = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan SelectedVolumeRefreshCadence = TimeSpan.FromHours(1);

        private static readonly TimeSpan VmInventoryCadence = TimeSpan.FromMinutes(5);   // Proxmox VMs + storage + VM disks
        private static readonly TimeSpan InfraInventoryCadence = TimeSpan.FromHours(24);  // NetApp volumes + replication + mapping

        // =====================================================================
        // Background loop
        // =====================================================================

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var nextSelectedVolumeUpdate = DateTime.UtcNow;
            var nextClusterStatusCheck = DateTime.UtcNow;

            var nextVmInventory = DateTime.UtcNow; // every 5 min
            var nextInfraInventory = DateTime.UtcNow; // every 24 hours

            try
            {
                // seed the snapshot tracker once on startup so Janitor doesn't wait too long
                await TrackNetappSnapshotsAsync(stoppingToken);

                // mark ready in the query DB metadata (Janitor checks this)
                await using var qdb0 = await _qdbf.CreateAsync(stoppingToken);
                await UpsertMetadataAsync(qdb0, "SnapshotTrackerReady", "true", stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CollectionService: initial snapshot tracker seed failed (will retry on cadence).");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // --- SnapMirror relation sync (scoped) ---
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var netappSnapmirrorService = scope.ServiceProvider.GetRequiredService<INetappSnapmirrorService>();
                        await netappSnapmirrorService.SyncSnapMirrorRelationsAsync(stoppingToken);
                    }

                    // Ensure SnapMirror policies
                    await EnsureSnapMirrorPoliciesAsync(stoppingToken);

                    // Update selected volumes hourly (legacy compatibility)
                    if (DateTime.UtcNow >= nextSelectedVolumeUpdate)
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var volumes = scope.ServiceProvider.GetRequiredService<INetappVolumeService>();
                        await volumes.UpdateAllSelectedVolumesAsync(stoppingToken);
                        nextSelectedVolumeUpdate = DateTime.UtcNow.Add(SelectedVolumeRefreshCadence);
                    }

                    // Cluster/host health every 2 minutes
                    if (DateTime.UtcNow >= nextClusterStatusCheck)
                    {
                        await CheckProxmoxClusterAndHostsStatusAsync(stoppingToken);
                        nextClusterStatusCheck = DateTime.UtcNow.Add(ClusterStatusCadence);
                    }

                    // VM-side inventory every 5 minutes
                    if (DateTime.UtcNow >= nextVmInventory)
                    {
                        await SyncInventoryVmSideAsync(stoppingToken);
                        nextVmInventory = DateTime.UtcNow.Add(VmInventoryCadence);
                    }

                    // Infra-side inventory every 24 hours
                    if (DateTime.UtcNow >= nextInfraInventory)
                    {
                        await SyncInventoryInfraSideAsync(stoppingToken);
                        nextInfraInventory = DateTime.UtcNow.Add(InfraInventoryCadence);
                        // enforce retention after we’ve refreshed infra state
                        await EnforceSnapshotRetentionAsync(stoppingToken);

                        nextInfraInventory = DateTime.UtcNow.Add(InfraInventoryCadence);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break; // graceful shutdown
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in CollectionService loop.");
                }

                await Task.Delay(LoopSleep, stoppingToken);
            }
        }



        // =====================================================================
        // Public API (for controllers/services)
        // =====================================================================

        /// <inheritdoc />
        public Task RunProxmoxClusterStatusCheckAsync(CancellationToken ct = default)
            => CheckProxmoxClusterAndHostsStatusAsync(ct);

        public Task RunInventoryInfraSideAsync(CancellationToken ct = default)
    => SyncInventoryInfraSideAsync(ct);

        // =====================================================================
        // SnapMirror policies sync (unchanged)
        // =====================================================================

        private async Task EnsureSnapMirrorPoliciesAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            await using var db = await _dbf.CreateAsync(ct);
            var netappSnapmirrorService = scope.ServiceProvider.GetRequiredService<INetappSnapmirrorService>();

            // Step 1: Find all PolicyUuids in use
            var refs = await db.SnapMirrorRelations
                .AsNoTracking()
                .Where(r => r.PolicyUuid != null && r.DestinationControllerId != 0)
                .Select(r => new { r.DestinationControllerId, r.PolicyUuid })
                .Distinct()
                .ToListAsync(ct);

            foreach (var pair in refs)
            {
                try
                {
                    var fetchedPolicy = await netappSnapmirrorService.SnapMirrorPolicyGet(
                        pair.DestinationControllerId,
                        pair.PolicyUuid!);

                    if (fetchedPolicy == null)
                        continue;

                    var dbPolicy = await db.SnapMirrorPolicies
                        .Include(p => p.Retentions)
                        .FirstOrDefaultAsync(p => p.Uuid == pair.PolicyUuid, ct);

                    if (dbPolicy == null)
                    {
                        db.SnapMirrorPolicies.Add(fetchedPolicy);
                        await db.SaveChangesAsync(ct);
                        continue;
                    }

                    bool changed = false;
                    if (dbPolicy.Name != fetchedPolicy.Name) { dbPolicy.Name = fetchedPolicy.Name; changed = true; }
                    if (dbPolicy.Scope != fetchedPolicy.Scope) { dbPolicy.Scope = fetchedPolicy.Scope; changed = true; }
                    if (dbPolicy.Type != fetchedPolicy.Type) { dbPolicy.Type = fetchedPolicy.Type; changed = true; }
                    if (dbPolicy.NetworkCompressionEnabled != fetchedPolicy.NetworkCompressionEnabled)
                    {
                        dbPolicy.NetworkCompressionEnabled = fetchedPolicy.NetworkCompressionEnabled;
                        changed = true;
                    }
                    if (dbPolicy.Throttle != fetchedPolicy.Throttle)
                    {
                        dbPolicy.Throttle = fetchedPolicy.Throttle;
                        changed = true;
                    }

                    // Compare retentions without relying on tuple field names
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
                    _logger.LogError(ex,
                        "Failed to sync SnapMirror policy for controller {ControllerId} and policy {PolicyUuid}",
                        pair.DestinationControllerId, pair.PolicyUuid);
                }
            }
        }

        // Order-insensitive, defensive comparison
        private static bool AreRetentionsEqual(
            IList<SnapMirrorPolicyRetention> a,
            IList<SnapMirrorPolicyRetention> b)
        {
            if (a is null || b is null) return a == b;
            if (a.Count != b.Count) return false;

            static string WarnAsString(object? warn) => warn switch
            {
                null => string.Empty,
                string s => s,
                bool bl => bl ? "true" : "false",
                int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => warn.ToString() ?? string.Empty
            };

            static IEnumerable<(string Label, int Count, bool Preserve, string Warn, string Period)> KeySel(
                IEnumerable<SnapMirrorPolicyRetention> x)
                => x.Select(r => (
                        Label: r.Label ?? string.Empty,
                        Count: r.Count,
                        Preserve: r.Preserve,
                        Warn: WarnAsString(r.Warn),
                        Period: r.Period ?? string.Empty
                    ))
                    .OrderBy(k => k.Label, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(k => k.Period, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(k => k.Count)
                    .ThenBy(k => k.Preserve)
                    .ThenBy(k => k.Warn, StringComparer.OrdinalIgnoreCase);

            return KeySel(a).SequenceEqual(KeySel(b));
        }

        // =====================================================================
        // Proxmox cluster + host status (updated 20251110)
        // =====================================================================

        private async Task CheckProxmoxClusterAndHostsStatusAsync(CancellationToken ct)
        {
            // -------- Read clusters from MAIN DB (source of truth) --------
            await using var main = await _dbf.CreateAsync(ct);
            var clusters = await main.ProxmoxClusters
                                     .Include(c => c.Hosts)
                                     .AsNoTracking()
                                     .ToListAsync(ct);

            // -------- Write status to QUERY DB --------
            await using var qdb = await _qdbf.CreateAsync(ct);
            var now = DateTime.UtcNow;

            foreach (var cluster in clusters)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var proxmoxService = scope.ServiceProvider.GetRequiredService<ProxmoxService>();
                    var proxOps = scope.ServiceProvider.GetRequiredService<IProxmoxOpsService>();

                    var (quorate, onlineCount, totalCount, hostStates, _) =
                        await proxmoxService.GetClusterStatusAsync(cluster, ct);

                    var perNodeSnippets = new List<string>();
                    var hostRows = new List<InventoryHostStatus>();

                    foreach (var host in cluster.Hosts)
                    {
                        var node = string.IsNullOrWhiteSpace(host.Hostname) ? host.HostAddress : host.Hostname!;
                        var key = host.Hostname ?? host.HostAddress;
                        var isOnline = hostStates.TryGetValue(key, out var up) && up;

                        // Default summary if offline
                        string nodeSummary = "API offline/unreachable";

                        if (isOnline)
                        {
                            using var nodeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            nodeCts.CancelAfter(TimeSpan.FromSeconds(8));

                            double? cpuPct = null;
                            double? memPct = null;
                            long? uptime = null;

                            // ---- /nodes/{node}/status ----
                            try
                            {
                                var url = $"https://{host.HostAddress}:8006/api2/json/nodes/{Uri.EscapeDataString(node)}/status";
                                var resp = await proxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, nodeCts.Token);
                                var json = await resp.Content.ReadAsStringAsync(nodeCts.Token);

                                using var doc = JsonDocument.Parse(json);
                                if (doc.RootElement.TryGetProperty("data", out var data))
                                {
                                    if (data.TryGetProperty("cpu", out var cpu) && cpu.ValueKind == JsonValueKind.Number)
                                        cpuPct = Math.Round(cpu.GetDouble() * 100.0, 1);

                                    if (data.TryGetProperty("memory", out var mem) && mem.ValueKind == JsonValueKind.Object)
                                    {
                                        long tot = mem.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0;
                                        long used = mem.TryGetProperty("used", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt64() : 0;
                                        if (tot > 0) memPct = Math.Round(used * 100.0 / tot, 1);
                                    }

                                    if (data.TryGetProperty("uptime", out var upSecs) && upSecs.ValueKind == JsonValueKind.Number)
                                        uptime = upSecs.GetInt64();
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Node status fetch failed for {Node}", node);
                            }

                            // ---- /nodes/{node}/storage ----
                            int storTotal = 0, storActive = 0, storDisabled = 0, storErrorish = 0;
                            try
                            {
                                var url = $"https://{host.HostAddress}:8006/api2/json/nodes/{Uri.EscapeDataString(node)}/storage";
                                var resp = await proxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, nodeCts.Token);
                                var json = await resp.Content.ReadAsStringAsync(nodeCts.Token);

                                using var doc = JsonDocument.Parse(json);
                                if (doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var s in arr.EnumerateArray())
                                    {
                                        storTotal++;

                                        bool active = s.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True;
                                        bool enabled = !(s.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.False);

                                        if (active) storActive++;
                                        if (!enabled) storDisabled++;

                                        if (s.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String)
                                        {
                                            var sv = st.GetString() ?? "";
                                            if (!sv.Equals("available", StringComparison.OrdinalIgnoreCase) &&
                                                !sv.Equals("active", StringComparison.OrdinalIgnoreCase) &&
                                                !sv.Equals("ok", StringComparison.OrdinalIgnoreCase))
                                            {
                                                storErrorish++;
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Node storage list failed for {Node}", node);
                            }

                            string cpuS = cpuPct.HasValue ? $"{cpuPct:0.0}%" : "n/a";
                            string memS = memPct.HasValue ? $"{memPct:0.0}%" : "n/a";
                            string upS = uptime.HasValue ? $"{TimeSpan.FromSeconds(uptime.Value):d\\.hh\\:mm}" : "n/a";
                            string storS = storTotal > 0 ? $"{storActive}/{storTotal} active" : "n/a";
                            string storWarn = (storDisabled > 0 || storErrorish > 0)
                                ? $" (disabled:{storDisabled}, issues:{storErrorish})"
                                : "";

                            nodeSummary = $"cpu {cpuS}, mem {memS}, up {upS}, storage {storS}{storWarn}";
                        }

                        perNodeSnippets.Add($"{node}: {(isOnline ? "✅" : "❌")} {nodeSummary}");

                        hostRows.Add(new InventoryHostStatus
                        {
                            ClusterId = cluster.Id,
                            HostId = host.Id,
                            Hostname = host.Hostname ?? "",
                            HostAddress = host.HostAddress,
                            IsOnline = isOnline,
                            LastStatus = isOnline ? "online" : "offline",
                            LastStatusMessage = nodeSummary,
                            LastCheckedUtc = now
                        });
                    }

                    var header = $"quorum={(quorate ? "yes" : "no")}, hosts={onlineCount}/{totalCount}";
                    var clusterRow = new InventoryClusterStatus
                    {
                        ClusterId = cluster.Id,
                        ClusterName = cluster.Name,
                        HasQuorum = quorate,
                        OnlineHostCount = onlineCount,
                        TotalHostCount = totalCount,
                        LastStatus = header,
                        LastStatusMessage = $"{header}; " + string.Join(" | ", perNodeSnippets),
                        LastCheckedUtc = now
                    };

                    // ---- UPSERT cluster row ----
                    var existingCluster = await qdb.InventoryClusterStatuses
                                                   .FirstOrDefaultAsync(x => x.ClusterId == clusterRow.ClusterId, ct);
                    if (existingCluster is null)
                        qdb.InventoryClusterStatuses.Add(clusterRow);
                    else
                        qdb.Entry(existingCluster).CurrentValues.SetValues(clusterRow);

                    // ---- UPSERT host rows ----
                    foreach (var h in hostRows)
                    {
                        var existingHost = await qdb.InventoryHostStatuses
                            .FirstOrDefaultAsync(x => x.ClusterId == h.ClusterId && x.HostId == h.HostId, ct);

                        if (existingHost is null)
                            qdb.InventoryHostStatuses.Add(h);
                        else
                            qdb.Entry(existingHost).CurrentValues.SetValues(h);
                    }

                    await qdb.SaveChangesAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to check status for cluster {ClusterId}", cluster.Id);

                    // Write an error row for the cluster so UI shows something useful
                    var errRow = new InventoryClusterStatus
                    {
                        ClusterId = cluster.Id,
                        ClusterName = cluster.Name,
                        HasQuorum = false,
                        OnlineHostCount = 0,
                        TotalHostCount = cluster.Hosts?.Count ?? 0,
                        LastStatus = "Error",
                        LastStatusMessage = ex.Message,
                        LastCheckedUtc = now
                    };

                    var existing = await qdb.InventoryClusterStatuses
                                           .FirstOrDefaultAsync(x => x.ClusterId == errRow.ClusterId, ct);
                    if (existing is null)
                        qdb.InventoryClusterStatuses.Add(errRow);
                    else
                        qdb.Entry(existing).CurrentValues.SetValues(errRow);

                    await qdb.SaveChangesAsync(ct);
                }
            }

            // -------- Garbage collect statuses for clusters removed from main DB --------
            var liveClusterIds = clusters.Select(c => c.Id).ToHashSet();
            qdb.InventoryClusterStatuses.RemoveRange(
                qdb.InventoryClusterStatuses.Where(x => !liveClusterIds.Contains(x.ClusterId)));
            qdb.InventoryHostStatuses.RemoveRange(
                qdb.InventoryHostStatuses.Where(x => !liveClusterIds.Contains(x.ClusterId)));
            await qdb.SaveChangesAsync(ct);
        }

        private async Task EnforceSnapshotRetentionAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var netapp = scope.ServiceProvider.GetRequiredService<INetappSnapshotService>();

            await using var main = await _dbf.CreateAsync(ct);
            await using var qdb = await _qdbf.CreateAsync(ct);

            var now = DateTime.UtcNow;

            // 1) Find expired snapshots from MAIN DB (don’t touch running jobs)
            var all = await main.BackupRecords
                .Include(r => r.Job)
                .AsNoTracking()
                .ToListAsync(ct);

            static bool IsExpired(BackupRecord r, DateTime nowUtc)
            {
                var unit = (r.RetentionUnit ?? "").ToLowerInvariant();
                return unit switch
                {
                    "hours" => r.TimeStamp.AddHours(r.RetentionCount) < nowUtc,
                    "days" => r.TimeStamp.AddDays(r.RetentionCount) < nowUtc,
                    "weeks" => r.TimeStamp.AddDays(7 * r.RetentionCount) < nowUtc,
                    _ => false
                };
            }

            var expiredGroups = all
                .Where(r => r.Job?.Status != "Running" && IsExpired(r, now))
                .GroupBy(r => new { r.ControllerId, r.StorageName, r.SnapshotName, r.JobId })
                .ToList();

            if (expiredGroups.Count == 0) return;

            // 2) Load SnapMirror relations once (MAIN DB)
            var relations = await main.SnapMirrorRelations.AsNoTracking().ToListAsync(ct);
            var relByPrimary = relations.ToDictionary(
                r => (r.SourceControllerId, (r.SourceVolume ?? "").ToLowerInvariant()),
                r => r);

            foreach (var grp in expiredGroups)
            {
                ct.ThrowIfCancellationRequested();

                var ex = grp.First();
                if (ex.ControllerId == 0 || string.IsNullOrWhiteSpace(ex.StorageName) || string.IsNullOrWhiteSpace(ex.SnapshotName))
                    continue;

                var primaryKey = (ex.ControllerId, ex.StorageName.ToLowerInvariant());

                try
                {
                    // 3) Delete on PRIMARY
                    var del = await netapp.DeleteSnapshotAsync(ex.ControllerId, ex.StorageName, ex.SnapshotName, ct);

                    // If API says "not found", treat as already gone
                    var okToProceed = del.Success ||
                                      (del.ErrorMessage?.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0);

                    if (!okToProceed)
                    {
                        // log and skip this group
                        _logger.LogWarning("Retention: delete failed for {Snap} on {Vol}@{Ctl}: {Err}",
                            ex.SnapshotName, ex.StorageName, ex.ControllerId, del.ErrorMessage);
                        continue;
                    }

                    // 4) Probe presence on both ends and upsert QUERY DB flags
                    bool existsOnPrimary = false;
                    bool existsOnSecondary = false;
                    int? secCtl = null;
                    string? secVol = null;

                    // Re-list primary
                    var primarySnaps = await netapp.GetSnapshotsAsync(ex.ControllerId, ex.StorageName, ct) ?? new List<string>();
                    existsOnPrimary = primarySnaps.Contains(ex.SnapshotName, StringComparer.OrdinalIgnoreCase);

                    // If relation exists, check secondary
                    if (relByPrimary.TryGetValue(primaryKey, out var rel)
                        && rel.DestinationControllerId != 0
                        && !string.IsNullOrWhiteSpace(rel.DestinationVolume))
                    {
                        var secCtlLocal = rel.DestinationControllerId;   // int (non-nullable)
                        var secVolLocal = rel.DestinationVolume!;

                        var secondarySnaps = await netapp.GetSnapshotsAsync(secCtlLocal, secVolLocal, ct) ?? new List<string>();
                        existsOnSecondary = secondarySnaps.Contains(ex.SnapshotName, StringComparer.OrdinalIgnoreCase);

                        // If you still need these outside, assign them here:
                        // secCtl = secCtlLocal;
                        // secVol = secVolLocal;
                    }
                    else
                    {
                        existsOnSecondary = false;
                    }

                    // Upsert QUERY DB snapshot flags keyed by (JobId, SnapshotName)
                    var row = await qdb.NetappSnapshots
                        .FirstOrDefaultAsync(s => s.JobId == ex.JobId && s.SnapshotName == ex.SnapshotName, ct);

                    if (row is null)
                    {
                        // Create a minimal row so downstream UI/Janitor can see state
                        row = new NetappSnapshot
                        {
                            JobId = ex.JobId,
                            SnapshotName = ex.SnapshotName,
                            PrimaryControllerId = ex.ControllerId,
                            PrimaryVolume = ex.StorageName,
                        };
                        qdb.NetappSnapshots.Add(row);
                    }

                    row.ExistsOnPrimary = existsOnPrimary;
                    row.ExistsOnSecondary = existsOnSecondary;
                    row.IsReplicated = existsOnSecondary;
                    row.LastChecked = DateTime.UtcNow;
                    if (secCtl.HasValue) row.SecondaryControllerId = secCtl.Value;
                    if (!string.IsNullOrWhiteSpace(secVol)) row.SecondaryVolume = secVol;

                    await qdb.SaveChangesAsync(ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exn)
                {
                    _logger.LogWarning(exn,
                        "Retention: exception while enforcing for {Snap} on {Vol}@{Ctl}",
                        ex.SnapshotName, ex.StorageName, ex.ControllerId);
                }
            }
        }

        public Task RunInventoryVmSideAsync(CancellationToken ct = default)
            => SyncInventoryVmSideAsync(ct);

        /// <summary>
        /// VM-side: scan Proxmox for VMs, NFS storages and VM disks (fast path).
        /// Runs every 5 minutes.
        /// </summary>
        private async Task SyncInventoryVmSideAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();

            await using var mainDb = await _dbf.CreateAsync(ct);
            await using var qdb = await _qdbf.CreateAsync(ct);

            var proxOps = scope.ServiceProvider.GetRequiredService<IProxmoxOpsService>();

            var clusters = await mainDb.ProxmoxClusters
                .Include(c => c.Hosts)
                .AsNoTracking()
                .ToListAsync(ct);

            // Garbage collect inventory for removed clusters
            var validClusterIds = clusters.Select(c => c.Id).ToHashSet();
            var invClusterIds = await qdb.InventoryStorages
                .Select(s => s.ClusterId)
                .Distinct()
                .ToListAsync(ct);

            foreach (var oldCid in invClusterIds)
            {
                if (!validClusterIds.Contains(oldCid))
                {
                    qdb.InventoryVmDisks.RemoveRange(qdb.InventoryVmDisks.Where(v => v.ClusterId == oldCid));
                    qdb.InventoryVms.RemoveRange(qdb.InventoryVms.Where(v => v.ClusterId == oldCid));
                    qdb.InventoryStorages.RemoveRange(qdb.InventoryStorages.Where(s => s.ClusterId == oldCid));
                }
            }
            await qdb.SaveChangesAsync(ct);

            // Per-cluster VM/Storage/Disk scan
            foreach (var cluster in clusters)
            {
                try
                {
                    await SyncProxmoxClusterInventoryAsync(qdb, cluster, proxOps, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Inventory VM-side: Proxmox sync failed for cluster {ClusterId}", cluster.Id);
                }
            }

            // Optional: fast remap storages -> NetApp volumes if you want immediate UI reflection
            // await MapStoragesToNetappVolumesAsync(qdb, ct);

            await UpsertMetadataAsync(qdb, "LastVmInventorySyncUtc", DateTime.UtcNow.ToString("O"), ct);
        }

        /// <summary>
        /// Infra-side: NetApp volumes, replication graph, and storage→volume mapping.
        /// Runs every 24 hours.
        /// </summary>
        private async Task SyncInventoryInfraSideAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();

            await using var mainDb = await _dbf.CreateAsync(ct);
            await using var qdb = await _qdbf.CreateAsync(ct);

            var netappVolumeService = scope.ServiceProvider.GetRequiredService<INetappVolumeService>();

            var controllers = await mainDb.NetappControllers.AsNoTracking().ToListAsync(ct);
            var relations = await mainDb.SnapMirrorRelations.AsNoTracking().ToListAsync(ct);

            // Build a case-insensitive map of selected volume UUIDs per controller
            var selectedByController = await mainDb.SelectedNetappVolumes
                .AsNoTracking()
                .Where(x => !string.IsNullOrWhiteSpace(x.Uuid))
                .GroupBy(x => x.NetappControllerId)
                .ToDictionaryAsync(
                    g => g.Key,
                    g => g.Select(v => v.Uuid!).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    ct);

            // NetApp volumes:
            // - If any selections exist, sync only those UUIDs per controller.
            // - Otherwise, do a full scan.
            try
            {
                if (selectedByController.Count > 0)
                {
                    await SyncNetappVolumesSelectedAsync(
                        qdb,
                        controllers,
                        netappVolumeService,
                        selectedByController,
                        ct);
                }
                else
                {
                    await SyncNetappVolumesAsync(
                        qdb,
                        controllers,
                        netappVolumeService,
                        ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Inventory infra-side: NetApp volume sync failed.");
            }

            // Replication table rebuild
            try
            {
                await SyncVolumeReplicationAsync(qdb, relations, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Inventory infra-side: replication sync failed.");
            }

            // (Optional) Redundant if your volume sync already does this, but safe to keep.
            try
            {
                await MapStoragesToNetappVolumesAsync(qdb, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Inventory infra-side: storage mapping failed.");
            }

            // Track snapshot presence after infra updates
            try
            {
                await TrackNetappSnapshotsAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Inventory infra-side: snapshot tracking failed.");
            }

            await UpsertMetadataAsync(qdb, "LastInfraInventorySyncUtc", DateTime.UtcNow.ToString("O"), ct);
        }



        private async Task TrackNetappSnapshotsAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var netappSnapshotService = scope.ServiceProvider.GetRequiredService<INetappSnapshotService>();
            var now = DateTime.UtcNow;

            // --- READS from MAIN DB ---
            await using var main = await _dbf.CreateAsync(ct);

            // 0) Valid controllers
            var validControllers = (await main.NetappControllers
                    .AsNoTracking()
                    .Select(n => n.Id)
                    .ToListAsync(ct))
                .ToHashSet();

            // 1) Relations
            var relations = await main.SnapMirrorRelations
                .AsNoTracking()
                .ToListAsync(ct);

            if (relations.Count == 0) return;

            // --- WRITES to QUERY DB ---
            await using var qdb = await _qdbf.CreateAsync(ct);

            // 2) Tracked snapshot rows -> lookup  (QUERY DB)
            var trackedSnaps = await qdb.NetappSnapshots
                .AsTracking()
                .ToListAsync(ct);

            var trackedLookup = trackedSnaps.ToDictionary(
                s => new JobSnapKey(s.JobId, s.SnapshotName),
                s => s,
                new JobSnapKeyComparer()
            );

            // 3) For each relation, list snapshots (primary+secondary), then upsert in batch
            foreach (var rel in relations)
            {
                if (!validControllers.Contains(rel.SourceControllerId) ||
                    !validControllers.Contains(rel.DestinationControllerId))
                {
                    _logger.LogWarning("Skipping relation {Uuid}: invalid controller(s) {Src}->{Dst}",
                        rel.Uuid, rel.SourceControllerId, rel.DestinationControllerId);
                    continue;
                }

                // Snapshot listings (via service)
                var secondarySnaps = await netappSnapshotService
                    .GetSnapshotsAsync(rel.DestinationControllerId, rel.DestinationVolume, ct);

                var primarySnapsList = await netappSnapshotService
                    .GetSnapshotsAsync(rel.SourceControllerId, rel.SourceVolume, ct);

                var primarySet = new HashSet<string>(primarySnapsList, StringComparer.OrdinalIgnoreCase);
                var secondarySet = new HashSet<string>(secondarySnaps, StringComparer.OrdinalIgnoreCase);

                // UNION: track snaps appearing on either side (covers primary-only during upgrades/lag)
                var allSnaps = new HashSet<string>(primarySet, StringComparer.OrdinalIgnoreCase);
                foreach (var s in secondarySet) allSnaps.Add(s);

                if (allSnaps.Count == 0)
                    continue;

                // Map (sourceVolume + snapshotName) -> JobId in ONE query per relation (MAIN DB)
                var jobMap = await main.BackupRecords
                    .AsNoTracking()
                    .Where(r => r.StorageName == rel.SourceVolume && allSnaps.Contains(r.SnapshotName))
                    .Select(r => new { r.SnapshotName, r.JobId, r.RetentionUnit })
                    .ToListAsync(ct);

                var jobBySnap = jobMap
                    .GroupBy(x => x.SnapshotName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                foreach (var snapName in allSnaps)
                {
                    if (!jobBySnap.TryGetValue(snapName, out var meta) || meta.JobId == 0)
                        continue; // no job context -> skip (policy unchanged)

                    var key = new JobSnapKey(meta.JobId, snapName);
                    var onPrimary = primarySet.Contains(snapName);
                    var onSecondary = secondarySet.Contains(snapName);

                    if (trackedLookup.TryGetValue(key, out var existing))
                    {
                        existing.ExistsOnPrimary = onPrimary;
                        existing.ExistsOnSecondary = onSecondary;
                        existing.IsReplicated = onPrimary && onSecondary;
                        existing.LastChecked = now;

                        // Always ensure relation ids/vols are present
                        if (existing.PrimaryControllerId == 0)
                        {
                            existing.PrimaryControllerId = rel.SourceControllerId;
                            existing.PrimaryVolume = rel.SourceVolume;
                        }
                        existing.SecondaryControllerId = rel.DestinationControllerId;
                        existing.SecondaryVolume = rel.DestinationVolume;
                    }
                    else
                    {
                        var label = (meta.RetentionUnit ?? "not_found").ToLowerInvariant();

                        var row = new NetappSnapshot
                        {
                            JobId = meta.JobId,
                            SnapshotName = snapName,
                            SnapmirrorLabel = label,
                            CreatedAt = now,
                            LastChecked = now,

                            ExistsOnPrimary = onPrimary,
                            ExistsOnSecondary = onSecondary,
                            IsReplicated = onPrimary && onSecondary,

                            PrimaryControllerId = rel.SourceControllerId,
                            PrimaryVolume = rel.SourceVolume,
                            SecondaryControllerId = rel.DestinationControllerId,
                            SecondaryVolume = rel.DestinationVolume
                        };

                        qdb.NetappSnapshots.Add(row);
                        trackedLookup[key] = row;
                    }
                }
            }

            // Save with SQLITE_BUSY retry (QUERY DB)
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    await qdb.SaveChangesAsync(ct);
                    break;
                }
                catch (DbUpdateException ey) when (
                    ey.InnerException is Microsoft.Data.Sqlite.SqliteException se &&
                    se.SqliteErrorCode == 5 /* SQLITE_BUSY */)
                {
                    if (i == 2) throw;
                    await Task.Delay(500, ct);
                }
            }
        }





        // =====================================================================
        // Proxmox Inventory (helpers)
        // =====================================================================

        private async Task SyncProxmoxClusterInventoryAsync(
            QueryDbContext qdb,
            ProxmoxCluster cluster,
            IProxmoxOpsService proxOps,
            CancellationToken ct)
        {
            var now = DateTime.UtcNow;

            // NEW: get online hosts from the Query DB (inventory)
            var onlineHosts = await qdb.InventoryHostStatuses
                .AsNoTracking()
                .Where(h => h.ClusterId == cluster.Id && h.IsOnline)
                .Select(h => new
                {
                    h.HostId,
                    h.Hostname,
                    h.HostAddress
                })
                .ToListAsync(ct);

            if (onlineHosts.Count == 0)
            {
                _logger.LogDebug(
                    "Inventory: no online hosts for cluster {ClusterId}; skipping storage/VM scan.",
                    cluster.Id);
                return;
            }

            var apiHost = onlineHosts[0]; // entry node
            var baseUrl = $"https://{apiHost.HostAddress}:8006/api2/json";

            // ---------------- VMs ----------------
            var vmUrl = $"{baseUrl}/cluster/resources?type=vm";
            using (var vmResp = await proxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, vmUrl, null, ct))
            {
                vmResp.EnsureSuccessStatusCode();
                using var vmDoc = JsonDocument.Parse(await vmResp.Content.ReadAsStringAsync(ct));

                var existingVms = await qdb.InventoryVms
                    .Where(v => v.ClusterId == cluster.Id)
                    .ToDictionaryAsync(v => v.VmId, ct);

                var seenVmIds = new HashSet<int>();

                if (vmDoc.RootElement.TryGetProperty("data", out var vmArr) && vmArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in vmArr.EnumerateArray())
                    {
                        if (!el.TryGetProperty("vmid", out var vmidProp) || vmidProp.ValueKind != JsonValueKind.Number)
                            continue;

                        var vmid = vmidProp.GetInt32();
                        seenVmIds.Add(vmid);

                        var node = el.TryGetProperty("node", out var nodeProp) && nodeProp.ValueKind == JsonValueKind.String
                            ? nodeProp.GetString() ?? ""
                            : "";

                        var name = el.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                            ? nameProp.GetString() ?? $"VM {vmid}"
                            : $"VM {vmid}";

                        var type = el.TryGetProperty("type", out var tProp) && tProp.ValueKind == JsonValueKind.String
                            ? tProp.GetString() ?? "qemu"
                            : "qemu";

                        var status = el.TryGetProperty("status", out var sProp) && sProp.ValueKind == JsonValueKind.String
                            ? sProp.GetString() ?? "unknown"
                            : "unknown";

                        if (!existingVms.TryGetValue(vmid, out var vm))
                        {
                            vm = new InventoryVm { ClusterId = cluster.Id, VmId = vmid };
                            qdb.InventoryVms.Add(vm);
                            existingVms[vmid] = vm;
                        }

                        vm.Name = name;
                        vm.NodeName = node;
                        vm.Type = type;
                        vm.Status = status;
                        vm.LastSeenUtc = now;
                    }
                }

                // remove stale VMs
                var staleVms = existingVms.Values.Where(v => !seenVmIds.Contains(v.VmId)).ToList();
                if (staleVms.Count > 0)
                    qdb.InventoryVms.RemoveRange(staleVms);

                await qdb.SaveChangesAsync(ct);
            }

            // ---------------- Storages (cluster view) ----------------
            var storUrl = $"{baseUrl}/cluster/resources?type=storage";
            using var storResp = await proxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, storUrl, null, ct);
            storResp.EnsureSuccessStatusCode();

            var storageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidateStorages = new List<(string StorageId, string Type, string ContentFlags)>();

            using (var storDoc = JsonDocument.Parse(await storResp.Content.ReadAsStringAsync(ct)))
            {
                if (storDoc.RootElement.TryGetProperty("data", out var storArr) && storArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in storArr.EnumerateArray())
                    {
                        var type = el.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                            ? t.GetString() ?? ""
                            : "";

                        var storageId = el.TryGetProperty("storage", out var sid) && sid.ValueKind == JsonValueKind.String
                            ? sid.GetString() ?? ""
                            : "";

                        var content = el.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                            ? c.GetString() ?? ""
                            : "";

                        if (string.IsNullOrWhiteSpace(storageId))
                            continue;

                        // We care about NFS storages that can hold VM images/rootfs
                        var isNfs = type.Equals("nfs", StringComparison.OrdinalIgnoreCase);
                        var isImageCapable =
                            content.Contains("images", StringComparison.OrdinalIgnoreCase) ||
                            content.Contains("rootdir", StringComparison.OrdinalIgnoreCase);

                        if (isNfs && isImageCapable)
                        {
                            storageIds.Add(storageId);
                            candidateStorages.Add((storageId, type, content));
                        }
                    }
                }
            }

            // Fallback: if cluster view returned nothing, use /storage (storage.cfg list)
            if (candidateStorages.Count == 0)
            {
                var cfgUrl = $"{baseUrl}/storage";
                _logger.LogDebug("Inventory: cluster storage list empty; falling back to {Url}", cfgUrl);
                using var cfgResp = await proxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, cfgUrl, null, ct);
                if (cfgResp.IsSuccessStatusCode)
                {
                    using var cfgDoc = JsonDocument.Parse(await cfgResp.Content.ReadAsStringAsync(ct));
                    if (cfgDoc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in arr.EnumerateArray())
                        {
                            var type = el.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                                ? t.GetString() ?? ""
                                : "";
                            if (!type.Equals("nfs", StringComparison.OrdinalIgnoreCase))
                                continue;

                            var storageId = el.TryGetProperty("storage", out var sid) && sid.ValueKind == JsonValueKind.String
                                ? sid.GetString() ?? ""
                                : "";
                            if (string.IsNullOrWhiteSpace(storageId))
                                continue;

                            var content = el.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                                ? c.GetString() ?? ""
                                : "";

                            var isImageCapable =
                                content.Contains("images", StringComparison.OrdinalIgnoreCase) ||
                                content.Contains("rootdir", StringComparison.OrdinalIgnoreCase);

                            if (isImageCapable)
                            {
                                storageIds.Add(storageId);
                                candidateStorages.Add((storageId, type, content));
                            }
                        }
                    }
                }
            }

            // Upsert rows for candidate storages
            var existingStor = await qdb.InventoryStorages
                .Where(s => s.ClusterId == cluster.Id)
                .ToDictionaryAsync(s => s.StorageId, ct);

            var seenStorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (storageId, type, content) in candidateStorages)
            {
                seenStorIds.Add(storageId);

                if (!existingStor.TryGetValue(storageId, out var s))
                {
                    s = new InventoryStorage { ClusterId = cluster.Id, StorageId = storageId };
                    qdb.InventoryStorages.Add(s);
                    existingStor[storageId] = s;
                }

                s.Type = type;
                s.ContentFlags = content;
                s.IsImageCapable = true;
                s.Shared = true;
                s.LastSeenUtc = now;
                s.LastScanStatus = "ok";
            }

            // remove storages no longer seen
            var staleStor = existingStor.Values.Where(s => !seenStorIds.Contains(s.StorageId)).ToList();
            if (staleStor.Count > 0)
                qdb.InventoryStorages.RemoveRange(staleStor);

            await qdb.SaveChangesAsync(ct);

            // ---------------- Enrich with details (server/export/path/options) ----------------
            foreach (var storageId in seenStorIds)
            {
                try
                {
                    // Primary: cluster storage cfg entry
                    var detUrl = $"{baseUrl}/storage/{Uri.EscapeDataString(storageId)}";
                    using var detResp = await proxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, detUrl, null, ct);

                    if (!existingStor.TryGetValue(storageId, out var s))
                        continue;

                    if (detResp.IsSuccessStatusCode)
                    {
                        using var detDoc = JsonDocument.Parse(await detResp.Content.ReadAsStringAsync(ct));
                        if (detDoc.RootElement.TryGetProperty("data", out var d))
                        {
                            var server = d.TryGetProperty("server", out var srv) && srv.ValueKind == JsonValueKind.String
                                ? (srv.GetString() ?? "").Trim()
                                : s.Server;

                            var export = d.TryGetProperty("export", out var exp) && exp.ValueKind == JsonValueKind.String
                                ? (exp.GetString() ?? "").Trim()
                                : s.Export;

                            // normalize export to start with '/'
                            if (!string.IsNullOrWhiteSpace(export) && !export.StartsWith("/", StringComparison.Ordinal))
                                export = "/" + export;

                            s.Server = string.IsNullOrWhiteSpace(server) ? s.Server : server;
                            s.Export = string.IsNullOrWhiteSpace(export) ? s.Export : export;
                            s.Path = d.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
                                ? (p.GetString() ?? "").Trim()
                                : s.Path;
                            s.Options = d.TryGetProperty("options", out var opt) && opt.ValueKind == JsonValueKind.String
                                ? (opt.GetString() ?? "").Trim()
                                : s.Options;

                            _logger.LogDebug("Inventory: storage {StorageId} server={Server} export={Export}", storageId, s.Server, s.Export);
                            continue; // enriched from cluster view
                        }
                    }

                    // Fallback: try per-node config (some envs only expose through nodes)
                    var nodeName = string.IsNullOrWhiteSpace(apiHost.Hostname) ? apiHost.HostAddress : apiHost.Hostname!;
                    var nodeCfgUrl = $"{baseUrl}/nodes/{Uri.EscapeDataString(nodeName)}/storage/{Uri.EscapeDataString(storageId)}";
                    using var nodeResp = await proxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, nodeCfgUrl, null, ct);
                    if (nodeResp.IsSuccessStatusCode)
                    {
                        using var nodeDoc = JsonDocument.Parse(await nodeResp.Content.ReadAsStringAsync(ct));
                        if (nodeDoc.RootElement.TryGetProperty("data", out var d))
                        {
                            var server = d.TryGetProperty("server", out var srv) && srv.ValueKind == JsonValueKind.String
                                ? (srv.GetString() ?? "").Trim()
                                : s.Server;

                            var export = d.TryGetProperty("export", out var exp) && exp.ValueKind == JsonValueKind.String
                                ? (exp.GetString() ?? "").Trim()
                                : s.Export;

                            if (!string.IsNullOrWhiteSpace(export) && !export.StartsWith("/", StringComparison.Ordinal))
                                export = "/" + export;

                            s.Server = string.IsNullOrWhiteSpace(server) ? s.Server : server;
                            s.Export = string.IsNullOrWhiteSpace(export) ? s.Export : export;

                            _logger.LogDebug("Inventory: (node) storage {StorageId} server={Server} export={Export}", storageId, s.Server, s.Export);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Inventory: storage detail read failed for cluster {ClusterId}, storage {StorageId}",
                        cluster.Id, storageId);
                }
            }

            await qdb.SaveChangesAsync(ct);

            // ---------------- VM ↔ Storage content mapping ----------------
            await SyncVmDisksForClusterAsync(qdb, cluster, proxOps, ct);
        }

        private static readonly Regex VmIdFromVolidRegex =
            new(@"vm-(\d+)-", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private async Task SyncVmDisksForClusterAsync(
            QueryDbContext qdb,
            ProxmoxCluster cluster,
            IProxmoxOpsService proxOps,
            CancellationToken ct)
        {
            var now = DateTime.UtcNow;

            // Storages we care about (image-capable)
            var storages = await qdb.InventoryStorages
                .AsNoTracking()
                .Where(s => s.ClusterId == cluster.Id && s.IsImageCapable)
                .ToListAsync(ct);

            // Get online hosts from inventory (query DB)
            var onlineHost = await qdb.InventoryHostStatuses
                .AsNoTracking()
                .Where(h => h.ClusterId == cluster.Id && h.IsOnline)
                .Select(h => new { h.Hostname, h.HostAddress })
                .FirstOrDefaultAsync(ct);

            if (!storages.Any() || onlineHost is null)
                return;

            // Use a single online node as the authoritative reader to avoid duplicates
            var nodeName = !string.IsNullOrWhiteSpace(onlineHost.Hostname)
                ? onlineHost.Hostname
                : onlineHost.HostAddress;

            // Clear old rows for this cluster, then rebuild quickly
            qdb.InventoryVmDisks.RemoveRange(qdb.InventoryVmDisks.Where(d => d.ClusterId == cluster.Id));
            await qdb.SaveChangesAsync(ct);

            // De-dupe keys to respect UNIQUE(ClusterId, VmId, StorageId, VolId)
            var seen = new HashSet<(int VmId, string StorageId, string VolId)>();
            var batch = new List<InventoryVmDisk>();

            foreach (var storage in storages)
            {
                try
                {
                    var url = $"https://{onlineHost.HostAddress}:8006/api2/json/nodes/{Uri.EscapeDataString(nodeName)}/storage/{Uri.EscapeDataString(storage.StorageId)}/content";
                    using var resp = await proxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
                    if (!resp.IsSuccessStatusCode) continue;

                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                    if (!doc.RootElement.TryGetProperty("data", out var arr) || arr.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var item in arr.EnumerateArray())
                    {
                        // Keep only VM/CT images/rootdir
                        if (item.TryGetProperty("content", out var contentProp) &&
                            contentProp.ValueKind == JsonValueKind.String)
                        {
                            var c = contentProp.GetString();
                            if (!string.Equals(c, "images", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(c, "rootdir", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                        }

                        // vmid from field or fallback to volid parsing
                        int vmid;
                        if (item.TryGetProperty("vmid", out var vmidProp) && vmidProp.ValueKind == JsonValueKind.Number)
                        {
                            vmid = vmidProp.GetInt32();
                        }
                        else if (item.TryGetProperty("volid", out var volidProp) &&
                                 volidProp.ValueKind == JsonValueKind.String)
                        {
                            var volidS = volidProp.GetString() ?? "";
                            var m = VmIdFromVolidRegex.Match(volidS);
                            if (!m.Success || !int.TryParse(m.Groups[1].Value, out vmid))
                                continue;
                        }
                        else
                        {
                            continue;
                        }

                        var volId = item.TryGetProperty("volid", out var vProp) && vProp.ValueKind == JsonValueKind.String
                            ? vProp.GetString() ?? ""
                            : "";
                        if (string.IsNullOrWhiteSpace(volId))
                            continue;

                        var key = (vmid, storage.StorageId, volId);
                        if (!seen.Add(key))
                            continue; // duplicate (would violate UNIQUE)

                        batch.Add(new InventoryVmDisk
                        {
                            ClusterId = cluster.Id,
                            VmId = vmid,
                            StorageId = storage.StorageId,
                            VolId = volId,
                            NodeName = nodeName,
                            LastSeenUtc = now
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Inventory: VM disk scan failed for Cluster {Cluster} Storage {Storage} via Node {Node}",
                        cluster.Id, storage.StorageId, nodeName);
                }
            }

            if (batch.Count > 0)
            {
                qdb.InventoryVmDisks.AddRange(batch);
                await qdb.SaveChangesAsync(ct);
            }
        }

        // =====================================================================
        // NetApp inventory + mapping + replication
        // =====================================================================

        private async Task SyncNetappVolumesAsync(
            QueryDbContext qdb,
            List<NetappController> controllers,
            INetappVolumeService volumeService,
            CancellationToken ct)
        {
            var now = DateTime.UtcNow;

            // Existing rows keyed by VolumeUuid
            var existing = await qdb.InventoryNetappVolumes
                .ToDictionaryAsync(v => v.VolumeUuid, ct);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var c in controllers)
            {
                List<NetappMountInfo> vols;
                try
                {
                    vols = await volumeService.GetVolumesWithMountInfoAsync(c.Id, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Inventory: NetApp volume list failed for controller {Id}", c.Id);
                    continue;
                }

                // Group by UUID (skip entries without a UUID)
                foreach (var g in vols.Where(v => !string.IsNullOrWhiteSpace(v.Uuid))
                                       .GroupBy(v => v.Uuid!, StringComparer.OrdinalIgnoreCase))
                {
                    var first = g.First();
                    var uuid = g.Key;

                    seen.Add(uuid);

                    // Aggregate distinct IPs from all entries (MountIps is a comma/space/semicolon list)
                    var ips = g.SelectMany(v =>
                                    string.IsNullOrWhiteSpace(v.MountIps)
                                        ? Enumerable.Empty<string>()
                                        : v.MountIps.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                               .Select(s => s.Trim())
                               .Where(s => !string.IsNullOrWhiteSpace(s))
                               .Distinct(StringComparer.OrdinalIgnoreCase)
                               .ToArray();

                    // Prefer a non-empty junction path if any entry has it
                    var junction = g.Select(v => v.JunctionPath)
                                    .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

                    // Snapshot locking (tri-state)
                    bool? snapshotLocking = null;
                    if (g.Any(v => v.SnapshotLockingEnabled == true))
                        snapshotLocking = true;
                    else if (g.Any(v => v.SnapshotLockingEnabled == false))
                        snapshotLocking = false;

                    if (!existing.TryGetValue(uuid, out var row))
                    {
                        row = new InventoryNetappVolume { VolumeUuid = uuid };
                        qdb.InventoryNetappVolumes.Add(row);
                        existing[uuid] = row;
                    }

                    row.NetappControllerId = c.Id;
                    row.SvmName = first.VserverName ?? string.Empty;
                    row.VolumeName = first.VolumeName ?? string.Empty;
                    row.JunctionPath = string.IsNullOrWhiteSpace(junction) ? null : junction;
                    row.NfsIps = ips.Length > 0 ? string.Join(",", ips) : null;
                    row.SnapshotLockingEnabled = snapshotLocking;
                    row.IsPrimary = c.IsPrimary == true;
                    row.LastSeenUtc = now;
                }
            }

            // Remove volumes not seen anymore
            var stale = existing.Values.Where(v => !seen.Contains(v.VolumeUuid)).ToList();
            if (stale.Count > 0)
                qdb.InventoryNetappVolumes.RemoveRange(stale);

            await qdb.SaveChangesAsync(ct);

            // After volumes updated, map storages -> NetApp volume UUID
            await MapStoragesToNetappVolumesAsync(qdb, ct);
        }

        private static string NormalizeExport(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.Trim();
            if (!s.StartsWith("/", StringComparison.Ordinal)) s = "/" + s;
            return s;
        }

        private async Task MapStoragesToNetappVolumesAsync(QueryDbContext qdb, CancellationToken ct)
        {
            var vols = await qdb.InventoryNetappVolumes.AsNoTracking().ToListAsync(ct);
            var storages = await qdb.InventoryStorages.ToListAsync(ct);

            foreach (var s in storages)
            {
                s.NetappVolumeUuid = null;
                s.MatchQuality = "none";

                if (string.IsNullOrWhiteSpace(s.Server) || string.IsNullOrWhiteSpace(s.Export))
                    continue;

                var exportNorm = NormalizeExport(s.Export);

                var matches = vols.Where(v =>
                    !string.IsNullOrEmpty(v.JunctionPath) &&
                    string.Equals(NormalizeExport(v.JunctionPath), exportNorm, StringComparison.OrdinalIgnoreCase) &&
                    (v.NfsIps ?? "")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim())
                        .Any(ip => ip.Equals(s.Server, StringComparison.OrdinalIgnoreCase))
                ).ToList();

                if (matches.Count == 1)
                {
                    s.NetappVolumeUuid = matches[0].VolumeUuid;
                    s.MatchQuality = "exact";
                }
                else if (matches.Count > 1)
                {
                    s.MatchQuality = "ambiguous";
                }
            }

            await qdb.SaveChangesAsync(ct);
        }

        private async Task SyncNetappVolumesSelectedAsync(
      QueryDbContext qdb,
      List<NetappController> controllers,
      INetappVolumeService volumeService,
      IDictionary<int, HashSet<string>> selectedByController,
      CancellationToken ct)
        {
            var now = DateTime.UtcNow;

            // Existing rows keyed by VolumeUuid
            var existing = await qdb.InventoryNetappVolumes
                .ToDictionaryAsync(v => v.VolumeUuid, ct);

            // Track what we actually observed per controller (for safe pruning)
            var seenByController = new Dictionary<int, HashSet<string>>();

            foreach (var c in controllers)
            {
                ct.ThrowIfCancellationRequested();

                if (!selectedByController.TryGetValue(c.Id, out var uuids) || uuids.Count == 0)
                    continue;

                var seenForThisController = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                seenByController[c.Id] = seenForThisController;

                // Process each selected UUID (distinct, trimmed)
                foreach (var rawUuid in uuids)
                {
                    ct.ThrowIfCancellationRequested();

                    var uuid = (rawUuid ?? string.Empty).Trim();
                    if (uuid.Length == 0) continue;

                    List<NetappMountInfo> infos;
                    try
                    {
                        infos = await volumeService.GetVolumesWithMountInfoByUuidAsync(c.Id, uuid, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Inventory: mount info fetch failed for controller {Ctl} uuid {Uuid}", c.Id, uuid);
                        continue;
                    }

                    if (infos is null || infos.Count == 0)
                        continue;

                    // Group by UUID (usually a single group)
                    foreach (var g in infos
                                 .Where(v => !string.IsNullOrWhiteSpace(v.Uuid))
                                 .GroupBy(v => v.Uuid!, StringComparer.OrdinalIgnoreCase))
                    {
                        var first = g.First();
                        var keyUuid = g.Key;

                        seenForThisController.Add(keyUuid);

                        // Gather distinct IPs across entries (MountIps may be comma/space/semicolon separated)
                        var ips = g.SelectMany(v =>
                                        string.IsNullOrWhiteSpace(v.MountIps)
                                            ? Enumerable.Empty<string>()
                                            : v.MountIps.Split(new[] { ',', ';', ' ' },
                                                StringSplitOptions.RemoveEmptyEntries))
                                   .Select(s => s.Trim())
                                   .Where(s => !string.IsNullOrWhiteSpace(s))
                                   .Distinct(StringComparer.OrdinalIgnoreCase)
                                   .ToArray();

                        // Prefer a non-empty junction path
                        var junction = g.Select(v => v.JunctionPath)
                                        .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

                        // Snapshot locking (tri-state)
                        bool? snapLock = null;
                        if (g.Any(v => v.SnapshotLockingEnabled == true)) snapLock = true;
                        else if (g.Any(v => v.SnapshotLockingEnabled == false)) snapLock = false;

                        if (!existing.TryGetValue(keyUuid, out var row))
                        {
                            row = new InventoryNetappVolume { VolumeUuid = keyUuid };
                            qdb.InventoryNetappVolumes.Add(row);
                            existing[keyUuid] = row;
                        }

                        row.NetappControllerId = c.Id;
                        row.SvmName = first.VserverName ?? string.Empty;
                        row.VolumeName = first.VolumeName ?? string.Empty;
                        row.JunctionPath = string.IsNullOrWhiteSpace(junction) ? null : junction;
                        row.NfsIps = ips.Length > 0 ? string.Join(",", ips) : null;
                        row.SnapshotLockingEnabled = snapLock;
                        row.IsPrimary = c.IsPrimary == true;
                        row.LastSeenUtc = now;
                    }
                }
            }

            // Prune only rows for controllers we processed where the UUID wasn't seen
            if (seenByController.Count > 0)
            {
                var stale = existing.Values
                    .Where(v =>
                        v.NetappControllerId != 0 &&
                        seenByController.TryGetValue(v.NetappControllerId, out var seenSet) &&
                        !seenSet.Contains(v.VolumeUuid))
                    .ToList();

                if (stale.Count > 0)
                    qdb.InventoryNetappVolumes.RemoveRange(stale);
            }

            await qdb.SaveChangesAsync(ct);

            // Keep mapping step
            await MapStoragesToNetappVolumesAsync(qdb, ct);
        }



        private async Task SyncVolumeReplicationAsync(
            QueryDbContext qdb,
            List<SnapMirrorRelation> relations,
            CancellationToken ct)
        {
            var now = DateTime.UtcNow;

            // Rebuild (small table)
            qdb.InventoryVolumeReplications.RemoveRange(qdb.InventoryVolumeReplications);
            await qdb.SaveChangesAsync(ct);

            // Materialize groups first (avoid CS1662)
            var groups = await qdb.InventoryNetappVolumes
                .AsNoTracking()
                .GroupBy(v => new { v.NetappControllerId, v.VolumeName })
                .ToListAsync(ct);

            var volsByControllerAndName = groups.ToDictionary(
                g => (g.Key.NetappControllerId, Name: g.Key.VolumeName),
                g => g.ToList());

            foreach (var r in relations)
            {
                if (r.SourceControllerId == 0 || r.DestinationControllerId == 0)
                    continue;
                if (string.IsNullOrWhiteSpace(r.SourceVolume) || string.IsNullOrWhiteSpace(r.DestinationVolume))
                    continue;

                if (!volsByControllerAndName.TryGetValue((r.SourceControllerId, r.SourceVolume), out var srcList))
                    continue;
                if (!volsByControllerAndName.TryGetValue((r.DestinationControllerId, r.DestinationVolume), out var dstList))
                    continue;

                foreach (var src in srcList)
                    foreach (var dst in dstList)
                    {
                        qdb.InventoryVolumeReplications.Add(new InventoryVolumeReplication
                        {
                            PrimaryVolumeUuid = src.VolumeUuid,
                            SecondaryVolumeUuid = dst.VolumeUuid,
                            LastSeenUtc = now
                        });
                    }
            }

            await qdb.SaveChangesAsync(ct);
        }

        private static async Task UpsertMetadataAsync(
            QueryDbContext qdb,
            string key,
            string value,
            CancellationToken ct)
        {
            var meta = await qdb.InventoryMetadata.FindAsync(new object[] { key }, ct);
            if (meta == null)
            {
                qdb.InventoryMetadata.Add(new InventoryMetadata { Key = key, Value = value });
            }
            else
            {
                meta.Value = value;
            }
            await qdb.SaveChangesAsync(ct);
        }

        // =====================================================================
        // JobSnapKey helpers (unchanged)
        // =====================================================================

        private readonly struct JobSnapKey
        {
            public int JobId { get; }
            public string SnapshotName { get; }

            public JobSnapKey(int jobId, string snapshotName)
            {
                JobId = jobId;
                SnapshotName = snapshotName;
            }

            public override bool Equals(object? obj)
            {
                if (obj is not JobSnapKey other) return false;
                return JobId == other.JobId
                    && string.Equals(SnapshotName, other.SnapshotName, StringComparison.OrdinalIgnoreCase);
            }

            public override int GetHashCode()
                => HashCode.Combine(JobId, SnapshotName?.ToLowerInvariant());
        }

        private sealed class JobSnapKeyComparer : IEqualityComparer<JobSnapKey>
        {
            public bool Equals(JobSnapKey x, JobSnapKey y)
                => x.JobId == y.JobId
                && string.Equals(x.SnapshotName, y.SnapshotName, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode(JobSnapKey obj)
                => HashCode.Combine(obj.JobId, obj.SnapshotName?.ToLowerInvariant());
        }
    }
}
