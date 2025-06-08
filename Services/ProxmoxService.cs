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
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace BareProx.Services
{
    public class ProxmoxService
    {
        private readonly ApplicationDbContext _context;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly INetappService _netappService;
        private readonly IEncryptionService _encryptionService;

        public ProxmoxService(
            ApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            INetappService netappService,
            IEncryptionService encryptionService)
        {
            _context = context;
            _httpClientFactory = httpClientFactory;
            _netappService = netappService;
            _encryptionService = encryptionService;
        }

        public async Task<bool> AuthenticateAndStoreTokenAsync(int clusterId, CancellationToken ct)
        {
            var cluster = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync(c => c.Id == clusterId, ct);

            if (cluster == null || !GetQueryableHosts(cluster).Any())
                return false;

            return await AuthenticateAndStoreTokenAsync(cluster, ct);
        }

        private async Task<bool> AuthenticateAndStoreTokenAsync(ProxmoxCluster cluster, CancellationToken ct)
        {
            var host = GetQueryableHosts(cluster).First();
            var url = $"https://{host.HostAddress}:8006/api2/json/access/ticket";

            var form = new Dictionary<string, string>
            {
                { "username", cluster.Username },
                { "password", _encryptionService.Decrypt(cluster.PasswordHash) }
            };

            var content = new FormUrlEncodedContent(form);
            var httpClient = _httpClientFactory.CreateClient("ProxmoxClient");

            try
            {
                var response = await httpClient.PostAsync(url, content, ct);
                if (!response.IsSuccessStatusCode) return false;

                var body = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                var data = doc.RootElement.GetProperty("data");

                cluster.ApiToken = _encryptionService.Encrypt(data.GetProperty("ticket").GetString());
                cluster.CsrfToken = _encryptionService.Encrypt(data.GetProperty("CSRFPreventionToken").GetString());
                cluster.LastChecked = DateTime.UtcNow;
                cluster.LastStatus = "Working";

                await _context.SaveChangesAsync(ct);
                return true;
            }
            catch (Exception ex)
            {
                cluster.LastStatus = $"Error: {ex.Message}";
                cluster.LastChecked = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);
                return false;
            }
        }

        private async Task<HttpClient> GetAuthenticatedClientAsync(ProxmoxCluster cluster, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(cluster.ApiToken) || string.IsNullOrEmpty(cluster.CsrfToken))
            {
                var ok = await AuthenticateAndStoreTokenAsync(cluster, ct);
                if (!ok)
                    throw new Exception("Authentication failed: missing token or CSRF.");

                // reload tokens
                await _context.Entry(cluster).ReloadAsync(ct);
            }

            var client = _httpClientFactory.CreateClient("ProxmoxClient");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("PVEAuthCookie", _encryptionService.Decrypt(cluster.ApiToken));

            client.DefaultRequestHeaders.Remove("CSRFPreventionToken");
            client.DefaultRequestHeaders.Add("CSRFPreventionToken", _encryptionService.Decrypt(cluster.CsrfToken));

            return client;
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
                var client = await GetAuthenticatedClientAsync(cluster, ct);
                var request = new HttpRequestMessage(method, url) { Content = content };
                var response = await client.SendAsync(request, ct);
                //string raw = await response.Content.ReadAsStringAsync();
                //Console.WriteLine($"Snapshot API response ({(int)response.StatusCode}): {raw}");
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // Re-authenticate and update tokens in DB
                    var reauth = await AuthenticateAndStoreTokenAsync(cluster, ct);
                    if (!reauth)
                        throw new ServiceUnavailableException("Authentication failed: missing token or CSRF.");

                    // reload tokens
                    await _context.Entry(cluster).ReloadAsync(ct);
                    client = await GetAuthenticatedClientAsync(cluster, ct);

                    // retry once
                    request = new HttpRequestMessage(method, url) { Content = content };
                    response = await client.SendAsync(request,ct);
                }

                response.EnsureSuccessStatusCode();
                return response;
            }
            catch (HttpRequestException ex)
            {
                // This catches socket errors, timeouts, connection refused, etc.
                var host = GetQueryableHosts(cluster).FirstOrDefault()?.HostAddress ?? "unknown";
                _ = _context.ProxmoxClusters
                    .Where(c => c.Id == cluster.Id)
                    .ExecuteUpdateAsync(b => b.SetProperty(c => c.LastStatus, _ => $"Unreachable: {ex.Message}")
                                             .SetProperty(c => c.LastChecked, _ => DateTime.UtcNow), ct);
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
            foreach (var host in GetQueryableHosts(cluster))
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
            var netappVolumes = await _netappService.GetVolumesWithMountInfoAsync(netappControllerId);

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
            foreach (var host in GetQueryableHosts(cluster))
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
                    var cfgData = cfgDoc.RootElement.GetProperty("data");

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
                        if (!Regex.IsMatch(prop.Name, @"^(scsi|sata|virtio|ide)\d+$", RegexOptions.IgnoreCase))
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




        public async Task<string> GetVmConfigAsync(
            ProxmoxCluster cluster,
            string host,
            int vmId,
            CancellationToken ct = default)
        {
            var hostAddress = GetQueryableHosts(cluster).First(h => h.Hostname == host).HostAddress;
            var url = $"https://{hostAddress}:8006/api2/json/nodes/{host}/qemu/{vmId}/config";

            var resp = await SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
            return await resp.Content.ReadAsStringAsync(ct);
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
            var client = await GetAuthenticatedClientAsync(cluster, ct);

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
            var client = await GetAuthenticatedClientAsync(cluster, ct);
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
            var client = await GetAuthenticatedClientAsync(cluster, ct);
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
            var host = GetHostByNodeName(cluster, nodeName);

            var hostAddress = host.HostAddress;
            var result = new List<ProxmoxVM>();

            // 2) List VMs on that node
            var listUrl = $"https://{hostAddress}:8006/api2/json/nodes/{nodeName}/qemu";
            var listResp = await SendWithRefreshAsync(cluster, HttpMethod.Get, listUrl, null, ct);
            using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync(ct));
            var vmArray = listDoc.RootElement.GetProperty("data").EnumerateArray();

            // regex to find disk lines: scsi0, virtio1, ide2, etc.
            var diskRegex = new Regex(@"^(scsi|virtio|sata|ide)\d+$", RegexOptions.IgnoreCase);

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

                    var val = prop.Value.GetString()!;
                    // e.g. "restore_123_202505…:vm-…-disk-0.qcow2,…"
                    if (val.StartsWith($"{storageNameFilter}:", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(new ProxmoxVM
                        {
                            Id = vmid,
                            Name = name,
                            HostName = nodeName,
                            HostAddress = hostAddress
                        });
                        break; // once one disk matches, no need to check more disks
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
            int ControllerId,
            bool startDisconnected,
            CancellationToken ct = default)
        {
            // 1) Parse and validate input JSON
            using var rootDoc = JsonDocument.Parse(originalConfigJson);
            if (!rootDoc.RootElement.TryGetProperty("data", out var config))
                return false;

            // 2) Lookup host
            var host = await _context.ProxmoxHosts
                .FirstOrDefaultAsync(h => h.HostAddress == hostAddress, ct);
            if (host == null || string.IsNullOrWhiteSpace(host.Hostname))
                return false;
            var nodeName = host.Hostname;

            // 3) Lookup cluster (for auth context)
            var cluster = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync(ct);
            if (cluster == null || !GetQueryableHosts(cluster).Any())
                return false;

            // 4) Get next free VMID
            var nextIdUrl = $"https://{hostAddress}:8006/api2/json/cluster/nextid";
            var idResp = await SendWithRefreshAsync(cluster, HttpMethod.Get, nextIdUrl, null, ct);
            var idJson = await idResp.Content.ReadAsStringAsync(ct);
            using var idDoc = JsonDocument.Parse(idJson);
            var vmid = idDoc.RootElement.GetProperty("data").GetString();
            if (string.IsNullOrEmpty(vmid))
                return false;

            // 5) Flatten existing config into form fields
            var payload = FlattenConfig(config);

            // 6) Apply our overrides
            payload["name"] = newVmName;
            payload["vmid"] = vmid;
            payload.Remove("meta");
            payload.Remove("digest");
            payload["protection"] = "0";

            // 7) Handle disconnected start
            if (startDisconnected)
            {
                // find any key like "net0", "net1", "net2", etc.
                var nets = payload.Keys
                    .Where(k => Regex.IsMatch(k, @"^net\d+$", RegexOptions.IgnoreCase))
                    .ToList();

                foreach (var netKey in nets)
                    payload.Remove(netKey);
            }

            // 8) Remap **all** disks to our clone storage

            // 8a) Extract old VMID from config (uses payload)
            string oldVmid = ExtractOldVmidFromConfig(payload);

            // 8b) Rename all files/folders in storage
            await _netappService.MoveAndRenameAllVmFilesAsync(
                volumeName: cloneStorageName,
                controllerId: ControllerId,
                oldvmid: oldVmid,
                newvmid: vmid);

            // 8c) Update disk paths in config payload
            UpdateDiskPathsInConfig(payload, oldVmid, vmid, cloneStorageName);

            // 9) Ensure a storage parameter is present
            if (!payload.ContainsKey("storage"))
            {
                payload["storage"] = cloneStorageName;
            }

            // 10) POST to create the new VM
            var url = $"https://{hostAddress}:8006/api2/json/nodes/{nodeName}/qemu";
            var content = new FormUrlEncodedContent(payload);

            try
            {
                var resp = await SendWithRefreshAsync(cluster, HttpMethod.Post, url, content, ct);
                var respBody = await resp.Content.ReadAsStringAsync(ct);
                Console.WriteLine($"RESTORE VM HTTP {(int)resp.StatusCode}: {respBody}");
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error restoring VM: {ex}");
                return false;
            }

        }

        private string ExtractOldVmidFromConfig(Dictionary<string, string> payload)
        {
            var diskRegex = new Regex(@"^(scsi|virtio|sata|ide)\d+$", RegexOptions.IgnoreCase);
            foreach (var diskKey in payload.Keys.Where(k => diskRegex.IsMatch(k)))
            {
                var diskVal = payload[diskKey];
                if (!diskVal.Contains(":"))
                    continue;

                var parts = diskVal.Split(new[] { ':' }, 2);
                if (parts.Length < 2)
                    continue;

                var diskPath = parts[1]; // e.g. "101/vm-101-disk-0.qcow2,discard=on,size=32G,ssd=1"
                                         // Look for pattern /{vmid}/vm-{vmid}- or just vm-{vmid}- in the filename
                var match = Regex.Match(diskPath, @"(\d+)/vm-(\d+)-");
                if (match.Success)
                    return match.Groups[2].Value; // VMID from "vm-101-disk-0.qcow2"
                                                  // fallback: look for just vm-{vmid}- in the string
                match = Regex.Match(diskPath, @"vm-(\d+)-");
                if (match.Success)
                    return match.Groups[1].Value;
            }
            throw new Exception("Could not determine old VMID from disk configuration.");
        }
        private void UpdateDiskPathsInConfig(
           Dictionary<string, string> payload,
           string oldVmid,
           string newVmid,
           string cloneStorageName)
        {
            var diskRegex = new Regex(@"^(scsi|virtio|sata|ide)\d+$", RegexOptions.IgnoreCase);

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
    string storageName,
    bool startDisconnected,
    CancellationToken ct = default)
        {
            // 1) Parse and validate input JSON
            using var rootDoc = JsonDocument.Parse(originalConfigJson);
            if (!rootDoc.RootElement.TryGetProperty("data", out var config))
                return false;

            // 2) Lookup host
            var host = await _context.ProxmoxHosts
                .FirstOrDefaultAsync(h => h.HostAddress == hostAddress, ct);
            if (host == null || string.IsNullOrWhiteSpace(host.Hostname))
                return false;
            var nodeName = host.Hostname;

            // 3) Lookup cluster (for auth context)
            var cluster = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync(ct);
            if (cluster == null || !GetQueryableHosts(cluster).Any())
                return false;

            // 4) Use the original VMID directly
            var vmid = originalVmId.ToString();

            // 5) Flatten existing config into form fields
            var payload = FlattenConfig(config);

            // 6) Set overrides explicitly
            payload["vmid"] = vmid;
            payload.Remove("meta");
            payload.Remove("digest");
            payload["protection"] = "0"; // explicitly disable protection

            // 7) Handle disconnected start (remove network interfaces)
            if (startDisconnected)
            {
                var nets = payload.Keys
                    .Where(k => Regex.IsMatch(k, @"^net\d+$", RegexOptions.IgnoreCase))
                    .ToList();
                foreach (var netKey in nets)
                    payload.Remove(netKey);
            }

            // 8) Remap all disks to the specified storage
            var diskRegex = new Regex(@"^(scsi|virtio|sata|ide)\d+$", RegexOptions.IgnoreCase);
            var diskKeys = payload.Keys.Where(k => diskRegex.IsMatch(k)).ToList();

            foreach (var diskKey in diskKeys)
            {
                var diskVal = payload[diskKey];
                if (!diskVal.Contains(":"))
                    continue;

                var parts = diskVal.Split(new[] { ':' }, 2);
                var diskDef = parts[1]; // "vm-100-disk-0.qcow2,discard=on,iothread=1"
                var sub = diskDef.Split(new[] { ',' }, 2);

                var filenameWithExt = sub[0]; // "vm-100-disk-0.qcow2"
                var options = sub.Length > 1 ? "," + sub[1] : string.Empty;

                payload[diskKey] = $"{storageName}:{filenameWithExt}{options}";
            }

            // 9) Ensure a storage parameter is present
            if (!payload.ContainsKey("storage"))
                payload["storage"] = storageName;

            // 10) POST request to recreate VM with original VMID
            var url = $"https://{hostAddress}:8006/api2/json/nodes/{nodeName}/qemu";
            var content = new FormUrlEncodedContent(payload);

            try
            {
                var resp = await SendWithRefreshAsync(cluster, HttpMethod.Post, url, content, ct);
                var respBody = await resp.Content.ReadAsStringAsync(ct);
                Console.WriteLine($"RESTORE VM (Replace) HTTP {(int)resp.StatusCode}: {respBody}");
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error replacing VM: {ex}");
                return false;
            }
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
            var nodeHost = GetQueryableHosts(cluster).FirstOrDefault(h => h.Hostname == node)?.HostAddress ?? "";
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
            var host = GetQueryableHosts(cluster).First(h => h.Hostname == nodeName);
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
            var deleteUrl = baseApiUrl;
            await SendWithRefreshAsync(cluster, HttpMethod.Delete, deleteUrl, null, ct);
        }

        /// <summary>
        /// Unmounts (deletes) an NFS storage entry from a Proxmox node.
        /// </summary>
        public async Task<bool> UnmountNfsStorageViaApiAsync(
            ProxmoxCluster cluster,
            string nodeName,
            string storageName, 
            CancellationToken ct = default)
        {
            // Find the host entry for this node
            var host = GetHostByNodeName(cluster, nodeName);

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

        public async Task<Dictionary<string, List<ProxmoxVM>>> GetVmsByStorageListAsync(
    ProxmoxCluster cluster,
    List<string> storageNames,
    CancellationToken ct = default)
        {
            var result = new Dictionary<string, List<ProxmoxVM>>();

            foreach (var storage in storageNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                result[storage] = new List<ProxmoxVM>();

                foreach (var host in GetQueryableHosts(cluster))
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

            foreach (var host in GetQueryableHosts(cluster))
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


        // --- Helper Functions ---

        private static Dictionary<string, string> FlattenConfig(JsonElement config)
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

        private static ProxmoxHost GetHostByNodeName(ProxmoxCluster cluster, string nodeName)
        {
            var host = cluster.Hosts.FirstOrDefault(h => h.Hostname == nodeName);
            if (host == null)
                throw new InvalidOperationException($"Node '{nodeName}' not found in cluster.");
            return host;
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

        /// <summary>
        /// Returns only the hosts that were last marked online. If none have been checked yet,
        /// or all are offline, returns the full list so we can still attempt a first‐time call.
        /// </summary>
        private IEnumerable<ProxmoxHost> GetQueryableHosts(ProxmoxCluster cluster)
        {
            var up = cluster.Hosts.Where(h => h.IsOnline == true).ToList();
            return up.Any() ? up : cluster.Hosts;
        }


    }


}
