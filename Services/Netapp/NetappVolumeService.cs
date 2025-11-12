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

        private static IEnumerable<string> ReadServices(JsonElement lif)
        {
            // Prefer service_policy.services if present
            if (lif.TryGetProperty("service_policy", out var sp) &&
                sp.ValueKind == JsonValueKind.Object &&
                sp.TryGetProperty("services", out var svcs1) &&
                svcs1.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in svcs1.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()))
                        yield return x.GetString()!;
            }

            // Fallback: top-level services
            if (lif.TryGetProperty("services", out var svcs2) &&
                svcs2.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in svcs2.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()))
                        yield return x.GetString()!;
            }
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
                .FirstOrDefaultAsync(c => c.Id == netappControllerId, ct)
                ?? throw new Exception($"NetApp controller {netappControllerId} not found.");

            var http = _authService.CreateAuthenticatedClient(controller, out var baseUrl);
            baseUrl = EnsureSlash(baseUrl);

            try
            {
                var svmUrl = $"{baseUrl}svm/svms?fields=name&max_records=10000";
                _logger.LogDebug("Fetching SVMs: {Url}", svmUrl);

                await foreach (var svm in EnumerateOntapRecordsAsync(http, svmUrl, ct))
                {
                    var svmName = svm.TryGetProperty("name", out var pName) && pName.ValueKind == JsonValueKind.String
                        ? pName.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(svmName)) continue;

                    var vdto = new VserverDto { Name = svmName };

                    var volsUrl = $"{baseUrl}storage/volumes?svm.name={Uri.EscapeDataString(svmName)}&fields=name,uuid&max_records=10000";
                    _logger.LogDebug("Fetching volumes for SVM {SVM}: {Url}", svmName, volsUrl);

                    await foreach (var vol in EnumerateOntapRecordsAsync(http, volsUrl, ct))
                    {
                        var volName = vol.TryGetProperty("name", out var pVol) && pVol.ValueKind == JsonValueKind.String
                            ? pVol.GetString()
                            : null;
                        if (string.IsNullOrWhiteSpace(volName)) continue;

                        string? uuid = null;
                        if (vol.TryGetProperty("uuid", out var pUuid) && pUuid.ValueKind == JsonValueKind.String)
                            uuid = pUuid.GetString();

                        vdto.Volumes.Add(new NetappVolumeDto
                        {
                            VolumeName = volName!,
                            Uuid = uuid ?? string.Empty,
                            ClusterId = controller.Id
                        });
                    }

                    _logger.LogDebug("SVM {SVM} volume count: {Count}", svmName, vdto.Volumes.Count);
                    vservers.Add(vdto);
                }

                _logger.LogInformation("Fetched {Count} SVMs.", vservers.Count);
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

        private async IAsyncEnumerable<JsonElement> EnumerateOntapRecordsAsync(
            HttpClient http,
            string initialUrl,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            string? url = initialUrl;

            while (!string.IsNullOrEmpty(url))
            {
                using var resp = await http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("ONTAP list call failed: {Status} {Body}", resp.StatusCode, body);
                }
                resp.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("records", out var recs) && recs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in recs.EnumerateArray())
                        yield return item;
                }

                string? next = null;
                if (doc.RootElement.TryGetProperty("_links", out var links) &&
                    links.ValueKind == JsonValueKind.Object &&
                    links.TryGetProperty("next", out var nextObj) &&
                    nextObj.ValueKind == JsonValueKind.Object &&
                    nextObj.TryGetProperty("href", out var href) &&
                    href.ValueKind == JsonValueKind.String)
                {
                    next = href.GetString();
                }

                url = string.IsNullOrWhiteSpace(next) ? null : EnsureAbsoluteOntapUrl(next, http.BaseAddress, initialUrl);
            }
        }

        private static string EnsureAbsoluteOntapUrl(string href, Uri? baseAddress, string fallbackFrom)
        {
            if (Uri.TryCreate(href, UriKind.Absolute, out var abs)) return abs.ToString();
            if (baseAddress != null && Uri.TryCreate(baseAddress, href, out var combined)) return combined.ToString();
            if (Uri.TryCreate(fallbackFrom, UriKind.Absolute, out var first) &&
                Uri.TryCreate(new Uri(first.GetLeftPart(UriPartial.Authority)), href, out var rebased))
                return rebased.ToString();
            return href;
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
                .FirstOrDefaultAsync(c => c.Id == controllerId, ct)
                ?? throw new Exception($"NetApp controller {controllerId} not found.");

            var http = _authService.CreateAuthenticatedClient(controller, out var baseUrl);
            baseUrl = EnsureSlash(baseUrl);

            try
            {
                // -----------------------------------------------------------------
                // 1) SVM -> NFS LIF IPs
                // -----------------------------------------------------------------
                var lifUrl = $"{baseUrl}network/ip/interfaces" +
                             "?fields=ip.address,svm.name,services,enabled,state,scope" +
                             "&max_records=10000";
                _logger.LogDebug("Fetching LIFs: {Url}", lifUrl);

                using var lifResp = await http.GetAsync(lifUrl, ct);
                var lifJson = await lifResp.Content.ReadAsStringAsync(ct);
                if (!lifResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Interface list failed: {Status} {Body}", lifResp.StatusCode, lifJson);
                    lifResp.EnsureSuccessStatusCode();
                }

                var svmToIps = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                using (var lifDoc = JsonDocument.Parse(lifJson))
                {
                    if (lifDoc.RootElement.TryGetProperty("records", out var recs) && recs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var lif in recs.EnumerateArray())
                        {
                            // enabled + state == "up"
                            var enabled = lif.TryGetProperty("enabled", out var pEn) && pEn.ValueKind == JsonValueKind.True;
                            if (!enabled) continue;

                            var up = lif.TryGetProperty("state", out var pState) &&
                                     pState.ValueKind == JsonValueKind.String &&
                                     string.Equals(pState.GetString(), "up", StringComparison.OrdinalIgnoreCase);
                            if (!up) continue;

                            // scope must be "svm"
                            var isSvm = lif.TryGetProperty("scope", out var pScope) &&
                                        pScope.ValueKind == JsonValueKind.String &&
                                        string.Equals(pScope.GetString(), "svm", StringComparison.OrdinalIgnoreCase);
                            if (!isSvm) continue;

                            // services must include "nfs" (flat array)
                            bool hasNfs = false;
                            if (lif.TryGetProperty("services", out var svcs) && svcs.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var s in svcs.EnumerateArray())
                                {
                                    if (s.ValueKind == JsonValueKind.String &&
                                        (s.GetString()?.IndexOf("nfs", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
                                    {
                                        hasNfs = true; break;
                                    }
                                }
                            }
                            if (!hasNfs) continue;

                            // get ip.address
                            string? ip = null;
                            if (lif.TryGetProperty("ip", out var pIp) && pIp.ValueKind == JsonValueKind.Object &&
                                pIp.TryGetProperty("address", out var pAddr) && pAddr.ValueKind == JsonValueKind.String)
                            {
                                ip = pAddr.GetString();
                            }

                            // get svm.name
                            string? svm = null;
                            if (lif.TryGetProperty("svm", out var pSvm) && pSvm.ValueKind == JsonValueKind.Object &&
                                pSvm.TryGetProperty("name", out var pSvmName) && pSvmName.ValueKind == JsonValueKind.String)
                            {
                                svm = pSvmName.GetString();
                            }

                            if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(svm)) continue;

                            if (!svmToIps.TryGetValue(svm, out var set))
                            {
                                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                svmToIps[svm] = set;
                            }
                            set.Add(ip);
                        }
                    }
                }


                _logger.LogDebug("SVMs with NFS LIFs: {Count}", svmToIps.Count);

                // -----------------------------------------------------------------
                // 2) Volumes (name, uuid, svm.name, nas.path, snapshot_locking_enabled)
                // -----------------------------------------------------------------
                var volUrl = $"{baseUrl}storage/volumes" +
                             "?fields=name,uuid,svm.name,nas.path,snapshot_locking_enabled" +
                             "&max_records=10000";
                _logger.LogDebug("Fetching volumes: {Url}", volUrl);

                using var volResp = await http.GetAsync(volUrl, ct);
                var volJson = await volResp.Content.ReadAsStringAsync(ct);
                if (!volResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Volume list failed: {Status} {Body}", volResp.StatusCode, volJson);
                    volResp.EnsureSuccessStatusCode();
                }

                var result = new List<NetappMountInfo>();
                using (var volDoc = JsonDocument.Parse(volJson))
                {
                    if (!volDoc.RootElement.TryGetProperty("records", out var recs) || recs.ValueKind != JsonValueKind.Array)
                        return result;

                    foreach (var vol in recs.EnumerateArray())
                    {
                        var volName = vol.TryGetProperty("name", out var pName) && pName.ValueKind == JsonValueKind.String
                            ? pName.GetString()
                            : null;
                        if (string.IsNullOrWhiteSpace(volName)) continue;

                        var uuid = vol.TryGetProperty("uuid", out var pUuid) && pUuid.ValueKind == JsonValueKind.String
                            ? pUuid.GetString()
                            : null;

                        string? svmName = null;
                        if (vol.TryGetProperty("svm", out var pSvm) &&
                            pSvm.TryGetProperty("name", out var pSvmName) &&
                            pSvmName.ValueKind == JsonValueKind.String)
                        {
                            svmName = pSvmName.GetString();
                        }
                        if (string.IsNullOrWhiteSpace(svmName)) continue;

                        // nas.path -> junction
                        string? junction = null;
                        if (vol.TryGetProperty("nas", out var pNas) &&
                            pNas.TryGetProperty("path", out var pPath) &&
                            pPath.ValueKind == JsonValueKind.String)
                        {
                            junction = pPath.GetString();
                        }

                        if (string.IsNullOrWhiteSpace(junction)) junction = null;
                        else if (!junction.StartsWith("/", StringComparison.Ordinal)) junction = "/" + junction;

                        // snapshot_locking_enabled (nullable)
                        bool? snaplockEnabled = null;
                        if (vol.TryGetProperty("snapshot_locking_enabled", out var pSle))
                        {
                            if (pSle.ValueKind == JsonValueKind.True) snaplockEnabled = true;
                            else if (pSle.ValueKind == JsonValueKind.False) snaplockEnabled = false;
                        }

                        if (!svmToIps.TryGetValue(svmName!, out var ips) || ips.Count == 0)
                            continue;

                        var ipList = ips.Where(s => !string.IsNullOrWhiteSpace(s))
                                        .Select(s => s.Trim())
                                        .Distinct(StringComparer.OrdinalIgnoreCase)
                                        .ToList();

                        var mountIpsJoined = ipList.Count > 0 ? string.Join(",", ipList) : null;
                        var effectiveExport = junction ?? ("/" + volName);

                        foreach (var ip in ipList)
                        {
                            result.Add(new NetappMountInfo
                            {
                                VolumeName = volName!,
                                Uuid = uuid,
                                VserverName = svmName!,
                                JunctionPath = junction,
                                MountPath = $"{ip}:{effectiveExport}",
                                MountIp = ip,
                                MountIps = mountIpsJoined,              // all SVM NFS IPs for this volume
                                SnapshotLockingEnabled = snaplockEnabled,
                                NetappControllerId = controllerId
                            });
                        }
                    }
                }

                // De-dupe identical rows
                result = result
                    .GroupBy(r => (r.NetappControllerId, r.VserverName, r.VolumeName, r.MountIp, r.MountPath))
                    .Select(g => g.First())
                    .ToList();

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
                throw new Exception("Failed to retrieve NetApp volumes/LIFs.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while building mount info.");
                throw;
            }
        }

        public async Task<List<NetappMountInfo>> GetVolumesWithMountInfoByUuidAsync(int controllerId, string volumeUuid, CancellationToken ct = default)
        {
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["op"] = "GetVolumesWithMountInfoByUuid",
                ["controllerId"] = controllerId,
                ["uuid"] = volumeUuid
            });

            var result = new List<NetappMountInfo>();

            if (string.IsNullOrWhiteSpace(volumeUuid))
                return result;

            var controller = await _context.NetappControllers
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == controllerId, ct)
                ?? throw new Exception($"NetApp controller {controllerId} not found.");

            var http = _authService.CreateAuthenticatedClient(controller, out var baseUrl);
            baseUrl = EnsureSlash(baseUrl);

            // 1) Fetch volume by UUID (name, svm, nas.path, snapshot_locking_enabled)
            var volUrl = $"{baseUrl}storage/volumes/{volumeUuid}?fields=name,uuid,svm.name,nas.path,snapshot_locking_enabled";
            _logger.LogDebug("Fetching volume by uuid: {Url}", volUrl);

            using var volResp = await http.GetAsync(volUrl, ct);
            var volBody = await volResp.Content.ReadAsStringAsync(ct);
            if (!volResp.IsSuccessStatusCode)
            {
                if ((int)volResp.StatusCode == 404)
                {
                    _logger.LogInformation("Volume uuid {Uuid} not found on controller {Ctl}.", volumeUuid, controllerId);
                    return result; // empty list
                }
                _logger.LogWarning("Volume fetch failed: {Status} {Body}", volResp.StatusCode, volBody);
                volResp.EnsureSuccessStatusCode();
            }

            using var volDoc = JsonDocument.Parse(volBody);
            var root = volDoc.RootElement;

            var volName = root.TryGetProperty("name", out var pName) && pName.ValueKind == JsonValueKind.String
                ? pName.GetString()
                : null;

            string? svmName = null;
            if (root.TryGetProperty("svm", out var pSvm) &&
                pSvm.ValueKind == JsonValueKind.Object &&
                pSvm.TryGetProperty("name", out var pSvmName) &&
                pSvmName.ValueKind == JsonValueKind.String)
            {
                svmName = pSvmName.GetString();
            }

            // nas.path -> junction
            string? junction = null;
            if (root.TryGetProperty("nas", out var pNas) &&
                pNas.ValueKind == JsonValueKind.Object &&
                pNas.TryGetProperty("path", out var pPath) &&
                pPath.ValueKind == JsonValueKind.String)
            {
                junction = pPath.GetString();
            }
            if (!string.IsNullOrWhiteSpace(junction) && !junction.StartsWith("/", StringComparison.Ordinal))
                junction = "/" + junction;

            // snapshot_locking_enabled
            bool? snaplockEnabled = null;
            if (root.TryGetProperty("snapshot_locking_enabled", out var pSle))
            {
                if (pSle.ValueKind == JsonValueKind.True) snaplockEnabled = true;
                else if (pSle.ValueKind == JsonValueKind.False) snaplockEnabled = false;
            }

            if (string.IsNullOrWhiteSpace(volName) || string.IsNullOrWhiteSpace(svmName))
                return result;

            // 2) Fetch NFS LIF IPs for this SVM
            var ips = await GetSvmNfsIpsAsync(http, baseUrl, svmName!, ct);
            if (ips.Count == 0)
                return result;

            var ipList = ips.Where(s => !string.IsNullOrWhiteSpace(s))
                            .Select(s => s.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

            var mountIpsJoined = ipList.Count > 0 ? string.Join(",", ipList) : null;
            var effectiveExport = !string.IsNullOrWhiteSpace(junction) ? junction! : ("/" + volName);

            foreach (var ip in ipList)
            {
                result.Add(new NetappMountInfo
                {
                    VolumeName = volName!,
                    Uuid = volumeUuid,
                    VserverName = svmName!,
                    JunctionPath = string.IsNullOrWhiteSpace(junction) ? null : junction,
                    MountPath = $"{ip}:{effectiveExport}",
                    MountIp = ip,
                    MountIps = mountIpsJoined,
                    SnapshotLockingEnabled = snaplockEnabled,
                    NetappControllerId = controllerId
                });
            }

            // De-dupe identical rows
            result = result
                .GroupBy(r => (r.NetappControllerId, r.VserverName, r.VolumeName, r.MountIp, r.MountPath))
                .Select(g => g.First())
                .ToList();

            return result;
        }

        // Helper: NFS LIF IPs for a specific SVM (enabled + up + scope=svm + contains 'nfs')
        private async Task<List<string>> GetSvmNfsIpsAsync(HttpClient http, string baseUrl, string svmName, CancellationToken ct)
        {
            var lifUrl = $"{baseUrl}network/ip/interfaces" +
                         $"?svm.name={Uri.EscapeDataString(svmName)}" +
                         "&fields=ip.address,svm.name,services,enabled,state,scope" +
                         "&max_records=10000";
            _logger.LogDebug("Fetching LIFs for SVM {SVM}: {Url}", svmName, lifUrl);

            using var lifResp = await http.GetAsync(lifUrl, ct);
            var lifJson = await lifResp.Content.ReadAsStringAsync(ct);
            if (!lifResp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Interface list failed: {Status} {Body}", lifResp.StatusCode, lifJson);
                lifResp.EnsureSuccessStatusCode();
            }

            var ips = new List<string>();
            using var lifDoc = JsonDocument.Parse(lifJson);
            if (lifDoc.RootElement.TryGetProperty("records", out var recs) && recs.ValueKind == JsonValueKind.Array)
            {
                foreach (var lif in recs.EnumerateArray())
                {
                    var enabled = lif.TryGetProperty("enabled", out var pEn) && pEn.ValueKind == JsonValueKind.True;
                    if (!enabled) continue;

                    var up = lif.TryGetProperty("state", out var pState) &&
                             pState.ValueKind == JsonValueKind.String &&
                             string.Equals(pState.GetString(), "up", StringComparison.OrdinalIgnoreCase);
                    if (!up) continue;

                    var isSvm = lif.TryGetProperty("scope", out var pScope) &&
                                pScope.ValueKind == JsonValueKind.String &&
                                string.Equals(pScope.GetString(), "svm", StringComparison.OrdinalIgnoreCase);
                    if (!isSvm) continue;

                    bool hasNfs = false;
                    if (lif.TryGetProperty("services", out var svcs) && svcs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in svcs.EnumerateArray())
                        {
                            if (s.ValueKind == JsonValueKind.String &&
                                (s.GetString()?.IndexOf("nfs", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
                            {
                                hasNfs = true; break;
                            }
                        }
                    }
                    if (!hasNfs) continue;

                    if (lif.TryGetProperty("ip", out var pIp) &&
                        pIp.ValueKind == JsonValueKind.Object &&
                        pIp.TryGetProperty("address", out var pAddr) &&
                        pAddr.ValueKind == JsonValueKind.String)
                    {
                        var ip = pAddr.GetString();
                        if (!string.IsNullOrWhiteSpace(ip))
                            ips.Add(ip!);
                    }
                }
            }

            return ips;
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

            // Require "restore_" or "attach_" prefix to avoid accidental deletes
            if (string.IsNullOrWhiteSpace(volumeName) ||
                (!volumeName.StartsWith("restore_", StringComparison.OrdinalIgnoreCase) &&
                 !volumeName.StartsWith("attach_", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning(
                    "Refusing to delete volume '{Volume}'. Name must start with 'restore_' or 'attach_'.",
                    volumeName);
                return false;
            }

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
                        _logger.LogWarning(
                            "Unexport failed for {Name} (uuid={Uuid}): {Code} {Body}",
                            volumeName, uuid, patchResp.StatusCode, body);
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
                    catch
                    {
                        // ignore parse errors
                    }

                    _logger.LogInformation(
                        "Delete accepted (async). JobUuid={JobUuid} Link={JobHref}",
                        jobUuid ?? "(n/a)", jobHref ?? "(n/a)");
                    return true;
                }

                var ok = deleteResp.IsSuccessStatusCode;
                if (!ok)
                {
                    var body = await deleteResp.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning(
                        "Delete failed for {Name} (uuid={Uuid}): {Code} {Body}",
                        volumeName, uuid, deleteResp.StatusCode, body);
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

            if (string.IsNullOrWhiteSpace(volumeUuid))
                throw new ArgumentException("Volume UUID is required.", nameof(volumeUuid));

            var controller = await _context.NetappControllers
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == controllerId, ct)
                ?? throw new Exception($"Controller {controllerId} not found.");

            // Fetch the row we will update (tracking enabled)
            var selected = await _context.SelectedNetappVolumes
                .FirstOrDefaultAsync(v => v.Uuid == volumeUuid && v.NetappControllerId == controllerId, ct)
                ?? throw new Exception($"SelectedNetappVolume {volumeUuid} not found for controller {controllerId}");

            var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl);
            baseUrl = EnsureSlash(baseUrl);

            var url = $"{baseUrl}storage/volumes/{volumeUuid}?fields=space,nas.export_policy.name,snapshot_locking_enabled";
            _logger.LogDebug("Sync GET: {Url}", url);

            using var resp = await httpClient.GetAsync(url, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Sync fetch failed for {Uuid}: {Status} {Body}", volumeUuid, resp.StatusCode, body);

                // If the volume no longer exists on the array, clear metrics but keep the selection.
                if ((int)resp.StatusCode == 404)
                {
                    selected.SpaceSize = null;
                    selected.SpaceAvailable = null;
                    selected.SpaceUsed = null;
                    selected.ExportPolicyName = null;
                    selected.SnapshotLockingEnabled = null;
                    await _context.SaveChangesAsync(ct);
                    return;
                }

                resp.EnsureSuccessStatusCode(); // throw for other errors
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // space.{size,available,used}
            if (root.TryGetProperty("space", out var spaceProp) && spaceProp.ValueKind == JsonValueKind.Object)
            {
                selected.SpaceSize = spaceProp.TryGetProperty("size", out var sizeProp) && sizeProp.ValueKind == JsonValueKind.Number
                    ? sizeProp.GetInt64()
                    : (long?)null;

                selected.SpaceAvailable = spaceProp.TryGetProperty("available", out var availProp) && availProp.ValueKind == JsonValueKind.Number
                    ? availProp.GetInt64()
                    : (long?)null;

                selected.SpaceUsed = spaceProp.TryGetProperty("used", out var usedProp) && usedProp.ValueKind == JsonValueKind.Number
                    ? usedProp.GetInt64()
                    : (long?)null;
            }
            else
            {
                selected.SpaceSize = null;
                selected.SpaceAvailable = null;
                selected.SpaceUsed = null;
            }

            // nas.export_policy.name
            selected.ExportPolicyName =
                root.TryGetProperty("nas", out var nasProp) && nasProp.ValueKind == JsonValueKind.Object
                && nasProp.TryGetProperty("export_policy", out var expPolProp) && expPolProp.ValueKind == JsonValueKind.Object
                && expPolProp.TryGetProperty("name", out var expNameProp) && expNameProp.ValueKind == JsonValueKind.String
                    ? (expNameProp.GetString() ?? string.Empty).Trim()
                    : null;

            // snapshot_locking_enabled
            selected.SnapshotLockingEnabled =
                root.TryGetProperty("snapshot_locking_enabled", out var snapLockProp) && snapLockProp.ValueKind == JsonValueKind.True
                    ? true
                    : root.TryGetProperty("snapshot_locking_enabled", out snapLockProp) && snapLockProp.ValueKind == JsonValueKind.False
                        ? (bool?)false
                        : (bool?)null;

            await _context.SaveChangesAsync(ct);
            _logger.LogInformation("Synced selected volume {Uuid}.", volumeUuid);
        }

    }
}
