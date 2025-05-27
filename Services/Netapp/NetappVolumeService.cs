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
using System.Text;
using System.Text.Json;

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

        public async Task<List<VserverDto>> GetVserversAndVolumesAsync(int netappControllerId, CancellationToken ct = default)
        {
            var vservers = new List<VserverDto>();

            var controller = await _context.NetappControllers.FindAsync(netappControllerId, ct);
            if (controller == null)
                throw new Exception("NetApp controller not found.");

            var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            try
            {
                var vserverUrl = $"{baseUrl}svm/svms";
                var vserverResponse = await httpClient.GetAsync(vserverUrl, ct);
                vserverResponse.EnsureSuccessStatusCode();

                var vserverJson = await vserverResponse.Content.ReadAsStringAsync(ct);
                using var vserverDoc = JsonDocument.Parse(vserverJson);
                var vserverElements = vserverDoc.RootElement.GetProperty("records").EnumerateArray();

                foreach (var vserverElement in vserverElements)
                {
                    var vserverName = vserverElement.GetProperty("name").GetString() ?? string.Empty;
                    var vserverDto = new VserverDto { Name = vserverName };

                    var volumesUrl = $"{baseUrl}storage/volumes?svm.name={Uri.EscapeDataString(vserverName)}";
                    var volumesResponse = await httpClient.GetAsync(volumesUrl, ct);
                    volumesResponse.EnsureSuccessStatusCode();

                    var volumesJson = await volumesResponse.Content.ReadAsStringAsync(ct);
                    using var volumesDoc = JsonDocument.Parse(volumesJson);
                    var volumeElements = volumesDoc.RootElement.GetProperty("records").EnumerateArray();

                    foreach (var volumeElement in volumeElements)
                    {
                        var volumeName = volumeElement.GetProperty("name").GetString() ?? string.Empty;
                        var uuid = volumeElement.TryGetProperty("uuid", out var uuidProp) ? uuidProp.GetString() : string.Empty;

                        var mountIp = volumeElement.TryGetProperty("nas", out var nasProp) &&
                                      nasProp.TryGetProperty("export_policy", out var exportPolicyProp) &&
                                      exportPolicyProp.TryGetProperty("rules", out var rulesProp) &&
                                      rulesProp.GetArrayLength() > 0 &&
                                      rulesProp[0].TryGetProperty("clients", out var clientsProp) &&
                                      clientsProp.GetArrayLength() > 0
                                      ? clientsProp[0].GetString()
                                      : string.Empty;

                        var volume = new NetappVolumeDto
                        {
                            VolumeName = volumeName,
                            Uuid = uuid,
                            MountIp = mountIp,
                            ClusterId = controller.Id
                        };

                        vserverDto.Volumes.Add(volume);
                    }

                    vservers.Add(vserverDto);
                }

                return vservers;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP error while fetching NetApp vservers or volumes.");
                throw new Exception("Failed to retrieve data from NetApp API.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in NetApp service.");
                throw;
            }
        }
        public async Task<List<NetappMountInfo>> GetVolumesWithMountInfoAsync(int controllerId, CancellationToken ct = default)
        {
            var controller = await _context.NetappControllers.FindAsync(controllerId, ct);
            if (controller == null)
                throw new Exception("NetApp controller not found.");

            var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            try
            {
                var interfaceUrl = $"{baseUrl}network/ip/interfaces?fields=ip.address,svm.name,services&services=data_nfs";
                var interfaceResponse = await httpClient.GetAsync(interfaceUrl, ct);
                interfaceResponse.EnsureSuccessStatusCode();

                var interfaceJson = await interfaceResponse.Content.ReadAsStringAsync(ct);
                using var interfaceDoc = JsonDocument.Parse(interfaceJson);
                var interfaceData = interfaceDoc.RootElement.GetProperty("records");

                var svmToIps = new Dictionary<string, List<string>>();
                foreach (var iface in interfaceData.EnumerateArray())
                {
                    var ip = iface.GetProperty("ip").GetProperty("address").GetString();
                    var svm = iface.GetProperty("svm").GetProperty("name").GetString();

                    if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(svm))
                        continue;

                    if (!svmToIps.ContainsKey(svm))
                        svmToIps[svm] = new List<string>();

                    svmToIps[svm].Add(ip);
                }

                var volumesUrl = $"{baseUrl}storage/volumes?fields=name,svm.name";
                var volumesResponse = await httpClient.GetAsync(volumesUrl, ct);
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

                    if (!svmToIps.TryGetValue(svmName, out var mountIps) || !mountIps.Any())
                        continue;

                    foreach (var mountIp in mountIps)
                    {
                        result.Add(new NetappMountInfo
                        {
                            VolumeName = volumeName,
                            VserverName = svmName,
                            MountPath = $"{mountIp}:/{volumeName}",
                            MountIp = mountIp
                        });
                    }
                }

                return result;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP error while fetching mount info from NetApp.");
                throw new Exception("Failed to retrieve volume mount info from NetApp API.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while building mount info.");
                throw;
            }
        }
        public async Task<List<string>> ListVolumesByPrefixAsync(string prefix, int controllerId, CancellationToken ct = default)
        {
            var controller = await _context.NetappControllers.FindAsync(controllerId, ct);
            if (controller == null) return new List<string>();

            var client = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            var url = $"{baseUrl}storage/volumes?fields=name";
            var resp = await client.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement
                      .GetProperty("records")
                      .EnumerateArray()
                      .Select(r => r.GetProperty("name").GetString()!)
                      .Where(n => n != null && n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                      .ToList();
        }
        public async Task<List<NetappVolumeDto>> ListVolumesAsync(int controllerId, CancellationToken ct = default)
        {
            var result = new List<NetappVolumeDto>();

            var controller = await _context.NetappControllers.FindAsync(controllerId, ct);
            if (controller == null)
                throw new Exception("NetApp controller not found.");

            var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            try
            {
                var url = $"{baseUrl}storage/volumes?fields=name,uuid,svm.name";
                var resp = await httpClient.GetAsync(url, ct);
                resp.EnsureSuccessStatusCode();

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                foreach (var volumeElement in doc.RootElement.GetProperty("records").EnumerateArray())
                {
                    var volumeName = volumeElement.GetProperty("name").GetString() ?? string.Empty;
                    var uuid = volumeElement.TryGetProperty("uuid", out var uuidProp) ? uuidProp.GetString() : string.Empty;

                    // If you want SVM name or more fields, fetch them here (optional)
                    result.Add(new NetappVolumeDto
                    {
                        VolumeName = volumeName,
                        Uuid = uuid,
                        ClusterId = controller.Id,
                        // add more if needed
                    });
                }
                return result;
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

        public async Task<VolumeInfo?> LookupVolumeAsync(string volumeName, int controllerId, CancellationToken ct = default)
        {
            var controller = await _context.NetappControllers
                .FirstOrDefaultAsync(c => c.Id == controllerId, ct);
            if (controller == null) return null;

            // 🔐 Use helper for encrypted auth and base URL
            var client = _authService.CreateAuthenticatedClient(controller, out var baseUrl);
            var url = $"{baseUrl}storage/volumes?name={Uri.EscapeDataString(volumeName)}&fields=uuid";

            var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var records = doc.RootElement.GetProperty("records");
            if (records.GetArrayLength() == 0) return null;

            var uuid = records[0].GetProperty("uuid").GetString();
            if (string.IsNullOrEmpty(uuid)) return null;

            return new VolumeInfo { Uuid = uuid };
        }
        public async Task<bool> DeleteVolumeAsync(string volumeName, int controllerId, CancellationToken ct = default)
        {
            // 1) Fetch controller
            var controller = await _context.NetappControllers.FindAsync(controllerId, ct);
            if (controller == null) return false;

            // 🔐 Prepare HTTP client + base URL
            var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl); // baseUrl = https://<ip>/api/

            // 2) Lookup UUID by name
            var lookupUrl = $"{baseUrl}storage/volumes?name={Uri.EscapeDataString(volumeName)}&fields=uuid";
            var lookupResp = await httpClient.GetAsync(lookupUrl, ct);
            if (!lookupResp.IsSuccessStatusCode) return false;

            using var lookupDoc = JsonDocument.Parse(await lookupResp.Content.ReadAsStringAsync(ct));
            var records = lookupDoc.RootElement.GetProperty("records");
            if (records.GetArrayLength() == 0) return false;

            var uuid = records[0].GetProperty("uuid").GetString();
            if (string.IsNullOrEmpty(uuid)) return false;

            // 3) Unexport by PATCHing nas.path = ""
            var patchUrl = $"{baseUrl}storage/volumes/{uuid}";
            var unexportPayload = new { nas = new { path = "" } };
            var patchContent = new StringContent(System.Text.Json.JsonSerializer.Serialize(unexportPayload), Encoding.UTF8, "application/json");
            var patchResp = await httpClient.PatchAsync(patchUrl, patchContent, ct);
            if (!patchResp.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Failed to unexport volume {Name} (uuid={Uuid}): {Code}", volumeName, uuid, patchResp.StatusCode);
                // Proceed to delete anyway
            }

            // 4) Delete by UUID
            var deleteUrl = $"{baseUrl}storage/volumes/{uuid}";
            var deleteResp = await httpClient.DeleteAsync(deleteUrl, ct);
            return deleteResp.IsSuccessStatusCode;
        }

    }
    }
