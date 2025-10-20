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

namespace BareProx.Services
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


        public async Task<Dictionary<string, List<ProxmoxVM>>> GetEligibleBackupStorageWithVMsAsyncToCache(
    ProxmoxCluster cluster,
    int netappControllerId,
    List<string>? onlyIncludeStorageNames = null,
    CancellationToken ct = default)
        {
            var storageVmMap = onlyIncludeStorageNames != null
                ? await _invCache.GetVmsByStorageListAsync(cluster, onlyIncludeStorageNames, ct)
                : await GetFilteredStorageWithVMsAsync(cluster.Id, netappControllerId, ct);

            return storageVmMap
                .Where(kvp =>
                    !kvp.Key.Contains("backup", StringComparison.OrdinalIgnoreCase) &&
                    !kvp.Key.Contains("restore_", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

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
                foreach (var storage in doc.RootElement.GetProperty("data").EnumerateArray())
                {
                    if (storage.GetProperty("type").GetString() == "nfs")
                        proxmoxStorageNames.Add(storage.GetProperty("storage").GetString()!);
                }
            }

            // 3) Fetch all NetApp NFS volumes
            var netappVolumes = await _netappVolumeService.GetVolumesWithMountInfoAsync(netappControllerId, ct);

            // 4) Only keep the intersection: NetApp volumes that Proxmox knows about
            var validVolumes = netappVolumes
                .Where(v => proxmoxStorageNames.Contains(v.VolumeName))
                .Select(v => v.VolumeName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 5) Seed result dictionary
            var result = validVolumes.ToDictionary(
                vol => vol,
                vol => new List<ProxmoxVM>()
            );

            // 6) For each host, list its VMs and scan their configs
            foreach (var host in _proxmoxHelpers.GetQueryableHosts(cluster))
            {
                // a) list VMs
                var vmListUrl = $"https://{host.HostAddress}:8006/api2/json/nodes/{host.Hostname}/qemu";
                var vmListResp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, vmListUrl, null, ct);
                using var vmListDoc = JsonDocument.Parse(await vmListResp.Content.ReadAsStringAsync(ct));
                var vmElems = vmListDoc.RootElement.GetProperty("data").EnumerateArray();

                foreach (var vmElem in vmElems)
                {
                    var vmId = vmElem.GetProperty("vmid").GetInt32();
                    var vmName = vmElem.TryGetProperty("name", out var nm)
                        ? nm.GetString()!
                        : $"VM {vmId}";

                    // fetch full config
                    var cfgJson = await GetVmConfigAsync(cluster, host.Hostname, vmId, ct);
                    using var cfgDoc = JsonDocument.Parse(cfgJson);
                    var cfgData = cfgDoc.RootElement.GetProperty("config");

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
                        if (!Regex.IsMatch(prop.Name, @"^(scsi|virtio|ide|sata|efidisk|tpmstate)\d+$", RegexOptions.IgnoreCase))
                            continue;

                        var val = prop.Value.GetString() ?? "";
                        var parts = val.Split(':', 2);
                        if (parts.Length < 2) continue;

                        var storageName = parts[0];
                        if (result.TryGetValue(storageName, out var list))
                        {
                            // dedupe
                            if (!list.Any(x => x.Id == vmId))
                                list.Add(vmDescriptor);
                        }
                    }
                }
            }

            return result;
        }


        public async Task<string> GetVmConfigAsync2(
            ProxmoxCluster cluster,
            string host,
            int vmId,
            CancellationToken ct = default)
        {
            var hostAddress = _proxmoxHelpers.GetQueryableHosts(cluster).First(h => h.Hostname == host).HostAddress;
            var url = $"https://{hostAddress}:8006/api2/json/nodes/{host}/qemu/{vmId}/config";

            var resp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
            return await resp.Content.ReadAsStringAsync(ct);
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

        public async Task<bool> RestoreVmFromConfigAsync(RestoreFormViewModel model,
      string hostAddress,
      string cloneStorageName,
      bool snapshotChainActive = false,         // ← NEW
      CancellationToken ct = default)
        {
            using var rootDoc = JsonDocument.Parse(model.OriginalConfig);
            if (!rootDoc.RootElement.TryGetProperty("config", out var config))
                return false;

            var host = await _context.ProxmoxHosts
                .FirstOrDefaultAsync(h => h.HostAddress == hostAddress, ct);
            if (host == null || string.IsNullOrWhiteSpace(host.Hostname))
                return false;
            var nodeName = host.Hostname;

            var cluster = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync(ct);
            if (cluster == null || !_proxmoxHelpers.GetQueryableHosts(cluster).Any())
                return false;

            var nextIdUrl = $"https://{hostAddress}:8006/api2/json/cluster/nextid";
            var idResp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, nextIdUrl, null, ct);
            using var idDoc = JsonDocument.Parse(await idResp.Content.ReadAsStringAsync(ct));
            var vmid = idDoc.RootElement.GetProperty("data").GetString();
            if (string.IsNullOrEmpty(vmid))
                return false;

            var payload = _proxmoxHelpers.FlattenConfig(config);

            // Detect old storage name from any disk key
            string? oldStorageName = null;
            var diskKeys = payload.Keys
                .Where(k => Regex.IsMatch(k, @"^(scsi|virtio|sata|efidisk|tpmstate)\d+$", RegexOptions.IgnoreCase))
                .ToList();

            foreach (var key in diskKeys)
            {
                var val = payload[key];
                if (!string.IsNullOrEmpty(val) && val.Contains(":"))
                {
                    oldStorageName = val.Split(':', 2)[0].Trim();
                    break;
                }
            }
            if (string.IsNullOrEmpty(oldStorageName))
                throw new InvalidOperationException("Failed to determine oldStorageName from config.");

            payload["name"] = model.NewVmName;
            payload.Remove("meta");
            payload.Remove("digest");
            payload["protection"] = "0";



            // Disconnect NICs if requested (main config)
            if (model.StartDisconnected)
            {
                foreach (var netKey in payload.Keys
                         .Where(k => Regex.IsMatch(k, @"^net\d+$", RegexOptions.IgnoreCase))
                         .ToList())
                {
                    var def = payload[netKey];
                    if (Regex.IsMatch(def, @"\blink_down=\d"))
                        payload[netKey] = Regex.Replace(def, @"\blink_down=\d", "link_down=1");
                    else
                        payload[netKey] = def + ",link_down=1";
                }
            }

            var oldVmid = _proxmoxHelpers.ExtractOldVmidFromConfig(payload);
            if (string.IsNullOrEmpty(oldVmid))
                throw new InvalidOperationException("Failed to determine oldVmid from config.");

            await RenameVmDirectoryAndFilesAsync(nodeName, cloneStorageName, oldVmid, vmid);

            _proxmoxHelpers.UpdateDiskPathsInConfig(payload, oldVmid, vmid, cloneStorageName);

            // Global remap old → new storage in all values
            foreach (var key in payload.Keys.ToList())
            {
                var val = payload[key];
                if (!string.IsNullOrEmpty(val))
                    payload[key] = val.Replace(oldStorageName, cloneStorageName, StringComparison.OrdinalIgnoreCase);
            }

            // Explicitly remap vmstate if present
            if (payload.ContainsKey("vmstate"))
            {
                payload["vmstate"] = RemapStorageAndVmid(
                    payload["vmstate"],
                    oldStorageName!,
                    cloneStorageName,
                    oldVmid,
                    vmid);
            }

            // 1) Prepare new IDs (before writing anything)
            string? newUuid = null, newVmgen = null;
            if (model.GenerateNewUuid)
            {
                newUuid = Guid.NewGuid().ToString("D");
                newVmgen = Guid.NewGuid().ToString("D");

                // Ensure main payload smbios1 has the new uuid
                if (payload.TryGetValue("smbios1", out var smbios))
                {
                    var parts = smbios.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                      .Where(p => !p.StartsWith("uuid=", StringComparison.OrdinalIgnoreCase));
                    payload["smbios1"] = string.Join(",", parts.Append($"uuid={newUuid}"));
                }
                else
                {
                    payload["smbios1"] = $"uuid={newUuid}";
                }
                payload["vmgenid"] = newVmgen!;
            }

            // 2) Build the .conf text
            var sb = new StringBuilder();

            // Write main config
            foreach (var kv in payload)
                sb.AppendLine($"{kv.Key}: {kv.Value}");

            // Handle snapshots if any
            if (rootDoc.RootElement.TryGetProperty("snapshots", out var snapElem) &&
                snapElem.ValueKind == JsonValueKind.Object)
            {
                foreach (var snapProp in snapElem.EnumerateObject())
                {
                    // Write snapshot section header WITHOUT "snapshot_" prefix
                    sb.AppendLine($"[{snapProp.Name}]");

                    // Flatten snapshot dictionary
                    var snapDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var snapLine in snapProp.Value.EnumerateObject())
                    {
                        snapDict[snapLine.Name] = snapLine.Value.GetString() ?? "";
                    }

                    // Disconnect NICs if requested (apply to snapshot section)
                    if (model.StartDisconnected)
                    {
                        foreach (var netKey in snapDict.Keys
                                 .Where(k => Regex.IsMatch(k, @"^net\d+$", RegexOptions.IgnoreCase))
                                 .ToList())
                        {
                            var def = snapDict[netKey];
                            if (Regex.IsMatch(def, @"\blink_down=\d"))
                                snapDict[netKey] = Regex.Replace(def, @"\blink_down=\d", "link_down=1");
                            else
                                snapDict[netKey] = def + ",link_down=1";
                        }
                    }
                    // Write new UUID/VMGENID if requested (apply to snapshot section)
                    if (newUuid != null && newVmgen != null)
                    {
                        if (snapDict.TryGetValue("smbios1", out var smbiosSnap))
                        {
                            var parts = smbiosSnap.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                                  .Where(p => !p.StartsWith("uuid=", StringComparison.OrdinalIgnoreCase));
                            snapDict["smbios1"] = string.Join(",", parts.Append($"uuid={newUuid}"));
                        }
                        else
                        {
                            snapDict["smbios1"] = $"uuid={newUuid}";
                        }
                        snapDict["vmgenid"] = newVmgen;
                    }

                    // Remap disk paths inside snapshot (keys like scsi0, virtio1, etc.)
                    _proxmoxHelpers.UpdateDiskPathsInConfig(snapDict, oldVmid, vmid, cloneStorageName);

                    // Global remap for all snapshot values: old storage → new storage
                    foreach (var k in snapDict.Keys.ToList())
                    {
                        var v = snapDict[k];
                        if (!string.IsNullOrEmpty(v))
                            snapDict[k] = v.Replace(oldStorageName, cloneStorageName, StringComparison.OrdinalIgnoreCase);
                    }

                    // Explicitly remap vmstate value (may contain storage & vmid)
                    if (snapDict.TryGetValue("vmstate", out var vmstateValue))
                    {
                        snapDict["vmstate"] = RemapStorageAndVmid(
                            vmstateValue,
                            oldStorageName!,
                            cloneStorageName,
                            oldVmid,
                            vmid);
                    }

                    // Write out remapped snapshot properties
                    foreach (var snapKvp in snapDict)
                        sb.AppendLine($"{snapKvp.Key}: {snapKvp.Value}");
                }
            }

            // Ensure "storage" key in main payload is set to cloneStorageName
            if (!payload.ContainsKey("storage"))
                payload["storage"] = cloneStorageName;

            var sshUser = cluster.Username;
            int atIdx = sshUser.IndexOf('@');
            if (atIdx > 0) sshUser = sshUser.Substring(0, atIdx);
            string sshPass = _encryptionService.Decrypt(cluster.PasswordHash);

            var configPath = $"/etc/pve/qemu-server/{vmid}.conf";
            var configContent = sb.ToString();

            // Generate a unique EOF marker to avoid conflicts
            var eofMarker = "EOF_" + Guid.NewGuid().ToString("N");
            var sshCmd = $"cat > {configPath} <<'{eofMarker}'\n{configContent}\n{eofMarker}\n";

            using (var ssh = new Renci.SshNet.SshClient(hostAddress, sshUser, sshPass))
            {
                ssh.Connect();
                using (var cmd = ssh.CreateCommand(sshCmd))
                {
                    var result = cmd.Execute();
                    if (cmd.ExitStatus != 0)
                    {
                        ssh.Disconnect();
                        return false;
                    }
                }
                ssh.Disconnect();
            }

            // ─────────────────────────────────────────────────────────────────────────
            // Post-restore: if snapshot chain was active AND a BareProx snapshot exists,
            // rollback to it (no autostart) and then delete it. Non-fatal on failure.
            // ─────────────────────────────────────────────────────────────────────────
            try
            {
                var vmidInt = int.Parse(vmid);

                var snaps = await _proxmoxSnapshots.GetSnapshotListAsync(cluster, nodeName, hostAddress, vmidInt, ct);
                if (snaps == null || snaps.Count == 0)
                {
                    _logger.LogInformation("No snapshots found on VMID {Vmid}. Nothing to repair or rollback.", vmidInt);
                    return false;
                }
                
                // Newest BareProx snapshot (preferred target for rollback)
                var bareproxSnap = snaps
                    .Where(s => !string.IsNullOrWhiteSpace(s.Name) &&
                                s.Name.StartsWith("BareProx-", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(s => s.Snaptime)
                    .FirstOrDefault();

                // Newest non-current snapshot (fallback target for rollback, and to decide chain repair)
                var newestNonCurrent = snaps
                    .Where(s => !string.Equals(s.Name, "current", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(s => s.Snaptime)
                    .FirstOrDefault();

                // If there is any snapshot except "current", repair the chain (regardless of rollback).
                if (newestNonCurrent != null)
                {
                    try
                    {
                        await RepairExternalSnapshotChainAsync(nodeName, cloneStorageName, vmidInt, ct);
                        _logger.LogInformation("Repaired external snapshot chain for VMID {Vmid}.", vmidInt);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Snapshot chain repair failed for VMID {Vmid}; continuing.", vmidInt);
                    }
                }

                if (model.RollbackSnapshot)
                {
                    var targetSnap = bareproxSnap ?? newestNonCurrent;
                    if (targetSnap == null)
                    {
                        _logger.LogInformation("Rollback requested but no suitable snapshot found on VMID {Vmid}.", vmidInt);
                    }
                    else
                    {
                        try
                        {
                            var ok = await _proxmoxSnapshots.RollbackSnapshotAsync(
                                cluster, nodeName, hostAddress, vmidInt,
                                snapshotName: targetSnap.Name,
                                startAfterRollback: false,
                                logger: _logger,
                                ct: ct);

                            if (!ok)
                            {
                                _logger.LogWarning(
                                    "Rollback task for snapshot '{Snap}' on VMID {Vmid} did not complete OK.",
                                    targetSnap.Name, vmidInt);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(
                                ex,
                                "Rollback call failed for snapshot '{Snap}' on VMID {Vmid}. Will still attempt delete.",
                                targetSnap.Name, vmidInt);
                        }

                        // Best-effort delete after rollback (unchanged behavior)
                        try
                        {
                            await _proxmoxSnapshots.DeleteSnapshotAsync(cluster, nodeName, hostAddress, vmidInt, targetSnap.Name, ct);
                            _logger.LogInformation("Deleted snapshot '{Snap}' after rollback on VMID {Vmid}.",
                                targetSnap.Name, vmidInt);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete snapshot '{Snap}' after rollback on VMID {Vmid}.",
                                targetSnap.Name, vmidInt);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Post-restore snapshot handling (repair/rollback/delete) skipped due to error.");
            }

            // NEW: Regenerate MAC addresses by letting Proxmox auto-assign (qm set -netN ...)
            if (model.GenerateNewMacAddresses)
            {
                // figure out which netX keys exist in the final payload we just wrote
                var netKeys = payload.Keys
                    .Where(k => Regex.IsMatch(k, @"^net\d+$", RegexOptions.IgnoreCase))
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (netKeys.Count > 0)
                {
                    using (var ssh2 = new Renci.SshNet.SshClient(hostAddress, sshUser, sshPass))
                    {
                        ssh2.Connect();

                        foreach (var netKey in netKeys)
                        {
                            var def = payload[netKey]; // e.g. "virtio=BC:24:11:AF:C1:EC,bridge=vmbr0,firewall=1,link_down=1"
                                                       // Strip the MAC from the first segment: "<model>[=<mac>]" -> "<model>"
                                                       // keep the rest (bridge=..., firewall=..., link_down=..., etc.)
                            var parts = def.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                            if (parts.Count == 0) continue;

                            var modelToken = parts[0]; // e.g. "virtio=BC:24:..." or "virtio"
                            var modelOnly = modelToken.Split('=', 2)[0]; // "virtio"

                            var tail = parts.Skip(1); // preserve all other options as-is
                            var newNetValue = string.Join(",", new[] { modelOnly }.Concat(tail));

                            // Build: qm set <vmid> -netN "<model[,opts-without-mac]>"
                            var netIdx = Regex.Match(netKey, @"^net(\d+)$", RegexOptions.IgnoreCase).Groups[1].Value;
                            var qmCmd = $"qm set {vmid} -net{netIdx} \"{newNetValue}\"";

                            using (var cmd2 = ssh2.CreateCommand(qmCmd))
                            {
                                var _ = cmd2.Execute();
                                if (cmd2.ExitStatus != 0)
                                {
                                    _logger.LogWarning("Failed to regenerate MAC for {NetKey} on VMID {Vmid}. Exit={Exit} Error={Err}",
                                        netKey, vmid, cmd2.ExitStatus, cmd2.Error);
                                }
                                else
                                {
                                    _logger.LogInformation("Regenerated MAC for {NetKey} on VMID {Vmid} using '{NewVal}'.",
                                        netKey, vmid, newNetValue);
                                }
                            }
                        }

                        ssh2.Disconnect();
                    }
                }
            }


            return true;
        }


        /// Replace storage prefix and VMID tokens inside a single Proxmox value
        /// like "mystorage:111/vm-111-disk-0.qcow2,discard=on".
        string RemapStorageAndVmid(
            string input,
            string oldStorage,
            string newStorage,
            string oldVmid,
            string newVmid)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            var updated = input;

            // 1) Replace storage prefix ONLY when it appears as "<storage>:" (case-insensitive).
            //    Example: "oldstore:..." -> "newstore:..."
            if (!string.IsNullOrEmpty(oldStorage) && !string.IsNullOrEmpty(newStorage))
            {
                var storagePrefix = new Regex(
                    @"(?i)(?<![A-Za-z0-9_])" + Regex.Escape(oldStorage) + @"(?=:)",
                    RegexOptions.CultureInvariant);
                updated = storagePrefix.Replace(updated, newStorage);
            }

            // 2) Replace the VMID when used as a path segment: ".../<oldVmid>/..."
            //    Supports separators ^, :, /, \  and forward/back for ahead.
            if (!string.IsNullOrEmpty(oldVmid) && !string.IsNullOrEmpty(newVmid))
            {
                var vmidSegment = new Regex(
                    @"(?:(?<=^)|(?<=:)|(?<=/)|(?<=\\))"
                  + Regex.Escape(oldVmid)
                  + @"(?=(/|\\))",
                    RegexOptions.CultureInvariant);
                updated = vmidSegment.Replace(updated, newVmid);

                // 3) Replace filename token "vm-<vmid>-"
                var vmToken = new Regex(@"(?i)\bvm-" + Regex.Escape(oldVmid) + "-", RegexOptions.CultureInvariant);
                updated = vmToken.Replace(updated, $"vm-{newVmid}-");
            }

            return updated;
        }

       public async Task<bool> RestoreVmFromConfigWithOriginalIdAsync(
            RestoreFormViewModel model,
            string hostAddress,
            string cloneStorageName,
            bool snapshotChainActive = false,
            CancellationToken ct = default)
        {
            using var rootDoc = JsonDocument.Parse(model.OriginalConfig);
            // ←— look for either "config" or "data"
            if (!rootDoc.RootElement.TryGetProperty("config", out var config) &&
                !rootDoc.RootElement.TryGetProperty("data", out config))
            {
                return false;
            }

            var host = await _context.ProxmoxHosts
                .FirstOrDefaultAsync(h => h.HostAddress == hostAddress, ct);
            if (host == null || string.IsNullOrWhiteSpace(host.Hostname))
                return false;
            var nodeName = host.Hostname;

            var cluster = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync(ct);
            if (cluster == null || !_proxmoxHelpers.GetQueryableHosts(cluster).Any())
                return false;

            var vmid = int.Parse(model.VmId).ToString();

            var payload = _proxmoxHelpers.FlattenConfig(config);

            // Disconnect NICs if requested (main config)
            if (model.StartDisconnected)
            {
                foreach (var netKey in payload.Keys
                         .Where(k => Regex.IsMatch(k, @"^net\d+$", RegexOptions.IgnoreCase))
                         .ToList())
                {
                    var def = payload[netKey];

                    // if there's already a link_down setting, just overwrite it
                    if (Regex.IsMatch(def, @"\blink_down=\d"))
                    {
                        payload[netKey] = Regex.Replace(def, @"\blink_down=\d", "link_down=1");
                    }
                    else
                    {
                        // otherwise append it
                        payload[netKey] = def + ",link_down=1";
                    }
                }
            }

            // Detect old storage name from any disk key
            string? oldStorageName = null;
            var diskKeys = payload.Keys
                .Where(k => Regex.IsMatch(k, @"^(scsi|virtio|ide|sata|efidisk|tpmstate)\d+$", RegexOptions.IgnoreCase))
                .ToList();

            foreach (var key in diskKeys)
            {
                var val = payload[key];
                if (!string.IsNullOrEmpty(val) && val.Contains(":"))
                {
                    oldStorageName = val.Split(':', 2)[0].Trim();
                    break;
                }
            }
            if (string.IsNullOrEmpty(oldStorageName))
                throw new InvalidOperationException("Failed to determine oldStorageName from config.");

            //payload["vmid"] = vmid; removed!
            payload.Remove("meta");
            payload.Remove("digest");
            payload["protection"] = "0";
            payload["storage"] = cloneStorageName; // ensure storage is set to new storage

            // Remap disk paths to new storage (main config)
            foreach (var diskKey in diskKeys)
            {
                var diskVal = payload[diskKey];
                if (!diskVal.Contains(":"))
                    continue;

                var parts = diskVal.Split(new[] { ':' }, 2);
                var diskDef = parts[1]; // "vm-100-disk-0.qcow2,discard=on,iothread=1"
                var sub = diskDef.Split(new[] { ',' }, 2);

                var filenameWithExt = sub[0];
                var options = sub.Length > 1 ? "," + sub[1] : string.Empty;

                payload[diskKey] = $"{cloneStorageName}:{filenameWithExt}{options}";
            }

            // Global replace oldStorageName → cloneStorageName in all payload values (catch anything missed)
            foreach (var key in payload.Keys.ToList())
            {
                var val = payload[key];
                if (!string.IsNullOrEmpty(val))
                {
                    payload[key] = val.Replace(oldStorageName, cloneStorageName, StringComparison.OrdinalIgnoreCase);
                }
            }

            // Explicitly remap vmstate if present
            if (payload.ContainsKey("vmstate"))
            {
                payload["vmstate"] = RemapStorageAndVmid(payload["vmstate"], oldStorageName, cloneStorageName, vmid, vmid);
            }

            var sb = new StringBuilder();

            // Write main config
            foreach (var kv in payload)
                sb.AppendLine($"{kv.Key}: {kv.Value}");

            // Handle snapshots if any
            if (rootDoc.RootElement.TryGetProperty("snapshots", out var snapElem) && snapElem.ValueKind == JsonValueKind.Object)
            {
                foreach (var snapProp in snapElem.EnumerateObject())
                {
                    // Snapshot header (no snapshot_ prefix)
                    sb.AppendLine($"[{snapProp.Name}]");

                    // Flatten snapshot dictionary
                    var snapDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var snapLine in snapProp.Value.EnumerateObject())
                    {
                        snapDict[snapLine.Name] = snapLine.Value.GetString() ?? "";
                    }

                    // Disconnect NICs if requested (apply to snapshot section)
                    if (model.StartDisconnected)
                    {
                        foreach (var netKey in snapDict.Keys
                                 .Where(k => Regex.IsMatch(k, @"^net\d+$", RegexOptions.IgnoreCase))
                                 .ToList())
                        {
                            var def = snapDict[netKey];
                            if (Regex.IsMatch(def, @"\blink_down=\d"))
                            {
                                snapDict[netKey] = Regex.Replace(def, @"\blink_down=\d", "link_down=1");
                            }
                            else
                            {
                                snapDict[netKey] = def + ",link_down=1";
                            }
                        }
                    }

                    // Remap disk paths in snapshot
                    var snapDiskKeys = snapDict.Keys
                        .Where(k => Regex.IsMatch(k, @"^(scsi|virtio|ide|sata|efidisk|tpmstate)\d+$", RegexOptions.IgnoreCase))
                        .ToList();

                    foreach (var diskKey in snapDiskKeys)
                    {
                        var diskVal = snapDict[diskKey];
                        if (!diskVal.Contains(":"))
                            continue;

                        // skip plain CD-ROMs, but allow cloud-init
                        if (diskVal.Contains("media=cdrom", StringComparison.OrdinalIgnoreCase) &&
                            !diskVal.Contains("cloudinit", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var parts = diskVal.Split(new[] { ':' }, 2);
                        var diskDef = parts[1];
                        var sub = diskDef.Split(new[] { ',' }, 2);

                        var filenameWithExt = sub[0];
                        var options = sub.Length > 1 ? "," + sub[1] : string.Empty;

                        snapDict[diskKey] = $"{cloneStorageName}:{filenameWithExt}{options}";
                    }

                    // Global replace oldStorageName → cloneStorageName in all snapshot values
                    foreach (var key in snapDict.Keys.ToList())
                    {
                        var val = snapDict[key];
                        if (!string.IsNullOrEmpty(val))
                        {
                            snapDict[key] = val.Replace(oldStorageName, cloneStorageName, StringComparison.OrdinalIgnoreCase);
                        }
                    }

                    // Explicitly remap vmstate in snapshot
                    if (snapDict.TryGetValue("vmstate", out var vmstateValue))
                    {
                        snapDict["vmstate"] = RemapStorageAndVmid(vmstateValue, oldStorageName, cloneStorageName, vmid, vmid);
                    }

                    // Write snapshot properties
                    foreach (var snapKvp in snapDict)
                        sb.AppendLine($"{snapKvp.Key}: {snapKvp.Value}");
                }
            }

            // Upload config file via SSH (cat << EOF style)
            var sshUser = cluster.Username;
            int atIdx = sshUser.IndexOf('@');
            if (atIdx > 0) sshUser = sshUser.Substring(0, atIdx);
            string sshPass = _encryptionService.Decrypt(cluster.PasswordHash);

            var configPath = $"/etc/pve/qemu-server/{vmid}.conf";
            var configContent = sb.ToString();

            var eofMarker = "EOF_" + Guid.NewGuid().ToString("N");
            var sshCmd = $"cat > {configPath} <<'{eofMarker}'\n{configContent}\n{eofMarker}\n";

            using (var ssh = new Renci.SshNet.SshClient(hostAddress, sshUser, sshPass))
            {
                ssh.Connect();
                using (var cmd = ssh.CreateCommand(sshCmd))
                {
                    var result = cmd.Execute();
                    if (cmd.ExitStatus != 0)
                    {
                        ssh.Disconnect();
                        return false;
                    }
                }
                ssh.Disconnect();
            }

            // ─────────────────────────────────────────────────────────────────────────
            // Post-restore: if snapshot chain was active AND a BareProx snapshot exists,
            // rollback to it (no autostart) and then delete it. Non-fatal on failure.
            // ─────────────────────────────────────────────────────────────────────────
            try
            {
                var vmidInt = int.Parse(vmid);

                var snaps = await _proxmoxSnapshots.GetSnapshotListAsync(cluster, nodeName, hostAddress, vmidInt, ct);
                if (snaps == null || snaps.Count == 0)
                {
                    _logger.LogInformation("No snapshots found on VMID {Vmid}. Nothing to repair or rollback.", vmidInt);
                    return false;
                }

                // Newest BareProx snapshot (preferred target for rollback)
                var bareproxSnap = snaps
                    .Where(s => !string.IsNullOrWhiteSpace(s.Name) &&
                                s.Name.StartsWith("BareProx-", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(s => s.Snaptime)
                    .FirstOrDefault();

                // Newest non-current snapshot (fallback target for rollback, and to decide chain repair)
                var newestNonCurrent = snaps
                    .Where(s => !string.Equals(s.Name, "current", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(s => s.Snaptime)
                    .FirstOrDefault();

                // If there is any snapshot except "current", repair the chain (regardless of rollback).
                if (newestNonCurrent != null)
                {
                    try
                    {
                        await RepairExternalSnapshotChainAsync(nodeName, cloneStorageName, vmidInt, ct);
                        _logger.LogInformation("Repaired external snapshot chain for VMID {Vmid}.", vmidInt);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Snapshot chain repair failed for VMID {Vmid}; continuing.", vmidInt);
                    }
                }

                if (model.RollbackSnapshot)
                {
                    var targetSnap = bareproxSnap ?? newestNonCurrent;
                    if (targetSnap == null)
                    {
                        _logger.LogInformation("Rollback requested but no suitable snapshot found on VMID {Vmid}.", vmidInt);
                    }
                    else
                    {
                        try
                        {
                            var ok = await _proxmoxSnapshots.RollbackSnapshotAsync(
                                cluster, nodeName, hostAddress, vmidInt,
                                snapshotName: targetSnap.Name,
                                startAfterRollback: false,
                                logger: _logger,
                                ct: ct);

                            if (!ok)
                            {
                                _logger.LogWarning(
                                    "Rollback task for snapshot '{Snap}' on VMID {Vmid} did not complete OK.",
                                    targetSnap.Name, vmidInt);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(
                                ex,
                                "Rollback call failed for snapshot '{Snap}' on VMID {Vmid}. Will still attempt delete.",
                                targetSnap.Name, vmidInt);
                        }

                        // Best-effort delete after rollback (unchanged behavior)
                        try
                        {
                            await _proxmoxSnapshots.DeleteSnapshotAsync(cluster, nodeName, hostAddress, vmidInt, targetSnap.Name, ct);
                            _logger.LogInformation("Deleted snapshot '{Snap}' after rollback on VMID {Vmid}.",
                                targetSnap.Name, vmidInt);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete snapshot '{Snap}' after rollback on VMID {Vmid}.",
                                targetSnap.Name, vmidInt);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Post-restore snapshot handling (repair/rollback/delete) skipped due to error.");
            }

            return true;
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

        /// <summary>
        /// Unmounts (deletes) an NFS storage entry from a Proxmox node.
        /// </summary>
        public async Task<bool> UnmountNfsStorageViaApiAsync_old(
            ProxmoxCluster cluster,
            string nodeName,
            string storageName,
            CancellationToken ct = default)
        {
            // Find the host entry for this node
            var host = _proxmoxHelpers.GetHostByNodeName(cluster, nodeName);

            // Build the DELETE URL
            var url = $"https://{host.HostAddress}:8006/api2/json/storage/{storageName}";

            try
            {
                // Send the DELETE. SendWithRefreshAsync will retry once on 401.
                var resp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Delete, url, null, ct);
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                // swallow or log if you want; failure to unmount is non‐fatal here
                return false;
            }
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
                    using var ssh = new Renci.SshNet.SshClient(
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

        public async Task<(
        bool Quorate,
        int OnlineNodeCount,
        int TotalNodeCount,
        Dictionary<string, bool> HostStates,
        string Message
    )> GetClusterStatusAsync(
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
                        ? (onlineCount == total
                            ? "Cluster healthy (all nodes online)"
                            : $"Quorum ok, but {total - onlineCount} node(s) offline")
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

        // GET /api2/json/nodes/{node}/network
        public async Task<IReadOnlyList<PveNetworkIf>> GetNodeNetworksAsync(
            string node,
            CancellationToken ct = default)
        {
            var resolved = await ResolveClusterAndHostAsync(node, ct);
            if (resolved == null) return Array.Empty<PveNetworkIf>();

            var (cluster, host) = resolved.Value;
            var url = $"https://{host.HostAddress}:8006/api2/json/nodes/{host.Hostname}/network";

            try
            {
                var resp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    return Array.Empty<PveNetworkIf>();

                var list = new List<PveNetworkIf>();
                foreach (var n in data.EnumerateArray())
                {
                    var type = n.TryGetProperty("type", out var t) ? t.GetString() : null;
                    var iface = n.TryGetProperty("iface", out var i) ? i.GetString() : null;

                    list.Add(new PveNetworkIf
                    {
                        Type = type,
                        Iface = iface
                    });
                }
                return list;
            }
            catch
            {
                return Array.Empty<PveNetworkIf>();
            }
        }

        // GET /api2/json/cluster/sdn/vnets
        public async Task<IReadOnlyList<PveSdnVnet>> GetSdnVnetsAsync(CancellationToken ct = default)
        {
            // Use any available cluster/host to hit the cluster endpoint
            var cluster = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync(ct);

            if (cluster == null || cluster.Hosts.Count == 0)
                return Array.Empty<PveSdnVnet>();

            var host = cluster.Hosts.First();
            var url = $"https://{host.HostAddress}:8006/api2/json/cluster/sdn/vnets";

            try
            {
                var resp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    return Array.Empty<PveSdnVnet>();

                var list = new List<PveSdnVnet>();
                foreach (var v in data.EnumerateArray())
                {
                    var vnet = v.TryGetProperty("vnet", out var vn) ? vn.GetString() : null;

                    int? tag = null;
                    if (v.TryGetProperty("tag", out var tg))
                    {
                        if (tg.ValueKind == JsonValueKind.Number) tag = tg.GetInt32();
                        else if (tg.ValueKind == JsonValueKind.String && int.TryParse(tg.GetString(), out var ti)) tag = ti;
                    }

                    list.Add(new PveSdnVnet
                    {
                        Vnet = vnet,
                        Tag = tag
                    });
                }
                return list;
            }
            catch
            {
                return Array.Empty<PveSdnVnet>();
            }
        }

        // GET /api2/json/nodes/{node}/storage/{storage}/content?content=iso
        public async Task<IReadOnlyList<PveStorageContentItem>> GetStorageContentAsync(
            string node,
            string storage,
            string content,
            CancellationToken ct = default)
        {
            var resolved = await ResolveClusterAndHostAsync(node, ct);
            if (resolved == null) return Array.Empty<PveStorageContentItem>();

            var (cluster, host) = resolved.Value;
            var url = $"https://{host.HostAddress}:8006/api2/json/nodes/{host.Hostname}/storage/{Uri.EscapeDataString(storage)}/content?content={Uri.EscapeDataString(content)}";

            try
            {
                var resp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    return Array.Empty<PveStorageContentItem>();

                var list = new List<PveStorageContentItem>();
                foreach (var i in data.EnumerateArray())
                {

                    long? ctime = null;
                    if (i.TryGetProperty("ctime", out var ctProp))
                    {
                        if (ctProp.ValueKind == JsonValueKind.Number) ctime = ctProp.GetInt64();
                        else if (ctProp.ValueKind == JsonValueKind.String && long.TryParse(ctProp.GetString(), out var l)) ctime = l;
                    }
                    // Proxmox can return volid/volId/volume; read all safely
                    string? name = i.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? volid = i.TryGetProperty("volid", out var vi) ? vi.GetString() : null;
                    string? volId = i.TryGetProperty("volId", out var vI) ? vI.GetString() : null;
                    string? volume = i.TryGetProperty("volume", out var vo) ? vo.GetString() : null;
                    string? cnt = i.TryGetProperty("content", out var c) ? c.GetString() : null;

                    list.Add(new PveStorageContentItem
                    {
                        Name = name,
                        Volid = volid,
                        VolId = volId,
                        Volume = volume,
                        Content = cnt,
                        Ctime = ctime
                    });
                }
                return list;
            }
            catch
            {
                return Array.Empty<PveStorageContentItem>();
            }
        }
        public async Task<IReadOnlyList<PveStorageListItem>> GetNodeStoragesAsync(
            string node,
            CancellationToken ct = default)
        {
            var resolved = await ResolveClusterAndHostAsync(node, ct);
            if (resolved == null) return Array.Empty<PveStorageListItem>();

            var (cluster, host) = resolved.Value;
            var url = $"https://{host.HostAddress}:8006/api2/json/nodes/{host.Hostname}/storage";

            try
            {
                var resp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    return Array.Empty<PveStorageListItem>();

                var list = new List<PveStorageListItem>();
                foreach (var s in data.EnumerateArray())
                {
                    var storage = s.TryGetProperty("storage", out var st) ? st.GetString() : null;
                    if (string.IsNullOrWhiteSpace(storage)) continue;

                    var content = s.TryGetProperty("content", out var c) ? c.GetString() : null;
                    var type = s.TryGetProperty("type", out var t) ? t.GetString() : null;

                    list.Add(new PveStorageListItem
                    {
                        Storage = storage,
                        Content = content,
                        Type = type
                    });
                }
                return list;
            }
            catch
            {
                return Array.Empty<PveStorageListItem>();
            }
        }

        private async Task<(ProxmoxCluster Cluster, ProxmoxHost Host, string SshUser, string SshPass)> GetSshTargetAsync(CancellationToken ct)
        {
            var cluster = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync(ct) ?? throw new InvalidOperationException("No Proxmox cluster configured.");

            var host = _proxmoxHelpers.GetQueryableHosts(cluster).FirstOrDefault()
                ?? cluster.Hosts.FirstOrDefault()
                ?? throw new InvalidOperationException("No Proxmox host configured.");

            var sshUser = cluster.Username.Split('@')[0];
            var sshPass = _encryptionService.Decrypt(cluster.PasswordHash);
            return (cluster, host, sshUser, sshPass);
        }

        public async Task<bool> IsVmidAvailableAsync(int vmid, CancellationToken ct = default)
        {
            var (cluster, host, _, _) = await GetSshTargetAsync(ct);
            var url = $"https://{host.HostAddress}:8006/api2/json/cluster/resources?type=vm";
            var resp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement.GetProperty("data").EnumerateArray();
            foreach (var e in arr)
            {
                if (e.TryGetProperty("vmid", out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() == vmid)
                    return false; // taken
            }
            // also consider existing conf file:
            return !await FileExistsAsync($"/etc/pve/qemu-server/{vmid}.conf", ct);
        }
        public async Task EnsureDirectoryAsync(string absPath, CancellationToken ct = default)
        {
            var (_, host, sshUser, sshPass) = await GetSshTargetAsync(ct);
            using var ssh = new Renci.SshNet.SshClient(host.HostAddress, sshUser, sshPass);
            ssh.Connect();
            var cmd = ssh.CreateCommand($"mkdir -p -- {_proxmoxHelpers.EscapeBash(absPath)}");
            var _ = cmd.Execute();
            if (cmd.ExitStatus != 0) throw new ProxmoxSshException(cmd.CommandText, cmd.ExitStatus, cmd.Error);
            ssh.Disconnect();
        }

        public async Task<bool> FileExistsAsync(string absPath, CancellationToken ct = default)
        {
            var (_, host, sshUser, sshPass) = await GetSshTargetAsync(ct);
            using var ssh = new Renci.SshNet.SshClient(host.HostAddress, sshUser, sshPass);
            ssh.Connect();
            using var cmd = ssh.CreateCommand($"test -e {_proxmoxHelpers.EscapeBash(absPath)} && echo OK || echo NO");
            var result = cmd.Execute();
            ssh.Disconnect();
            return result.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase);
        }
        public async Task<string> ReadTextFileAsync(string absPath, CancellationToken ct = default)
        {
            var (_, host, sshUser, sshPass) = await GetSshTargetAsync(ct);
            using var ssh = new Renci.SshNet.SshClient(host.HostAddress, sshUser, sshPass);
            ssh.Connect();
            using var cmd = ssh.CreateCommand($"cat -- {_proxmoxHelpers.EscapeBash(absPath)}");
            var text = cmd.Execute();
            ssh.Disconnect();
            return text ?? string.Empty;
        }
        public async Task WriteTextFileAsync(string absPath, string content, CancellationToken ct = default)
        {
            var (_, host, sshUser, sshPass) = await GetSshTargetAsync(ct);

            absPath = _proxmoxHelpers.ToPosix(absPath);
            var dir = _proxmoxHelpers.GetDirPosix(absPath);

            var conn = new PasswordConnectionInfo(host.HostAddress, sshUser, sshPass)
            {
                Timeout = TimeSpan.FromSeconds(30) // connect timeout
            };

            using var ssh = new SshClient(conn)
            {
                KeepAliveInterval = TimeSpan.FromSeconds(15)
            };

            ssh.Connect();

            // ensure target dir & truncate file
            ExecOrThrow(ssh, $"mkdir -p -- {_proxmoxHelpers.EscapeBash(dir)} && : > {_proxmoxHelpers.EscapeBash(absPath)}");

            // stream as base64 in small chunks to avoid channel aborts
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content ?? string.Empty));
            const int chunkSize = 4096;
            for (int i = 0; i < b64.Length; i += chunkSize)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = b64.Substring(i, Math.Min(chunkSize, b64.Length - i));
                ExecOrThrow(ssh, $"printf %s {_proxmoxHelpers.EscapeBash(chunk)} | base64 -d >> {_proxmoxHelpers.EscapeBash(absPath)}");
            }

            // best-effort flush
            ExecOrThrow(ssh, "sync || true");

            ssh.Disconnect();
        }

        private static void ExecOrThrow(SshClient ssh, string command)
        {
            using var cmd = ssh.CreateCommand(command);
            cmd.CommandTimeout = TimeSpan.FromMinutes(2); // per-exec timeout
            var _ = cmd.Execute();
            if (cmd.ExitStatus != 0)
                throw new ProxmoxSshException(cmd.CommandText, cmd.ExitStatus, cmd.Error);
        }

        public async Task SetCdromAsync(int vmid, string volidOrName, CancellationToken ct = default)
        {
            // Accept either "storage:iso/file.iso" or just "file.iso" (then assume 'local:iso/...')
            var volid = volidOrName.Contains(':') ? volidOrName : $"local:iso/{volidOrName}";
            var (cluster, host, _, _) = await GetSshTargetAsync(ct);

            // Use API: change CDROM (ide2) – qm set --cdrom
            var url = $"https://{host.HostAddress}:8006/api2/json/nodes/{host.Hostname}/qemu/{vmid}/config";
            var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["cdrom"] = volid });
            await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Post, url, form, ct);
        }
        public class ProxmoxSshException : Exception
        {
            public int? ExitStatus { get; }
            public string Command { get; }
            public string? Stderr { get; }

            public ProxmoxSshException(string command, int? exitStatus, string? stderr)
                : base($"SSH command failed ({exitStatus?.ToString() ?? "null"}): {command}\n{stderr}")
            {
                Command = command;
                ExitStatus = exitStatus;
                Stderr = stderr;
            }
        }

        public async Task AddDummyDiskAsync(int vmid, string storage, int slot, int sizeGiB, CancellationToken ct = default)
        {
            // qm set {vmid} --virtio{slot} {storage}:{sizeGiB}
            // Example: qm set 119 --virtio5 vm_migration:1
            var (_, host, sshUser, sshPass) = await GetSshTargetAsync(ct);
            var cmdText = $"qm set {vmid} --virtio{slot} {_proxmoxHelpers.EscapeBash($"{storage}:{sizeGiB}")}";

            using var ssh = new Renci.SshNet.SshClient(host.HostAddress, sshUser, sshPass);
            ssh.Connect();
            using var cmd = ssh.CreateCommand(cmdText);
            var _ = cmd.Execute();
            var rc = cmd.ExitStatus;
            var err = cmd.Error;
            ssh.Disconnect();

            if (rc != 0)
                throw new ProxmoxSshException(cmdText, rc, err);
        }
        public async Task<int?> FirstFreeVirtioSlotAsync(int vmid, CancellationToken ct = default)
        {
            // Read current conf and find used virtioN entries
            var confPath = $"/etc/pve/qemu-server/{vmid}.conf";
            var text = await ReadTextFileAsync(confPath, ct);

            var used = new HashSet<int>();
            foreach (Match m in Regex.Matches(text ?? string.Empty, @"^\s*virtio(?<n>\d+):", RegexOptions.Multiline))
            {
                if (int.TryParse(m.Groups["n"]?.Value, out var n))
                    used.Add(n);
            }

            // Proxmox conventionally supports 0..15 for each bus type
            for (int i = 0; i <= 15; i++)
                if (!used.Contains(i)) return i;

            return null; // all taken
        }
        public async Task AddEfiDiskAsync(int vmid, string storage, CancellationToken ct = default)
        {
            // qm set {vmid} --efidisk0 {storage}:0
            // Example: qm set 118 --efidisk0 vm_migration:0
            var (_, host, sshUser, sshPass) = await GetSshTargetAsync(ct);
            var target = $"{storage}:0";
            var cmdText = $"qm set {vmid} --efidisk0 {_proxmoxHelpers.EscapeBash(target)}";

            using var ssh = new Renci.SshNet.SshClient(host.HostAddress, sshUser, sshPass);
            ssh.Connect();
            using var cmd = ssh.CreateCommand(cmdText);
            var _ = cmd.Execute();
            var rc = cmd.ExitStatus;
            var err = cmd.Error;
            ssh.Disconnect();

            if (rc != 0)
                throw new ProxmoxSshException(cmdText, rc, err);
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
            var script = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\r\n?", "\n");

            try
            {
                using var ssh = new Renci.SshNet.SshClient(host.HostAddress, sshUser, sshPass);
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
        /// <summary>
        /// Checks via API if "snapshot-as-volume-chain" is active on the storage.
        /// Returns false if the field is missing or not set.
        /// Uses GET /api2/json/storage/{storage}.
        /// </summary>
        public async Task<bool> IsSnapshotChainActiveFromDefAsync(
            ProxmoxCluster cluster,
            string storageName,

            CancellationToken ct = default)
        {
            if (cluster?.Hosts == null || cluster.Hosts.Count == 0)
                return false;

            // Pick any reachable node in the cluster (the storage definition is cluster-wide)
            var host = _proxmoxHelpers.GetQueryableHosts(cluster).FirstOrDefault()
                       ?? cluster.Hosts.First();

            var url = $"https://{host.HostAddress}:8006/api2/json/storage/{Uri.EscapeDataString(storageName)}";

            var resp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data))
                return false;

            // If Proxmox added the flag, evaluate it
            if (data.TryGetProperty("snapshot-as-volume-chain", out var prop))
            {
                switch (prop.ValueKind)
                {
                    case JsonValueKind.True:
                        return true;
                    case JsonValueKind.False:
                        return false;
                    case JsonValueKind.Number:
                        return prop.GetInt32() != 0;
                    case JsonValueKind.String:
                        var val = prop.GetString();
                        return val == "1" || val?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
                }
            }

            // Default: flag not present or unreadable
            return false;
        }

        public async Task<bool> RepairExternalSnapshotChainAsync(
            string nodeName,
            string storageName,
            int vmid,
            CancellationToken ct = default)
        {
            var resolved = await ResolveClusterAndHostAsync(nodeName, ct)
                          ?? throw new InvalidOperationException($"Node '{nodeName}' not found.");
            var (cluster, host) = (resolved.Cluster, resolved.Host);

            var sshUser = cluster.Username.Split('@')[0];
            var sshPass = _encryptionService.Decrypt(cluster.PasswordHash);

            // single-quote for bash: 'foo'  -> 'foo'
            // handles embedded single quotes:  a'b  ->  'a'"'"'b'
            string BashQ(string s) => "'" + (s ?? string.Empty).Replace("'", "'\"'\"'") + "'";

            var sb = new StringBuilder();

            sb.AppendLine("set -euo pipefail");
            sb.AppendLine();
            sb.AppendLine("storage=" + BashQ(storageName));
            sb.AppendLine("vmid=" + vmid);
            sb.AppendLine();
            sb.AppendLine("base=\"\"");
            sb.AppendLine("if [ -d \"/mnt/pve/$storage/images\" ]; then");
            sb.AppendLine("  base=\"/mnt/pve/$storage\"");
            sb.AppendLine("else");
            sb.AppendLine("  conf_path=\"$(pvesm config \"$storage\" 2>/dev/null | awk -F': ' '/^path: /{print $2}')\" || true");
            sb.AppendLine("  if [ -n \"$conf_path\" ] && [ -d \"$conf_path/images\" ]; then");
            sb.AppendLine("    base=\"$conf_path\"");
            sb.AppendLine("  fi");
            sb.AppendLine("fi");
            sb.AppendLine("[ -z \"$base\" ] && { echo \"ERR: cannot resolve path for storage '$storage'\" >&2; exit 2; }");
            sb.AppendLine();
            sb.AppendLine("dir=\"$base/images/$vmid\"");
            sb.AppendLine("[ -d \"$dir\" ] || { echo \"ERR: dir not found: $dir\" >&2; exit 3; }");
            sb.AppendLine("cd \"$dir\"");
            sb.AppendLine();
            sb.AppendLine("command -v qemu-img >/dev/null 2>&1 || { echo \"ERR: qemu-img missing\" >&2; exit 4; }");
            sb.AppendLine();

            // --- helper: get a clean format name for any image path (qcow2/raw/...) ---
            sb.AppendLine("norm_fmt() {");
            sb.AppendLine("  local f=\"$1\" fmt=\"\"");
            sb.AppendLine("  fmt=\"$(qemu-img info --output=json -- \"$f\" 2>/dev/null | tr -d '\\r' | tr -d '\\n' | sed -n 's/.*\\\"format\\\"[[:space:]]*:[[:space:]]*\\\"\\([^\\\"]*\\)\\\".*/\\1/p')\" || true");
            sb.AppendLine("  if [ -z \"$fmt\" ]; then");
            sb.AppendLine("    fmt=\"$(qemu-img info -- \"$f\" 2>/dev/null | sed -n 's/^file format:[[:space:]]*//p' | head -n1 | tr -d '\\r' | tr '\\n' ' ' | awk '{print $NF}')\"");
            sb.AppendLine("  fi");
            sb.AppendLine("  case \"$fmt\" in");
            sb.AppendLine("    qcow2|raw|vmdk|vdi|vpc) echo \"$fmt\" ;;");
            sb.AppendLine("    *) case \"$f\" in *.qcow2) echo qcow2 ;; *.raw) echo raw ;; *) echo qcow2 ;; esac ;;");
            sb.AppendLine("  esac");
            sb.AppendLine("}");
            sb.AppendLine();

            // --- helper: safe rebase with normalized formats ---
            sb.AppendLine("rebase_safe() {");
            sb.AppendLine("  local img=\"$1\" base=\"$2\"");
            sb.AppendLine("  local topfmt bfmt");
            sb.AppendLine("  topfmt=\"$(norm_fmt \"$img\")\"");
            sb.AppendLine("  bfmt=\"$(norm_fmt \"$base\")\"");
            sb.AppendLine("  qemu-img rebase -u -f \"$topfmt\" -F \"$bfmt\" -b \"$base\" -- \"$img\"");
            sb.AppendLine("  echo \"REB: $img -> $base (f=$topfmt,F=$bfmt)\"");
            sb.AppendLine("}");
            sb.AppendLine();

            // --- if any active vm-<vmid>-disk-*.qcow2 is a symlink, replace with real overlay pointing at its current base ---
            sb.AppendLine("ensure_overlay_is_file() {");
            sb.AppendLine("  local img=\"$1\"");
            sb.AppendLine("  if [ -L \"$img\" ]; then");
            sb.AppendLine("    local base bfmt");
            sb.AppendLine("    base=\"$(qemu-img info --output=json -- \"$img\" 2>/dev/null | tr -d '\\n' | sed -n 's/.*\\\"backing-filename\\\"[[:space:]]*:[[:space:]]*\\\"\\([^\\\"]*\\)\\\".*/\\1/p')\"");
            sb.AppendLine("    [ -z \"$base\" ] && base=\"$(qemu-img info -- \"$img\" 2>/dev/null | sed -n 's/^backing file:[[:space:]]*//p' | head -n1)\"");
            sb.AppendLine("    base=\"${base#./}\"");
            sb.AppendLine("    base=\"$(readlink -f -- \"$base\" 2>/dev/null || echo \"$base\")\"");
            sb.AppendLine("    bfmt=\"$(norm_fmt \"$base\")\"");
            sb.AppendLine("    rm -f -- \"$img\"");
            sb.AppendLine("    qemu-img create -f qcow2 -o backing_file=\"$base\",backing_fmt=\"$bfmt\" -- \"$img\"");
            sb.AppendLine("    echo \"CREATED overlay $img -> $base (F=$bfmt)\"");
            sb.AppendLine("  fi");
            sb.AppendLine("}");
            sb.AppendLine();

            // --- optional: strip qcow2 dirty bitmaps from active overlays (safe; metadata-only) ---
            sb.AppendLine("cleanup_bitmaps() {");
            sb.AppendLine("  for f in vm-$vmid-disk-*.qcow2; do");
            sb.AppendLine("    [ -e \"$f\" ] || continue");
            sb.AppendLine("    for b in $(qemu-img info \"$f\" 2>/dev/null | awk '/bitmaps:/{p=1;next} p && /name:/{print $2}' | tr -d ','); do");
            sb.AppendLine("      qemu-img bitmap --remove \"$f\" \"$b\" || true");
            sb.AppendLine("      echo \"Removed bitmap $b from $f\"");
            sb.AppendLine("    done");
            sb.AppendLine("  done");
            sb.AppendLine("}");
            sb.AppendLine();

            // --- core fixer for one image: repair missing/moved backing file and rebase with proper -F ---
            sb.AppendLine("fix_one() {");
            sb.AppendLine("  local img=\"$1\" backing base_b repl disknum cand");
            sb.AppendLine("  backing=\"$(qemu-img info --output=json -- \"$img\" | tr -d '\\n' | sed -n 's/.*\\\"backing-filename\\\"[[:space:]]*:[[:space:]]*\\\"\\([^\\\"]*\\)\\\".*/\\1/p')\" || true");
            sb.AppendLine("  [ -z \"$backing\" ] && return 0"); // no backing
            sb.AppendLine("  backing=\"${backing#./}\"");
            sb.AppendLine("  base_b=\"$(basename -- \"$backing\")\"");
            sb.AppendLine("  [ -e \"$base_b\" ] && return 0"); // exists → ok
            sb.AppendLine();
            sb.AppendLine("  # try same disk index with current vmid");
            sb.AppendLine("  repl=\"$(echo \"$base_b\" | sed -E 's/vm-[0-9]+-/vm-'" + "\"$vmid\"" + "'-/g')\" || true");
            sb.AppendLine("  if [ -n \"$repl\" ] && [ -e \"$repl\" ]; then");
            sb.AppendLine("    rebase_safe \"$img\" \"$repl\"");
            sb.AppendLine("    return 0");
            sb.AppendLine("  fi");
            sb.AppendLine();
            sb.AppendLine("  # try match by -disk-N");
            sb.AppendLine("  disknum=\"$(echo \"$base_b\" | sed -n 's/.*-disk-\\([0-9]\\+\\)\\.qcow2/\\1/p')\" || true");
            sb.AppendLine("  cand=\"\"");
            sb.AppendLine("  if [ -n \"$disknum\" ]; then");
            sb.AppendLine("    cand=\"$(ls -1 snap-*-vm-*-disk-\"$disknum\".qcow2 2>/dev/null | head -n1)\" || true");
            sb.AppendLine("  fi");
            sb.AppendLine("  [ -z \"$cand\" ] && cand=\"$(ls -1 snap-*.qcow2 2>/dev/null | head -n1)\" || true");
            sb.AppendLine("  if [ -n \"$cand\" ] && [ -e \"$cand\" ]; then");
            sb.AppendLine("    rebase_safe \"$img\" \"$cand\"");
            sb.AppendLine("    return 0");
            sb.AppendLine("  fi");
            sb.AppendLine();
            sb.AppendLine("  echo \"WARN: could not repair backing for $img (missing '$base_b')\" >&2");
            sb.AppendLine("  return 0");
            sb.AppendLine("}");
            sb.AppendLine();

            // --- run: make sure top overlays are real files, clean bitmaps, then fix any missing backings ---
            sb.AppendLine("shopt -s nullglob");
            sb.AppendLine("for t in vm-$vmid-disk-*.qcow2; do ensure_overlay_is_file \"$t\"; done");
            sb.AppendLine("cleanup_bitmaps || true");
            sb.AppendLine("for q in *.qcow2; do fix_one \"$q\"; done");
            sb.AppendLine("echo \"OK: chain repair attempted in $dir\"");

            var script = sb.ToString();

            try
            {
                using var ssh = new Renci.SshNet.SshClient(host.HostAddress, sshUser, sshPass);
                ssh.Connect();

                var eof = "EOF_" + Guid.NewGuid().ToString("N");
                var cmdText = "cat <<'" + eof + "' | tr -d '\\r' | bash\n" + script + "\n" + eof + "\n";

                using var cmd = ssh.CreateCommand(cmdText);
                cmd.CommandTimeout = TimeSpan.FromMinutes(5);
                var output = cmd.Execute();
                var rc = cmd.ExitStatus;
                var err = cmd.Error;

                ssh.Disconnect();

                _logger.LogInformation("RepairExternalSnapshotChainAsync on {Host}: rc={RC}\n{Out}\n{Err}",
                    host.HostAddress, rc, (output ?? "").Trim(), (err ?? "").Trim());

                return rc == 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Snapshot chain repair failed on node {Node}", nodeName);
                return false;
            }
        }


    }

}
