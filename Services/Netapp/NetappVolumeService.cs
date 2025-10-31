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

using System.Net.Http;
using System.Text;
using System.Text.Json;
using BareProx.Data;
using BareProx.Models;
using BareProx.Services.Netapp;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Services
{
    public class NetappVolumeService : INetappVolumeService
    {
        private readonly ApplicationDbContext _context;
        private readonly INetappAuthService _authService;
        private readonly ILogger<NetappVolumeService> _logger;

        public NetappVolumeService(
            ApplicationDbContext context,
            INetappAuthService authService,
            ILogger<NetappVolumeService> logger)
        {
            _context = context;
            _authService = authService;
            _logger = logger;
        }

        private static string EnsureSlash(string baseUrl) =>
            string.IsNullOrEmpty(baseUrl) || baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";

        // ---------------------------------------------------------------------
        // Vservers + Volumes (full tree)
        // ---------------------------------------------------------------------
        public async Task<List<VserverDto>> GetVserversAndVolumesAsync(int netappControllerId, CancellationToken ct = default)
        {
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["op"] = "GetVserversAndVolumes",
                ["controllerId"] = netappControllerId
            });

            var vservers = new List<VserverDto>();

            var controller = await _context.NetappControllers
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == netappControllerId, ct);

            if (controller == null)
            {
                _logger.LogError("NetApp controller {Id} not found.", netappControllerId);
                throw new Exception("NetApp controller not found.");
            }

            var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl);
            baseUrl = EnsureSlash(baseUrl);

            try
            {
                _logger.LogDebug("Fetching SVM list.");
                var vserverUrl = $"{baseUrl}svm/svms";
                var vserverResponse = await httpClient.GetAsync(vserverUrl, ct);
                if (!vserverResponse.IsSuccessStatusCode)
                {
                    var body = await vserverResponse.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("SVM list failed: {Status} {Body}", vserverResponse.StatusCode, body);
                }
                vserverResponse.EnsureSuccessStatusCode();

                var vserverJson = await vserverResponse.Content.ReadAsStringAsync(ct);
                using var vserverDoc = JsonDocument.Parse(vserverJson);
                var vserverElements = vserverDoc.RootElement.GetProperty("records").EnumerateArray();

                foreach (var vserverElement in vserverElements)
                {
                    var vserverName = vserverElement.GetProperty("name").GetString() ?? string.Empty;
                    var vserverDto = new VserverDto { Name = vserverName };

                    _logger.LogDebug("Fetching volumes for SVM {SVM}.", vserverName);
                    var volumesUrl = $"{baseUrl}storage/volumes?svm.name={Uri.EscapeDataString(vserverName)}&fields=name,uuid";
                    var volumesResponse = await httpClient.GetAsync(volumesUrl, ct);
                    if (!volumesResponse.IsSuccessStatusCode)
                    {
                        var body = await volumesResponse.Content.ReadAsStringAsync(ct);
                        _logger.LogWarning("Volume list for {SVM} failed: {Status} {Body}", vserverName, volumesResponse.StatusCode, body);
                    }
                    volumesResponse.EnsureSuccessStatusCode();

                    var volumesJson = await volumesResponse.Content.ReadAsStringAsync(ct);
                    using var volumesDoc = JsonDocument.Parse(volumesJson);
                    var volumeElements = volumesDoc.RootElement.GetProperty("records").EnumerateArray();

                    foreach (var volumeElement in volumeElements)
                    {
                        var volumeName = volumeElement.GetProperty("name").GetString() ?? string.Empty;
                        var uuid = volumeElement.TryGetProperty("uuid", out var uuidProp) ? uuidProp.GetString() : string.Empty;

                        vserverDto.Volumes.Add(new NetappVolumeDto
                        {
                            VolumeName = volumeName,
                            Uuid = uuid,
                            // NOTE: If NetappVolumeDto exposes NetappControllerId, prefer that instead of ClusterId.
                            ClusterId = controller.Id
                        });
                    }

                    _logger.LogDebug("SVM {SVM} volume count: {Count}", vserverName, vserverDto.Volumes.Count);
                    vservers.Add(vserverDto);
                }

                _logger.LogInformation("Fetched {SvmCount} SVMs.", vservers.Count);
                return vservers;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("GetVserversAndVolumes cancelled.");
                throw;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP error while fetching SVMs/volumes.");
                throw new Exception("Failed to retrieve data from NetApp API.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in GetVserversAndVolumes.");
                throw;
            }
        }

        // ---------------------------------------------------------------------
        // Mount info: SVM -> NFS IPs + volumes
        // ---------------------------------------------------------------------
        public async Task<List<NetappMountInfo>> GetVolumesWithMountInfoAsync(int controllerId, CancellationToken ct = default)
        {
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["op"] = "GetVolumesWithMountInfo",
                ["controllerId"] = controllerId
            });

            var controller = await _context.NetappControllers
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == controllerId, ct);

            if (controller == null)
            {
                _logger.LogError("NetApp controller {Id} not found.", controllerId);
                throw new Exception("NetApp controller not found.");
            }

            var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl);
            baseUrl = EnsureSlash(baseUrl);

            try
            {
                // NFS data LIFs per SVM
                var interfaceUrl = $"{baseUrl}network/ip/interfaces?fields=ip.address,svm.name,services&services=data_nfs";
                _logger.LogDebug("Fetching data LIFs: {Url}", interfaceUrl);
                var interfaceResponse = await httpClient.GetAsync(interfaceUrl, ct);
                if (!interfaceResponse.IsSuccessStatusCode)
                {
                    var body = await interfaceResponse.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("Interface list failed: {Status} {Body}", interfaceResponse.StatusCode, body);
                }
                interfaceResponse.EnsureSuccessStatusCode();

                var interfaceJson = await interfaceResponse.Content.ReadAsStringAsync(ct);
                using var interfaceDoc = JsonDocument.Parse(interfaceJson);
                var interfaceData = interfaceDoc.RootElement.GetProperty("records");

                var svmToIps = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var iface in interfaceData.EnumerateArray())
                {
                    var ip = iface.GetProperty("ip").GetProperty("address").GetString();
                    var svm = iface.GetProperty("svm").GetProperty("name").GetString();

                    if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(svm))
                        continue;

                    if (!svmToIps.TryGetValue(svm, out var list))
                    {
                        list = new List<string>();
                        svmToIps[svm] = list;
                    }
                    list.Add(ip);
                }

                _logger.LogDebug("SVMs with data LIFs: {Count}", svmToIps.Count);

                // All volumes with svm.name
                var volumesUrl = $"{baseUrl}storage/volumes?fields=name,svm.name";
                _logger.LogDebug("Fetching volumes: {Url}", volumesUrl);
                var volumesResponse = await httpClient.GetAsync(volumesUrl, ct);
                if (!volumesResponse.IsSuccessStatusCode)
                {
                    var body = await volumesResponse.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("Volume list failed: {Status} {Body}", volumesResponse.StatusCode, body);
                }
                volumesResponse.EnsureSuccessStatusCode();

                var volumesJson = await volumesResponse.Content.ReadAsStringAsync(ct);
                using var volumesDoc = JsonDocument.Parse(volumesJson);
                var volumeData = volumesDoc.RootElement.GetProperty("records");

                var result = new List<NetappMountInfo>();
                foreach (var volume in volumeData.EnumerateArray())
                {
                    var volumeName = volume.GetProperty("name").GetString();
                    var svmName = volume.GetProperty("svm").GetProperty("name").GetString();
                    if (string.IsNullOrWhiteSpace(volumeName) || string.IsNullOrWhiteSpace(svmName))
                        continue;

                    if (!svmToIps.TryGetValue(svmName, out var mountIps) || mountIps.Count == 0)
                        continue;

                    foreach (var mountIp in mountIps)
                    {
                        result.Add(new NetappMountInfo
                        {
                            VolumeName = volumeName,
                            VserverName = svmName,
                            MountPath = $"{mountIp}:/{volumeName}", // NOTE: assumes junction == volumeName
                            MountIp = mountIp,
                            NetappControllerId = controllerId,
                        });
                    }
                }

                _logger.LogInformation("Built mount info: {Count} entries.", result.Count);
                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("GetVolumesWithMountInfo cancelled.");
                throw;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP error while fetching mount info.");
                throw new Exception("Failed to retrieve volume mount info from NetApp API.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while building mount info.");
                throw;
            }
        }

        // ---------------------------------------------------------------------
        // Volume listing helpers
        // ---------------------------------------------------------------------
        public async Task<List<string>> ListVolumesByPrefixAsync(string prefix, int controllerId, CancellationToken ct = default)
        {
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["op"] = "ListVolumesByPrefix",
                ["controllerId"] = controllerId,
                ["prefix"] = prefix
            });

            var controller = await _context.NetappControllers
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == controllerId, ct);
            if (controller == null)
            {
                _logger.LogWarning("Controller {Id} not found.", controllerId);
                return new List<string>();
            }

            var client = _authService.CreateAuthenticatedClient(controller, out var baseUrl);
            baseUrl = EnsureSlash(baseUrl);

            var url = $"{baseUrl}storage/volumes?fields=name";
            _logger.LogDebug("Fetching volumes: {Url}", url);

            try
            {
                var resp = await client.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("Volume list failed: {Status} {Body}", resp.StatusCode, body);
                }
                resp.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var names = doc.RootElement
                               .GetProperty("records")
                               .EnumerateArray()
                               .Select(r => r.GetProperty("name").GetString())
                               .Where(n => !string.IsNullOrWhiteSpace(n) && n!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                               .Select(n => n!)
                               .ToList();

                _logger.LogInformation("Found {Count} volumes with prefix '{Prefix}'.", names.Count, prefix);
                return names;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("ListVolumesByPrefix cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ListVolumesByPrefix.");
                throw;
            }
        }

        public async Task<List<NetappVolumeDto>> ListVolumesAsync(int controllerId, CancellationToken ct = default)
        {
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["op"] = "ListVolumes",
                ["controllerId"] = controllerId
            });

            var result = new List<NetappVolumeDto>();

            var controller = await _context.NetappControllers
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == controllerId, ct);
            if (controller == null)
            {
                _logger.LogError("NetApp controller {Id} not found.", controllerId);
                throw new Exception("NetApp controller not found.");
            }

            var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl);
            baseUrl = EnsureSlash(baseUrl);

            try
            {
                var url = $"{baseUrl}storage/volumes?fields=name,uuid,svm.name";
                _logger.LogDebug("Fetching volumes: {Url}", url);

                var resp = await httpClient.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("Volume list failed: {Status} {Body}", resp.StatusCode, body);
                }
                resp.EnsureSuccessStatusCode();

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                foreach (var volumeElement in doc.RootElement.GetProperty("records").EnumerateArray())
                {
                    var volumeName = volumeElement.GetProperty("name").GetString() ?? string.Empty;
                    var uuid = volumeElement.TryGetProperty("uuid", out var uuidProp) ? uuidProp.GetString() : string.Empty;

                    result.Add(new NetappVolumeDto
                    {
                        VolumeName = volumeName,
                        Uuid = uuid,
                        // NOTE: If NetappVolumeDto exposes NetappControllerId, prefer that instead of ClusterId.
                        ClusterId = controller.Id
                    });
                }

                _logger.LogInformation("Listed {Count} volumes.", result.Count);
                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("ListVolumes cancelled.");
                throw;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP error while listing NetApp volumes.");
                throw new Exception("Failed to retrieve NetApp volumes.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while listing NetApp volumes.");
                throw;
            }
        }

        // ---------------------------------------------------------------------
        // Lookups / Mutations
        // ---------------------------------------------------------------------
        public async Task<VolumeInfo?> LookupVolumeAsync(string volumeName, int controllerId, CancellationToken ct = default)
        {
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["op"] = "LookupVolume",
                ["controllerId"] = controllerId,
                ["volume"] = volumeName
            });

            var controller = await _context.NetappControllers
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == controllerId, ct);
            if (controller == null)
            {
                _logger.LogWarning("Controller {Id} not found in LookupVolume.", controllerId);
                return null;
            }

            var client = _authService.CreateAuthenticatedClient(controller, out var baseUrl);
            baseUrl = EnsureSlash(baseUrl);

            var url = $"{baseUrl}storage/volumes?name={Uri.EscapeDataString(volumeName)}&fields=uuid";
            _logger.LogDebug("Lookup volume: {Url}", url);

            var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Lookup failed: {Status} {Body}", resp.StatusCode, body);
                return null;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var records = doc.RootElement.GetProperty("records");
            if (records.GetArrayLength() == 0) return null;

            var uuid = records[0].GetProperty("uuid").GetString();
            if (string.IsNullOrEmpty(uuid)) return null;

            return new VolumeInfo { Uuid = uuid };
        }

        public async Task<bool> DeleteVolumeAsync(string volumeName, int controllerId, CancellationToken ct = default)
        {
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["op"] = "DeleteVolume",
                ["controllerId"] = controllerId,
                ["volume"] = volumeName
            });

            var controller = await _context.NetappControllers
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == controllerId, ct);
            if (controller == null)
            {
                _logger.LogWarning("Controller {Id} not found.", controllerId);
                return false;
            }

            var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl);
            baseUrl = EnsureSlash(baseUrl);

            try
            {
                // 1) Lookup UUID by name
                var lookupUrl = $"{baseUrl}storage/volumes?name={Uri.EscapeDataString(volumeName)}&fields=uuid";
                _logger.LogDebug("Lookup for delete: {Url}", lookupUrl);

                var lookupResp = await httpClient.GetAsync(lookupUrl, ct);
                if (!lookupResp.IsSuccessStatusCode)
                {
                    var body = await lookupResp.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("Volume lookup failed: {Status} {Body}", lookupResp.StatusCode, body);
                    return false;
                }

                using var lookupDoc = JsonDocument.Parse(await lookupResp.Content.ReadAsStringAsync(ct));
                var records = lookupDoc.RootElement.GetProperty("records");
                if (records.GetArrayLength() == 0)
                {
                    _logger.LogInformation("Volume '{Volume}' not found.", volumeName);
                    return false;
                }

                var uuid = records[0].GetProperty("uuid").GetString();
                if (string.IsNullOrEmpty(uuid)) return false;

                // 2) (Optional) Unexport first – best-effort (PATCH)
                var patchUrl = $"{baseUrl}storage/volumes/{uuid}";
                var unexportPayload = new { nas = new { path = "" } };
                using (var patchReq = new HttpRequestMessage(HttpMethod.Patch, patchUrl)
                {
                    Content = new StringContent(JsonSerializer.Serialize(unexportPayload), Encoding.UTF8, "application/json")
                })
                {
                    _logger.LogDebug("PATCH unexport: {Url}", patchUrl);
                    var patchResp = await httpClient.SendAsync(patchReq, ct);
                    if (!patchResp.IsSuccessStatusCode)
                    {
                        var body = await patchResp.Content.ReadAsStringAsync(ct);
                        _logger.LogWarning("Unexport failed for {Name} (uuid={Uuid}): {Code} {Body}", volumeName, uuid, patchResp.StatusCode, body);
                        // proceed anyway
                    }
                }

                // 3) DELETE with force + return_timeout
                const int timeoutSec = 120;
                var deleteUrl = $"{baseUrl}storage/volumes/{uuid}?force=true&return_timeout={timeoutSec}";
                _logger.LogDebug("DELETE volume (force): {Url}", deleteUrl);

                var deleteResp = await httpClient.DeleteAsync(deleteUrl, ct);

                if ((int)deleteResp.StatusCode == 202)
                {
                    var body = await deleteResp.Content.ReadAsStringAsync(ct);
                    string? jobHref = null;
                    string? jobUuid = null;

                    try
                    {
                        using var jobDoc = JsonDocument.Parse(body);
                        var root = jobDoc.RootElement;
                        if (root.TryGetProperty("job", out var job))
                        {
                            jobUuid = job.TryGetProperty("uuid", out var uuidProp) ? uuidProp.GetString() : null;
                            if (job.TryGetProperty("_links", out var links) &&
                                links.TryGetProperty("self", out var self) &&
                                self.TryGetProperty("href", out var href))
                            {
                                jobHref = href.GetString();
                            }
                        }
                    }
                    catch { /* ignore parse errors */ }

                    _logger.LogInformation("Delete accepted (async). JobUuid={JobUuid} Link={JobHref}", jobUuid ?? "(n/a)", jobHref ?? "(n/a)");
                    return true;
                }

                var ok = deleteResp.IsSuccessStatusCode;
                if (!ok)
                {
                    var body = await deleteResp.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("Delete failed for {Name} (uuid={Uuid}): {Code} {Body}", volumeName, uuid, deleteResp.StatusCode, body);
                }
                else
                {
                    _logger.LogInformation("Deleted volume {Name} (uuid={Uuid}).", volumeName, uuid);
                }

                return ok;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("DeleteVolume cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting volume {Volume}.", volumeName);
                throw;
            }
        }

        // ---------------------------------------------------------------------
        // Selected volumes sync
        // ---------------------------------------------------------------------
        public async Task UpdateAllSelectedVolumesAsync(CancellationToken ct = default)
        {
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["op"] = "UpdateAllSelectedVolumes"
            });

            var selectedVolumes = await _context.SelectedNetappVolumes
                .AsNoTracking()
                .Select(v => new { v.NetappControllerId, v.Uuid })
                .ToListAsync(ct);

            _logger.LogInformation("Syncing {Count} selected volumes.", selectedVolumes.Count);

            foreach (var v in selectedVolumes)
            {
                try
                {
                    await SyncSelectedVolumesAsync(v.NetappControllerId, v.Uuid, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to sync volume {Uuid} for controller {Controller}.", v.Uuid, v.NetappControllerId);
                }
            }
        }

        public async Task SyncSelectedVolumesAsync(int controllerId, string volumeUuid, CancellationToken ct = default)
        {
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["op"] = "SyncSelectedVolume",
                ["controllerId"] = controllerId,
                ["uuid"] = volumeUuid
            });

            var controller = await _context.NetappControllers
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == controllerId, ct);

            if (controller == null)
                throw new Exception($"Controller {controllerId} not found.");

            var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl);
            baseUrl = EnsureSlash(baseUrl);

            var url = $"{baseUrl}storage/volumes/{volumeUuid}?fields=space,nas.export_policy.name,snapshot_locking_enabled";
            _logger.LogDebug("Sync GET: {Url}", url);

            var resp = await httpClient.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Sync fetch failed for {Uuid}: {Status} {Body}", volumeUuid, resp.StatusCode, body);
            }
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var selected = await _context.SelectedNetappVolumes
                .FirstOrDefaultAsync(v => v.Uuid == volumeUuid && v.NetappControllerId == controllerId, ct);
            if (selected == null)
                throw new Exception($"SelectedNetappVolume {volumeUuid} not found for controller {controllerId}");

            if (root.TryGetProperty("space", out var spaceProp))
            {
                selected.SpaceSize = spaceProp.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : (long?)null;
                selected.SpaceAvailable = spaceProp.TryGetProperty("available", out var availProp) ? availProp.GetInt64() : (long?)null;
                selected.SpaceUsed = spaceProp.TryGetProperty("used", out var usedProp) ? usedProp.GetInt64() : (long?)null;
            }

            selected.ExportPolicyName =
                root.TryGetProperty("nas", out var nasProp)
                && nasProp.TryGetProperty("export_policy", out var expPolProp)
                && expPolProp.TryGetProperty("name", out var expNameProp)
                    ? expNameProp.GetString()
                    : null;

            selected.SnapshotLockingEnabled =
                root.TryGetProperty("snapshot_locking_enabled", out var snapLockProp)
                    ? snapLockProp.GetBoolean()
                    : (bool?)null;

            await _context.SaveChangesAsync(ct);
            _logger.LogInformation("Synced selected volume {Uuid}.", volumeUuid);
        }
    }
}
