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


using BareProx.Data;
using BareProx.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BareProx.Services.Proxmox.Helpers
{
    public sealed class ProxmoxHelpersService : IProxmoxHelpersService
    {
        private readonly IDbContextFactory<ApplicationDbContext> _dbf;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IEncryptionService _encryptionService;
        private readonly ILogger<ProxmoxHelpersService> _logger;
        private readonly IQueryDbFactory _qdbf;

        public ProxmoxHelpersService(
            IDbContextFactory<ApplicationDbContext> dbf,
            IHttpClientFactory httpClientFactory,
            IEncryptionService encryptionService,
            ILogger<ProxmoxHelpersService> logger,
            IQueryDbFactory qdbf)
        {
            _dbf = dbf;
            _httpClientFactory = httpClientFactory;
            _encryptionService = encryptionService;
            _logger = logger;
            _qdbf = qdbf;
        }

        // ---------- Host pickers ----------
        /// <summary>
        /// Preferred: returns configured hosts filtered by InventoryHostStatus.IsOnline.
        /// Falls back to all configured hosts if inventory is empty or no hosts are online.
        /// </summary>
        public async Task<IEnumerable<ProxmoxHost>> GetQueryableHostsAsync(
            ProxmoxCluster cluster,
            CancellationToken ct = default)
        {
            var configured = cluster.Hosts ?? Enumerable.Empty<ProxmoxHost>();
            if (!configured.Any()) return configured;

            using var qdb = _qdbf.Create();
            var onlineIds = await qdb.InventoryHostStatuses
                .AsNoTracking()
                .Where(h => h.ClusterId == cluster.Id && h.IsOnline)
                .Select(h => h.HostId)
                .ToListAsync(ct);

            if (onlineIds.Count == 0)
                return configured; // fallback

            var up = configured.Where(h => onlineIds.Contains(h.Id)).ToList();
            return up.Count > 0 ? up : configured;
        }

        public ProxmoxHost GetHostByNodeName(ProxmoxCluster cluster, string nodeName)
        {
            var host = cluster.Hosts.FirstOrDefault(h => h.Hostname == nodeName);
            if (host == null)
                throw new InvalidOperationException($"Node '{nodeName}' not found in cluster.");
            return host;
        }

        // ---------- NEW: cluster loading / resolver ----------
        public async Task<List<ProxmoxCluster>> LoadAllClustersAsync(CancellationToken ct = default)
        {
            await using var db = await _dbf.CreateDbContextAsync(ct);
            return await db.ProxmoxClusters
                           .Include(c => c.Hosts)
                           .ToListAsync(ct);
        }

        public (ProxmoxCluster Cluster, ProxmoxHost Host)? ResolveClusterAndHostFromLoaded(
            IEnumerable<ProxmoxCluster> clusters, string nodeOrAddress)
        {
            if (clusters is null) return null;

            foreach (var c in clusters)
            {
                var h = c.Hosts.FirstOrDefault(x =>
                    string.Equals(x.Hostname, nodeOrAddress, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.HostAddress, nodeOrAddress, StringComparison.OrdinalIgnoreCase));

                if (h != null) return (c, h);
            }

            // Fallback to the first available
            var first = clusters.FirstOrDefault();
            var firstHost = first?.Hosts.FirstOrDefault();
            return (first != null && firstHost != null) ? (first, firstHost) : null;
        }

        // ---------- Config flatten/extract/update ----------
        public Dictionary<string, string> FlattenConfig(JsonElement config)
        {
            var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in config.EnumerateObject())
            {
                switch (prop.Value.ValueKind)
                {
                    case JsonValueKind.String: payload[prop.Name] = prop.Value.GetString()!; break;
                    case JsonValueKind.Number: payload[prop.Name] = prop.Value.GetRawText(); break;
                }
            }
            return payload;
        }

        public string ExtractOldVmidFromConfig(Dictionary<string, string> payload)
        {
            if (payload == null || payload.Count == 0)
                throw new Exception("Empty payload.");

            var diskKeyRx = new Regex(@"^(scsi|virtio|ide|sata|efidisk|tpmstate)\d+$", RegexOptions.IgnoreCase);

            string? TryExtract(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return null;
                var v = value.Trim();
                var parts = v.Split(new[] { ':' }, 2);
                var rhs = parts.Length == 2 ? parts[1] : v;
                var core = rhs.Split(new[] { ',' }, 2)[0].Trim();

                var m1 = Regex.Match(core, @"(?:^|/)(\d+)/vm-(\d+)-");
                if (m1.Success) return m1.Groups[2].Value;

                var m2 = Regex.Match(core, @"vm-(\d+)-");
                if (m2.Success) return m2.Groups[1].Value;

                return null;
            }

            foreach (var kv in payload)
            {
                if (!diskKeyRx.IsMatch(kv.Key)) continue;
                var vm = TryExtract(kv.Value);
                if (!string.IsNullOrEmpty(vm)) return vm;
            }

            if (payload.TryGetValue("vmstate", out var vmstateVal))
            {
                var vm = TryExtract(vmstateVal);
                if (!string.IsNullOrEmpty(vm)) return vm;
            }

            throw new Exception("Could not determine old VMID from disk configuration.");
        }

        public void UpdateDiskPathsInConfig(
            Dictionary<string, string> payload,
            string oldVmid,
            string newVmid,
            string cloneStorageName)
        {
            if (payload == null || payload.Count == 0) return;

            var diskKeyRx = new Regex(@"^(scsi|virtio|sata|ide|efidisk|tpmstate)\d+$", RegexOptions.IgnoreCase);
            var keys = payload.Keys.Where(k => diskKeyRx.IsMatch(k)).ToList();

            foreach (var key in keys)
            {
                var raw = payload[key];
                if (string.IsNullOrWhiteSpace(raw) || !raw.Contains(":")) continue;

                var parts = raw.Split(new[] { ':' }, 2);
                if (parts.Length < 2) continue;

                var rhs = parts[1];
                var sub = rhs.Split(new[] { ',' }, 2);
                var pathWithFilename = sub[0].Trim();
                var options = sub.Length > 1 ? ("," + sub[1]) : string.Empty;

                // Skip pure CD-ROM (unless cloud-init)
                if (options.IndexOf("media=cdrom", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    options.IndexOf("cloudinit", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var newPath = pathWithFilename
                    .Replace($"{oldVmid}/", $"{newVmid}/", StringComparison.Ordinal)
                    .Replace($"vm-{oldVmid}-", $"vm-{newVmid}-", StringComparison.OrdinalIgnoreCase);

                payload[key] = $"{cloneStorageName}:{newPath}{options}";
            }
        }

        // ---------- JSON helpers ----------
        public bool TryGetTruthy(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var v)) return false;
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => v.TryGetInt32(out var i) && i != 0,
                JsonValueKind.String => int.TryParse(v.GetString(), out var si) ? si != 0 :
                                        bool.TryParse(v.GetString(), out var sb) && sb,
                _ => false
            };
        }

        public long TryGetInt64(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var v)) return 0L;
            try
            {
                return v.ValueKind switch
                {
                    JsonValueKind.Number => v.GetInt64(),
                    JsonValueKind.String => long.TryParse(v.GetString(), out var l) ? l : 0L,
                    _ => 0L
                };
            }
            catch { return 0L; }
        }

        // ---------- path/bash helpers ----------
        public string ToPosix(string p) => (p ?? "").Replace('\\', '/');

        public string GetDirPosix(string p)
        {
            p = ToPosix(p);
            var i = p.LastIndexOf('/');
            if (i < 0) return "/";
            return i == 0 ? "/" : p[..i];
        }

        public string EscapeBash(string path) => "'" + (path ?? string.Empty).Replace("'", "'\"'\"'") + "'";
    }
}
