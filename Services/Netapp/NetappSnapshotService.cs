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
using BareProx.Services.Netapp;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BareProx.Services
{
    public class NetappSnapshotService : INetappSnapshotService
    {
        private readonly IDbContextFactory<ApplicationDbContext> _dbf;   // NEW
        private readonly INetappAuthService _authService;
        private readonly ILogger<NetappSnapshotService> _logger;
        private readonly IAppTimeZoneService _tz;

        public NetappSnapshotService(
            IDbContextFactory<ApplicationDbContext> dbf,                 // NEW
            IAppTimeZoneService tz,
            INetappAuthService authService,
            ILogger<NetappSnapshotService> logger)
        {
            _dbf = dbf;
            _tz = tz;
            _authService = authService;
            _logger = logger;
        }

        // ---------------------------------------------------------------------
        // Create
        // ---------------------------------------------------------------------
        public async Task<SnapshotResult> CreateSnapshotAsync(
       int controllerId,
       string storageName,
       string snapmirrorLabel,
       bool snapLocking = false,
       int? lockRetentionCount = null,
       string? lockRetentionUnit = null,
       string? volumeUuid = null,            // optional input UUID (preferred)
       string? svmName = null,               // optional SVM hint (used only if UUID is missing)
       CancellationToken ct = default)
        {
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["op"] = "CreateSnapshot",
                ["controllerId"] = controllerId,
                ["storage"] = storageName,
                ["label"] = snapmirrorLabel,
                ["locking"] = snapLocking,
                ["uuidSupplied"] = !string.IsNullOrWhiteSpace(volumeUuid),
                ["svmSupplied"] = !string.IsNullOrWhiteSpace(svmName)
            });

            _logger.LogInformation("Creating NetApp snapshot (locking={Locking}).", snapLocking);

            try
            {
                // 0) DB lookup for mapping (VolumeName, Uuid, Vserver/SVM)
                using var db = await _dbf.CreateDbContextAsync(ct);

                var map = await db.SelectedNetappVolumes
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x =>
                        x.NetappControllerId == controllerId &&
                        x.VolumeName == storageName, ct);

                // Effective selectors
                var effVolumeName = map?.VolumeName ?? storageName;
                var effUuid = !string.IsNullOrWhiteSpace(volumeUuid) ? volumeUuid : map?.Uuid;          // UUID first
                var effSvm = !string.IsNullOrWhiteSpace(svmName) ? svmName : map?.Vserver;            // SVM only used if UUID missing

                // Guard: if both param UUID and DB UUID exist but differ -> fail fast
                if (!string.IsNullOrWhiteSpace(volumeUuid) &&
                    !string.IsNullOrWhiteSpace(map?.Uuid) &&
                    !string.Equals(volumeUuid, map!.Uuid, StringComparison.OrdinalIgnoreCase))
                {
                    var msg = $"Volume UUID mismatch for '{storageName}' on controller {controllerId}: " +
                              $"param='{volumeUuid}', db='{map.Uuid}'. Aborting to avoid acting on the wrong volume.";
                    _logger.LogError(msg);
                    return new SnapshotResult { Success = false, ErrorMessage = msg };
                }

                if (map is null && string.IsNullOrWhiteSpace(effUuid))
                {
                    _logger.LogWarning("[No UUID] No SelectedNetappVolumes row; resolving by name (svm={Svm}) for '{Storage}' on controller {ControllerId}.",
                        effSvm ?? "<unknown>", storageName, controllerId);
                }

                // 1) Build snapshot name (App TZ)
                var creationTimeLocal = _tz.ConvertUtcToApp(DateTime.UtcNow);
                var timestamp = creationTimeLocal.ToString("yyyy-MM-dd-HH_mm-ss");
                var snapshotName = $"BP_{snapmirrorLabel}-{timestamp}";

                var body = new SnapshotCreateBody
                {
                    Name = snapshotName,
                    SnapMirrorLabel = snapmirrorLabel
                };

                // 2) Optional SnapLock expiry (prefer compliance clock via UUID; else name+svm)
                if (snapLocking)
                {
                    if (lockRetentionCount == null || string.IsNullOrWhiteSpace(lockRetentionUnit))
                        return new SnapshotResult { Success = false, ErrorMessage = "snapLocking requested but no retention count/unit supplied." };

                    TimeSpan offset = lockRetentionUnit switch
                    {
                        "Hours" => TimeSpan.FromHours(lockRetentionCount.Value),
                        "Days" => TimeSpan.FromDays(lockRetentionCount.Value),
                        "Weeks" => TimeSpan.FromDays(lockRetentionCount.Value * 7),
                        _ => throw new ArgumentException($"Unknown unit '{lockRetentionUnit}'")
                    };

                    var complianceBase = await ResolveComplianceClockBaseAsync(
                                             controllerId,
                                             effVolumeName,
                                             effUuid,     // UUID-first
                                             effSvm,      // SVM hint only if UUID missing
                                             ct)
                                         ?? creationTimeLocal;

                    var expiryWallClock = DateTime.SpecifyKind(complianceBase.Add(offset), DateTimeKind.Unspecified);
                    if (expiryWallClock <= complianceBase)
                        return new SnapshotResult { Success = false, ErrorMessage = $"Computed SnapLock expiry '{expiryWallClock:yyyy-MM-dd HH:mm:ss}' must be in the future." };

                    body.SnapLock = new SnapshotCreateBody.SnapLockBlock { ExpiryTime = expiryWallClock };
                    _logger.LogInformation("SnapLock expiry set to {Expiry}.", expiryWallClock);
                }

                // 3) POST snapshot (UUID-first; else name + optional SVM; ambiguity handled inside)
                await SendSnapshotRequestAsync(
                    controllerId: controllerId,
                    volumeName: effVolumeName,
                    body: body,
                    volumeUuid: effUuid,
                    svmName: effSvm,       // used only if UUID is missing
                    ct: ct);

                _logger.LogInformation("NetApp snapshot created successfully: {SnapshotName}.", snapshotName);
                return new SnapshotResult { Success = true, SnapshotName = snapshotName };
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Create snapshot cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create snapshot.");
                return new SnapshotResult { Success = false, ErrorMessage = ex.Message };
            }
        }





        // ---------------------------------------------------------------------
        // List (by controller + volume)
        // ---------------------------------------------------------------------
        public async Task<List<string>> GetSnapshotsAsync(int controllerId, string volumeName, CancellationToken ct = default)
        {
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["op"] = "ListSnapshots",
                ["controllerId"] = controllerId,
                ["volume"] = volumeName
            });

            _logger.LogDebug("Listing snapshots.");

            await using var db = await _dbf.CreateDbContextAsync(ct);

            var controller = await db.NetappControllers
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == controllerId, ct)
                ?? throw new Exception($"NetApp controller not found (id={controllerId}).");

            // ❗ EF-safe predicate (let DB collation handle case-insensitivity)
            var map = await db.SelectedNetappVolumes
                .AsNoTracking()
                .FirstOrDefaultAsync(x =>
                    x.NetappControllerId == controllerId &&
                    x.VolumeName == volumeName, ct);

            var client = _authService.CreateAuthenticatedClient(controller, out var baseUrl);
            if (!baseUrl.EndsWith("/")) baseUrl += "/";

            // Resolve volume UUID (prefer mapping)
            string? volumeUuid = map?.Uuid;
            if (string.IsNullOrWhiteSpace(volumeUuid))
            {
                var query = string.IsNullOrWhiteSpace(map?.Vserver)
                    ? $"storage/volumes?name={Uri.EscapeDataString(volumeName)}&fields=uuid,name,svm.name"
                    : $"storage/volumes?name={Uri.EscapeDataString(volumeName)}&svm.name={Uri.EscapeDataString(map!.Vserver)}&fields=uuid,name,svm.name";

                var volResp = await client.GetAsync(baseUrl + query, ct);
                var body = await volResp.Content.ReadAsStringAsync(ct);
                if (!volResp.IsSuccessStatusCode)
                    _logger.LogWarning("Volume lookup failed: {Status} {Body}", volResp.StatusCode, body);
                volResp.EnsureSuccessStatusCode();

                using var volDoc = JsonDocument.Parse(body);
                var volRecs = volDoc.RootElement.GetProperty("records");
                if (volRecs.GetArrayLength() == 0)
                {
                    _logger.LogInformation("Volume '{Volume}' not found.", volumeName);
                    return new List<string>();
                }
                if (volRecs.GetArrayLength() > 1 && string.IsNullOrWhiteSpace(map?.Vserver))
                {
                    _logger.LogWarning("Multiple volumes named '{Volume}' on controller {ControllerId}; specify SVM or ensure mapping includes Vserver.",
                        volumeName, controllerId);
                    return new List<string>();
                }

                volumeUuid = volRecs[0].GetProperty("uuid").GetString();
                if (string.IsNullOrWhiteSpace(volumeUuid))
                {
                    _logger.LogWarning("NetApp API returned empty UUID for volume '{Volume}'.", volumeName);
                    return new List<string>();
                }
            }

            // Fetch snapshots (follow pagination)
            var snapshotNames = new List<string>();
            string? nextHref = $"storage/volumes/{volumeUuid}/snapshots?fields=name";
            while (!string.IsNullOrEmpty(nextHref))
            {
                var resp = await client.GetAsync(baseUrl + nextHref, ct);
                var txt = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    _logger.LogWarning("Snapshot list page failed: {Status} {Body}", resp.StatusCode, txt);
                resp.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(txt);
                var root = doc.RootElement;

                if (root.TryGetProperty("records", out var recs) && recs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in recs.EnumerateArray())
                    {
                        var name = e.TryGetProperty("name", out var nProp) ? nProp.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(name)) snapshotNames.Add(name!);
                    }
                }

                // _links.next.href if more pages
                if (root.TryGetProperty("_links", out var links) &&
                    links.TryGetProperty("next", out var next) &&
                    next.TryGetProperty("href", out var hrefProp))
                {
                    nextHref = hrefProp.GetString();
                }
                else
                {
                    nextHref = null;
                }
            }

            _logger.LogDebug("Found {Count} snapshots on volume {Volume}.", snapshotNames.Count, volumeName);
            return snapshotNames;
        }



        //// ---------------------------------------------------------------------
        //// List (batch) for UI tree
        //// ---------------------------------------------------------------------
        //public async Task<List<VolumeSnapshotTreeDto>> GetSnapshotsForVolumesAsync(
        //     int controllerId,
        //     HashSet<string> volumeNames,
        //     CancellationToken ct = default)
        //{
        //    using var scope = _logger.BeginScope(new Dictionary<string, object?>
        //    {
        //        ["op"] = "ListSnapshotsForVolumes",
        //        ["controllerId"] = controllerId,
        //        ["count"] = volumeNames?.Count
        //    });

        //    var result = new List<VolumeSnapshotTreeDto>();
        //    if (volumeNames is null || volumeNames.Count == 0)
        //        return result;

        //    await using var db = await _dbf.CreateDbContextAsync(ct);

        //    var controller = await db.NetappControllers
        //        .AsNoTracking()
        //        .FirstOrDefaultAsync(c => c.Id == controllerId, ct)
        //        ?? throw new Exception($"No NetApp controller #{controllerId} found.");

        //    var http = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

        //    // 1) Pull mapping rows for these volumes on this controller (for UUID/SVM).
        //    var maps = await db.SelectedNetappVolumes
        //        .AsNoTracking()
        //        .Where(x => x.NetappControllerId == controllerId && volumeNames.Contains(x.VolumeName))
        //        .Select(x => new { x.VolumeName, x.Uuid, Vserver = x.Vserver })
        //        .ToListAsync(ct);

        //    var mapByName = maps.ToDictionary(m => m.VolumeName, StringComparer.OrdinalIgnoreCase);

        //    // 2) Resolve (uuid, svm) for each requested name (prefer mapping; fallback to ONTAP per volume).
        //    async Task<(string name, string? uuid, string? svm)> ResolveAsync(string name)
        //    {
        //        if (mapByName.TryGetValue(name, out var m) && !string.IsNullOrWhiteSpace(m.Uuid))
        //            return (name, m.Uuid, m.Vserver);

        //        // Fallback: lookup by name (+ svm if we know it from mapping without uuid)
        //        var svm = mapByName.TryGetValue(name, out var m2) ? m2.Vserver : null;
        //        var query = string.IsNullOrWhiteSpace(svm)
        //            ? $"storage/volumes?name={Uri.EscapeDataString(name)}&fields=uuid,svm.name"
        //            : $"storage/volumes?name={Uri.EscapeDataString(name)}&svm.name={Uri.EscapeDataString(svm)}&fields=uuid,svm.name";

        //        var resp = await http.GetAsync(baseUrl + query, ct);
        //        var txt = await resp.Content.ReadAsStringAsync(ct);
        //        if (!resp.IsSuccessStatusCode)
        //        {
        //            _logger.LogWarning("Volume lookup failed (controller {ControllerId}, volume {Volume}): {Status} {Body}",
        //                controllerId, name, resp.StatusCode, txt);
        //            return (name, null, svm);
        //        }

        //        using var doc = JsonDocument.Parse(txt);
        //        if (!doc.RootElement.TryGetProperty("records", out var recs) || recs.GetArrayLength() == 0)
        //            return (name, null, svm);

        //        if (recs.GetArrayLength() > 1 && string.IsNullOrWhiteSpace(svm))
        //        {
        //            _logger.LogWarning("Multiple volumes named '{Volume}' on controller {ControllerId}; specify SVM or ensure mapping has Vserver.",
        //                name, controllerId);
        //            return (name, null, svm);
        //        }

        //        var uuid = recs[0].GetProperty("uuid").GetString();
        //        var resolvedSvm = recs[0].GetProperty("svm").GetProperty("name").GetString();

        //        return (name, string.IsNullOrWhiteSpace(uuid) ? null : uuid, resolvedSvm);
        //    }

        //    // Resolve all inputs (with a little concurrency).
        //    var resolved = new List<(string name, string? uuid, string? svm)>(volumeNames.Count);
        //    var sem = new SemaphoreSlim(6);
        //    var tasks = volumeNames.Select(async name =>
        //    {
        //        await sem.WaitAsync(ct);
        //        try { resolved.Add(await ResolveAsync(name)); }
        //        finally { sem.Release(); }
        //    });
        //    await Task.WhenAll(tasks);

        //    // 3) Fetch snapshots per resolved UUID.
        //    async Task<VolumeSnapshotTreeDto?> FetchSnapshotsAsync((string name, string? uuid, string? svm) v)
        //    {
        //        if (string.IsNullOrWhiteSpace(v.uuid))
        //        {
        //            _logger.LogInformation("Skipping volume {Volume}: UUID unresolved.", v.name);
        //            return null;
        //        }

        //        var url = $"{baseUrl}storage/volumes/{v.uuid}/snapshots?fields=name";
        //        var resp = await http.GetAsync(url, ct);
        //        var txt = await resp.Content.ReadAsStringAsync(ct);
        //        if (!resp.IsSuccessStatusCode)
        //        {
        //            _logger.LogWarning("Snapshot list failed (controller {ControllerId}, volume {Volume}, uuid {Uuid}): {Status} {Body}",
        //                controllerId, v.name, v.uuid, resp.StatusCode, txt);
        //            return null;
        //        }

        //        using var doc = JsonDocument.Parse(txt);
        //        var snaps = doc.RootElement.GetProperty("records")
        //            .EnumerateArray()
        //            .Select(e => e.GetProperty("name").GetString() ?? "")
        //            .Where(n => !string.IsNullOrWhiteSpace(n))
        //            .ToList();

        //        _logger.LogDebug("Volume {Volume}: {Count} snapshots.", v.name, snaps.Count);

        //        return new VolumeSnapshotTreeDto
        //        {
        //            Vserver = v.svm ?? "",
        //            VolumeName = v.name,
        //            Snapshots = snaps
        //        };
        //    }

        //    var trees = new ConcurrentBag<VolumeSnapshotTreeDto>();
        //    var snapTasks = resolved.Select(async v =>
        //    {
        //        await sem.WaitAsync(ct);
        //        try
        //        {
        //            var dto = await FetchSnapshotsAsync(v);
        //            if (dto != null) trees.Add(dto);
        //        }
        //        finally { sem.Release(); }
        //    });
        //    await Task.WhenAll(snapTasks);

        //    // Preserve a stable order based on input names
        //    var order = volumeNames.Select((n, i) => new { n, i })
        //                           .ToDictionary(x => x.n, x => x.i, StringComparer.OrdinalIgnoreCase);

        //    return trees.OrderBy(t => order.TryGetValue(t.VolumeName, out var i) ? i : int.MaxValue)
        //                .ToList();
        //}


        // ---------------------------------------------------------------------
        // Delete
        // ---------------------------------------------------------------------
        public async Task<DeleteSnapshotResult> DeleteSnapshotAsync(
      int controllerId,
      string volumeName,
      string snapshotName,
      CancellationToken ct = default)
        {
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["op"] = "DeleteSnapshot",
                ["controllerId"] = controllerId,
                ["volume"] = volumeName,
                ["snapshot"] = snapshotName
            });

            _logger.LogInformation("Deleting snapshot.");

            try
            {
                await using var db = await _dbf.CreateDbContextAsync(ct);

                var controller = await db.NetappControllers
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == controllerId, ct);

                if (controller is null)
                {
                    var em = $"NetApp controller #{controllerId} not found.";
                    _logger.LogWarning(em);
                    return new DeleteSnapshotResult { ErrorMessage = em };
                }

                // ❗ EF-safe predicate (no StringComparison in DB)
                var map = await db.SelectedNetappVolumes
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x =>
                        x.NetappControllerId == controllerId &&
                        x.VolumeName == volumeName, ct);

                var http = _authService.CreateAuthenticatedClient(controller, out var baseUrl);
                if (!baseUrl.EndsWith("/")) baseUrl += "/";

                // 1) Resolve volume UUID (mapping first, fallback to API by name [+ optional svm])
                string? volumeUuid = map?.Uuid;
                if (string.IsNullOrWhiteSpace(volumeUuid))
                {
                    var query = string.IsNullOrWhiteSpace(map?.Vserver)
                        ? $"storage/volumes?name={Uri.EscapeDataString(volumeName)}&fields=uuid,name,svm.name"
                        : $"storage/volumes?name={Uri.EscapeDataString(volumeName)}&svm.name={Uri.EscapeDataString(map!.Vserver)}&fields=uuid,name,svm.name";

                    var volResp = await http.GetAsync(baseUrl + query, ct);
                    var volTxt = await volResp.Content.ReadAsStringAsync(ct);
                    if (!volResp.IsSuccessStatusCode)
                        _logger.LogWarning("Volume lookup failed (controller {ControllerId}): {Status} {Body}",
                            controllerId, volResp.StatusCode, volTxt);
                    volResp.EnsureSuccessStatusCode();

                    using var volDoc = JsonDocument.Parse(volTxt);
                    var volRecs = volDoc.RootElement.GetProperty("records");

                    if (volRecs.GetArrayLength() == 0)
                    {
                        var em = $"Volume '{volumeName}' not found (controller={controllerId}).";
                        _logger.LogWarning(em);
                        return new DeleteSnapshotResult { ErrorMessage = em };
                    }
                    if (volRecs.GetArrayLength() > 1 && string.IsNullOrWhiteSpace(map?.Vserver))
                    {
                        var em = $"Multiple volumes named '{volumeName}' found; specify SVM or ensure mapping row has Vserver.";
                        _logger.LogWarning(em);
                        return new DeleteSnapshotResult { ErrorMessage = em };
                    }

                    volumeUuid = volRecs[0].GetProperty("uuid").GetString();
                    if (string.IsNullOrWhiteSpace(volumeUuid))
                    {
                        var em = $"NetApp API returned empty UUID for volume '{volumeName}'.";
                        _logger.LogWarning(em);
                        return new DeleteSnapshotResult { ErrorMessage = em };
                    }
                }

                // 2) Find snapshot UUID by name (handle pagination)
                string? nextHref = $"storage/volumes/{volumeUuid}/snapshots?fields=name,uuid";
                string? snapshotUuid = null;

                while (!string.IsNullOrEmpty(nextHref) && snapshotUuid is null)
                {
                    var snapResp = await http.GetAsync(baseUrl + nextHref, ct);
                    var snapTxt = await snapResp.Content.ReadAsStringAsync(ct);
                    if (!snapResp.IsSuccessStatusCode)
                        _logger.LogWarning("Snapshot list page failed: {Status} {Body}", snapResp.StatusCode, snapTxt);
                    snapResp.EnsureSuccessStatusCode();

                    using var snapDoc = JsonDocument.Parse(snapTxt);
                    var root = snapDoc.RootElement;

                    if (root.TryGetProperty("records", out var recs) && recs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var e in recs.EnumerateArray())
                        {
                            var name = e.TryGetProperty("name", out var nProp) ? nProp.GetString() : null;
                            if (name is not null && string.Equals(name, snapshotName, StringComparison.Ordinal))
                            {
                                snapshotUuid = e.TryGetProperty("uuid", out var uProp) ? uProp.GetString() : null;
                                break;
                            }
                        }
                    }

                    if (snapshotUuid is null &&
                        root.TryGetProperty("_links", out var links) &&
                        links.TryGetProperty("next", out var next) &&
                        next.TryGetProperty("href", out var hrefProp))
                    {
                        nextHref = hrefProp.GetString();
                    }
                    else
                    {
                        nextHref = null;
                    }
                }

                if (string.IsNullOrWhiteSpace(snapshotUuid))
                {
                    var em = $"Snapshot '{snapshotName}' not found on volume '{volumeName}'.";
                    _logger.LogInformation(em);
                    return new DeleteSnapshotResult { ErrorMessage = em };
                }

                // 3) Delete (handle 202 Accepted job)
                var delUrl = $"{baseUrl}storage/volumes/{volumeUuid}/snapshots/{snapshotUuid}";
                var delResp = await http.DeleteAsync(delUrl, ct);
                var delTxt = await delResp.Content.ReadAsStringAsync(ct);

                if ((int)delResp.StatusCode == 202)
                {
                    // Accepted; ONTAP will finish in background
                    _logger.LogInformation("Snapshot delete accepted (async job).");
                    return new DeleteSnapshotResult { Success = true };
                }

                if (!delResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Snapshot delete failed: {Status} {Body}", delResp.StatusCode, delTxt);
                    return new DeleteSnapshotResult
                    {
                        ErrorMessage = $"Failed to delete snapshot: {delResp.StatusCode} - {delTxt}"
                    };
                }

                _logger.LogInformation("Snapshot deleted successfully.");
                return new DeleteSnapshotResult { Success = true };
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Delete snapshot cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception while deleting snapshot.");
                return new DeleteSnapshotResult { ErrorMessage = $"Exception: {ex.Message}" };
            }
        }



        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        /// <summary>
        /// Resolve the correct SnapLock compliance clock base time for the volume's home node.
        /// Returns Unspecified-kind DateTime representing node wall-clock time.
        /// </summary>
        /// <summary>
        /// Resolve the SnapLock compliance-clock base time (node wall-clock) for a volume
        /// on a specific NetApp controller. Returns an Unspecified-kind DateTime.
        /// </summary>
        private async Task<DateTime?> ResolveComplianceClockBaseAsync(
            int controllerId,
            string volumeName,
            string? volumeUuid = null,
            string? svmName = null,
            CancellationToken ct = default)
        {
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["op"] = "ResolveComplianceClock",
                ["controllerId"] = controllerId,
                ["volume"] = volumeName,
                ["uuidArg"] = volumeUuid,
                ["svmArg"] = svmName
            });

            await using var db = await _dbf.CreateDbContextAsync(ct);

            var controller = await db.NetappControllers
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == controllerId, ct)
                ?? throw new Exception($"NetApp controller {controllerId} not found.");

            var http = _authService.CreateAuthenticatedClient(controller, out var baseUrl);
            if (!baseUrl.EndsWith("/")) baseUrl += "/";

            // --- 1) Resolve volume UUID (prefer provided) ---
            string uuid = volumeUuid ?? string.Empty;
            if (string.IsNullOrWhiteSpace(uuid))
            {
                var query = string.IsNullOrWhiteSpace(svmName)
                    ? $"storage/volumes?name={Uri.EscapeDataString(volumeName)}&fields=uuid,aggregates.uuid,svm.name,name"
                    : $"storage/volumes?name={Uri.EscapeDataString(volumeName)}&svm.name={Uri.EscapeDataString(svmName)}&fields=uuid,aggregates.uuid,svm.name,name";

                var volResp = await http.GetAsync(baseUrl + query, ct);
                var volTxt = await volResp.Content.ReadAsStringAsync(ct);
                if (!volResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Volume lookup failed (controller {ControllerId}): {Status} {Body}",
                        controllerId, volResp.StatusCode, volTxt);
                }
                volResp.EnsureSuccessStatusCode();

                using var volDoc = JsonDocument.Parse(volTxt);
                if (!volDoc.RootElement.TryGetProperty("records", out var volRecs) || volRecs.GetArrayLength() == 0)
                {
                    _logger.LogWarning("Volume {Volume} not found on controller {ControllerId}.", volumeName, controllerId);
                    return null;
                }
                if (volRecs.GetArrayLength() > 1 && string.IsNullOrWhiteSpace(svmName))
                {
                    _logger.LogWarning("Multiple volumes named {Volume}; specify SVM or pass UUID.", volumeName);
                    return null;
                }
                uuid = volRecs[0].GetProperty("uuid").GetString() ?? "";
                if (string.IsNullOrWhiteSpace(uuid))
                {
                    _logger.LogWarning("NetApp API returned empty UUID for volume {Volume}.", volumeName);
                    return null;
                }

                // If we didn’t request aggregates yet, we’ll re-fetch below; but we already asked for aggregates.uuid.
                // So we can reuse this element for aggregate extraction.
                // fall through with volElem set:
                var volElem = volRecs[0];

                // --- 2) Extract first aggregate UUID from the same response if present ---
                if (!volElem.TryGetProperty("aggregates", out var aggrArr) ||
                    aggrArr.ValueKind != JsonValueKind.Array || aggrArr.GetArrayLength() == 0)
                {
                    // If missing (older ONTAP/fields), fetch volume by UUID to get aggregates
                    var volByUuidResp = await http.GetAsync($"{baseUrl}storage/volumes/{uuid}?fields=aggregates.uuid", ct);
                    var volByUuidTxt = await volByUuidResp.Content.ReadAsStringAsync(ct);
                    if (!volByUuidResp.IsSuccessStatusCode)
                        _logger.LogWarning("Volume-by-UUID fetch failed: {Status} {Body}", volByUuidResp.StatusCode, volByUuidTxt);
                    volByUuidResp.EnsureSuccessStatusCode();

                    using var volByUuidDoc = JsonDocument.Parse(volByUuidTxt);
                    if (!volByUuidDoc.RootElement.TryGetProperty("aggregates", out aggrArr) ||
                        aggrArr.ValueKind != JsonValueKind.Array || aggrArr.GetArrayLength() == 0)
                    {
                        _logger.LogWarning("Volume {Volume} has no aggregates.", volumeName);
                        return null;
                    }
                }

                var aggrUuid = aggrArr[0].GetProperty("uuid").GetString();
                if (string.IsNullOrWhiteSpace(aggrUuid))
                {
                    _logger.LogWarning("Aggregate UUID missing for volume {Volume}.", volumeName);
                    return null;
                }

                // --- 3) Aggregate → home node UUID ---
                var aggrResp = await http.GetAsync($"{baseUrl}storage/aggregates/{aggrUuid}?fields=home_node.uuid,home_node.name", ct);
                var aggrTxt = await aggrResp.Content.ReadAsStringAsync(ct);
                if (!aggrResp.IsSuccessStatusCode)
                    _logger.LogWarning("Aggregate fetch failed: {Status} {Body}", aggrResp.StatusCode, aggrTxt);
                aggrResp.EnsureSuccessStatusCode();

                using var aggrDoc = JsonDocument.Parse(aggrTxt);
                var homeNode = aggrDoc.RootElement.GetProperty("home_node");
                var nodeUuid = homeNode.GetProperty("uuid").GetString();
                if (string.IsNullOrWhiteSpace(nodeUuid))
                {
                    _logger.LogWarning("home_node.uuid missing for aggregate {AggregateUuid}.", aggrUuid);
                    return null;
                }

                // --- 4) Compliance clock for that node ---
                var ccResp = await http.GetAsync($"{baseUrl}storage/snaplock/compliance-clocks?node.uuid={Uri.EscapeDataString(nodeUuid)}&fields=time,node.name,node.uuid", ct);
                var ccTxt = await ccResp.Content.ReadAsStringAsync(ct);
                if (!ccResp.IsSuccessStatusCode)
                    _logger.LogWarning("Compliance clock fetch failed: {Status} {Body}", ccResp.StatusCode, ccTxt);
                ccResp.EnsureSuccessStatusCode();

                using var ccDoc = JsonDocument.Parse(ccTxt);
                var ccRecs = ccDoc.RootElement.GetProperty("records");
                if (ccRecs.GetArrayLength() == 0)
                {
                    _logger.LogWarning("No compliance clock record for node {NodeUuid}.", nodeUuid);
                    return null;
                }

                var timeProp = ccRecs[0].GetProperty("time");
                var timeStr = timeProp.GetString();

                if (string.IsNullOrWhiteSpace(timeStr)) return null;

                // ONTAP returns wall-clock like "yyyy-MM-dd HH:mm:ss" (sometimes ISO8601). Handle both.
                if (DateTime.TryParseExact(timeStr, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ||
                    DateTime.TryParse(timeStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                {
                    return DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
                }

                _logger.LogWarning("Unable to parse compliance clock '{Time}' for node {NodeUuid}.", timeStr, nodeUuid);
                return null;
            }
            else
            {
                // We had UUID already; fetch aggregates -> node -> clock using UUID path
                // --- 2) Volume by UUID → aggregates ---
                var volByUuidResp = await http.GetAsync($"{baseUrl}storage/volumes/{uuid}?fields=aggregates.uuid", ct);
                var volByUuidTxt = await volByUuidResp.Content.ReadAsStringAsync(ct);
                if (!volByUuidResp.IsSuccessStatusCode)
                    _logger.LogWarning("Volume-by-UUID fetch failed: {Status} {Body}", volByUuidResp.StatusCode, volByUuidTxt);
                volByUuidResp.EnsureSuccessStatusCode();

                using var volByUuidDoc = JsonDocument.Parse(volByUuidTxt);
                if (!volByUuidDoc.RootElement.TryGetProperty("aggregates", out var aggrArr) ||
                    aggrArr.ValueKind != JsonValueKind.Array || aggrArr.GetArrayLength() == 0)
                {
                    _logger.LogWarning("Volume {Volume} has no aggregates.", volumeName);
                    return null;
                }

                var aggrUuid = aggrArr[0].GetProperty("uuid").GetString();
                if (string.IsNullOrWhiteSpace(aggrUuid))
                {
                    _logger.LogWarning("Aggregate UUID missing for volume {Volume}.", volumeName);
                    return null;
                }

                // --- 3) Aggregate → home node UUID ---
                var aggrResp = await http.GetAsync($"{baseUrl}storage/aggregates/{aggrUuid}?fields=home_node.uuid,home_node.name", ct);
                var aggrTxt = await aggrResp.Content.ReadAsStringAsync(ct);
                if (!aggrResp.IsSuccessStatusCode)
                    _logger.LogWarning("Aggregate fetch failed: {Status} {Body}", aggrResp.StatusCode, aggrTxt);
                aggrResp.EnsureSuccessStatusCode();

                using var aggrDoc = JsonDocument.Parse(aggrTxt);
                var homeNode = aggrDoc.RootElement.GetProperty("home_node");
                var nodeUuid = homeNode.GetProperty("uuid").GetString();
                if (string.IsNullOrWhiteSpace(nodeUuid))
                {
                    _logger.LogWarning("home_node.uuid missing for aggregate {AggregateUuid}.", aggrUuid);
                    return null;
                }

                // --- 4) Compliance clock for that node ---
                var ccResp = await http.GetAsync($"{baseUrl}storage/snaplock/compliance-clocks?node.uuid={Uri.EscapeDataString(nodeUuid)}&fields=time,node.name,node.uuid", ct);
                var ccTxt = await ccResp.Content.ReadAsStringAsync(ct);
                if (!ccResp.IsSuccessStatusCode)
                    _logger.LogWarning("Compliance clock fetch failed: {Status} {Body}", ccResp.StatusCode, ccTxt);
                ccResp.EnsureSuccessStatusCode();

                using var ccDoc = JsonDocument.Parse(ccTxt);
                var ccRecs = ccDoc.RootElement.GetProperty("records");
                if (ccRecs.GetArrayLength() == 0)
                {
                    _logger.LogWarning("No compliance clock record for node {NodeUuid}.", nodeUuid);
                    return null;
                }

                var timeStr = ccRecs[0].GetProperty("time").GetString();
                if (string.IsNullOrWhiteSpace(timeStr)) return null;

                if (DateTime.TryParseExact(timeStr, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ||
                    DateTime.TryParse(timeStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                {
                    return DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
                }

                _logger.LogWarning("Unable to parse compliance clock '{Time}' for node {NodeUuid}.", timeStr, nodeUuid);
                return null;
            }
        }


        /// <summary>
        /// Posts a snapshot create request to ONTAP for the given volume.
        /// Prefer passing a UUID. If UUID is null/empty, we look up by name (and svm.name if provided).
        /// </summary>
        private async Task SendSnapshotRequestAsync(
       int controllerId,
       string volumeName,
       SnapshotCreateBody body,
       string? volumeUuid,
       string? svmName,                          // <- NEW: optional SVM hint
       CancellationToken ct = default)
        {
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["op"] = "PostSnapshot",
                ["controllerId"] = controllerId,
                ["volume"] = volumeName,
                ["uuidArg"] = volumeUuid,
                ["svmArg"] = svmName,
                ["snapName"] = body?.Name,
                ["label"] = body?.SnapMirrorLabel
            });

            // Resolve controller
            await using var db = await _dbf.CreateDbContextAsync(ct);
            var controller = await db.NetappControllers
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == controllerId, ct)
                ?? throw new Exception($"NetApp controller {controllerId} not found.");

            var http = _authService.CreateAuthenticatedClient(controller, out var baseUrl);
            if (!baseUrl.EndsWith("/")) baseUrl += "/";

            // --- Resolve volume UUID (prefer provided UUID) ---
            string? uuid = volumeUuid;

            if (string.IsNullOrWhiteSpace(uuid))
            {
                // Build lookup; include SVM filter when provided to avoid ambiguity
                var lookupPath = string.IsNullOrWhiteSpace(svmName)
                    ? $"storage/volumes?name={Uri.EscapeDataString(volumeName)}&fields=uuid,svm.name,name&max_records=2"
                    : $"storage/volumes?name={Uri.EscapeDataString(volumeName)}&svm.name={Uri.EscapeDataString(svmName)}&fields=uuid,svm.name,name&max_records=2";

                var lookupUrl = baseUrl + lookupPath;
                var lookupResp = await http.GetAsync(lookupUrl, ct);
                var lookupTxt = await lookupResp.Content.ReadAsStringAsync(ct);

                if (!lookupResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Volume lookup failed (controller {ControllerId}): {Status} {Body}",
                        controllerId, lookupResp.StatusCode, lookupTxt);
                }
                lookupResp.EnsureSuccessStatusCode();

                using var lookupDoc = JsonDocument.Parse(lookupTxt);
                if (!lookupDoc.RootElement.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
                    throw new Exception("Unexpected NetApp API response format for volume lookup.");

                var count = records.GetArrayLength();
                if (count == 0)
                    throw new Exception($"Volume '{volumeName}' not found on controller {controllerId}{(string.IsNullOrWhiteSpace(svmName) ? "" : $" (svm={svmName})")}.");

                if (count > 1)
                {
                    var svms = records.EnumerateArray()
                                      .Select(e => e.GetProperty("svm").GetProperty("name").GetString())
                                      .Where(s => !string.IsNullOrWhiteSpace(s))
                                      .Distinct(StringComparer.OrdinalIgnoreCase)
                                      .OrderBy(s => s)
                                      .ToArray();

                    var svmList = svms.Length > 0 ? $" (SVMs: {string.Join(", ", svms)})" : string.Empty;
                    var callerSvm = string.IsNullOrWhiteSpace(svmName) ? "" : $" (requested svm={svmName})";
                    throw new Exception(
                        $"Multiple volumes named '{volumeName}' found on controller {controllerId}{svmList}{callerSvm}. " +
                        $"Provide svmName or store/use the volume UUID.");
                }

                uuid = records[0].GetProperty("uuid").GetString();
                if (string.IsNullOrWhiteSpace(uuid))
                    throw new Exception($"NetApp API returned empty UUID for volume '{volumeName}'.");
            }
            else
            {
                // Optional verification of the UUID
                var verifyUrl = $"{baseUrl}storage/volumes/{uuid}?fields=uuid,svm.name,name";
                var verifyResp = await http.GetAsync(verifyUrl, ct);
                var verifyTxt = await verifyResp.Content.ReadAsStringAsync(ct);

                if (!verifyResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Volume UUID verify failed (controller {ControllerId}, uuid {Uuid}): {Status} {Body}",
                        controllerId, uuid, verifyResp.StatusCode, verifyTxt);
                    verifyResp.EnsureSuccessStatusCode();
                }
            }

            // --- POST snapshot ---
            var snapshotUrl = $"{baseUrl}storage/volumes/{uuid}/snapshots";
            var jsonPayload = Newtonsoft.Json.JsonConvert.SerializeObject(body);
            _logger.LogDebug("POST {Url} payload: {Payload}", snapshotUrl, jsonPayload);

            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            var resp = await http.PostAsync(snapshotUrl, content, ct);
            var respTxt = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                _logger.LogWarning("Snapshot POST failed (controller {ControllerId}, volUuid {Uuid}): {Status} {Body}",
                    controllerId, uuid, resp.StatusCode, respTxt);

            resp.EnsureSuccessStatusCode();
        }



    }
}
