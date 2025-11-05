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
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using BareProx.Data;
using BareProx.Models;
using BareProx.Services;
using BareProx.Services.Proxmox;
using BareProx.Services.Proxmox.Ops;
using BareProx.Services.Netapp;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BareProx.Services.Background
{

    public sealed class CollectionService : BackgroundService, ICollectionService
    {
        private readonly IDbFactory _dbf;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<CollectionService> _logger;

        public CollectionService(
            IDbFactory dbf,
            IServiceScopeFactory scopeFactory,
            ILogger<CollectionService> logger)
        {
            _dbf = dbf;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        // =====================================================================
        // Background loop
        // =====================================================================

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

                    // Ensure SnapMirror policies
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

                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            }
        }

        // =====================================================================
        // Public API (for controllers/services)
        // =====================================================================

        /// <inheritdoc />
        public Task RunProxmoxClusterStatusCheckAsync(CancellationToken ct = default)
            => CheckProxmoxClusterAndHostsStatusAsync(ct);

        // =====================================================================
        // SnapMirror policies sync
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
        // Proxmox cluster + host status
        // =====================================================================

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

                    // 2) Per-node health: API reachability + status + storage
                    var perNodeSnippets = new List<string>();

                    foreach (var host in cluster.Hosts)
                    {
                        var node = string.IsNullOrWhiteSpace(host.Hostname)
                            ? host.HostAddress
                            : host.Hostname!;
                        var key = host.Hostname ?? host.HostAddress;

                        var isOnline = hostStates.TryGetValue(key, out var up) && up;

                        host.IsOnline = isOnline;
                        host.LastChecked = DateTime.UtcNow;
                        host.LastStatus = isOnline ? "online" : "offline";

                        if (!isOnline)
                        {
                            host.LastStatusMessage = "API offline/unreachable";
                            perNodeSnippets.Add($"{node}: ❌ offline");
                            continue;
                        }

                        using var nodeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        nodeCts.CancelAfter(TimeSpan.FromSeconds(8));

                        double? cpuPct = null;
                        double? memPct = null;
                        long? uptime = null;

                        // 2a) /nodes/{node}/status
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
                                    long tot = mem.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number
                                        ? t.GetInt64()
                                        : 0;
                                    long used = mem.TryGetProperty("used", out var u) && u.ValueKind == JsonValueKind.Number
                                        ? u.GetInt64()
                                        : 0;
                                    if (tot > 0)
                                        memPct = Math.Round(used * 100.0 / tot, 1);
                                }

                                if (data.TryGetProperty("uptime", out var upSecs) && upSecs.ValueKind == JsonValueKind.Number)
                                    uptime = upSecs.GetInt64();
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Node status fetch failed for {Node}", node);
                        }

                        // 2b) /nodes/{node}/storage
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
                        string storWarn =
                            (storDisabled > 0 || storErrorish > 0)
                                ? $" (disabled:{storDisabled}, issues:{storErrorish})"
                                : "";

                        var nodeSummary = $"cpu {cpuS}, mem {memS}, up {upS}, storage {storS}{storWarn}";
                        host.LastStatusMessage = nodeSummary;

                        perNodeSnippets.Add($"{node}: ✅ {nodeSummary}");
                    }

                    var header = $"quorum={(quorate ? "yes" : "no")}, hosts={onlineCount}/{totalCount}";
                    cluster.LastStatus = header;
                    cluster.LastStatusMessage = $"{header}; " + string.Join(" | ", perNodeSnippets);
                    cluster.LastChecked = DateTime.UtcNow;

                    // Save with SQLite retry
                    for (int i = 0; i < 3; i++)
                    {
                        try
                        {
                            await db.SaveChangesAsync(ct);
                            break;
                        }
                        catch (DbUpdateException ex)
                            when (ex.InnerException is SqliteException se && se.SqliteErrorCode == 5) // SQLITE_BUSY
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
