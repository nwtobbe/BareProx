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

using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BareProx.Data;
using BareProx.Models;
using BareProx.Services;
using BareProx.Services.Proxmox;
using BareProx.Services.Proxmox.Ops;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BareProx.Services.Background
{
    public sealed class CollectionService : BackgroundService
    {
        private readonly IDbFactory _dbf;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<CollectionService> _logger;

        public CollectionService(IDbFactory dbf, IServiceScopeFactory scopeFactory, ILogger<CollectionService> logger)
        {
            _dbf = dbf;
            _scopeFactory = scopeFactory;
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
                    // --- SnapMirror relation sync (scoped) ---
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var netappSnapmirrorService = scope.ServiceProvider.GetRequiredService<INetappSnapmirrorService>();
                        await netappSnapmirrorService.SyncSnapMirrorRelationsAsync(stoppingToken);
                    }

                    // Ensure policies (scoped inside)
                    await EnsureSnapMirrorPoliciesAsync(stoppingToken);

                    // Update selected volumes hourly
                    if (DateTime.UtcNow >= nextSelectedVolumeUpdate)
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var volumes = scope.ServiceProvider.GetRequiredService<INetappVolumeService>();
                        await volumes.UpdateAllSelectedVolumesAsync(stoppingToken);
                        nextSelectedVolumeUpdate = DateTime.UtcNow.AddHours(1);
                    }

                    // Cluster/host health every 2 minutes
                    if (DateTime.UtcNow >= nextClusterStatusCheck)
                    {
                        await CheckProxmoxClusterAndHostsStatusAsync(stoppingToken);
                        nextClusterStatusCheck = DateTime.UtcNow.AddMinutes(2);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break; // graceful shutdown
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in CollectionService loop (SnapMirror/Cluster checks).");
                }

                // Sleep between iterations
                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            }
        }

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
                    var fetchedPolicy = await netappSnapmirrorService.SnapMirrorPolicyGet(pair.DestinationControllerId, pair.PolicyUuid!);
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
                    if (dbPolicy.NetworkCompressionEnabled != fetchedPolicy.NetworkCompressionEnabled) { dbPolicy.NetworkCompressionEnabled = fetchedPolicy.NetworkCompressionEnabled; changed = true; }
                    if (dbPolicy.Throttle != fetchedPolicy.Throttle) { dbPolicy.Throttle = fetchedPolicy.Throttle; changed = true; }

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
            System.Collections.Generic.IList<SnapMirrorPolicyRetention> a,
            System.Collections.Generic.IList<SnapMirrorPolicyRetention> b)
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

            static System.Collections.Generic.IEnumerable<(string Label, int Count, bool Preserve, string Warn, string Period)> KeySel(
                System.Collections.Generic.IEnumerable<SnapMirrorPolicyRetention> x)
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

        private async Task CheckProxmoxClusterAndHostsStatusAsync(CancellationToken ct)
        {
            await using var db = await _dbf.CreateAsync(ct);

            using var scope = _scopeFactory.CreateScope();
            var proxmoxService = scope.ServiceProvider.GetRequiredService<ProxmoxService>();
            var proxOps = scope.ServiceProvider.GetRequiredService<IProxmoxOpsService>();

            var clusters = await db.ProxmoxClusters
                .Include(c => c.Hosts)
                .ToListAsync(ct);

            foreach (var cluster in clusters)
            {
                try
                {
                    // 1) Cluster-wide (quorum & basic host reachability)
                    var (quorate, onlineCount, totalCount, hostStates, summary) =
                        await proxmoxService.GetClusterStatusAsync(cluster, ct);

                    cluster.HasQuorum = quorate;
                    cluster.OnlineHostCount = onlineCount;
                    cluster.TotalHostCount = totalCount;

                    // 2) Per-node deeper health (API reachability + node status + storage state)
                    //    We keep this best-effort and squeeze results into LastStatusMessage fields to avoid schema changes.
                    var perNodeSnippets = new System.Collections.Generic.List<string>();

                    foreach (var host in cluster.Hosts)
                    {
                        var node = string.IsNullOrWhiteSpace(host.Hostname) ? host.HostAddress : host.Hostname!;
                        var key = host.Hostname ?? host.HostAddress;

                        var isOnline = hostStates.TryGetValue(key, out var up) && up;

                        // Defaults
                        string nodeSummary = $"node={node}: offline";
                        host.IsOnline = isOnline;
                        host.LastChecked = DateTime.UtcNow;
                        host.LastStatus = isOnline ? "online" : "offline";

                        if (!isOnline)
                        {
                            host.LastStatusMessage = "API offline/unreachable";
                            perNodeSnippets.Add($"{node}: ❌ offline");
                            continue;
                        }

                        // Small per-node timeout so one slow box doesn't block the sweep
                        using var nodeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        nodeCts.CancelAfter(TimeSpan.FromSeconds(8));

                        // 2a) /nodes/{node}/status
                        double? cpuPct = null;
                        double? memPct = null;
                        long? uptime = null;

                        try
                        {
                            var url = $"https://{host.HostAddress}:8006/api2/json/nodes/{Uri.EscapeDataString(node)}/status";
                            var resp = await proxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, nodeCts.Token);
                            var json = await resp.Content.ReadAsStringAsync(nodeCts.Token);

                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.TryGetProperty("data", out var data))
                            {
                                // cpu is fraction (0..1); mem has total/free/used; uptime seconds
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

                        // 2b) /nodes/{node}/storage — count active vs total
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
                                    // Some storages expose "state" or "status"; treat non-"available/active/ok" as issue-ish
                                    if (s.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String)
                                    {
                                        var sv = st.GetString() ?? "";
                                        if (!sv.Equals("available", StringComparison.OrdinalIgnoreCase) &&
                                            !sv.Equals("active", StringComparison.OrdinalIgnoreCase) &&
                                            !sv.Equals("ok", StringComparison.OrdinalIgnoreCase))
                                            storErrorish++;
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Node storage list failed for {Node}", node);
                        }

                        // Build per-node snippet
                        string cpuS = cpuPct.HasValue ? $"{cpuPct:0.0}%" : "n/a";
                        string memS = memPct.HasValue ? $"{memPct:0.0}%" : "n/a";
                        string upS = uptime.HasValue ? $"{TimeSpan.FromSeconds(uptime.Value):d\\.hh\\:mm}" : "n/a";
                        string storS = storTotal > 0 ? $"{storActive}/{storTotal} active" : "n/a";
                        string storWarn = (storDisabled > 0 || storErrorish > 0) ? $" (disabled:{storDisabled}, issues:{storErrorish})" : "";

                        nodeSummary = $"cpu {cpuS}, mem {memS}, up {upS}, storage {storS}{storWarn}";
                        host.LastStatusMessage = nodeSummary;

                        perNodeSnippets.Add($"{node}: ✅ {nodeSummary}");
                    }

                    // 3) Persist summaries
                    var header = $"quorum={(quorate ? "yes" : "no")}, hosts={onlineCount}/{totalCount}";
                    cluster.LastStatus = header;
                    cluster.LastStatusMessage = $"{header}; " + string.Join(" | ", perNodeSnippets);
                    cluster.LastChecked = DateTime.UtcNow;

                    // Save with SQLite retry (db is scoped)
                    for (int i = 0; i < 3; i++)
                    {
                        try
                        {
                            await db.SaveChangesAsync(ct);
                            break;
                        }
                        catch (DbUpdateException ex) when (ex.InnerException is SqliteException se && se.SqliteErrorCode == 5) // SQLITE_BUSY
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

                    for (int i = 0; i < 3; i++)
                    {
                        try
                        {
                            await db.SaveChangesAsync(ct);
                            break;
                        }
                        catch (DbUpdateException ey) when (ey.InnerException is SqliteException se && se.SqliteErrorCode == 5)
                        {
                            if (i == 2) throw;
                            await Task.Delay(500, ct);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 2) Refresh ExistsOnPrimary/Secondary flags and LastChecked for each tracked NetappSnapshot.
        /// </summary>
        private async Task TrackNetappSnapshots(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var netappSnapshotService = scope.ServiceProvider.GetRequiredService<INetappSnapshotService>();
            var now = DateTime.UtcNow;

            // 0) Preload all valid NetappController IDs
            var validControllerIds = await db.NetappControllers
                .AsNoTracking()
                .Select(n => n.Id)
                .ToListAsync(ct);
            var validSet = new System.Collections.Generic.HashSet<int>(validControllerIds);

            // 1) Load all SnapMirror relations up‐front
            var relations = await db.SnapMirrorRelations
                .AsNoTracking()
                .ToListAsync(ct);

            // 2) Load every snapshot row you’re currently tracking
            var trackedSnaps = await db.NetappSnapshots
                .AsTracking()
                .ToListAsync(ct);

            // Build a lookup keyed by (JobId, SnapshotName)
            var trackedLookup = trackedSnaps
                .ToDictionary(
                    s => new JobSnapKey(s.JobId, s.SnapshotName),
                    s => s,
                    new JobSnapKeyComparer()
                );

            // 3) For each SnapMirror relation (primary→secondary):
            foreach (var rel in relations)
            {
                if (!validSet.Contains(rel.SourceControllerId)
                    || !validSet.Contains(rel.DestinationControllerId))
                {
                    _logger.LogWarning(
                        "Skipping relation {Uuid} because source or destination controller is invalid ({src} → {dst})",
                        rel.Uuid,
                        rel.SourceControllerId,
                        rel.DestinationControllerId
                    );
                    continue;
                }

                // Secondary and primary snapshot listings
                var secList = await netappSnapshotService.GetSnapshotsAsync(
                    rel.DestinationControllerId,
                    rel.DestinationVolume, ct);

                var primaryList = await netappSnapshotService.GetSnapshotsAsync(
                    rel.SourceControllerId,
                    rel.SourceVolume, ct);

                foreach (var snapName in secList)
                {
                    // Look up the JobId by BackupRecords (StorageName+SnapshotName)
                    var matchingJobId = await db.BackupRecords
                        .AsNoTracking()
                        .Where(r => r.StorageName == rel.SourceVolume && r.SnapshotName == snapName)
                        .Select(r => r.JobId)
                        .FirstOrDefaultAsync(ct);

                    if (matchingJobId == 0)
                        continue;

                    var key = new JobSnapKey(matchingJobId, snapName);

                    if (trackedLookup.TryGetValue(key, out var existingSnap))
                    {
                        existingSnap.ExistsOnSecondary = true;
                        existingSnap.LastChecked = now;
                        existingSnap.ExistsOnPrimary = primaryList
                            .Any(x => x.Equals(snapName, StringComparison.OrdinalIgnoreCase));
                        existingSnap.SecondaryControllerId = rel.DestinationControllerId;
                        existingSnap.SecondaryVolume = rel.DestinationVolume;
                        existingSnap.IsReplicated = true;
                    }
                    else
                    {
                        // Label: avoid NRE if RetentionUnit is null
                        var label = await db.BackupRecords
                            .AsNoTracking()
                            .Where(r => r.JobId == matchingJobId &&
                                        r.StorageName == rel.SourceVolume &&
                                        r.SnapshotName == snapName)
                            .Select(r => (r.RetentionUnit ?? string.Empty).ToLower())
                            .FirstOrDefaultAsync(ct)
                            ?? "not_found";

                        var newSnap = new NetappSnapshot
                        {
                            CreatedAt = now,
                            ExistsOnPrimary = primaryList.Any(x => x.Equals(snapName, StringComparison.OrdinalIgnoreCase)),
                            ExistsOnSecondary = true,
                            IsReplicated = true,
                            JobId = matchingJobId,
                            LastChecked = now,
                            PrimaryControllerId = rel.SourceControllerId,
                            PrimaryVolume = rel.SourceVolume,
                            SecondaryControllerId = rel.DestinationControllerId,
                            SecondaryVolume = rel.DestinationVolume,
                            SnapmirrorLabel = label,
                            SnapshotName = snapName
                        };

                        db.NetappSnapshots.Add(newSnap);
                        trackedLookup[key] = newSnap;
                    }
                }
            }

            // Save with SQLITE_BUSY retry
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

        private async Task PruneOldOrStuckJobs(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var now = DateTime.UtcNow;
            var cutoff = now.AddDays(-30);

            // 30d policy
            string[] failedStatuses = { "Failed", "Error", "Aborted", "Cancelled" };
            string[] inProgressStatuses = { "Pending", "Queued", "Running", "InProgress",
                                            "Started", "Creating Proxmox snapshots", "Waiting for Proxmox snapshots",
                                            "Proxmox snapshots completed", "Paused VMs", "NetApp snapshot created", "Triggering SnapMirror update" };

            var toDeleteIds = await db.Jobs
                .AsNoTracking()
                .Where(j => (j.CompletedAt ?? j.StartedAt) < cutoff &&
                            (j.Type == "Restore" ||
                             (j.Type == "Backup" && j.Status != null && failedStatuses.Contains(j.Status)) ||
                             (j.Status == null || inProgressStatuses.Contains(j.Status))))
                .Select(j => j.Id)
                .ToListAsync(ct);

            if (toDeleteIds.Count == 0)
            {
                _logger.LogDebug("PruneJobs: nothing to delete (cutoff {Cutoff}).", cutoff);
                return;
            }

            // Collect VM-result IDs for log deletion
            var vmResultIds = await db.JobVmResults
                .AsNoTracking()
                .Where(r => toDeleteIds.Contains(r.JobId))
                .Select(r => r.Id)
                .ToListAsync(ct);

            for (int attempt = 0; attempt < 3; attempt++)
            {
                using var tx = await db.Database.BeginTransactionAsync(ct);
                try
                {
                    // 1) Logs -> via JobVmResultId
                    if (vmResultIds.Count > 0)
                    {
                        db.JobVmLogs.RemoveRange(
                            db.JobVmLogs.Where(l => vmResultIds.Contains(l.JobVmResultId)));
                    }

                    // 2) VM results -> via JobId
                    db.JobVmResults.RemoveRange(
                        db.JobVmResults.Where(r => toDeleteIds.Contains(r.JobId)));

                    // 3) Snapshot tracking & backup records
                    db.NetappSnapshots.RemoveRange(
                        db.NetappSnapshots.Where(s => toDeleteIds.Contains(s.JobId)));
                    db.BackupRecords.RemoveRange(
                        db.BackupRecords.Where(r => toDeleteIds.Contains(r.JobId)));

                    // 4) Finally the jobs
                    db.Jobs.RemoveRange(
                        db.Jobs.Where(j => toDeleteIds.Contains(j.Id)));

                    var affected = await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);

                    _logger.LogInformation(
                        "Pruned {JobCount} jobs older than {Days} days (Restore:any, Backup:failed, stale in-progress). Rows affected: {Rows}.",
                        toDeleteIds.Count, 30, affected);
                    break;
                }
                catch (DbUpdateException ey) when (ey.InnerException is SqliteException se && se.SqliteErrorCode == 5)
                {
                    await tx.RollbackAsync(ct);
                    if (attempt == 2) throw;
                    await Task.Delay(500, ct);
                }
                catch (Exception ex)
                {
                    await tx.RollbackAsync(ct);
                    _logger.LogError(ex, "Failed pruning jobs.");
                    throw;
                }
            }
        }

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
