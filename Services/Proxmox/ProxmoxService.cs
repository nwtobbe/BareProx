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
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Linq;                    // for OrderBy/Select
using System.Security.Cryptography;   // for SHA1
using Renci.SshNet;
using BareProx.Services.Proxmox.Authentication;
using BareProx.Services.Proxmox.Helpers;
using BareProx.Services.Restore;
using BareProx.Services.Proxmox.Ops;
using BareProx.Services.Proxmox.Snapshots;

namespace BareProx.Services.Proxmox
{
    public class ProxmoxService
    {
        private readonly ApplicationDbContext _context;
        private readonly IEncryptionService _encryptionService;
        private readonly INetappVolumeService _netappVolumeService;
        private readonly ILogger<ProxmoxService> _logger;
        private readonly IProxmoxHelpersService _proxmoxHelpers;
        private readonly IProxmoxAuthenticator _proxmoxAuthenticator;
        private readonly IProxmoxInventoryCache _invCache;
        private readonly IProxmoxOpsService _proxmoxOps;
        private readonly IProxmoxSnapshotsService _proxmoxSnapshots;

        public ProxmoxService(
            ApplicationDbContext context,
            IEncryptionService encryptionService,
            INetappVolumeService netappVolumeService,
            ILogger<ProxmoxService> logger,
            IProxmoxHelpersService proxmoxHelpers,
            IProxmoxAuthenticator proxmoxAuthenticator,
            IProxmoxInventoryCache invCache,
            IProxmoxOpsService proxmoxOps,
            IProxmoxSnapshotsService proxmoxSnapshots)
        {
            _context = context;
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
            var cluster = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync(c => c.Id == clusterId, ct);
            if (cluster == null)
                return new Dictionary<string, List<ProxmoxVM>>();

            // 2) Discover which NFS storages Proxmox actually has mounted
            var proxmoxStorageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var host in _proxmoxHelpers.GetQueryableHosts(cluster))
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
            var selectedEnabledForController = await _context.SelectedNetappVolumes
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

            foreach (var host in _proxmoxHelpers.GetQueryableHosts(cluster))
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
                    var vmName = vmElem.TryGetProperty("name", out var nm) ? (nm.GetString() ?? $"VM {vmId}") : $"VM {vmId}";

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
                        if (!diskRegex.IsMatch(prop.Name)) continue;

                        var val = prop.Value.GetString() ?? string.Empty;
                        var parts = val.Split(':', 2);
                        if (parts.Length < 2) continue;

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
    string host,
    int vmId,
    CancellationToken ct = default)
        {
            // Find the correct host address
            var hostAddress = _proxmoxHelpers.GetQueryableHosts(cluster).First(h => h.Hostname == host).HostAddress;
            // Prepare SSH user (strip @pam/@pve if present)
            var sshUser = cluster.Username;
            int atIndex = sshUser.IndexOf('@');
            if (atIndex > 0)
                sshUser = sshUser.Substring(0, atIndex);

            // Get raw config text via SSH
            var rawConfig = await GetRawProxmoxVmConfigAsync(
                hostAddress,
                sshUser,
                _encryptionService.Decrypt(cluster.PasswordHash),
                vmId
            );

            // Parse raw config into a C# object with:
            // - root config
            // - snapshots (dictionary: name => dictionary)
            var (rootConfig, snapshots) = ParseProxmoxConfigWithSnapshots(rawConfig);

            // Build JSON result
            var result = new Dictionary<string, object>
            {
                ["config"] = rootConfig
            };

            if (snapshots.Count > 0)
            {
                result["snapshots"] = snapshots;
            }

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

                    var val = prop.Value.GetString();
                    if (string.IsNullOrWhiteSpace(val))
                        continue;

                    var cleanVal = val.Trim();
                    var parts = cleanVal.Split(':', 2);

                    if (parts.Length > 1 &&
                        parts[0].Equals(storageNameFilter, StringComparison.OrdinalIgnoreCase))
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
            var nodeHost = _proxmoxHelpers
                .GetQueryableHosts(cluster)
                .FirstOrDefault(h => h.Hostname == node)?.HostAddress ?? string.Empty;

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
            var host = _proxmoxHelpers.GetQueryableHosts(cluster).First(h => h.Hostname == nodeName);
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

                foreach (var host in _proxmoxHelpers.GetQueryableHosts(cluster))
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

            foreach (var host in _proxmoxHelpers.GetQueryableHosts(cluster))
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
            var clusters = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .ToListAsync(ct);

            if (clusters.Count == 0) return null;

            // Try to find a host whose Hostname or HostAddress matches the node string
            foreach (var c in clusters)
            {
                var h = c.Hosts.FirstOrDefault(x =>
                    x.Hostname.Equals(node, StringComparison.OrdinalIgnoreCase) ||
                    x.HostAddress.Equals(node, StringComparison.OrdinalIgnoreCase));

                if (h != null) return (c, h);
            }

            // Fallback: just use the first cluster/host
            var fallbackCluster = clusters.First();
            var fallbackHost = fallbackCluster.Hosts.FirstOrDefault();
            if (fallbackHost == null) return null;

            return (fallbackCluster, fallbackHost);
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
      
    }
}