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

using System.Text.Json;
using System.Text;
using BareProx.Data;
using BareProx.Models;
using Microsoft.EntityFrameworkCore;
using System.Net;
using Polly;
using Newtonsoft.Json;
using BareProx.Services.Netapp;

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
        public async Task<SnapshotResult> CreateSnapshotAsync(
   int clusterId,
   string storageName,
   string snapmirrorLabel,
   bool snapLocking = false,
   int? lockRetentionCount = null,
   string? lockRetentionUnit = null,
   CancellationToken ct = default
)
        {
            try
            {
                var volumes = await _netappVolumeService.GetVolumesWithMountInfoAsync(clusterId, ct);
                var volume = volumes.FirstOrDefault(v =>
                    v.VolumeName.Equals(storageName, StringComparison.OrdinalIgnoreCase));

                if (volume == null)
                {
                    return new SnapshotResult
                    {
                        Success = false,
                        ErrorMessage = $"No matching NetApp volume for storage name '{storageName}'."
                    };
                }

                // 1) Capture a single timestamp in app‐tz
                var creationTime = _tz.ConvertUtcToApp(DateTime.UtcNow);

                // 2) Build snapshot name from that timestamp
                var timestamp = creationTime.ToString("yyyy-MM-dd-HH_mm-ss");
                var snapshotName = $"BP_{snapmirrorLabel}-{timestamp}";

                // 3) Prepare payload
                var body = new SnapshotCreateBody
                {
                    Name = snapshotName,
                    SnapMirrorLabel = snapmirrorLabel
                };

                // 4) If locking is requested, validate & compute expiry
                if (snapLocking)
                {
                    if (lockRetentionCount == null || string.IsNullOrEmpty(lockRetentionUnit))
                    {
                        return new SnapshotResult
                        {
                            Success = false,
                            ErrorMessage = "snapLocking requested but no retention count/unit supplied."
                        };
                    }

                    // convert count+unit to TimeSpan
                    TimeSpan offset = lockRetentionUnit switch
                    {
                        "Hours" => TimeSpan.FromHours(lockRetentionCount.Value),
                        "Days" => TimeSpan.FromDays(lockRetentionCount.Value),
                        "Weeks" => TimeSpan.FromDays(lockRetentionCount.Value * 7),
                        _ => throw new ArgumentException($"Unknown unit '{lockRetentionUnit}'")
                    };

                    var expiry = creationTime.Add(offset);
                    if (expiry <= creationTime)
                    {
                        return new SnapshotResult
                        {
                            Success = false,
                            ErrorMessage = $"Expiry time '{expiry:yyyy-MM-dd HH:mm:ss}' must be in the future."
                        };
                    }

                    // attach to payload
                    body.ExpiryTime = expiry;
                    body.SnapLock = new SnapshotCreateBody.SnapLockBlock { ExpiryTime = expiry };
                    body.SnapLockExpiryTime = expiry;
                }

                // 5) Send the snapshot request
                await SendSnapshotRequestAsync(volume.VolumeName, body, ct);

                // 6) Return success
                return new SnapshotResult
                {
                    Success = true,
                    SnapshotName = snapshotName
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create snapshot.");
                return new SnapshotResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        public async Task<List<string>> GetSnapshotsAsync(int ControllerId, string volumeName, CancellationToken ct = default)
        {
            // lookup Controller
            var controller = await _context.NetappControllers
                .FirstOrDefaultAsync(c => c.Id == ControllerId, ct);
            if (controller == null)
            {
                _logger.LogError(
                    "No NetappController record for ID {Id}", ControllerId);
                throw new Exception("NetApp controller not found.");
            }

            // 🔐 Use encrypted credentials + helper
            var client = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            // Step 1: Lookup the volume UUID using the volume name
            var volLookupUrl = $"{baseUrl}storage/volumes?name={Uri.EscapeDataString(volumeName)}";
            var volResp = await client.GetAsync(volLookupUrl, ct);
            volResp.EnsureSuccessStatusCode();

            using var volDoc = JsonDocument.Parse(await volResp.Content.ReadAsStringAsync(ct));
            var volRecs = volDoc.RootElement.GetProperty("records");
            if (volRecs.GetArrayLength() == 0)
                return new List<string>(); // Volume not found

            var volumeUuid = volRecs[0].GetProperty("uuid").GetString();

            // Step 2: Fetch snapshots for that volume
            var snapUrl = $"{baseUrl}storage/volumes/{volumeUuid}/snapshots?fields=name";
            var snapResp = await client.GetAsync(snapUrl, ct);
            snapResp.EnsureSuccessStatusCode();

            using var snapDoc = JsonDocument.Parse(await snapResp.Content.ReadAsStringAsync(ct));
            var snapshotNames = snapDoc.RootElement
                .GetProperty("records")
                .EnumerateArray()
                .Select(e => e.GetProperty("name").GetString() ?? string.Empty)
                .Where(name => !string.IsNullOrEmpty(name))
                .ToList();

            return snapshotNames;
        }

        public async Task<List<VolumeSnapshotTreeDto>> GetSnapshotsForVolumesAsync(HashSet<string> volumeNames, CancellationToken ct = default)
        {
            var controller = await _context.NetappControllers.FirstOrDefaultAsync(ct);
            if (controller == null)
                throw new Exception("No NetApp controller found.");

            // 🔐 Use encrypted credentials and base URL helper
            var client = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            var volumesUrl = $"{baseUrl}storage/volumes?fields=name,uuid,svm.name";
            var volumesResp = await client.GetAsync(volumesUrl, ct);
            volumesResp.EnsureSuccessStatusCode();

            using var volumesDoc = JsonDocument.Parse(await volumesResp.Content.ReadAsStringAsync(ct));
            var volumeRecords = volumesDoc.RootElement.GetProperty("records");

            var result = new List<VolumeSnapshotTreeDto>();

            foreach (var vol in volumeRecords.EnumerateArray())
            {
                var name = vol.GetProperty("name").GetString() ?? "";
                var uuid = vol.GetProperty("uuid").GetString() ?? "";
                var svm = vol.GetProperty("svm").GetProperty("name").GetString() ?? "";

                if (!volumeNames.Contains(name))
                    continue;

                var snapUrl = $"{baseUrl}storage/volumes/{uuid}/snapshots?fields=name";
                var snapResp = await client.GetAsync(snapUrl, ct);
                if (!snapResp.IsSuccessStatusCode)
                    continue;

                using var snapDoc = JsonDocument.Parse(await snapResp.Content.ReadAsStringAsync(ct));
                var snapshots = snapDoc.RootElement
                    .GetProperty("records")
                    .EnumerateArray()
                    .Select(e => e.GetProperty("name").GetString() ?? "")
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToList();

                result.Add(new VolumeSnapshotTreeDto
                {
                    Vserver = svm,
                    VolumeName = name,
                    Snapshots = snapshots
                });
            }

            return result;
        }

        public async Task<DeleteSnapshotResult> DeleteSnapshotAsync(int controllerId, string volumeName, string snapshotName, CancellationToken ct = default)
        {
            var result = new DeleteSnapshotResult();

            var controller = await _context.NetappControllers.FirstOrDefaultAsync(c => c.Id == controllerId, ct);
            if (controller == null)
            {
                result.ErrorMessage = $"NetApp controller #{controllerId} not found.";
                return result;
            }

            var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            try
            {
                // 1. Get volume UUID
                var volResp = await httpClient.GetAsync($"{baseUrl}storage/volumes?name={Uri.EscapeDataString(volumeName)}", ct);
                volResp.EnsureSuccessStatusCode();
                using var volDoc = JsonDocument.Parse(await volResp.Content.ReadAsStringAsync(ct));
                var volRecs = volDoc.RootElement.GetProperty("records");
                if (volRecs.GetArrayLength() == 0)
                {
                    result.ErrorMessage = $"Volume '{volumeName}' not found.";
                    return result;
                }
                var volumeUuid = volRecs[0].GetProperty("uuid").GetString()!;

                // 2. Get snapshot UUID
                var snapResp = await httpClient.GetAsync($"{baseUrl}storage/volumes/{volumeUuid}/snapshots?fields=name,uuid", ct);
                snapResp.EnsureSuccessStatusCode();
                using var snapDoc = JsonDocument.Parse(await snapResp.Content.ReadAsStringAsync(ct));
                var snapRecs = snapDoc.RootElement
                    .GetProperty("records")
                    .EnumerateArray()
                    .FirstOrDefault(e => e.GetProperty("name").GetString() == snapshotName);

                if (snapRecs.ValueKind != JsonValueKind.Object)
                {
                    result.ErrorMessage = $"Snapshot '{snapshotName}' not found on volume '{volumeName}'.";
                    return result;
                }
                var snapshotUuid = snapRecs.GetProperty("uuid").GetString()!;

                // 3. Delete the snapshot
                var deleteResp = await httpClient.DeleteAsync($"{baseUrl}storage/volumes/{volumeUuid}/snapshots/{snapshotUuid}", ct);
                if (deleteResp.IsSuccessStatusCode)
                {
                    result.Success = true;
                }
                else
                {
                    var body = await deleteResp.Content.ReadAsStringAsync(ct);
                    result.ErrorMessage = $"Failed to delete snapshot: {deleteResp.StatusCode} - {body}";
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Exception: {ex.Message}";
            }

            return result;
        }

        private async Task SendSnapshotRequestAsync(
            string volumeName,
            SnapshotCreateBody body,
            CancellationToken ct = default
            )
        {
            // 1) Find a NetApp controller in the DB (unchanged)
            var controller = await _context.NetappControllers.FirstOrDefaultAsync(ct);
            if (controller == null)
                throw new Exception("NetApp controller not found.");

            // 2) Build an authenticated HttpClient (unchanged)
            var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            // 3) Lookup volume UUID by name (unchanged)
            var lookupUrl = $"{baseUrl}storage/volumes?name={Uri.EscapeDataString(volumeName)}";
            var lookupResponse = await httpClient.GetAsync(lookupUrl, ct);
            lookupResponse.EnsureSuccessStatusCode();

            var lookupJson = await lookupResponse.Content.ReadAsStringAsync(ct);
            using var lookupDoc = JsonDocument.Parse(lookupJson);
            var records = lookupDoc.RootElement.GetProperty("records");

            if (records.GetArrayLength() == 0)
                throw new Exception($"Volume '{volumeName}' not found in NetApp API.");

            var uuid = records[0].GetProperty("uuid").GetString();

            // 4) Create snapshot using UUID, but serialize the full DTO (instead of just name/label)
            var snapshotUrl = $"{baseUrl}storage/volumes/{uuid}/snapshots";

            // Use Newtonsoft.Json here so that your [JsonConverter] attributes (CustomDateTimeConverter) are honored
            var jsonPayload = JsonConvert.SerializeObject(body);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync(snapshotUrl, content, ct);
            response.EnsureSuccessStatusCode();
        }


    }
}