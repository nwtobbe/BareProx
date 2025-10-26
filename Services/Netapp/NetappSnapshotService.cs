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

using System.Globalization;
using System.Text;
using System.Text.Json;
using BareProx.Data;
using BareProx.Models;
using BareProx.Services.Netapp;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace BareProx.Services
{
    public class NetappSnapshotService : INetappSnapshotService
    {
        private readonly ApplicationDbContext _context;
        private readonly INetappAuthService _authService;
        private readonly ILogger<NetappSnapshotService> _logger;
        private readonly IAppTimeZoneService _tz;
        private readonly INetappVolumeService _netappVolumeService;

        public NetappSnapshotService(
            ApplicationDbContext context,
            IAppTimeZoneService tz,
            INetappAuthService authService,
            ILogger<NetappSnapshotService> logger,
            INetappVolumeService netappVolumeService)
        {
            _context = context;
            _tz = tz;
            _authService = authService;
            _logger = logger;
            _netappVolumeService = netappVolumeService;
        }

        // ---------------------------------------------------------------------
        // Create
        // ---------------------------------------------------------------------
        public async Task<SnapshotResult> CreateSnapshotAsync(
            int clusterId,
            string storageName,
            string snapmirrorLabel,
            bool snapLocking = false,
            int? lockRetentionCount = null,
            string? lockRetentionUnit = null,
            CancellationToken ct = default)
        {
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["op"] = "CreateSnapshot",
                ["clusterId"] = clusterId,
                ["storage"] = storageName,
                ["label"] = snapmirrorLabel,
                ["locking"] = snapLocking
            });

            _logger.LogInformation("Creating NetApp snapshot (locking={Locking}).", snapLocking);

            try
            {
                var volumes = await _netappVolumeService.GetVolumesWithMountInfoAsync(clusterId, ct);
                var volume = volumes.FirstOrDefault(v =>
                    v.VolumeName.Equals(storageName, StringComparison.OrdinalIgnoreCase));

                if (volume == null)
                {
                    _logger.LogWarning("No matching NetApp volume found for storage '{Storage}'.", storageName);
                    return new SnapshotResult { Success = false, ErrorMessage = $"No matching NetApp volume for storage '{storageName}'." };
                }

                // 1) Timestamp (app tz) used in name only
                var creationTimeLocal = _tz.ConvertUtcToApp(DateTime.UtcNow);
                var timestamp = creationTimeLocal.ToString("yyyy-MM-dd-HH_mm-ss");
                var snapshotName = $"BP_{snapmirrorLabel}-{timestamp}";
                _logger.LogDebug("Using snapshot name {SnapshotName}.", snapshotName);

                // 2) Base payload
                var body = new SnapshotCreateBody
                {
                    Name = snapshotName,
                    SnapMirrorLabel = snapmirrorLabel
                };

                // 3) Optional SnapLock expiry
                if (snapLocking)
                {
                    if (lockRetentionCount == null || string.IsNullOrWhiteSpace(lockRetentionUnit))
                    {
                        _logger.LogWarning("SnapLock requested without retention parameters.");
                        return new SnapshotResult
                        {
                            Success = false,
                            ErrorMessage = "snapLocking requested but no retention count/unit supplied."
                        };
                    }

                    TimeSpan offset = lockRetentionUnit switch
                    {
                        "Hours" => TimeSpan.FromHours(lockRetentionCount.Value),
                        "Days" => TimeSpan.FromDays(lockRetentionCount.Value),
                        "Weeks" => TimeSpan.FromDays(lockRetentionCount.Value * 7),
                        _ => throw new ArgumentException($"Unknown unit '{lockRetentionUnit}'")
                    };

                    var complianceBase = await ResolveComplianceClockBaseAsync(storageName, ct)
                                         ?? creationTimeLocal;

                    var expiryWallClock = DateTime.SpecifyKind(complianceBase.Add(offset), DateTimeKind.Unspecified);
                    if (expiryWallClock <= complianceBase)
                    {
                        _logger.LogWarning("Computed SnapLock expiry {Expiry} is not in the future (base={Base}).",
                            expiryWallClock, complianceBase);
                        return new SnapshotResult
                        {
                            Success = false,
                            ErrorMessage = $"Computed SnapLock expiry '{expiryWallClock:yyyy-MM-dd HH:mm:ss}' must be in the future."
                        };
                    }

                    body.SnapLock = new SnapshotCreateBody.SnapLockBlock
                    {
                        ExpiryTime = expiryWallClock
                    };

                    _logger.LogInformation("SnapLock expiry set to {Expiry} (base={Base}, unit={Unit}, count={Count}).",
                        expiryWallClock, complianceBase, lockRetentionUnit, lockRetentionCount);
                }

                // 4) POST
                await SendSnapshotRequestAsync(volume.VolumeName, body, ct);

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
        public async Task<List<string>> GetSnapshotsAsync(int ControllerId, string volumeName, CancellationToken ct = default)
        {
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["op"] = "ListSnapshots",
                ["controllerId"] = ControllerId,
                ["volume"] = volumeName
            });

            _logger.LogDebug("Listing snapshots.");

            var controller = await _context.NetappControllers
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == ControllerId, ct);

            if (controller == null)
            {
                _logger.LogError("No NetappController record for ID {Id}.", ControllerId);
                throw new Exception("NetApp controller not found.");
            }

            var client = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            // Volume UUID
            var volLookupUrl = $"{baseUrl}storage/volumes?name={Uri.EscapeDataString(volumeName)}";
            var volResp = await client.GetAsync(volLookupUrl, ct);
            if (!volResp.IsSuccessStatusCode)
            {
                var body = await volResp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Volume lookup failed: {Status} {Body}", volResp.StatusCode, body);
            }
            volResp.EnsureSuccessStatusCode();

            using var volDoc = JsonDocument.Parse(await volResp.Content.ReadAsStringAsync(ct));
            var volRecs = volDoc.RootElement.GetProperty("records");
            if (volRecs.GetArrayLength() == 0)
            {
                _logger.LogInformation("Volume '{Volume}' not found.", volumeName);
                return new List<string>();
            }

            var volumeUuid = volRecs[0].GetProperty("uuid").GetString();

            // Snapshots
            var snapUrl = $"{baseUrl}storage/volumes/{volumeUuid}/snapshots?fields=name";
            var snapResp = await client.GetAsync(snapUrl, ct);
            if (!snapResp.IsSuccessStatusCode)
            {
                var body = await snapResp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Snapshot list failed: {Status} {Body}", snapResp.StatusCode, body);
            }
            snapResp.EnsureSuccessStatusCode();

            using var snapDoc = JsonDocument.Parse(await snapResp.Content.ReadAsStringAsync(ct));
            var snapshotNames = snapDoc.RootElement
                .GetProperty("records")
                .EnumerateArray()
                .Select(e => e.GetProperty("name").GetString() ?? string.Empty)
                .Where(name => !string.IsNullOrEmpty(name))
                .ToList();

            _logger.LogDebug("Found {Count} snapshots on volume {Volume}.", snapshotNames.Count, volumeName);
            return snapshotNames;
        }

        // ---------------------------------------------------------------------
        // List (batch) for UI tree
        // ---------------------------------------------------------------------
        public async Task<List<VolumeSnapshotTreeDto>> GetSnapshotsForVolumesAsync(HashSet<string> volumeNames, CancellationToken ct = default)
        {
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["op"] = "ListSnapshotsForVolumes",
                ["count"] = volumeNames?.Count
            });

            _logger.LogDebug("Listing snapshots for {Count} volumes.", volumeNames?.Count ?? 0);

            var controller = await _context.NetappControllers.AsNoTracking().FirstOrDefaultAsync(ct);
            if (controller == null)
            {
                _logger.LogError("No NetApp controller found.");
                throw new Exception("No NetApp controller found.");
            }

            var client = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            var volumesUrl = $"{baseUrl}storage/volumes?fields=name,uuid,svm.name";
            var volumesResp = await client.GetAsync(volumesUrl, ct);
            if (!volumesResp.IsSuccessStatusCode)
            {
                var body = await volumesResp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Volumes fetch failed: {Status} {Body}", volumesResp.StatusCode, body);
            }
            volumesResp.EnsureSuccessStatusCode();

            using var volumesDoc = JsonDocument.Parse(await volumesResp.Content.ReadAsStringAsync(ct));
            var volumeRecords = volumesDoc.RootElement.GetProperty("records");

            var result = new List<VolumeSnapshotTreeDto>();

            foreach (var vol in volumeRecords.EnumerateArray())
            {
                var name = vol.GetProperty("name").GetString() ?? "";
                if (!volumeNames.Contains(name))
                    continue;

                var uuid = vol.GetProperty("uuid").GetString() ?? "";
                var svm = vol.GetProperty("svm").GetProperty("name").GetString() ?? "";

                var snapUrl = $"{baseUrl}storage/volumes/{uuid}/snapshots?fields=name";
                var snapResp = await client.GetAsync(snapUrl, ct);
                if (!snapResp.IsSuccessStatusCode)
                {
                    var body = await snapResp.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("Snapshot list failed for volume {Volume}: {Status} {Body}", name, snapResp.StatusCode, body);
                    continue;
                }

                using var snapDoc = JsonDocument.Parse(await snapResp.Content.ReadAsStringAsync(ct));
                var snapshots = snapDoc.RootElement
                    .GetProperty("records")
                    .EnumerateArray()
                    .Select(e => e.GetProperty("name").GetString() ?? "")
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToList();

                _logger.LogDebug("Volume {Volume}: {Count} snapshots.", name, snapshots.Count);

                result.Add(new VolumeSnapshotTreeDto
                {
                    Vserver = svm,
                    VolumeName = name,
                    Snapshots = snapshots
                });
            }

            return result;
        }

        // ---------------------------------------------------------------------
        // Delete
        // ---------------------------------------------------------------------
        public async Task<DeleteSnapshotResult> DeleteSnapshotAsync(int controllerId, string volumeName, string snapshotName, CancellationToken ct = default)
        {
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["op"] = "DeleteSnapshot",
                ["controllerId"] = controllerId,
                ["volume"] = volumeName,
                ["snapshot"] = snapshotName
            });

            _logger.LogInformation("Deleting snapshot.");

            var result = new DeleteSnapshotResult();

            var controller = await _context.NetappControllers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == controllerId, ct);
            if (controller == null)
            {
                _logger.LogWarning("Controller {ControllerId} not found.", controllerId);
                result.ErrorMessage = $"NetApp controller #{controllerId} not found.";
                return result;
            }

            var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            try
            {
                // Volume UUID
                var volResp = await httpClient.GetAsync($"{baseUrl}storage/volumes?name={Uri.EscapeDataString(volumeName)}", ct);
                if (!volResp.IsSuccessStatusCode)
                {
                    var body = await volResp.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("Volume lookup failed: {Status} {Body}", volResp.StatusCode, body);
                }
                volResp.EnsureSuccessStatusCode();

                using var volDoc = JsonDocument.Parse(await volResp.Content.ReadAsStringAsync(ct));
                var volRecs = volDoc.RootElement.GetProperty("records");
                if (volRecs.GetArrayLength() == 0)
                {
                    _logger.LogWarning("Volume '{Volume}' not found.", volumeName);
                    result.ErrorMessage = $"Volume '{volumeName}' not found.";
                    return result;
                }
                var volumeUuid = volRecs[0].GetProperty("uuid").GetString()!;

                // Snapshot UUID
                var snapResp = await httpClient.GetAsync($"{baseUrl}storage/volumes/{volumeUuid}/snapshots?fields=name,uuid", ct);
                if (!snapResp.IsSuccessStatusCode)
                {
                    var body = await snapResp.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("Snapshot list failed: {Status} {Body}", snapResp.StatusCode, body);
                }
                snapResp.EnsureSuccessStatusCode();

                using var snapDoc = JsonDocument.Parse(await snapResp.Content.ReadAsStringAsync(ct));
                var snapRecs = snapDoc.RootElement
                    .GetProperty("records")
                    .EnumerateArray()
                    .FirstOrDefault(e => e.GetProperty("name").GetString() == snapshotName);

                if (snapRecs.ValueKind != JsonValueKind.Object)
                {
                    _logger.LogInformation("Snapshot '{Snapshot}' not found on volume '{Volume}'.", snapshotName, volumeName);
                    result.ErrorMessage = $"Snapshot '{snapshotName}' not found on volume '{volumeName}'.";
                    return result;
                }
                var snapshotUuid = snapRecs.GetProperty("uuid").GetString()!;

                // Delete
                var deleteResp = await httpClient.DeleteAsync($"{baseUrl}storage/volumes/{volumeUuid}/snapshots/{snapshotUuid}", ct);
                if (!deleteResp.IsSuccessStatusCode)
                {
                    var body = await deleteResp.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("Snapshot delete failed: {Status} {Body}", deleteResp.StatusCode, body);
                    result.ErrorMessage = $"Failed to delete snapshot: {deleteResp.StatusCode} - {body}";
                    return result;
                }

                _logger.LogInformation("Snapshot deleted successfully.");
                result.Success = true;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Delete snapshot cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception while deleting snapshot.");
                result.ErrorMessage = $"Exception: {ex.Message}";
            }

            return result;
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        /// <summary>
        /// Resolve the correct SnapLock compliance clock base time for the volume's home node.
        /// Returns Unspecified-kind DateTime representing node wall-clock time.
        /// </summary>
        private async Task<DateTime?> ResolveComplianceClockBaseAsync(string volumeName, CancellationToken ct)
        {
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["op"] = "ResolveComplianceClock",
                ["volume"] = volumeName
            });

            var controller = await _context.NetappControllers.AsNoTracking().FirstOrDefaultAsync(ct)
                             ?? throw new Exception("NetApp controller not found.");

            var http = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            // 1) Volume lookup → UUID + first aggregate UUID
            var volLookup = await http.GetAsync($"{baseUrl}storage/volumes?name={Uri.EscapeDataString(volumeName)}&fields=uuid,aggregates.uuid", ct);
            if (!volLookup.IsSuccessStatusCode)
            {
                var body = await volLookup.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Volume lookup failed: {Status} {Body}", volLookup.StatusCode, body);
            }
            volLookup.EnsureSuccessStatusCode();

            using var volDoc = JsonDocument.Parse(await volLookup.Content.ReadAsStringAsync(ct));
            var volRecs = volDoc.RootElement.GetProperty("records");
            if (volRecs.GetArrayLength() == 0)
            {
                _logger.LogWarning("Volume {Volume} not found.", volumeName);
                return null;
            }

            var vol = volRecs[0];
            if (!vol.TryGetProperty("aggregates", out var aggrArr) ||
                aggrArr.ValueKind != JsonValueKind.Array || aggrArr.GetArrayLength() == 0)
            {
                _logger.LogWarning("Volume {Volume} has no aggregates.", volumeName);
                return null;
            }

            var aggrUuid = aggrArr[0].GetProperty("uuid").GetString();
            if (string.IsNullOrEmpty(aggrUuid))
            {
                _logger.LogWarning("Aggregate UUID missing for volume {Volume}.", volumeName);
                return null;
            }

            // 2) Aggregate → home node UUID
            var aggrResp = await http.GetAsync($"{baseUrl}storage/aggregates/{aggrUuid}?fields=home_node.uuid,home_node.name", ct);
            if (!aggrResp.IsSuccessStatusCode)
            {
                var body = await aggrResp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Aggregate fetch failed: {Status} {Body}", aggrResp.StatusCode, body);
            }
            aggrResp.EnsureSuccessStatusCode();

            using var aggrDoc = JsonDocument.Parse(await aggrResp.Content.ReadAsStringAsync(ct));
            var homeNode = aggrDoc.RootElement.GetProperty("home_node");
            var nodeUuid = homeNode.GetProperty("uuid").GetString();

            if (string.IsNullOrEmpty(nodeUuid))
            {
                _logger.LogWarning("home_node.uuid missing for aggregate {AggregateUuid}.", aggrUuid);
                return null;
            }

            // 3) Compliance clock for that node (request fields=time)
            var ccResp = await http.GetAsync($"{baseUrl}storage/snaplock/compliance-clocks?node.uuid={Uri.EscapeDataString(nodeUuid)}&fields=time,node.name,node.uuid", ct);
            if (!ccResp.IsSuccessStatusCode)
            {
                var body = await ccResp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Compliance clock fetch failed: {Status} {Body}", ccResp.StatusCode, body);
            }
            ccResp.EnsureSuccessStatusCode();

            using var ccDoc = JsonDocument.Parse(await ccResp.Content.ReadAsStringAsync(ct));
            var ccRecs = ccDoc.RootElement.GetProperty("records");

            if (ccRecs.GetArrayLength() == 0)
            {
                _logger.LogWarning("No compliance clock record for node {NodeUuid}.", nodeUuid);
                return null;
            }

            var cc = ccRecs[0];
            if (!cc.TryGetProperty("time", out var timeProp) || timeProp.ValueKind != JsonValueKind.String)
            {
                _logger.LogWarning("Compliance clock time missing for node {NodeUuid}.", nodeUuid);
                return null;
            }

            var timeStr = timeProp.GetString();
            if (string.IsNullOrWhiteSpace(timeStr))
                return null;

            // ONTAP REST returns "yyyy-MM-dd HH:mm:ss" (wall-clock)
            if (DateTime.TryParseExact(timeStr, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);

            if (DateTime.TryParse(timeStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed2))
                return DateTime.SpecifyKind(parsed2, DateTimeKind.Unspecified);

            _logger.LogWarning("Unable to parse compliance clock '{Time}' for node {NodeUuid}.", timeStr, nodeUuid);
            return null;
        }

        private async Task SendSnapshotRequestAsync(
            string volumeName,
            SnapshotCreateBody body,
            CancellationToken ct = default)
        {
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["op"] = "PostSnapshot",
                ["volume"] = volumeName,
                ["name"] = body?.Name,
                ["label"] = body?.SnapMirrorLabel
            });

            var controller = await _context.NetappControllers.AsNoTracking().FirstOrDefaultAsync(ct)
                             ?? throw new Exception("NetApp controller not found.");

            var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            // Lookup volume UUID by name
            var lookupUrl = $"{baseUrl}storage/volumes?name={Uri.EscapeDataString(volumeName)}";
            var lookupResponse = await httpClient.GetAsync(lookupUrl, ct);
            if (!lookupResponse.IsSuccessStatusCode)
            {
                var bodyStr = await lookupResponse.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Volume lookup failed: {Status} {Body}", lookupResponse.StatusCode, bodyStr);
            }
            lookupResponse.EnsureSuccessStatusCode();

            var lookupJson = await lookupResponse.Content.ReadAsStringAsync(ct);
            using var lookupDoc = JsonDocument.Parse(lookupJson);
            var records = lookupDoc.RootElement.GetProperty("records");

            if (records.GetArrayLength() == 0)
                throw new Exception($"Volume '{volumeName}' not found in NetApp API.");

            var uuid = records[0].GetProperty("uuid").GetString();

            // POST snapshot using UUID
            var snapshotUrl = $"{baseUrl}storage/volumes/{uuid}/snapshots";
            var jsonPayload = JsonConvert.SerializeObject(body);
            _logger.LogDebug("POST {Url} payload: {Payload}", snapshotUrl, jsonPayload);

            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(snapshotUrl, content, ct);
            if (!response.IsSuccessStatusCode)
            {
                var resp = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Snapshot POST failed: {Status} {Body}", response.StatusCode, resp);
            }
            response.EnsureSuccessStatusCode();
        }
    }
}
