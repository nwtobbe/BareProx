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
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Linq;                    // for OrderBy/Select
using System.Security.Cryptography;   // for SHA1
using Renci.SshNet;
using BareProx.Services.Proxmox.Authentication;
using BareProx.Services.Proxmox.Helpers;

namespace BareProx.Services
{
    public class ProxmoxService
    {
        private readonly ApplicationDbContext _context;
        private readonly INetappService _netappService;
        private readonly IEncryptionService _encryptionService;
        private readonly INetappVolumeService _netappVolumeService;
        private readonly ILogger<RestoreService> _logger;
        private readonly IProxmoxHelpers _proxmoxHelpers;
        private readonly IProxmoxAuthenticator _proxmoxAuthenticator;

        public ProxmoxService(
            ApplicationDbContext context,
            INetappService netappService,
            IEncryptionService encryptionService,
            INetappVolumeService netappVolumeService,
            ILogger<RestoreService> logger,
            IProxmoxHelpers proxmoxHelpers,
            IProxmoxAuthenticator proxmoxAuthenticator)
        {
            _context = context;
            _netappService = netappService;
            _encryptionService = encryptionService;
            _netappVolumeService = netappVolumeService;
            _logger = logger;
            _proxmoxHelpers = proxmoxHelpers;
            _proxmoxAuthenticator = proxmoxAuthenticator;
        }


        /// <summary>
        /// Sends a request and retries once if a 401 is returned, refreshing the API token.
        /// </summary>
        private async Task<HttpResponseMessage> SendWithRefreshAsync(
        ProxmoxCluster cluster,
        HttpMethod method,
        string url,
        HttpContent content = null,
        CancellationToken ct = default)
        {
            try
            {
                var client = await _proxmoxAuthenticator.GetAuthenticatedClientAsync(cluster, ct);

                // Buffer the request content so we can log it
                string requestBody = null;
                if (content != null)
                {
                    requestBody = await content.ReadAsStringAsync(ct);
                }

                var request = new HttpRequestMessage(method, url)
                {
                    Content = content
                };

                _logger.LogDebug(
                    "▶ Proxmox {Method} {Url}\nPayload:\n{Payload}",
                    method, url, requestBody ?? "<no content>");

                // first attempt
                var response = await client.SendAsync(request, ct);

                // capture the response body
                var responseBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogDebug(
                    "◀ Proxmox {StatusCode} {ReasonPhrase}\nBody:\n{Body}",
                    (int)response.StatusCode, response.ReasonPhrase, responseBody);

                // retry on 401
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _logger.LogInformation("Proxmox auth expired, re-authenticating…");
                    var reauth = await _proxmoxAuthenticator.AuthenticateAndStoreTokenCAsync(cluster, ct);
                    if (!reauth)
                        throw new ServiceUnavailableException("Authentication failed: missing token or CSRF.");

                    // reload fresh tokens
                    await _context.Entry(cluster).ReloadAsync(ct);
                    client = await _proxmoxAuthenticator.GetAuthenticatedClientAsync(cluster, ct);

                    // rebuild request (and re-buffer)
                    request = new HttpRequestMessage(method, url);
                    if (requestBody != null)
                        request.Content = new StringContent(requestBody, Encoding.UTF8, content.Headers.ContentType.MediaType);

                    response = await client.SendAsync(request, ct);
                    responseBody = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogDebug(
                        "◀ (retry) Proxmox {StatusCode} {ReasonPhrase}\nBody:\n{Body}",
                        (int)response.StatusCode, response.ReasonPhrase, responseBody);
                }

                response.EnsureSuccessStatusCode();
                return response;
            }
            catch (HttpRequestException ex)
            {
                // socket/timeout/etc
                var host = _proxmoxHelpers.GetQueryableHosts(cluster).FirstOrDefault()?.HostAddress ?? "unknown";
                _ = _context.ProxmoxClusters
                    .Where(c => c.Id == cluster.Id)
                    .ExecuteUpdateAsync(b => b
                        .SetProperty(c => c.LastStatus, _ => $"Unreachable: {ex.Message}")
                        .SetProperty(c => c.LastChecked, _ => DateTime.UtcNow),
                    ct);

                throw new ServiceUnavailableException(
                    $"Cannot reach Proxmox host at {host}:8006. {ex.Message}", ex);
            }
        }




        public async Task<bool> CheckIfVmExistsAsync(ProxmoxCluster cluster, ProxmoxHost host, int vmId, CancellationToken ct = default)
        {
            var url = $"https://{host.HostAddress}:8006/api2/json/nodes/{host.Hostname}/qemu/{vmId}/config";
            try
            {
                var resp = await SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }


        public async Task<Dictionary<string, List<ProxmoxVM>>> GetEligibleBackupStorageWithVMsAsync(
    ProxmoxCluster cluster,
    int netappControllerId,
    List<string>? onlyIncludeStorageNames = null,
    CancellationToken ct = default)
        {
            var storageVmMap = onlyIncludeStorageNames != null
                ? await GetVmsByStorageListAsync(cluster, onlyIncludeStorageNames, ct)
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
                var resp = await SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
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
                var vmListResp = await SendWithRefreshAsync(cluster, HttpMethod.Get, vmListUrl, null, ct);
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

            var resp = await SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
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
            var statusResp = await SendWithRefreshAsync(cluster, HttpMethod.Get, statusUrl, null, ct);
            var statusJson = await statusResp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(statusJson);
            var current = doc.RootElement.GetProperty("data").GetProperty("status").GetString();

            // Only suspend if it’s running
            if (string.Equals(current, "running", StringComparison.OrdinalIgnoreCase))
            {
                var url = $"https://{hostaddress}:8006/api2/json/nodes/{host}/qemu/{vmId}/status/suspend";
                await SendWithRefreshAsync(cluster, HttpMethod.Post, url, null, ct);
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
            var statusResp = await SendWithRefreshAsync(cluster, HttpMethod.Get, statusUrl, null, ct);
            var statusJson = await statusResp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(statusJson);
            var current = doc.RootElement.GetProperty("data").GetProperty("status").GetString();

            // Only resume if it’s paused
            if (string.Equals(current, "paused", StringComparison.OrdinalIgnoreCase))
            {
                var url = $"https://{hostaddress}:8006/api2/json/nodes/{host}/qemu/{vmId}/status/resume";
                await SendWithRefreshAsync(cluster, HttpMethod.Post, url, null, ct);
            }
        }

        public async Task<string?> CreateSnapshotAsync(
            ProxmoxCluster cluster,
            string node,
            string hostAddress,
            int vmid,
            string snapshotName,
            string description,
            bool withMemory,
            bool dontTrySuspend,
            CancellationToken ct = default)
        {
            var client = await _proxmoxAuthenticator.GetAuthenticatedClientAsync(cluster, ct);

            var url = $"https://{hostAddress}:8006/api2/json/nodes/{node}/qemu/{vmid}/snapshot";

            var data = new Dictionary<string, string>
            {
                ["snapname"] = snapshotName,
                ["description"] = description,
                ["vmstate"] = withMemory ? "1" : "0"
            };

            var content = new FormUrlEncodedContent(data);
            var response = await client.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var upid = doc.RootElement
                          .GetProperty("data")
                          .GetString();

            return upid;
        }

        public async Task<bool> WaitForTaskCompletionAsync(
     ProxmoxCluster cluster,
     string node,
     string hostAddress,
     string upid,
     TimeSpan timeout,
     ILogger logger,
     CancellationToken ct = default)
        {
            var url = $"https://{hostAddress}:8006/api2/json/nodes/{node}/tasks/{Uri.EscapeDataString(upid)}/status";
            var start = DateTime.UtcNow;

            while (DateTime.UtcNow - start < timeout)
            {
                try
                {
                    var resp = await SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
                    resp.EnsureSuccessStatusCode();

                    var json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    var data = doc.RootElement.GetProperty("data");

                    var status = data.GetProperty("status").GetString();
                    if (status == "stopped")
                    {
                        var exit = data.GetProperty("exitstatus").GetString();
                        if (exit == "OK")
                        {
                            logger.LogInformation("Snapshot task {Upid} completed successfully.", upid);
                            return true;
                        }

                        logger.LogWarning("Snapshot task {Upid} failed with exitstatus: {Exit}", upid, exit);
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to check task status for UPID: {Upid}", upid);
                }

                await Task.Delay(TimeSpan.FromSeconds(5));
            }

            logger.LogWarning("Timeout waiting for snapshot task {Upid}", upid);
            return false;
        }

        public async Task<string?> GetVmStatusAsync(ProxmoxCluster cluster, string node, string hostAddress, int vmid, CancellationToken ct = default)
        {
            var client = await _proxmoxAuthenticator.GetAuthenticatedClientAsync(cluster, ct);
            var url = $"https://{hostAddress}:8006/api2/json/nodes/{node}/qemu/{vmid}/status/current";

            var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement
                      .GetProperty("data")
                      .GetProperty("status")
                      .GetString();
        }

        public async Task<List<ProxmoxSnapshotInfo>> GetSnapshotListAsync(
            ProxmoxCluster cluster,
            string node,
            string hostAddress,
            int vmid, 
            CancellationToken ct = default)
        {
            var client = await _proxmoxAuthenticator.GetAuthenticatedClientAsync(cluster, ct);
            var url = $"https://{hostAddress}:8006/api2/json/nodes/{node}/qemu/{vmid}/snapshot";
            var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var dataProp) ||
                dataProp.ValueKind != JsonValueKind.Array)
            {
                return new List<ProxmoxSnapshotInfo>();
            }

            var list = new List<ProxmoxSnapshotInfo>();

            foreach (var snapshot in dataProp.EnumerateArray())
            {
                var name = snapshot.GetProperty("name").GetString() ?? "";

                int snaptime = 0;
                if (snapshot.TryGetProperty("snaptime", out var snaptimeProp))
                {
                    if (snaptimeProp.ValueKind == JsonValueKind.Number)
                        snaptime = snaptimeProp.GetInt32();
                    else if (snaptimeProp.ValueKind == JsonValueKind.String &&
                             int.TryParse(snaptimeProp.GetString(), out var parsedTime))
                        snaptime = parsedTime;
                }

                int vmstate = 0;
                if (snapshot.TryGetProperty("vmstate", out var vmstateProp) &&
                    vmstateProp.ValueKind == JsonValueKind.Number)
                {
                    vmstate = vmstateProp.GetInt32();
                }

                list.Add(new ProxmoxSnapshotInfo
                {
                    Name = name,
                    Snaptime = snaptime,
                    Vmstate = vmstate
                });
            }

            return list;
        }

        public async Task DeleteSnapshotAsync(
            ProxmoxCluster cluster,
            string host,
            string hostaddress,
            int vmId,
            string snapshotName,
            CancellationToken ct = default)
        {
            var url = $"https://{hostaddress}:8006/api2/json/nodes/{host}/qemu/{vmId}/snapshot/{snapshotName}";
            await SendWithRefreshAsync(cluster, HttpMethod.Delete, url, null, ct);
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
            var listResp = await SendWithRefreshAsync(cluster, HttpMethod.Get, listUrl, null, ct);
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
                var cfgResp = await SendWithRefreshAsync(cluster, HttpMethod.Get, cfgUrl, null, ct);
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

        public async Task<bool> RestoreVmFromConfigAsync(
        string originalConfigJson,
        string hostAddress,
        string newVmName,
        string cloneStorageName,
        int controllerId,
        bool startDisconnected,
        CancellationToken ct = default)
        {
            using var rootDoc = JsonDocument.Parse(originalConfigJson);
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
            var idResp = await SendWithRefreshAsync(cluster, HttpMethod.Get, nextIdUrl, null, ct);
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


            payload["name"] = newVmName;
            payload["vmid"] = vmid;
            payload.Remove("meta");
            payload.Remove("digest");
            payload["protection"] = "0";

            if (startDisconnected)
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

            var oldVmid = ExtractOldVmidFromConfig(payload);
            if (string.IsNullOrEmpty(oldVmid))
                throw new InvalidOperationException("Failed to determine oldVmid from config.");

            await _netappService.MoveAndRenameAllVmFilesAsync(cloneStorageName, controllerId, oldVmid, vmid);
            UpdateDiskPathsInConfig(payload, oldVmid, vmid, cloneStorageName);

            // --- Global remap of old storage name to new storage name in all values of payload ---
            foreach (var key in payload.Keys.ToList())
            {
                var val = payload[key];
                if (!string.IsNullOrEmpty(val))
                {
                    val = val.Replace(oldStorageName, cloneStorageName, StringComparison.OrdinalIgnoreCase);
                    payload[key] = val;
                }
            }

            // Also explicitly remap vmstate if present (usually handled above, but just in case)
            if (payload.ContainsKey("vmstate"))
            {
                payload["vmstate"] = RemapStorageAndVmid(payload["vmstate"], oldStorageName, cloneStorageName, oldVmid, vmid);
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
                    // Write snapshot section header WITHOUT "snapshot_" prefix
                    sb.AppendLine($"[{snapProp.Name}]");

                    // Flatten snapshot dictionary
                    var snapDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var snapLine in snapProp.Value.EnumerateObject())
                    {
                        snapDict[snapLine.Name] = snapLine.Value.GetString() ?? "";
                    }

                    if (startDisconnected)
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

                    // Remap disk paths inside snapshot (keys like scsi0, virtio1, etc.)
                    UpdateDiskPathsInConfig(snapDict, oldVmid, vmid, cloneStorageName);

                    // Global remap for all snapshot values: old storage → new storage
                    foreach (var key in snapDict.Keys.ToList())
                    {
                        var val = snapDict[key];
                        if (!string.IsNullOrEmpty(val))
                        {
                            snapDict[key] = val.Replace(oldStorageName, cloneStorageName, StringComparison.OrdinalIgnoreCase);
                        }
                    }

                    // Explicitly remap vmstate value (may contain storage & vmid)
                    if (snapDict.TryGetValue("vmstate", out var vmstateValue))
                    {
                        snapDict["vmstate"] = RemapStorageAndVmid(vmstateValue, oldStorageName, cloneStorageName, oldVmid, vmid);
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
                        return false; // or throw exception if preferred
                    }
                }
                ssh.Disconnect();
            }

            return true;

        }


        string RemapStorageAndVmid(string input, string oldStorage, string newStorage, string oldVmid, string newVmid)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            // Replace old storage name
            var updated = input.Replace(oldStorage, newStorage, StringComparison.OrdinalIgnoreCase);

            // Replace old VMID in paths — both in directory and filename
            updated = Regex.Replace(updated, $@"(?<=[:/]){oldVmid}(?=[/\\])", newVmid);
            updated = Regex.Replace(updated, $@"vm-{oldVmid}-", $"vm-{newVmid}-");

            return updated;
        }

        private string ExtractOldVmidFromConfig(Dictionary<string, string> payload)
        {
            var diskRegex = new Regex(@"^(scsi|virtio|ide|sata|efidisk|tpmstate)\d+$", RegexOptions.IgnoreCase);
            foreach (var diskKey in payload.Keys.Where(k => diskRegex.IsMatch(k)))
            {
                var diskVal = payload[diskKey]?.Trim() ?? "";
                if (string.IsNullOrEmpty(diskVal))
                    continue;

                if (!diskVal.Contains(":"))
                    continue;

                var parts = diskVal.Split(new[] { ':' }, 2);
                if (parts.Length < 2)
                    continue;

                var diskPath = parts[1].Trim(); // e.g. "111/vm-111-disk-0.qcow2,iothread=1,size=32G"

                // Look for pattern "/{vmid}/vm-{vmid}-"
                var match = Regex.Match(diskPath, @"(\d+)/vm-(\d+)-");
                if (match.Success)
                {
                    var vmid = match.Groups[2].Value.Trim();
                    if (!string.IsNullOrEmpty(vmid))
                        return vmid;
                }

                // Fallback: look for just "vm-{vmid}-" in the string
                match = Regex.Match(diskPath, @"vm-(\d+)-");
                if (match.Success)
                {
                    var vmid = match.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(vmid))
                        return vmid;
                }
            }
            throw new Exception("Could not determine old VMID from disk configuration.");
        }

        private void UpdateDiskPathsInConfig(
           Dictionary<string, string> payload,
           string oldVmid,
           string newVmid,
           string cloneStorageName)
        {
            var diskRegex = new Regex(@"^(scsi|virtio|sata|ide|efidisk|tpmstate)\d+$", RegexOptions.IgnoreCase);

            var diskKeys = payload.Keys
                .Where(k => diskRegex.IsMatch(k))
                .ToList();

            foreach (var diskKey in diskKeys)
            {
                var diskVal = payload[diskKey];
                if (!diskVal.Contains(":"))
                    continue;

                var parts = diskVal.Split(new[] { ':' }, 2);
                if (parts.Length < 2)
                    continue;

                var diskDef = parts[1];
                var sub = diskDef.Split(new[] { ',' }, 2);

                var pathWithFilename = sub[0]; // e.g. "101/vm-101-disk-0.qcow2"
                var options = sub.Length > 1 ? "," + sub[1] : string.Empty;

                // ✅ Skip pure CD-ROM (media=cdrom but NOT cloudinit)
                if (options.Contains("media=cdrom", StringComparison.OrdinalIgnoreCase) &&
                    !options.Contains("cloudinit", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Update both folder and filename
                var newPathWithFilename = pathWithFilename
                    .Replace($"{oldVmid}/", $"{newVmid}/")
                    .Replace($"vm-{oldVmid}-", $"vm-{newVmid}-");

                // Handle case with no slashes (old VMID is just in filename)
                if (!newPathWithFilename.Contains($"/{newVmid}/"))
                    newPathWithFilename = newPathWithFilename.Replace($"vm-{oldVmid}-", $"vm-{newVmid}-");

                payload[diskKey] = $"{cloneStorageName}:{newPathWithFilename}{options}";
            }
        }

        public async Task<bool> RestoreVmFromConfigWithOriginalIdAsync(
    string originalConfigJson,
    string hostAddress,
    int originalVmId,
    string cloneStorageName,
    bool startDisconnected,
    CancellationToken ct = default)
        {
            using var rootDoc = JsonDocument.Parse(originalConfigJson);
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

            var vmid = originalVmId.ToString();

            var payload = _proxmoxHelpers.FlattenConfig(config);

            // Disconnect NICs if requested (main config)
            if (startDisconnected)
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

            payload["vmid"] = vmid;
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

                    // Disconnect NICs if requested (snapshots)
                    if (startDisconnected)
                        if (startDisconnected)
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

                    // Remap disk paths in snapshot
                    var snapDiskKeys = snapDict.Keys.Where(k => Regex.IsMatch(k, @"^(scsi|virtio|ide|sata|efidisk|tpmstate)\d+$", RegexOptions.IgnoreCase)).ToList();
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

            return true;
        }

         public async Task<bool> MountNfsStorageViaApiAsync(
    ProxmoxCluster cluster,
    string node,
    string storageName,
    string serverIp,
    string exportPath,
    string content = "images,backup,iso,vztmpl",
    string options = "vers=3",
    CancellationToken ct = default)
        {
            var nodeHost = _proxmoxHelpers.GetQueryableHosts(cluster).FirstOrDefault(h => h.Hostname == node)?.HostAddress ?? "";
            if (string.IsNullOrEmpty(nodeHost)) return false;

            var url = $"https://{nodeHost}:8006/api2/json/storage";

            var payload = new Dictionary<string, string>
            {
                ["type"] = "nfs",
                ["storage"] = storageName,
                ["server"] = serverIp,
                ["export"] = exportPath,
                ["content"] = content,
                ["options"] = options
            };

            var contentBody = new FormUrlEncodedContent(payload);

            try
            {
                var resp = await SendWithRefreshAsync(cluster, HttpMethod.Post, url, contentBody, ct);
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }
        // Shutdown VM
        public async Task ShutdownAndRemoveVmAsync(ProxmoxCluster cluster, string nodeName, int vmId, CancellationToken ct = default)
        {
            var host = _proxmoxHelpers.GetQueryableHosts(cluster).First(h => h.Hostname == nodeName);
            var baseApiUrl = $"https://{host.HostAddress}:8006/api2/json/nodes/{nodeName}/qemu/{vmId}";

            // 1) Get current VM status
            var statusUrl = $"{baseApiUrl}/status/current";
            var statusResp = await SendWithRefreshAsync(cluster, HttpMethod.Get, statusUrl, null, ct);
            var statusJson = await statusResp.Content.ReadAsStringAsync(ct);
            using var statusDoc = JsonDocument.Parse(statusJson);
            var status = statusDoc.RootElement.GetProperty("data").GetProperty("status").GetString();

            // 2) If running, shutdown and poll
            if (string.Equals(status, "running", StringComparison.OrdinalIgnoreCase))
            {
                var shutdownUrl = $"{baseApiUrl}/status/stop";
                await SendWithRefreshAsync(cluster, HttpMethod.Post, shutdownUrl, null, ct);

                var sw = Stopwatch.StartNew();
                var maxWait = TimeSpan.FromMinutes(5);
                while (sw.Elapsed < maxWait)
                {
                    var pollResp = await SendWithRefreshAsync(cluster, HttpMethod.Get, statusUrl, null, ct);
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
            await SendWithRefreshAsync(cluster, HttpMethod.Delete, deleteUrl, null, ct);
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
                var resp = await SendWithRefreshAsync(cluster, HttpMethod.Delete, url, null, ct);
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
            var apiResp = await SendWithRefreshAsync(cluster, HttpMethod.Delete, deleteUrl, null, ct);
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


        public async Task<Dictionary<string, List<ProxmoxVM>>> GetVmsByStorageListAsync(
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
                    var resp = await SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
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
                    var resp = await SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);

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

    }


}
