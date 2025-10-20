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
using BareProx.Services.Proxmox.Authentication;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BareProx.Services.Proxmox.Helpers
{

    public sealed class ProxmoxHelpersService : IProxmoxHelpersService
    {
        private readonly ApplicationDbContext _context;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IEncryptionService _encryptionService;
        private readonly ILogger<ProxmoxAuthenticator> _logger;

        public ProxmoxHelpersService(
            ApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IEncryptionService encryptionService,
            ILogger<ProxmoxAuthenticator> logger)
        {
            _context = context;
            _httpClientFactory = httpClientFactory;
            _encryptionService = encryptionService;
            _logger = logger;
        }
        /// <summary>
        /// Returns only the hosts that were last marked online. If none have been checked yet,
        /// or all are offline, returns the full list so we can still attempt a first‐time call.
        /// </summary>
        public IEnumerable<ProxmoxHost> GetQueryableHosts(ProxmoxCluster cluster)
        {
            var up = cluster.Hosts.Where(h => h.IsOnline == true).ToList();
            return up.Any() ? up : cluster.Hosts;
        }

        public ProxmoxHost GetHostByNodeName(ProxmoxCluster cluster, string nodeName)
        {
            var host = cluster.Hosts.FirstOrDefault(h => h.Hostname == nodeName);
            if (host == null)
                throw new InvalidOperationException($"Node '{nodeName}' not found in cluster.");
            return host;
        }

        public Dictionary<string, string> FlattenConfig(JsonElement config)
        {
            var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in config.EnumerateObject())
            {
                switch (prop.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        payload[prop.Name] = prop.Value.GetString()!;
                        break;
                    case JsonValueKind.Number:
                        payload[prop.Name] = prop.Value.GetRawText();
                        break;
                }
            }
            return payload;
        }

        public string ExtractOldVmidFromConfig(Dictionary<string, string> payload)
        {
            if (payload == null || payload.Count == 0)
                throw new Exception("Empty payload.");

            var diskKeyRx = new Regex(@"^(scsi|virtio|ide|sata|efidisk|tpmstate)\d+$", RegexOptions.IgnoreCase);
            string? candidate = null;

            // helper to try extract from a single value (the part after "storage:")
            string? TryExtract(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return null;

                var v = value.Trim();

                // If it contains a storage prefix, analyze only the path part after the first ':'
                var parts = v.Split(new[] { ':' }, 2);
                var rhs = parts.Length == 2 ? parts[1] : v;

                // Strip options after comma
                var core = rhs.Split(new[] { ',' }, 2)[0].Trim();

                // Prefer a strong pattern ".../<vmid>/vm-<vmid>-..."
                var m1 = Regex.Match(core, @"(?:^|/)(\d+)/vm-(\d+)-");
                if (m1.Success)
                {
                    // We prefer the vmid from the filename (group 2). If they differ, group 2 is usually authoritative.
                    var vmFromFile = m1.Groups[2].Value;
                    if (!string.IsNullOrEmpty(vmFromFile))
                        return vmFromFile;
                }

                // Fallback: any "vm-<id>-" token
                var m2 = Regex.Match(core, @"vm-(\d+)-");
                if (m2.Success)
                {
                    var vm = m2.Groups[1].Value;
                    if (!string.IsNullOrEmpty(vm))
                        return vm;
                }

                return null;
            }

            // 1) Try disk-like entries first
            foreach (var kv in payload)
            {
                if (!diskKeyRx.IsMatch(kv.Key))
                    continue;

                var vm = TryExtract(kv.Value);
                if (!string.IsNullOrEmpty(vm))
                    return vm;
            }

            // 2) Try vmstate if present
            if (payload.TryGetValue("vmstate", out var vmstateVal))
            {
                var vm = TryExtract(vmstateVal);
                if (!string.IsNullOrEmpty(vm))
                    return vm;
            }

            throw new Exception("Could not determine old VMID from disk configuration.");
        }

        /// Update disk-like values in the payload to point to the new storage & VMID.
        /// Skips pure CD-ROM lines (unless they are cloud-init).
        public void UpdateDiskPathsInConfig(
            Dictionary<string, string> payload,
            string oldVmid,
            string newVmid,
            string cloneStorageName)
        {
            if (payload == null || payload.Count == 0)
                return;

            var diskKeyRx = new Regex(@"^(scsi|virtio|sata|ide|efidisk|tpmstate)\d+$", RegexOptions.IgnoreCase);

            var keys = payload.Keys.Where(k => diskKeyRx.IsMatch(k)).ToList();
            foreach (var key in keys)
            {
                var raw = payload[key];
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                if (!raw.Contains(":"))
                    continue; // not a volid form; leave as-is

                var parts = raw.Split(new[] { ':' }, 2);
                if (parts.Length < 2)
                    continue;

                var rhs = parts[1];
                var sub = rhs.Split(new[] { ',' }, 2);
                var pathWithFilename = sub[0].Trim();           // e.g. "101/vm-101-disk-0.qcow2" or "pool/vm-101-disk-0"
                var options = sub.Length > 1 ? ("," + sub[1]) : string.Empty;

                // Skip pure CD-ROM (keep cloud-init)
                if (options.IndexOf("media=cdrom", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    options.IndexOf("cloudinit", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                // Replace VMID occurrences:
                // - directory segment ".../<oldVmid>/..."
                // - filename token "vm-<oldVmid>-"
                var newPath = pathWithFilename
                    .Replace($"{oldVmid}/", $"{newVmid}/", StringComparison.Ordinal) // path segment
                    .Replace($"vm-{oldVmid}-", $"vm-{newVmid}-", StringComparison.OrdinalIgnoreCase); // filename token

                // If there was no directory part but filename contained vm-<old>-,
                // the replace above already fixed it. We don't force-add "<newVmid>/"
                // because not all storages use the subdir form (e.g., rbd/zfs).

                payload[key] = $"{cloneStorageName}:{newPath}{options}";
            }
        }

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

        public string ToPosix(string p) => (p ?? "").Replace('\\', '/');
        public string GetDirPosix(string p)
        {
            p = ToPosix(p);
            var i = p.LastIndexOf('/');
            if (i < 0) return "/";
            return i == 0 ? "/" : p[..i];
        }
        // Small helper to quote bash paths
        public string EscapeBash(string path) => "'" + path.Replace("'", "'\"'\"'") + "'";

        // Disk-ish keys we care about in qemu .conf payloads
        private static readonly Regex KeyIsDiskLike = new(
            @"^(virtio|scsi|sata|ide)\d+|^(efidisk0|tpmstate0)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Common VMID patterns inside RHS values
        private static readonly Regex VmidFromVmDash = new(
            @"vm-(?<id>\d+)-(?:disk|state)-",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex VmidFromPathSegment = new(
            @"(?<!\d)(?<id>\d{1,9})(?=/)",
            RegexOptions.Compiled);

        // Replace helpers
        private static readonly Regex ReplaceVmIdInName = new(
            @"vm-(?<id>\d+)-",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ReplaceVmIdInSnap = new(
            @"(?<=-vm-)(?<id>\d+)(?=-disk-)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);


    }
}