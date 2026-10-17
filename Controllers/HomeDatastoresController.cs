/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 * Copyright (C) 2025-2026 Tobias Modig
 * Licensed under GNU AGPL version 3.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BareProx.Data;
using BareProx.Services.Proxmox.Ops;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BareProx.Controllers
{
    [Route("Home/Datastores")]
    public sealed class HomeDatastoresController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly IQueryDbFactory _qdbf;
        private readonly IProxmoxOpsService _ops;
        private readonly ILogger<HomeDatastoresController> _logger;

        public HomeDatastoresController(ApplicationDbContext db, IQueryDbFactory qdbf, IProxmoxOpsService ops,
            ILogger<HomeDatastoresController> logger)
        {
            _db = db;
            _qdbf = qdbf;
            _ops = ops;
            _logger = logger;
        }

        [HttpGet]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            var clusters = await _db.ProxmoxClusters.AsNoTracking()
                .Include(c => c.Hosts).ToListAsync(ct);
            // InventoryStorages is the authoritative database shortlist. It is maintained
            // by BareProx inventory collection from cluster resources and contains only
            // NFS datastores capable of holding VM disks. Query it before contacting nodes.
            await using var qdb = await _qdbf.CreateAsync(ct);
            var inventoried = await qdb.InventoryStorages.AsNoTracking()
                .Where(s => s.Type == "nfs" && s.Shared && s.IsImageCapable && s.LastScanStatus == "ok")
                .Select(s => new { s.ClusterId, s.StorageId })
                .ToListAsync(ct);
            var allowed = inventoried
                .GroupBy(s => s.ClusterId)
                .ToDictionary(g => g.Key, g => g.Select(s => s.StorageId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase));

            var result = new List<StorageUsage>();
            var warnings = new List<string>();

            // Bound simultaneous Proxmox queries so a large cluster cannot exhaust connections.
            using var limit = new SemaphoreSlim(8);
            var requests = clusters.Where(cluster => allowed.ContainsKey(cluster.Id))
                .SelectMany(cluster => cluster.Hosts.Select(async host =>
            {
                await limit.WaitAsync(ct);
                try
                {
                    var node = string.IsNullOrWhiteSpace(host.Hostname) ? host.HostAddress : host.Hostname;
                    if (string.IsNullOrWhiteSpace(node) || string.IsNullOrWhiteSpace(host.HostAddress)) return;
                    var url = $"https://{host.HostAddress}:8006/api2/json/nodes/{Uri.EscapeDataString(node)}/storage";
                    using var response = await _ops.SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
                    response.EnsureSuccessStatusCode();
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    if (!json.RootElement.TryGetProperty("data", out var array) || array.ValueKind != JsonValueKind.Array)
                        return;
                    var rows = new List<StorageUsage>();
                    foreach (var item in array.EnumerateArray())
                    {
                        // /nodes/{node}/storage: active=1 means mounted/available on this node.
                        if (!GetFlag(item, "active") || !GetFlag(item, "shared") ||
                            !string.Equals(GetText(item, "type"), "nfs", StringComparison.OrdinalIgnoreCase)) continue;
                        var storage = GetText(item, "storage");
                        var total = GetBytes(item, "total");
                        var used = GetBytes(item, "used");
                        if (string.IsNullOrWhiteSpace(storage) ||
                            !allowed[cluster.Id].Contains(storage) || total <= 0 || used < 0) continue;
                        rows.Add(new StorageUsage(cluster.Id, cluster.Name, node, storage,
                            GetText(item, "type"), GetFlag(item, "shared"), used, total));
                    }
                    lock (result) result.AddRange(rows);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not retrieve datastore usage for cluster {ClusterId}, host {Host}",
                        cluster.Id, host.HostAddress);
                    lock (warnings) warnings.Add($"{cluster.Name}: some nodes could not be queried");
                }
                finally { limit.Release(); }
            })).ToArray();
            await Task.WhenAll(requests);

            // Only database-inventoried, currently mounted shared NFS storage is collected.
            // Deduplicate each datastore
            // across nodes within its cluster to avoid duplicate capacity bars.
            var deduped = result
                .GroupBy(x => (x.ClusterId, x.Storage, Node: x.Shared ? "" : x.Node))
                .Select(g => g.OrderByDescending(x => x.TotalBytes).First())
                .OrderBy(x => x.ClusterName).ThenBy(x => x.Storage).ThenBy(x => x.Node)
                .Select(x => new {
                    cluster = x.ClusterName, node = x.Shared ? "Shared" : x.Node,
                    storage = x.Storage, type = x.Type,
                    usedBytes = x.UsedBytes, totalBytes = x.TotalBytes,
                    percentUsed = Math.Round(100.0 * x.UsedBytes / x.TotalBytes, 1)
                })
                .ToList();
            return Ok(new { storages = deduped, warnings = warnings.Distinct().ToArray() });
        }

        private static string GetText(JsonElement e, string key) =>
            e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : "";
        private static long GetBytes(JsonElement e, string key) =>
            e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var n) ? n : -1;
        private static bool GetFlag(JsonElement e, string key) =>
            e.TryGetProperty(key, out var value) && (
                value.ValueKind == JsonValueKind.True ||
                (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n == 1) ||
                (value.ValueKind == JsonValueKind.String && value.GetString() == "1"));

        private sealed record StorageUsage(int ClusterId, string ClusterName, string Node,
            string Storage, string Type, bool Shared, long UsedBytes, long TotalBytes);
    }
}
