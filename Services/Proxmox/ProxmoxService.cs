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
using BareProx.Services.Proxmox.Authentication;
using BareProx.Services.Proxmox.Helpers;
using BareProx.Services.Proxmox.Ops;
using BareProx.Services.Proxmox.Snapshots;
using BareProx.Services.Restore;
using Microsoft.EntityFrameworkCore;
using Polly;
using Renci.SshNet;
using System.Diagnostics;
using System.Linq;                    // for OrderBy/Select
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;   // for SHA1
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BareProx.Services.Proxmox
{
    public class ProxmoxService
    {
        private readonly IEncryptionService _encryptionService;
        private readonly INetappVolumeService _netappVolumeService;
        private readonly ILogger<ProxmoxService> _logger;
        private readonly IProxmoxHelpersService _proxmoxHelpers;
        private readonly IProxmoxAuthenticator _proxmoxAuthenticator;
        private readonly IProxmoxInventoryCache _invCache;
        private readonly IProxmoxOpsService _proxmoxOps;
        private readonly IProxmoxSnapshotsService _proxmoxSnapshots;
        private readonly IDbFactory _dbf;

        public ProxmoxService(
            IDbFactory dbf,
            IEncryptionService encryptionService,
            INetappVolumeService netappVolumeService,
            ILogger<ProxmoxService> logger,
            IProxmoxHelpersService proxmoxHelpers,
            IProxmoxAuthenticator proxmoxAuthenticator,
            IProxmoxInventoryCache invCache,
            IProxmoxOpsService proxmoxOps,
            IProxmoxSnapshotsService proxmoxSnapshots)
        {
            _dbf = dbf;
            _encryptionService = encryptionService;
            _netappVolumeService = netappVolumeService;
            _logger = logger;
            _proxmoxHelpers = proxmoxHelpers;
            _proxmoxAuthenticator = proxmoxAuthenticator;
            _invCache = invCache;
            _proxmoxOps = proxmoxOps;
            _proxmoxSnapshots = proxmoxSnapshots;
        }

       
        public async Task<bool> CheckIfVmExistsAsync(ProxmoxCluster cluster, ProxmoxHost host, int vmId, CancellationToken ct = default)
        {
            var url = $"https://{host.HostAddress}:8006/api2/json/nodes/{host.Hostname}/qemu/{vmId}/config";
            try
            {
                var resp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }


        //    public async Task<Dictionary<string, List<ProxmoxVM>>> GetEligibleBackupStorageWithVMsAsyncToCache(
        //ProxmoxCluster cluster,
        //int netappControllerId,
        //List<string>? onlyIncludeStorageNames = null,
        //CancellationToken ct = default)
        //    {
        //        var storageVmMap = onlyIncludeStorageNames != null
        //            ? await _invCache.GetVmsByStorageListAsync(cluster, onlyIncludeStorageNames, ct)
        //            : await GetFilteredStorageWithVMsAsync(cluster.Id, netappControllerId, ct);

        //        return storageVmMap
        //            .Where(kvp =>
        //                !kvp.Key.Contains("backup", StringComparison.OrdinalIgnoreCase) &&
        //                !kvp.Key.Contains("restore_", StringComparison.OrdinalIgnoreCase))
        //            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        //    }

        public async Task<Dictionary<string, List<ProxmoxVM>>> GetFilteredStorageWithVMsAsync(
       int clusterId,
       int netappControllerId,
       CancellationToken ct = default)
        {
            // 1) Load cluster + hosts
            await using var db = await _dbf.CreateAsync(ct);
            var cluster = await db.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync(c => c.Id == clusterId, ct);
            if (cluster == null)
                return new Dictionary<string, List<ProxmoxVM>>();

            // 2) Discover which NFS storages Proxmox actually has mounted
            var proxmoxStorageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hosts = _proxmoxHelpers.GetQueryableHostsAsync(cluster, CancellationToken.None)
                                       .GetAwaiter().GetResult();

            foreach (var host in hosts)
            {
                var url = $"https://{host.HostAddress}:8006/api2/json/nodes/{host.Hostname}/storage";
                var resp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var storage in data.EnumerateArray())
                {
                    if (string.Equals(storage.GetProperty("type").GetString(), "nfs", StringComparison.OrdinalIgnoreCase))
                    {
                        var name = storage.GetProperty("storage").GetString();
                        if (!string.IsNullOrWhiteSpace(name))
                            proxmoxStorageNames.Add(name);
                    }
                }
            }

            // 3) Load *selected* NetApp volumes for this controller (Disabled != true)
            var selectedEnabledForController = await db.SelectedNetappVolumes
                .AsNoTracking()
                .Where(v => v.NetappControllerId == netappControllerId && v.Disabled != true)
                .Select(v => v.VolumeName)
                .ToListAsync(ct);

            var selectedEnabledSet = new HashSet<string>(selectedEnabledForController, StringComparer.OrdinalIgnoreCase);

            // 4) Fetch NetApp volumes (mount info) and keep only those that are selected & exist in Proxmox
            var netappVolumes = await _netappVolumeService.GetVolumesWithMountInfoAsync(netappControllerId, ct);

            var validVolumes = netappVolumes
                .Select(v => v.VolumeName)
                .Where(v => proxmoxStorageNames.Contains(v) && selectedEnabledSet.Contains(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 5) Seed result dictionary
            var result = validVolumes.ToDictionary(
                vol => vol,
                vol => new List<ProxmoxVM>(),
                StringComparer.OrdinalIgnoreCase);

            // 6) For each host, list its VMs and scan their configs
            var diskRegex = new Regex(@"^(scsi|virtio|ide|sata|efidisk|tpmstate)\d+$", RegexOptions.IgnoreCase);

            var onlineHosts = _proxmoxHelpers.GetQueryableHostsAsync(cluster, CancellationToken.None)
                                             .GetAwaiter().GetResult();

            foreach (var host in onlineHosts)
            {
                // a) list VMs
                var vmListUrl = $"https://{host.HostAddress}:8006/api2/json/nodes/{host.Hostname}/qemu";
                var vmListResp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, vmListUrl, null, ct);
                using var vmListDoc = JsonDocument.Parse(await vmListResp.Content.ReadAsStringAsync(ct));
                if (!vmListDoc.RootElement.TryGetProperty("data", out var vmArr) || vmArr.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var vmElem in vmArr.EnumerateArray())
                {
                    if (!vmElem.TryGetProperty("vmid", out var vmidEl) || vmidEl.ValueKind != JsonValueKind.Number)
                        continue;

                    var vmId = vmidEl.GetInt32();
                    var vmName = vmElem.TryGetProperty("name", out var nm)
                        ? (nm.GetString() ?? $"VM {vmId}")
                        : $"VM {vmId}";

                    // fetch full config
                    var cfgJson = await GetVmConfigAsync(cluster, host.Hostname, vmId, ct);
                    using var cfgDoc = JsonDocument.Parse(cfgJson);
                    if (!cfgDoc.RootElement.TryGetProperty("config", out var cfgData) || cfgData.ValueKind != JsonValueKind.Object)
                        continue;

                    var vmDescriptor = new ProxmoxVM
                    {
                        Id = vmId,
                        Name = vmName,
                        HostName = host.Hostname,
                        HostAddress = host.HostAddress
                    };

                    // b) scan every disk line
                    foreach (var prop in cfgData.EnumerateObject())
                    {
                        if (!diskRegex.IsMatch(prop.Name))
                            continue;

                        var raw = prop.Value.GetString();
                        if (string.IsNullOrWhiteSpace(raw))
                            continue;

                        var val = raw.Trim();

                        // --- NEW: skip ISO/CDROM-only entries so ISO storages (proxmox_ds1, etc.)
                        //           are not treated as data/storage for backup selection ---
                        if (val.StartsWith("none,", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (val.IndexOf(".iso", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            val.IndexOf(":iso/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            val.IndexOf("media=cdrom", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // e.g. "proxmox_ds1:iso/virtio-win-0.1.285.iso,media=cdrom,size=..."
                            continue;
                        }

                        var parts = val.Split(':', 2);
                        if (parts.Length < 2)
                            continue;

                        var storageName = parts[0];
                        if (result.TryGetValue(storageName, out var list) && !list.Any(x => x.Id == vmId))
                            list.Add(vmDescriptor);
                    }
                }
            }

            return result;
        }



        public async Task<string> GetRawProxmoxVmConfigAsync(
            string host,
            string username,
            string password,
            int vmId)
        {
            return await Task.Run(() =>
            {
                var configPath = $"/etc/pve/qemu-server/{vmId}.conf";
                using var client = new SshClient(host, username, password);
                client.Connect();
                using var cmd = client.CreateCommand($"cat {configPath}");
                var result = cmd.Execute();
                client.Disconnect();
                return result;
            });
        }

        public async Task<string> GetVmConfigAsync(
            ProxmoxCluster cluster,
            string host,                  // node name or host address
            int vmId,
            CancellationToken ct = default)
        {
            // Prefer inventory-backed "queryable" hosts; fall back to any configured host
            var hosts = await _proxmoxHelpers.GetQueryableHostsAsync(cluster, ct);

            var h =
                hosts.FirstOrDefault(x =>
                    string.Equals(x.Hostname, host, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.HostAddress, host, StringComparison.OrdinalIgnoreCase))
                ?? cluster.Hosts?.FirstOrDefault(x =>
                    string.Equals(x.Hostname, host, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.HostAddress, host, StringComparison.OrdinalIgnoreCase));

            if (h is null || string.IsNullOrWhiteSpace(h.HostAddress))
                throw new InvalidOperationException("No matching Proxmox host (by name or address) with a usable HostAddress.");

            var hostAddress = h.HostAddress!;

            // Prepare SSH user (strip realm like @pam / @pve)
            var sshUser = cluster.Username ?? "root@pam";
            var atIndex = sshUser.IndexOf('@');
            if (atIndex > 0) sshUser = sshUser[..atIndex];

            // Get raw config via SSH
            var rawConfig = await GetRawProxmoxVmConfigAsync(
                hostAddress,
                sshUser,
                _encryptionService.Decrypt(cluster.PasswordHash),
                vmId
            );

            // Parse raw config and build JSON
            var (rootConfig, snapshots) = ParseProxmoxConfigWithSnapshots(rawConfig);

            var result = new Dictionary<string, object>
            {
                ["config"] = rootConfig
            };
            if (snapshots.Count > 0)
                result["snapshots"] = snapshots;

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }


        private (Dictionary<string, string> config, Dictionary<string, Dictionary<string, string>> snapshots)
       ParseProxmoxConfigWithSnapshots(string raw)
        {
            var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var snapshots = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            Dictionary<string, string> currentSection = config;
            string? currentSnapshot = null;

            foreach (var line in raw.Split('\n'))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                    continue;

                // Section header (e.g. [test1], [snapshot_xyz])
                var sectionMatch = Regex.Match(trimmed, @"^\[(.+)\]$");
                if (sectionMatch.Success)
                {
                    currentSnapshot = sectionMatch.Groups[1].Value;
                    currentSection = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    snapshots[currentSnapshot] = currentSection;
                    continue;
                }

                // Key: value line
                var idx = trimmed.IndexOf(':');
                if (idx < 0)
                    continue;

                var key = trimmed.Substring(0, idx).Trim();
                var val = trimmed.Substring(idx + 1).Trim();

                currentSection[key] = val;
            }

            return (config, snapshots);
        }

        public async Task PauseVmAsync(
            ProxmoxCluster cluster,
            string host,
            string hostaddress,
            int vmId,
            CancellationToken ct = default)
        {
            // Fetch current status
            var statusUrl = $"https://{hostaddress}:8006/api2/json/nodes/{host}/qemu/{vmId}/status/current";
            var statusResp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, statusUrl, null, ct);
            var statusJson = await statusResp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(statusJson);
            var current = doc.RootElement.GetProperty("data").GetProperty("status").GetString();

            // Only suspend if it’s running
            if (string.Equals(current, "running", StringComparison.OrdinalIgnoreCase))
            {
                var url = $"https://{hostaddress}:8006/api2/json/nodes/{host}/qemu/{vmId}/status/suspend";
                await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Post, url, null, ct);
            }
        }

        public async Task UnpauseVmAsync(
            ProxmoxCluster cluster,
            string host,
            string hostaddress,
            int vmId,
            CancellationToken ct = default)
        {
            // Fetch current status
            var statusUrl = $"https://{hostaddress}:8006/api2/json/nodes/{host}/qemu/{vmId}/status/current";
            var statusResp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, statusUrl, null, ct);
            var statusJson = await statusResp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(statusJson);
            var current = doc.RootElement.GetProperty("data").GetProperty("qmpstatus").GetString();

            // Only resume if it’s paused
            if (string.Equals(current, "paused", StringComparison.OrdinalIgnoreCase))
            {
                var url = $"https://{hostaddress}:8006/api2/json/nodes/{host}/qemu/{vmId}/status/resume";
                await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Post, url, null, ct);
            }
        }

        public async Task<string?> GetVmStatusAsync(
            ProxmoxCluster cluster,
            string node,
            string hostAddress,
            int vmid,
            CancellationToken ct = default)
        {
            var url = $"https://{hostAddress}:8006/api2/json/nodes/{node}/qemu/{vmid}/status/current";

            // Host-aware send + retry-once (401/403) with ticket refresh
            var resp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            // "running" | "stopped" | "paused" etc.
            return doc.RootElement.GetProperty("data").GetProperty("status").GetString();
        }

        public async Task<List<ProxmoxVM>> GetVmsOnNodeAsync(
      ProxmoxCluster cluster,
      string nodeName,
      string storageNameFilter,
      CancellationToken ct = default)
        {
            // 1) Find the hostAddress for that node
            var host = _proxmoxHelpers.GetHostByNodeName(cluster, nodeName);
            if (host == null || string.IsNullOrWhiteSpace(host.HostAddress))
                throw new InvalidOperationException($"No Proxmox host found for node '{nodeName}'.");

            var hostAddress = host.HostAddress;
            var result = new List<ProxmoxVM>();

            // 2) List VMs on that node
            var listUrl = $"https://{hostAddress}:8006/api2/json/nodes/{nodeName}/qemu";
            var listResp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, listUrl, null, ct);
            using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync(ct));
            var vmArray = listDoc.RootElement.GetProperty("data").EnumerateArray();

            // regex to find disk lines: scsi0, virtio1, ide2, etc.
            var diskRegex = new Regex(@"^(scsi|virtio|ide|sata|efidisk|tpmstate)\d+$", RegexOptions.IgnoreCase);

            foreach (var vmElem in vmArray)
            {
                var vmid = vmElem.GetProperty("vmid").GetInt32();
                var name = vmElem.TryGetProperty("name", out var nm)
                               ? nm.GetString()!
                               : $"VM {vmid}";

                // 3) fetch its full config
                var cfgUrl = $"https://{hostAddress}:8006/api2/json/nodes/{nodeName}/qemu/{vmid}/config";
                var cfgResp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, cfgUrl, null, ct);
                using var cfgDoc = JsonDocument.Parse(await cfgResp.Content.ReadAsStringAsync(ct));
                var data = cfgDoc.RootElement.GetProperty("data");

                // 4) scan disk entries for our storageNameFilter
                foreach (var prop in data.EnumerateObject())
                {
                    if (!diskRegex.IsMatch(prop.Name))
                        continue;

                    var raw = prop.Value.GetString();
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;

                    var cleanVal = raw.Trim();

                    // --- ISO / CDROM filters (same logic as GetFilteredStorageWithVMsAsync) ---
                    // skip unassigned CD-ROMs and ISO-only entries
                    if (cleanVal.StartsWith("none,", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (cleanVal.IndexOf(".iso", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cleanVal.IndexOf(":iso/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cleanVal.IndexOf("media=cdrom", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // e.g. "proxmox_ds1:iso/virtio-win-0.1.285.iso,media=cdrom,size=..."
                        continue;
                    }
                    // ------------------------------------------------------------------------

                    var parts = cleanVal.Split(':', 2);
                    if (parts.Length <= 1)
                        continue;

                    if (parts[0].Equals(storageNameFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(new ProxmoxVM
                        {
                            Id = vmid,
                            Name = name,
                            HostName = nodeName,
                            HostAddress = hostAddress
                        });
                        break; // no need to check more disks
                    }
                }
            }

            return result;
        }


        public async Task<bool> MountNfsStorageViaApiAsync(
            ProxmoxCluster cluster,
            string node,
            string storageName,
            string serverIp,
            string exportPath,
            bool snapshotChainActive = false,
            string content = "images,backup,iso,vztmpl",
            string options = "vers=3",
            CancellationToken ct = default)
        {
            var nodeHost =
                (await _proxmoxHelpers.GetQueryableHostsAsync(cluster, ct))
                    .FirstOrDefault(h =>
                        string.Equals(h.Hostname, node, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(h.HostAddress, node, StringComparison.OrdinalIgnoreCase))?.HostAddress
                ?? cluster.Hosts?.FirstOrDefault(h =>
                        string.Equals(h.Hostname, node, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(h.HostAddress, node, StringComparison.OrdinalIgnoreCase))?.HostAddress
                ?? string.Empty;


            if (string.IsNullOrWhiteSpace(nodeHost))
                return false;

            var createUrl = $"https://{nodeHost}:8006/api2/json/storage";

            var payload = new Dictionary<string, string>
            {
                ["type"] = "nfs",
                ["storage"] = storageName,
                ["server"] = serverIp,
                ["export"] = exportPath,
                ["content"] = content,
                ["options"] = options,
                ["snapshot-as-volume-chain"] = snapshotChainActive ? "1" : "0"
            };

            var body = new FormUrlEncodedContent(payload);

            // 1) Try to create (cluster-wide). If it already exists, we'll still verify mount status below.
            try
            {
                var resp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Post, createUrl, body, ct);
                // Don't early-return here; we want to verify mount state even if creation failed because it exists.
                // Some returns may be 409/500 "already defined" but the storage is fine to use.
                // We'll rely on verification below for the true/false result.
            }
            catch
            {
                // Swallow and continue to verification; storage may already exist.
            }

            // 2) Verify it's mounted on the requested node
            return await VerifyStorageMountedAsync(cluster, nodeHost, node, storageName, ct);
        }

        private async Task<bool> VerifyStorageMountedAsync(
            ProxmoxCluster cluster,
            string nodeHost,
            string node,
            string storageName,
            CancellationToken ct)
        {
            // Poll /status for up to ~30s (30 x 1s)
            var statusUrl =
                $"https://{nodeHost}:8006/api2/json/nodes/{Uri.EscapeDataString(node)}/storage/{Uri.EscapeDataString(storageName)}/status";

            for (int i = 0; i < 30; i++)
            {
                try
                {
                    var resp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, statusUrl, null, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync(ct);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("data", out var data))
                        {
                            // active: 1/0 or true/false; total: bytes (long); state: "available"/"unknown"/etc.
                            bool active = _proxmoxHelpers.TryGetTruthy(data, "active");
                            long total = _proxmoxHelpers.TryGetInt64(data, "total");
                            string state = TryGetString(data, "state") ?? string.Empty;
                            // Some backends report no 'state', so key off active && total > 0 primarily.
                            if (active && total > 0 && (string.IsNullOrEmpty(state) || state.Equals("available", StringComparison.OrdinalIgnoreCase)))
                            {
                                // Final quick sanity check: can we list content?
                                var contentUrl =
                                    $"https://{nodeHost}:8006/api2/json/nodes/{Uri.EscapeDataString(node)}/storage/{Uri.EscapeDataString(storageName)}/content";
                                var listResp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, contentUrl, null, ct);
                                if (listResp.IsSuccessStatusCode)
                                    return true; // Mounted and accessible
                            }
                        }
                    }
                }
                catch
                {
                    // ignore transient errors during polling
                }

                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }

            return false;
        }

        private static string? TryGetString(JsonElement parent, string name)
        {
            return parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        // Shutdown VM
        public async Task ShutdownAndRemoveVmAsync(ProxmoxCluster cluster, string nodeName, int vmId, CancellationToken ct = default)
        {
            var host =
                (await _proxmoxHelpers.GetQueryableHostsAsync(cluster, ct))
                    .FirstOrDefault(h => string.Equals(h.Hostname, nodeName, StringComparison.OrdinalIgnoreCase))
                ?? cluster.Hosts?.FirstOrDefault(h => string.Equals(h.Hostname, nodeName, StringComparison.OrdinalIgnoreCase));

            if (host is null)
                throw new InvalidOperationException($"No Proxmox host with node name '{nodeName}' found in cluster {cluster.Id}.");

            var baseApiUrl = $"https://{host.HostAddress}:8006/api2/json/nodes/{nodeName}/qemu/{vmId}";

            // 1) Get current VM status
            var statusUrl = $"{baseApiUrl}/status/current";
            var statusResp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, statusUrl, null, ct);
            var statusJson = await statusResp.Content.ReadAsStringAsync(ct);
            using var statusDoc = JsonDocument.Parse(statusJson);
            var status = statusDoc.RootElement.GetProperty("data").GetProperty("status").GetString();

            // 2) If running, shutdown and poll
            if (string.Equals(status, "running", StringComparison.OrdinalIgnoreCase))
            {
                var shutdownUrl = $"{baseApiUrl}/status/stop";
                await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Post, shutdownUrl, null, ct);

                var sw = Stopwatch.StartNew();
                var maxWait = TimeSpan.FromMinutes(5);
                while (sw.Elapsed < maxWait)
                {
                    var pollResp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, statusUrl, null, ct);
                    var pollJson = await pollResp.Content.ReadAsStringAsync(ct);
                    using var pollDoc = JsonDocument.Parse(pollJson);
                    var s = pollDoc.RootElement.GetProperty("data").GetProperty("status").GetString();
                    if (string.Equals(s, "stopped", StringComparison.OrdinalIgnoreCase))
                        break;
                    await Task.Delay(5000);
                }
                if (sw.Elapsed >= maxWait)
                    throw new InvalidOperationException("Timeout waiting for VM shutdown.");
            }

            // 3) Delete VM
            var deleteUrl = $"{baseApiUrl}?purge=1";
            await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Delete, deleteUrl, null, ct);
        }

        public async Task<bool> UnmountNfsStorageViaApiAsync(
    ProxmoxCluster cluster,
    string nodenName,
    string storageName,
    CancellationToken ct = default)
        {
            // 1) Delete the storage via the Proxmox API
            var primaryHost = cluster.Hosts.First();
            var deleteUrl = $"https://{primaryHost.HostAddress}:8006/api2/json/storage/{storageName}";
            var apiResp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Delete, deleteUrl, null, ct);
            if (!apiResp.IsSuccessStatusCode)
                return false;

            // 2) Compute the mount-point
            var mountPoint = $"/mnt/pve/{storageName}";

            // 3) SSH into each node and unmount + remove directory
            var sshUser = cluster.Username.Split('@')[0];
            var sshPass = _encryptionService.Decrypt(cluster.PasswordHash);

            foreach (var node in cluster.Hosts)
            {
                try
                {
                    using var ssh = new SshClient(
                        node.HostAddress, sshUser, sshPass);
                    ssh.Connect();

                    // Unmount (ignore errors if already unmounted)
                    var umount = ssh.CreateCommand($"umount {mountPoint}");
                    umount.Execute();

                    // Remove the (now-empty) mount-point directory
                    var remove = ssh.CreateCommand($"rm -rf {mountPoint}");
                    remove.Execute();

                    ssh.Disconnect();
                }
                catch (Exception ex)
                {
                    // Log and continue; cleanup failure isn't fatal
                    _logger.LogWarning(ex, "Failed to clean up {MountPoint} on {Node}", mountPoint, node.HostAddress);
                }
            }

            return true;
        }


        public async Task<Dictionary<string, List<ProxmoxVM>>> GetVmsByStorageListAsyncToCache(
            ProxmoxCluster cluster,
            List<string> storageNames,
            CancellationToken ct = default)
        {
            var result = new Dictionary<string, List<ProxmoxVM>>();

            foreach (var storage in storageNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                result[storage] = new List<ProxmoxVM>();

                var hosts = _proxmoxHelpers.GetQueryableHostsAsync(cluster, CancellationToken.None)
                           .GetAwaiter().GetResult()
                           .ToList();
                if (hosts.Count == 0 && cluster.Hosts != null)
                    hosts = cluster.Hosts.ToList();

                foreach (var host in hosts)
                {
                    var vms = await GetVmsOnNodeAsync(cluster, host.Hostname, storage, ct);
                    result[storage].AddRange(vms);
                }
            }

            return result;
        }

        public async Task<List<ProxmoxStorageDto>> GetNfsStorageAsync(ProxmoxCluster cluster, CancellationToken ct = default)
        {
            var result = new List<ProxmoxStorageDto>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // avoid duplicates by name

            var hosts = _proxmoxHelpers.GetQueryableHostsAsync(cluster, CancellationToken.None)
                           .GetAwaiter().GetResult()
                           .ToList();
            if (hosts.Count == 0 && cluster.Hosts != null)
                hosts = cluster.Hosts.ToList();

            foreach (var host in hosts)
            {
                var url = $"https://{host.HostAddress}:8006/api2/json/nodes/{host.Hostname}/storage";

                try
                {
                    var resp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);

                    foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
                    {
                        if (item.GetProperty("type").GetString() != "nfs")
                            continue;

                        var storageName = item.GetProperty("storage").GetString() ?? "";
                        if (string.IsNullOrWhiteSpace(storageName) || seen.Contains(storageName))
                            continue;

                        seen.Add(storageName);

                        var path = item.TryGetProperty("path", out var pathProp) ? pathProp.GetString() ?? "" : "";

                        result.Add(new ProxmoxStorageDto
                        {
                            Id = storageName,
                            Storage = storageName,
                            Type = "nfs",
                            Path = path,
                            Node = host?.Hostname ?? ""
                        });
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ProxmoxService] Failed to load storage from {host.Hostname}: {ex.Message}");
                    continue;
                }
            }

            return result;
        }

        public async Task<(bool Quorate,int OnlineNodeCount,int TotalNodeCount,Dictionary<string, bool> HostStates,string Message)> GetClusterStatusAsync(
        ProxmoxCluster cluster,
        CancellationToken ct = default)
        {
            var errors = new List<string>();

            // 1) Try each host until one responds
            foreach (var host in cluster.Hosts)
            {
                try
                {
                    var url = $"https://{host.HostAddress}:8006/api2/json/cluster/status";
                    var resp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);

                    if (!resp.IsSuccessStatusCode)
                    {
                        errors.Add($"{host.HostAddress} → HTTP {(int)resp.StatusCode}");
                        continue;
                    }

                    var body = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(body);

                    if (!doc.RootElement.TryGetProperty("data", out var items)
                        || items.ValueKind != JsonValueKind.Array)
                    {
                        errors.Add($"{host.HostAddress} → invalid JSON");
                        continue;
                    }

                    bool quorate = false;
                    int total = 0, onlineCount = 0;
                    var hostStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

                    foreach (var item in items.EnumerateArray())
                    {
                        if (!item.TryGetProperty("type", out var t))
                            continue;
                        var type = t.GetString();

                        if (type == "cluster")
                        {
                            // quorate may be boolean or numeric
                            if (item.TryGetProperty("quorate", out var q))
                            {
                                quorate = q.ValueKind switch
                                {
                                    JsonValueKind.True => true,
                                    JsonValueKind.False => false,
                                    JsonValueKind.Number => q.GetInt32() == 1,
                                    _ => false
                                };
                            }

                            // total node count
                            if (item.TryGetProperty("nodes", out var n) && n.ValueKind == JsonValueKind.Number)
                                total = n.GetInt32();
                        }
                        else if (type == "node")
                        {
                            // node name
                            var name = item.GetProperty("name").GetString()!;
                            // online may be boolean or numeric
                            bool isOnline = false;
                            if (item.TryGetProperty("online", out var on))
                            {
                                isOnline = on.ValueKind switch
                                {
                                    JsonValueKind.True => true,
                                    JsonValueKind.False => false,
                                    JsonValueKind.Number => on.GetInt32() == 1,
                                    _ => false
                                };
                            }
                            hostStates[name] = isOnline;
                            if (isOnline) onlineCount++;
                        }
                    }

                    // 3) Build summary
                    string message = quorate
                        ? onlineCount == total
                            ? "Cluster healthy (all nodes online)"
                            : $"Quorum ok, but {total - onlineCount} node(s) offline"
                        : "Cluster lost quorum!";

                    return (quorate, onlineCount, total, hostStates, message);
                }
                catch (Exception ex)
                {
                    errors.Add($"{host.HostAddress} → {ex.Message}");
                }
            }

            // 4) No host responded correctly
            var errMsg = errors.Count > 0
                ? string.Join("; ", errors)
                : "No hosts configured";
            return (false, 0, 0, new Dictionary<string, bool>(), $"Cluster unreachable: {errMsg}");
        }

        // Helper: find a cluster + host that matches the given node (by Hostname or HostAddress)
        private async Task<(ProxmoxCluster Cluster, ProxmoxHost Host)?> ResolveClusterAndHostAsync(
     string node,
     CancellationToken ct = default)
        {
            var wanted = node?.Trim() ?? string.Empty;

            // Load clusters + hosts without tracking to reduce overhead
            await using var db = await _dbf.CreateAsync(ct);

            var clusters = await db.ProxmoxClusters
                .Include(c => c.Hosts)
                .AsNoTracking()
                .ToListAsync(ct);

            if (clusters.Count == 0)
                return null;

            // 1) If caller provided a node hint, try to match an ONLINE (queryable) host first
            if (!string.IsNullOrEmpty(wanted))
            {
                foreach (var c in clusters)
                {
                    var onlineHosts = await _proxmoxHelpers.GetQueryableHostsAsync(c, ct);
                    var oh = onlineHosts.FirstOrDefault(h =>
                                string.Equals(h.Hostname, wanted, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(h.HostAddress, wanted, StringComparison.OrdinalIgnoreCase));
                    if (oh != null)
                        return (c, oh);
                }

                // 2) If not online, try any configured host in any cluster
                foreach (var c in clusters)
                {
                    var h = c.Hosts?.FirstOrDefault(x =>
                                string.Equals(x.Hostname, wanted, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(x.HostAddress, wanted, StringComparison.OrdinalIgnoreCase));
                    if (h != null)
                        return (c, h);
                }
            }

            // 3) No hint or no match: prefer the first cluster with any ONLINE host
            foreach (var c in clusters)
            {
                var onlineHosts = await _proxmoxHelpers.GetQueryableHostsAsync(c, ct);
                var oh = onlineHosts.FirstOrDefault();
                if (oh != null)
                    return (c, oh);
            }

            // 4) Final fallback: first configured host we can find
            var fbCluster = clusters.FirstOrDefault(c => c.Hosts != null && c.Hosts.Count > 0);
            if (fbCluster == null)
                return null;

            var fbHost = fbCluster.Hosts.FirstOrDefault();
            return fbHost == null ? null : (fbCluster, fbHost);
        }



        public async Task<bool> RenameVmDirectoryAndFilesAsync(
            string nodeName,
            string storageName,
            string oldVmid,
            string newVmid,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(nodeName)) throw new ArgumentException(nameof(nodeName));
            if (string.IsNullOrWhiteSpace(storageName)) throw new ArgumentException(nameof(storageName));
            if (string.IsNullOrWhiteSpace(oldVmid) || string.IsNullOrWhiteSpace(newVmid)) throw new ArgumentException("vmids required");
            if (!Regex.IsMatch(oldVmid, @"^\d+$") || !Regex.IsMatch(newVmid, @"^\d+$"))
                throw new ArgumentException("vmids must be numeric");
            if (oldVmid == newVmid) return true;

            var resolved = await ResolveClusterAndHostAsync(nodeName, ct)
                          ?? throw new InvalidOperationException($"Node '{nodeName}' not found in any cluster.");

            ProxmoxCluster cluster = resolved.Cluster;
            ProxmoxHost host = resolved.Host;
            var sshUser = cluster.Username.Split('@')[0];
            var sshPass = _encryptionService.Decrypt(cluster.PasswordHash);

            // Pre-quote for bash
            var qStorage = _proxmoxHelpers.EscapeBash(storageName);
            var qOld = _proxmoxHelpers.EscapeBash(oldVmid);
            var qNew = _proxmoxHelpers.EscapeBash(newVmid);

            var sb = new StringBuilder();
            sb.AppendLine("set -euo pipefail");
            sb.AppendLine("export LC_ALL=C");
            sb.AppendLine($"storage={qStorage}");
            sb.AppendLine($"oldid={qOld}");
            sb.AppendLine($"newid={qNew}");
            sb.AppendLine();

            // Resolve base path (prefer /mnt/pve/<storage>, else pvesm config path)
            sb.AppendLine(@"base="""";");
            sb.AppendLine(@"if [ -d ""/mnt/pve/$storage/images/$oldid"" ]; then");
            sb.AppendLine(@"  base=""/mnt/pve/$storage""");
            sb.AppendLine(@"else");
            sb.AppendLine(@"  conf_path=""$(pvesm config ""$storage"" 2>/dev/null | awk -F': ' '/^path: /{print $2}')"" || true");
            sb.AppendLine(@"  if [ -n ""$conf_path"" ] && [ -d ""$conf_path/images/$oldid"" ]; then");
            sb.AppendLine(@"    base=""$conf_path""");
            sb.AppendLine(@"  fi");
            sb.AppendLine(@"fi");
            sb.AppendLine(@"if [ -z ""$base"" ]; then");
            sb.AppendLine(@"  echo ""ERR: could not resolve storage base path for '$storage'"" >&2");
            sb.AppendLine(@"  exit 2");
            sb.AppendLine(@"fi");
            sb.AppendLine();

            sb.AppendLine(@"src=""$base/images/$oldid""");
            sb.AppendLine(@"dst=""$base/images/$newid""");
            sb.AppendLine();

            sb.AppendLine(@"if [ ! -d ""$src"" ]; then");
            sb.AppendLine(@"  echo ""ERR: source dir not found: $src"" >&2");
            sb.AppendLine(@"  exit 3");
            sb.AppendLine(@"fi");
            sb.AppendLine(@"if [ -e ""$dst"" ]; then");
            sb.AppendLine(@"  echo ""ERR: destination already exists: $dst"" >&2");
            sb.AppendLine(@"  exit 4");
            sb.AppendLine(@"fi");
            sb.AppendLine();

            // Move directory and enter it
            sb.AppendLine(@"mv ""$src"" ""$dst""");
            sb.AppendLine(@"cd ""$dst""");
            sb.AppendLine();

            // Rename files that contain oldid in the NAME (no symlinks, no content edits)
            sb.AppendLine(@"find . -maxdepth 1 -type f -print0 | while IFS= read -r -d '' p; do");
            sb.AppendLine(@"  b=""$(basename ""$p"")""");
            sb.AppendLine(@"  case ""$b"" in *""$oldid""*)");
            sb.AppendLine(@"    nb=""${b//$oldid/$newid}""");
            sb.AppendLine(@"    if [ ""$b"" != ""$nb"" ]; then");
            sb.AppendLine(@"      mv -T -- ""$p"" ""$(dirname ""$p"")/$nb""");
            sb.AppendLine(@"      echo ""REN: $p -> $(dirname ""$p"")/$nb""");
            sb.AppendLine(@"    fi");
            sb.AppendLine(@"  ;; esac");
            sb.AppendLine(@"done");
            sb.AppendLine();

            sb.AppendLine(@"echo ""OK: renamed directory and file names in $dst (base=$base)""");

            // Normalize CRLF -> LF
            var script = Regex.Replace(sb.ToString(), @"\r\n?", "\n");

            try
            {
                using var ssh = new SshClient(host.HostAddress, sshUser, sshPass);
                ssh.Connect();

                // Strip any stray \r on the remote side too, and force bash.
                var eof = "EOF_" + Guid.NewGuid().ToString("N");
                var cmdText = $"cat <<'{eof}' | tr -d '\\r' | bash\n{script}\n{eof}\n";

                using var cmd = ssh.CreateCommand(cmdText);
                cmd.CommandTimeout = TimeSpan.FromMinutes(5);
                var output = cmd.Execute();
                var exit = cmd.ExitStatus;
                var err = cmd.Error;

                ssh.Disconnect();

                if (exit != 0)
                {
                    _logger.LogError(
                        "RenameVmDirectoryAndFilesAsync failed on {Host} (exit {Exit}).\nSTDERR:\n{Err}\nSTDOUT:\n{Out}",
                        host.HostAddress, exit, (err ?? "").Trim(), (output ?? "").Trim());
                    return false;
                }

                _logger.LogInformation(
                    "RenameVmDirectoryAndFilesAsync OK on {Host}.\nSTDOUT:\n{Out}",
                    host.HostAddress, (output ?? "").Trim());
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SSH failure while renaming VM dir/files on node {Node}", nodeName);
                return false;
            }
        }

        public sealed class ProxmoxStorageContentItem
        {
            public string volid { get; set; } = default!;    // "proxmox_ds1:vm-100-disk-0"
            public string content { get; set; } = default!;  // "images", "rootdir", ...
            public string? vmid { get; set; }               // "100"
            public string? format { get; set; }             // "raw", "qcow2"
            public long? size { get; set; }                 // bytes
                                                            // other fields exist, but we don't need them for now
        }

        public async Task<IReadOnlyList<ProxmoxStorageContentItem>> GetStorageDisksAsync(
            ProxmoxCluster cluster,
            string nodeName,
            string storageId,
            CancellationToken ct = default)
        {
            // 1) Resolve node → hostAddress (same pattern as GetVmsOnNodeAsync)
            var host = _proxmoxHelpers.GetHostByNodeName(cluster, nodeName);
            if (host == null || string.IsNullOrWhiteSpace(host.HostAddress))
                throw new InvalidOperationException($"No Proxmox host found for node '{nodeName}'.");

            var hostAddress = host.HostAddress;

            // 2) Call Proxmox API
            var url =
                $"https://{hostAddress}:8006/api2/json/nodes/{Uri.EscapeDataString(nodeName)}/storage/{Uri.EscapeDataString(storageId)}/content";

            var resp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
            if (!resp.IsSuccessStatusCode)
                return Array.Empty<ProxmoxStorageContentItem>();

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
                dataEl.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ProxmoxStorageContentItem>();
            }

            var list = new List<ProxmoxStorageContentItem>();

            foreach (var item in dataEl.EnumerateArray())
            {
                var content = item.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString() ?? string.Empty
                    : string.Empty;

                // we only care about disk-like things
                if (!string.Equals(content, "images", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(content, "rootdir", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var volid = item.TryGetProperty("volid", out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? string.Empty
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(volid))
                    continue;

                string? vmid = null;
                if (item.TryGetProperty("vmid", out var vmidEl) &&
                    vmidEl.ValueKind == JsonValueKind.String)
                {
                    vmid = vmidEl.GetString();
                }
                else if (item.TryGetProperty("vmid", out vmidEl) &&
                         vmidEl.ValueKind == JsonValueKind.Number)
                {
                    vmid = vmidEl.GetInt32().ToString();
                }

                string? format = null;
                if (item.TryGetProperty("format", out var fmtEl) &&
                    fmtEl.ValueKind == JsonValueKind.String)
                {
                    format = fmtEl.GetString();
                }

                long? size = null;
                if (item.TryGetProperty("size", out var sizeEl) &&
                    sizeEl.ValueKind == JsonValueKind.Number)
                {
                    if (sizeEl.TryGetInt64(out var sVal))
                        size = sVal;
                }

                list.Add(new ProxmoxStorageContentItem
                {
                    volid = volid,
                    content = content,
                    vmid = vmid,
                    format = format,
                    size = size
                });
            }

            return list;
        }
        public async Task<bool> AttachDiskToVmAsync(
                ProxmoxCluster cluster,
                string nodeName,
                int vmId,
                string diskKey,
                string diskValue,
                CancellationToken ct = default)
        {
            if (cluster is null) throw new ArgumentNullException(nameof(cluster));
            if (string.IsNullOrWhiteSpace(nodeName)) throw new ArgumentException("Node name required.", nameof(nodeName));
            if (string.IsNullOrWhiteSpace(diskKey)) throw new ArgumentException("Disk key required.", nameof(diskKey));
            if (string.IsNullOrWhiteSpace(diskValue)) throw new ArgumentException("Disk value required.", nameof(diskValue));

            // Resolve node → host
            var host = _proxmoxHelpers.GetHostByNodeName(cluster, nodeName);
            if (host == null || string.IsNullOrWhiteSpace(host.HostAddress))
                throw new InvalidOperationException($"No Proxmox host found for node '{nodeName}'.");

            var hostAddress = host.HostAddress;

            // Proxmox API: set VM config key
            // POST /nodes/{node}/qemu/{vmid}/config
            var url =
                $"https://{hostAddress}:8006/api2/json/nodes/{Uri.EscapeDataString(nodeName)}/qemu/{vmId}/config";

            var payload = new Dictionary<string, string>
            {
                [diskKey] = diskValue
            };

            var body = new FormUrlEncodedContent(payload);

            var resp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Post, url, body, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var text = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogError(
                    "AttachDiskToVmAsync failed for VM {VmId} on node {Node}. Status {Status} Body: {Body}",
                    vmId, nodeName, resp.StatusCode, text);
                return false;
            }

            _logger.LogInformation(
                "AttachDiskToVmAsync: attached {DiskKey}={DiskValue} to VM {VmId} on node {Node}",
                diskKey, diskValue, vmId, nodeName);

            return true;
        }

        public async Task<(bool Ok, string? NewRelativePath)> EnsureAttachDiskSymlinkAsync(
    ProxmoxCluster cluster,
    string nodeName,
    string storageName,
    string sourceRelativePath,   // e.g. "images/101/vm-101-disk-1.qcow2"
    int targetVmId,              // e.g. 114
    CancellationToken ct = default)
        {
            if (cluster is null) throw new ArgumentNullException(nameof(cluster));
            if (string.IsNullOrWhiteSpace(nodeName)) throw new ArgumentException(nameof(nodeName));
            if (string.IsNullOrWhiteSpace(storageName)) throw new ArgumentException(nameof(storageName));
            if (string.IsNullOrWhiteSpace(sourceRelativePath)) throw new ArgumentException(nameof(sourceRelativePath));

            // Split "images/101/vm-101-disk-1.qcow2"
            var lastSlash = sourceRelativePath.LastIndexOf('/');
            var srcDir = lastSlash >= 0 ? sourceRelativePath[..lastSlash] : string.Empty;
            var srcFile = lastSlash >= 0 ? sourceRelativePath[(lastSlash + 1)..] : sourceRelativePath;

            // Expect dir "images/<oldId>"
            var dirMatch = Regex.Match(srcDir, @"^images/(?<oldId>\d+)$", RegexOptions.IgnoreCase);
            if (!dirMatch.Success)
            {
                _logger.LogWarning("EnsureAttachDiskSymlinkAsync: source path '{Path}' does not match 'images/<id>/...'", sourceRelativePath);
                return (false, null);
            }

            var oldId = dirMatch.Groups["oldId"].Value;

            // Expect file "vm-<oldId>-<rest>"
            var fileMatch = Regex.Match(srcFile, @"^vm-(?<oldId>\d+)-(?<rest>.+)$", RegexOptions.IgnoreCase);
            if (!fileMatch.Success)
            {
                _logger.LogWarning("EnsureAttachDiskSymlinkAsync: source file '{File}' does not match 'vm-<id>-...'", srcFile);
                return (false, null);
            }

            var oldIdInFile = fileMatch.Groups["oldId"].Value;
            var rest = fileMatch.Groups["rest"].Value;

            if (!string.Equals(oldId, oldIdInFile, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("EnsureAttachDiskSymlinkAsync: dir id '{DirId}' != file id '{FileId}' in '{Path}'", oldId, oldIdInFile, sourceRelativePath);
                return (false, null);
            }

            var newId = targetVmId.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Desired new relative path: images/<newId>/vm-<newId>-<rest>
            var newDir = $"images/{newId}";
            var newFile = $"vm-{newId}-{rest}";
            var newRelativePath = $"{newDir}/{newFile}";

            // Resolve node → host
            var host = _proxmoxHelpers.GetHostByNodeName(cluster, nodeName);
            if (host == null || string.IsNullOrWhiteSpace(host.HostAddress))
                throw new InvalidOperationException($"No Proxmox host found for node '{nodeName}'.");

            var hostAddress = host.HostAddress;
            var sshUser = cluster.Username.Split('@')[0];
            var sshPass = _encryptionService.Decrypt(cluster.PasswordHash);

            // Bash-escape
            var qStorage = _proxmoxHelpers.EscapeBash(storageName);
            var qSrcRel = _proxmoxHelpers.EscapeBash(sourceRelativePath);
            var qDstRel = _proxmoxHelpers.EscapeBash(newRelativePath);

            var sb = new StringBuilder();
            sb.AppendLine("set -euo pipefail");
            sb.AppendLine("export LC_ALL=C");
            sb.AppendLine($"storage={qStorage}");
            sb.AppendLine($"srcRel={qSrcRel}");
            sb.AppendLine($"dstRel={qDstRel}");
            sb.AppendLine();

            // Resolve base path (same pattern as RenameVmDirectoryAndFilesAsync)
            sb.AppendLine(@"base="""";");
            sb.AppendLine(@"if [ -d ""/mnt/pve/$storage"" ]; then");
            sb.AppendLine(@"  base=""/mnt/pve/$storage""");
            sb.AppendLine(@"else");
            sb.AppendLine(@"  conf_path=""$(pvesm config ""$storage"" 2>/dev/null | awk -F': ' '/^path: /{print $2}')"" || true");
            sb.AppendLine(@"  if [ -n ""$conf_path"" ]; then");
            sb.AppendLine(@"    base=""$conf_path""");
            sb.AppendLine(@"  fi");
            sb.AppendLine(@"fi");
            sb.AppendLine(@"if [ -z ""$base"" ]; then");
            sb.AppendLine(@"  echo ""ERR: could not resolve storage base path for '$storage'"" >&2");
            sb.AppendLine(@"  exit 2");
            sb.AppendLine(@"fi");
            sb.AppendLine();

            sb.AppendLine(@"src=""$base/$srcRel""");
            sb.AppendLine(@"dst=""$base/$dstRel""");
            sb.AppendLine(@"dstDir=""$(dirname ""$dst"")""");
            sb.AppendLine();

            sb.AppendLine(@"if [ ! -f ""$src"" ]; then");
            sb.AppendLine(@"  echo ""ERR: source file not found: $src"" >&2");
            sb.AppendLine(@"  exit 3");
            sb.AppendLine(@"fi");
            sb.AppendLine();

            // If dest dir exists, rename to .old (exact spec you gave)
            sb.AppendLine(@"if [ -d ""$dstDir"" ]; then");
            sb.AppendLine(@"  mv ""$dstDir"" ""$dstDir.old""");
            sb.AppendLine(@"fi");
            sb.AppendLine(@"mkdir -p ""$dstDir""");
            sb.AppendLine();

            sb.AppendLine(@"if [ -e ""$dst"" ] && [ ! -L ""$dst"" ]; then");
            sb.AppendLine(@"  echo ""ERR: destination exists and is not a symlink: $dst"" >&2");
            sb.AppendLine(@"  exit 4");
            sb.AppendLine(@"fi");
            sb.AppendLine(@"rm -f ""$dst""");
            sb.AppendLine(@"ln -s ""$src"" ""$dst""");
            sb.AppendLine(@"echo ""OK: symlink $dst -> $src""");

            var script = Regex.Replace(sb.ToString(), @"\r\n?", "\n");

            try
            {
                using var ssh = new SshClient(hostAddress, sshUser, sshPass);
                ssh.Connect();

                var eof = "EOF_" + Guid.NewGuid().ToString("N");
                var cmdText = $"cat <<'{eof}' | tr -d '\\r' | bash\n{script}\n{eof}\n";

                using var cmd = ssh.CreateCommand(cmdText);
                cmd.CommandTimeout = TimeSpan.FromMinutes(5);

                var output = cmd.Execute();
                var exit = cmd.ExitStatus;
                var err = cmd.Error;

                ssh.Disconnect();

                if (exit != 0)
                {
                    _logger.LogError(
                        "EnsureAttachDiskSymlinkAsync failed on {Host} (exit {Exit}).\nSTDERR:\n{Err}\nSTDOUT:\n{Out}",
                        hostAddress, exit, (err ?? "").Trim(), (output ?? "").Trim());
                    return (false, null);
                }

                _logger.LogInformation(
                    "EnsureAttachDiskSymlinkAsync OK on {Host}.\nSTDOUT:\n{Out}",
                    hostAddress, (output ?? "").Trim());

                return (true, newRelativePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SSH failure in EnsureAttachDiskSymlinkAsync on node {Node}", nodeName);
                return (false, null);
            }
        }
        public async Task<(bool Ok, string? Error)> PrepareAttachSymlinkOnCloneAsync(
            string nodeName,
            string storageName,
            string sourceVmId,
            string destVmId,
            string sourceFileName,
            string destFileName,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(nodeName)) throw new ArgumentException(nameof(nodeName));
            if (string.IsNullOrWhiteSpace(storageName)) throw new ArgumentException(nameof(storageName));
            if (string.IsNullOrWhiteSpace(sourceVmId)) throw new ArgumentException(nameof(sourceVmId));
            if (string.IsNullOrWhiteSpace(destVmId)) throw new ArgumentException(nameof(destVmId));
            if (string.IsNullOrWhiteSpace(sourceFileName)) throw new ArgumentException(nameof(sourceFileName));
            if (string.IsNullOrWhiteSpace(destFileName)) throw new ArgumentException(nameof(destFileName));

            try
            {
                var resolved = await ResolveClusterAndHostAsync(nodeName, ct);
                if (resolved == null)
                {
                    var msg = $"Node '{nodeName}' not found in any cluster.";
                    _logger.LogError(msg);
                    return (false, msg);
                }

                var cluster = resolved.Value.Cluster;
                var host = resolved.Value.Host;

                var sshUser = cluster.Username.Split('@')[0];
                var sshPass = _encryptionService.Decrypt(cluster.PasswordHash);

                var qStorage = _proxmoxHelpers.EscapeBash(storageName);
                var qSrcId = _proxmoxHelpers.EscapeBash(sourceVmId);
                var qDstId = _proxmoxHelpers.EscapeBash(destVmId);
                var qSrcFile = _proxmoxHelpers.EscapeBash(sourceFileName);
                var qDstFile = _proxmoxHelpers.EscapeBash(destFileName);

                var sb = new StringBuilder();
                sb.AppendLine("set -euo pipefail");
                sb.AppendLine("export LC_ALL=C");
                sb.AppendLine($"storage={qStorage}");
                sb.AppendLine($"srcid={qSrcId}");
                sb.AppendLine($"dstid={qDstId}");
                sb.AppendLine($"srcfile={qSrcFile}");
                sb.AppendLine($"dstfile={qDstFile}");
                sb.AppendLine();

                // Resolve base path (prefer /mnt/pve/<storage>, else pvesm config path)
                sb.AppendLine(@"base="""";");
                sb.AppendLine(@"if [ -d ""/mnt/pve/$storage/images"" ]; then");
                sb.AppendLine(@"  base=""/mnt/pve/$storage""");
                sb.AppendLine(@"else");
                sb.AppendLine(@"  conf_path=""$(pvesm config ""$storage"" 2>/dev/null | awk -F': ' '/^path: /{print $2}')"" || true");
                sb.AppendLine(@"  if [ -n ""$conf_path"" ] && [ -d ""$conf_path/images"" ]; then");
                sb.AppendLine(@"    base=""$conf_path""");
                sb.AppendLine(@"  fi");
                sb.AppendLine(@"fi");
                sb.AppendLine(@"if [ -z ""$base"" ]; then");
                sb.AppendLine(@"  echo ""ERR: could not resolve storage base path for '$storage'"" >&2");
                sb.AppendLine(@"  exit 2");
                sb.AppendLine(@"fi");
                sb.AppendLine();

                sb.AppendLine(@"srcdir=""$base/images/$srcid""");
                sb.AppendLine(@"dstdir=""$base/images/$dstid""");
                sb.AppendLine();

                sb.AppendLine(@"if [ ! -d ""$srcdir"" ]; then");
                sb.AppendLine(@"  echo ""ERR: source VM directory not found: $srcdir"" >&2");
                sb.AppendLine(@"  exit 3");
                sb.AppendLine(@"fi");
                sb.AppendLine();

                // If dest dir exists and is a *different* VM, rename it away.
                // For same-VM attach (srcid == dstid) we keep the directory and just add a new file.
                sb.AppendLine(@"if [ ""$srcid"" != ""$dstid"" ]; then");
                sb.AppendLine(@"  if [ -d ""$dstdir"" ]; then");
                sb.AppendLine(@"    ts=""$(date +%s)""");
                sb.AppendLine(@"    olddir=""${dstdir}.old.${ts}""");
                sb.AppendLine(@"    mv ""$dstdir"" ""$olddir""");
                sb.AppendLine(@"  fi");
                sb.AppendLine(@"fi");
                sb.AppendLine();

                sb.AppendLine(@"mkdir -p ""$dstdir""");
                sb.AppendLine();

                sb.AppendLine(@"src=""$srcdir/$srcfile""");
                sb.AppendLine(@"dst=""$dstdir/$dstfile""");
                sb.AppendLine();

                sb.AppendLine(@"if [ ! -f ""$src"" ]; then");
                sb.AppendLine(@"  echo ""ERR: source file not found: $src"" >&2");
                sb.AppendLine(@"  exit 4");
                sb.AppendLine(@"fi");
                sb.AppendLine();

                sb.AppendLine(@"if [ -e ""$dst"" ]; then");
                sb.AppendLine(@"  echo ""ERR: destination already exists: $dst"" >&2");
                sb.AppendLine(@"  exit 5");
                sb.AppendLine(@"fi");
                sb.AppendLine();

                sb.AppendLine(@"ln -s ""$src"" ""$dst""");
                sb.AppendLine(@"echo ""OK: linked $dst -> $src""");

                var script = Regex.Replace(sb.ToString(), @"\r\n?", "\n");

                using var ssh = new SshClient(host.HostAddress, sshUser, sshPass);
                ssh.Connect();

                var eof = "EOF_" + Guid.NewGuid().ToString("N");
                var cmdText = $"cat <<'{eof}' | tr -d '\\r' | bash\n{script}\n{eof}\n";

                using var cmd = ssh.CreateCommand(cmdText);
                cmd.CommandTimeout = TimeSpan.FromMinutes(5);
                var output = cmd.Execute();
                var exit = cmd.ExitStatus;
                var err = cmd.Error;

                ssh.Disconnect();

                if (exit != 0)
                {
                    var msg = $"SSH script exit {exit}. STDERR: {(err ?? "").Trim()}";
                    _logger.LogError(
                        "PrepareAttachSymlinkOnCloneAsync failed on {Host} (exit {Exit}).\nSTDERR:\n{Err}\nSTDOUT:\n{Out}",
                        host.HostAddress, exit, (err ?? "").Trim(), (output ?? "").Trim());
                    return (false, msg);
                }

                _logger.LogInformation(
                    "PrepareAttachSymlinkOnCloneAsync OK on {Host}.\nSTDOUT:\n{Out}",
                    host.HostAddress, (output ?? "").Trim());

                return (true, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SSH failure while preparing attach symlink on node {Node}", nodeName);
                return (false, ex.Message);
            }
        }


    }
}