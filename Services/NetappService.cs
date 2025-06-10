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


namespace BareProx.Services
{
    public class NetappService : INetappService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<NetappService> _logger;
        private readonly IAppTimeZoneService _tz;
        private readonly INetappAuthService _authService;
        private readonly INetappVolumeService _volumeService;


        public NetappService(ApplicationDbContext context,
            ILogger<NetappService> logger,
            IAppTimeZoneService tz,
            INetappAuthService authService,
            INetappVolumeService volumeService)
        {
            _context = context;
            _logger = logger;
            _tz = tz;
            _authService = authService;
            _volumeService = volumeService;
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
                var volumes = await _volumeService.GetVolumesWithMountInfoAsync(clusterId, ct);
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


        public async Task<FlexCloneResult> CloneVolumeFromSnapshotAsync(
            string volumeName,
            string snapshotName,
            string cloneName,
            int controllerId,
            CancellationToken ct = default)
        {
            // 1) Fetch controller
            var controller = await _context.NetappControllers.FindAsync(controllerId, ct);
            if (controller == null)
                return new FlexCloneResult { Success = false, Message = "Controller not found." };

            var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            // 2) Lookup volume UUID + SVM name
            var volLookupUrl = $"{baseUrl}storage/volumes?name={Uri.EscapeDataString(volumeName)}&fields=uuid,svm.name";
            var volResp = await httpClient.GetAsync(volLookupUrl, ct);
            if (!volResp.IsSuccessStatusCode)
            {
                var err = await volResp.Content.ReadAsStringAsync(ct);
                return new FlexCloneResult { Success = false, Message = $"Volume lookup failed: {err}" };
            }

            using var volDoc = JsonDocument.Parse(await volResp.Content.ReadAsStringAsync(ct));
            var volRecs = volDoc.RootElement.GetProperty("records");
            if (volRecs.GetArrayLength() == 0)
                return new FlexCloneResult { Success = false, Message = $"Volume '{volumeName}' not found." };

            var volEntry = volRecs[0];
            var volumeUuid = volEntry.GetProperty("uuid").GetString()!;
            var svmName = volEntry.GetProperty("svm").GetProperty("name").GetString()!;

            // 3) Lookup snapshot UUID under that volume (optional if cloning snapshot)
            var snapUuid = (string?)null;
            if (!string.IsNullOrWhiteSpace(snapshotName))
            {
                var snapLookupUrl = $"{baseUrl}storage/volumes/{volumeUuid}/snapshots?fields=name,uuid";
                var snapResp = await httpClient.GetAsync(snapLookupUrl, ct);
                if (!snapResp.IsSuccessStatusCode)
                {
                    var err = await snapResp.Content.ReadAsStringAsync(ct);
                    return new FlexCloneResult { Success = false, Message = $"Snapshot lookup failed: {err}" };
                }

                using var snapDoc = JsonDocument.Parse(await snapResp.Content.ReadAsStringAsync(ct));
                var snapRec = snapDoc.RootElement.GetProperty("records")
                    .EnumerateArray()
                    .FirstOrDefault(e => e.GetProperty("name").GetString() == snapshotName);

                if (snapRec.ValueKind != JsonValueKind.Object)
                    return new FlexCloneResult { Success = false, Message = $"Snapshot '{snapshotName}' not found." };

                snapUuid = snapRec.GetProperty("uuid").GetString();
            }

            // 4) Build the FlexClone payload
            var payload = new Dictionary<string, object>
            {
                ["name"] = cloneName,
                ["clone"] = new Dictionary<string, object>
                {
                    ["parent_volume"] = new { uuid = volumeUuid },
                    ["is_flexclone"] = true
                },
                ["svm"] = new { name = svmName }
            };
            if (snapUuid != null)
            {
                // only include parent_snapshot if cloning a snapshot
                ((Dictionary<string, object>)payload["clone"])["parent_snapshot"] = new { uuid = snapUuid };
            }

            var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );
            var cloneResp = await httpClient.PostAsync($"{baseUrl}storage/volumes", content, ct);

            // 5) Handle the 202 Accepted and parse the Job UUID
            if (cloneResp.StatusCode == HttpStatusCode.Accepted)
            {
                using var respDoc = JsonDocument.Parse(await cloneResp.Content.ReadAsStringAsync(ct));
                var jobUuid = respDoc
                    .RootElement
                    .GetProperty("job")
                    .GetProperty("uuid")
                    .GetString();

                return new FlexCloneResult
                {
                    Success = true,
                    CloneVolumeName = cloneName,
                    JobUuid = jobUuid    // new property to track the async job
                };
            }

            // 6) On failure, bubble up the message
            var body = await cloneResp.Content.ReadAsStringAsync(ct);
            return new FlexCloneResult
            {
                Success = false,
                Message = $"Clone failed ({(int)cloneResp.StatusCode}): {body}"
            };
        }

        public async Task<List<string>> GetNfsEnabledIpsAsync(string vserver, CancellationToken ct = default)
        {
            var controller = await _context.NetappControllers.FirstOrDefaultAsync(ct);
            if (controller == null)
                throw new Exception("NetApp controller not found.");

            var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            var url = $"{baseUrl}network/ip/interfaces?svm.name={Uri.EscapeDataString(vserver)}&fields=ip.address,services";

            var resp = await httpClient.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement
                      .GetProperty("records")
                      .EnumerateArray()
                      .Where(e =>
                          e.TryGetProperty("services", out var services) &&
                          services.EnumerateArray().Any(s => s.GetString() == "data_nfs"))
                      .Select(e => e.GetProperty("ip").GetProperty("address").GetString() ?? "")
                      .Where(ip => !string.IsNullOrWhiteSpace(ip))
                      .Distinct()
                      .ToList();
        }



        public async Task<bool> CopyExportPolicyAsync(
            string sourceVolumeName,
            string targetCloneName,
            int controllerId,
            CancellationToken ct = default)
        {
            // 1) Fetch controller
            var controller = await _context.NetappControllers.FindAsync(controllerId, ct);
            if (controller == null) return false;

            var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            // 2) Lookup source export policy *name*
            var srcLookupUrl = $"{baseUrl}storage/volumes" +
                $"?name={Uri.EscapeDataString(sourceVolumeName)}" +
                "&fields=nas.export_policy.name";
            var srcResp = await httpClient.GetAsync(srcLookupUrl, ct);
            srcResp.EnsureSuccessStatusCode();

            using var srcDoc = JsonDocument.Parse(await srcResp.Content.ReadAsStringAsync(ct));
            var srcRecs = srcDoc.RootElement.GetProperty("records");
            if (srcRecs.GetArrayLength() == 0) return false;

            var policyName = srcRecs[0]
                .GetProperty("nas")
                .GetProperty("export_policy")
                .GetProperty("name")
                .GetString();

            if (string.IsNullOrWhiteSpace(policyName))
                return false;

            // 3) Lookup clone’s UUID
            var tgtLookupUrl = $"{baseUrl}storage/volumes" +
                $"?name={Uri.EscapeDataString(targetCloneName)}&fields=uuid";
            var tgtResp = await httpClient.GetAsync(tgtLookupUrl, ct);
            tgtResp.EnsureSuccessStatusCode();

            using var tgtDoc = JsonDocument.Parse(await tgtResp.Content.ReadAsStringAsync(ct));
            var tgtRecs = tgtDoc.RootElement.GetProperty("records");
            if (tgtRecs.GetArrayLength() == 0) return false;

            var tgtUuid = tgtRecs[0].GetProperty("uuid").GetString()!;

            // 4) PATCH by name instead of id
            var patchUrl = $"{baseUrl}storage/volumes/{tgtUuid}";
            var payload = new
            {
                nas = new
                {
                    export_policy = new { name = policyName }
                }
            };

            var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );
            var request = new HttpRequestMessage(HttpMethod.Patch, patchUrl) { Content = content };
            var patchResp = await httpClient.SendAsync(request, ct);

            if (!patchResp.IsSuccessStatusCode)
            {
                var err = await patchResp.Content.ReadAsStringAsync(ct);
                _logger.LogError("Export policy patch failed: {0}", err);
            }

            return patchResp.IsSuccessStatusCode;
        }




        public async Task<List<string>> ListFlexClonesAsync(int controllerId, CancellationToken ct = default)
        {
            var controller = await _context.NetappControllers.FindAsync(controllerId, ct);
            if (controller == null)
                throw new InvalidOperationException("Controller not found");

            var client = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            var url = $"{baseUrl}storage/volumes?name=restore_*";
            var resp = await client.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement
                      .GetProperty("records")
                      .EnumerateArray()
                      .Select(r => r.GetProperty("name").GetString())
                      .Where(n => !string.IsNullOrEmpty(n))
                      .Distinct()
                      .ToList();
        }


        public async Task<bool> SetVolumeExportPathAsync(
           string volumeUuid,
           string exportPath,
           int controllerId,
           CancellationToken ct = default)
        {
            // 1) Fetch controller
            var controller = await _context.NetappControllers
                .FirstOrDefaultAsync(c => c.Id == controllerId, ct);
            if (controller == null)
                return false;

            // 2) Use helper to get HttpClient and base URL
            var client = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            var patchUrl = $"{baseUrl}storage/volumes/{volumeUuid}";
            var geturl = $"{patchUrl}?fields=nas.path";
            var payloadObj = new { nas = new { path = exportPath } };
            var payloadJson = System.Text.Json.JsonSerializer.Serialize(payloadObj);

            // 3) Polly retry policy
            var retryPolicy = Policy<bool>
                .Handle<HttpRequestException>()
                .OrResult(ok => !ok)
                .WaitAndRetryAsync(
                    retryCount: 5,
                    sleepDurationProvider: i => TimeSpan.FromSeconds(Math.Pow(2, i - 1)),
                    onRetry: (outcome, delay, attempt, _) =>
                    {
                        if (outcome.Exception != null)
                        {
                            _logger?.LogWarning(
                                outcome.Exception,
                                "[ExportPath:{Attempt}] HTTP error; retrying in {Delay}s",
                                attempt, delay.TotalSeconds);
                        }
                        else
                        {
                            _logger?.LogWarning(
                                "[ExportPath:{Attempt}] verification failed; retrying in {Delay}s",
                                attempt, delay.TotalSeconds);
                        }
                    }
                );

            // 4) Execute PATCH + GET/verify under policy
            return await retryPolicy.ExecuteAsync(
                async (pollyCt) =>
                {
                    // a) PATCH
                    using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
                    var patchResp = await client.PatchAsync(patchUrl, content, pollyCt);
                    if (!patchResp.IsSuccessStatusCode)
                    {
                        _logger?.LogError("[ExportPath] PATCH failed: {Status}", patchResp.StatusCode);
                        return false;
                    }

                    // b) GET and capture raw JSON
                    var getResp = await client.GetAsync(geturl, pollyCt);
                    if (!getResp.IsSuccessStatusCode)
                    {
                        _logger?.LogError("[ExportPath] GET failed: {Status}", getResp.StatusCode);
                        return false;
                    }

                    string text = await getResp.Content.ReadAsStringAsync(pollyCt);
                    JsonDocument doc;
                    try
                    {
                        doc = JsonDocument.Parse(text);
                    }
                    catch (System.Text.Json.JsonException je)
                    {
                        _logger?.LogError(je, "[ExportPath] Invalid JSON: {Json}", text);
                        return false;
                    }

                    // c) Safe navigation: nas → path
                    if (!doc.RootElement.TryGetProperty("nas", out var nasElem) ||
                        !nasElem.TryGetProperty("path", out var pathElem))
                    {
                        _logger?.LogWarning("[ExportPath] missing 'nas.path' in response: {Json}", text);
                        return false;
                    }

                    var actual = pathElem.GetString();
                    if (actual != exportPath)
                    {
                        _logger?.LogInformation(
                            "[ExportPath] path not yet applied: expected={Expected} actual={Actual}",
                            exportPath, actual);
                        return false;
                    }

                    return true;
                },
                ct // pass the outer CancellationToken here
            );
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

        /// <summary>
        /// Recursively moves and renames all files under /images/{oldvmid} to /images/{newvmid} on the specified NetApp volume.
        /// Only files with oldvmid in their name are renamed; directory structure is preserved.
        /// </summary>
        public async Task<bool> MoveAndRenameAllVmFilesAsync(
           string volumeName,
           int controllerId,
           string oldvmid,
           string newvmid,
           CancellationToken ct = default)
        {
            // 1. Lookup controller and volume UUID
            var controller = await _context.NetappControllers.FindAsync(controllerId, ct);
            if (controller == null)
            {
                _logger.LogError("NetApp controller not found for ID {ControllerId}", controllerId);
                return false;
            }
            var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            var lookupUrl = $"{baseUrl}storage/volumes?name={Uri.EscapeDataString(volumeName)}";
            var lookupResp = await httpClient.GetAsync(lookupUrl, ct);
            if (!lookupResp.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to lookup volume {VolumeName} for rename: {Status}", volumeName, lookupResp.StatusCode);
                return false;
            }

            using var lookupDoc = JsonDocument.Parse(await lookupResp.Content.ReadAsStringAsync(ct));
            var lookupRecords = lookupDoc.RootElement.GetProperty("records");
            if (lookupRecords.GetArrayLength() == 0)
            {
                _logger.LogError("Volume '{VolumeName}' not found on controller.", volumeName);
                return false;
            }
            var volEntry = lookupRecords[0];
            var volumeUuid = volEntry.GetProperty("uuid").GetString()!;


            // 2. Ensure the destination directory exists: /images/{newvmid}
            var folderPath = $"images/{newvmid}";
            var encodedFolderPath = Uri.EscapeDataString(folderPath); // e.g. images%2F113
            var createDirUrl = $"{baseUrl}storage/volumes/{volumeUuid}/files/{encodedFolderPath}";
            var createDirBody = new { type = "directory", unix_permissions = 0 };
            var createDirContent = new StringContent(System.Text.Json.JsonSerializer.Serialize(createDirBody), Encoding.UTF8, "application/json");

            var createDirResp = await httpClient.PostAsync(createDirUrl, createDirContent, ct);
            if (createDirResp.IsSuccessStatusCode)
            {
                _logger.LogInformation("Created directory {FolderPath}", folderPath);
            }
            else
            {
                var err = await createDirResp.Content.ReadAsStringAsync(ct);
                // If it already exists, just log and continue
                if (err.Contains("\"error\"", StringComparison.OrdinalIgnoreCase) && !err.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Failed to create directory {FolderPath}: {Status} {Body}", folderPath, createDirResp.StatusCode, err);
                }
            }

            // 3. Collect expected filenames and move files recursively
            var expectedFiles = new List<string>();

            async Task<bool> MoveFilesRecursive(string oldFolder)
            {
                var listUrl = $"{baseUrl}storage/volumes/{volumeUuid}/files/{Uri.EscapeDataString(oldFolder.TrimStart('/'))}";
                var listResp = await httpClient.GetAsync(listUrl, ct);
                if (!listResp.IsSuccessStatusCode)
                {
                    _logger.LogError("Failed to list NetApp folder {Folder}: {Status}", oldFolder, listResp.StatusCode);
                    return false;
                }

                var json = await listResp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
                    return true; // nothing to do

                foreach (var rec in records.EnumerateArray())
                {
                    var name = rec.GetProperty("name").GetString();
                    var type = rec.GetProperty("type").GetString(); // "file" or "directory"
                    if (string.IsNullOrEmpty(name) || name == "." || name == "..")
                        continue;

                    if (type == "directory")
                    {
                        var subOldPath = $"{oldFolder}/{name}";
                        var ok = await MoveFilesRecursive(subOldPath);
                        if (!ok)
                            return false;
                    }
                    else if (type == "file")
                    {
                        if (!name.Contains(oldvmid))
                            continue;

                        var oldFilePath = $"{oldFolder}/{name}";
                        var newFileName = name.Replace(oldvmid, newvmid);
                        var newFilePath = oldFilePath
                            .Replace($"{oldvmid}/", $"{newvmid}/")
                            .Replace(name, newFileName);

                        // Remove any leading slash
                        if (oldFilePath.StartsWith("/")) oldFilePath = oldFilePath.Substring(1);
                        if (newFilePath.StartsWith("/")) newFilePath = newFilePath.Substring(1);

                        expectedFiles.Add(newFileName);

                        var moveUrl = $"{baseUrl}storage/file/moves";
                        var moveBody = new
                        {
                            files_to_move = new
                            {
                                sources = new[] {
                            new {
                                path = oldFilePath,
                                volume = new { name = volumeName, uuid = volumeUuid }
                            }
                        },
                                destinations = new[] {
                            new {
                                path = newFilePath,
                                volume = new { name = volumeName, uuid = volumeUuid }
                            }
                        }
                            }
                        };
                        var moveContent = new StringContent(System.Text.Json.JsonSerializer.Serialize(moveBody), Encoding.UTF8, "application/json");
                        var moveResp = await httpClient.PostAsync(moveUrl, moveContent, ct);
                        var moveErrBody = await moveResp.Content.ReadAsStringAsync(ct);
                        if (!moveResp.IsSuccessStatusCode)
                        {
                            _logger.LogError("Failed to move NetApp file {OldPath} → {NewPath}: {Status} {Body}",
                                oldFilePath, newFilePath, moveResp.StatusCode, moveErrBody);
                            return false;
                        }
                        _logger.LogInformation("Moved NetApp file: {OldPath} → {NewPath}", oldFilePath, newFilePath);
                    }
                }
                return true;
            }

            var moveResult = await MoveFilesRecursive($"/images/{oldvmid}");

            // 4. Poll for the files to appear in the destination folder
            async Task<bool> WaitForFilesInDestinationAsync(string destFolder, List<string> filesToCheck, int timeoutSeconds = 20)
            {
                var encodedDestFolder = Uri.EscapeDataString(destFolder);
                var url = $"{baseUrl}storage/volumes/{volumeUuid}/files/{encodedDestFolder}";

                for (int i = 0; i < timeoutSeconds; i++)
                {
                    var resp = await httpClient.GetAsync(url, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        var content = await resp.Content.ReadAsStringAsync(ct);
                        using var doc = JsonDocument.Parse(content);
                        if (doc.RootElement.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array)
                        {
                            var foundFiles = new HashSet<string>(
                                records.EnumerateArray()
                                       .Where(r => r.GetProperty("type").GetString() == "file")
                                       .Select(r => r.GetProperty("name").GetString() ?? "")
                            );
                            if (filesToCheck.All(f => foundFiles.Contains(f)))
                            {
                                _logger.LogInformation("All files found in destination after {Seconds}s", i + 1);
                                return true;
                            }
                        }
                    }
                    await Task.Delay(1000);
                }

                _logger.LogWarning(
                    "Not all files appeared in destination folder {DestFolder} after {Timeout}s: {Files}",
                    destFolder, timeoutSeconds, string.Join(",", filesToCheck));
                return false;
            }

            var waitResult = await WaitForFilesInDestinationAsync($"images/{newvmid}", expectedFiles);
            return moveResult && waitResult;
        }

        public async Task SyncSelectedVolumesAsync(int controllerId, string volumeUuid, CancellationToken ct = default)
        {
            var controller = await _context.NetappControllers.FirstOrDefaultAsync(c => c.Id == controllerId, ct);
            if (controller == null)
                throw new Exception($"Controller {controllerId} not found.");

            var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl);
            var url = $"{baseUrl}storage/volumes/{volumeUuid}?fields=space,nas.export_policy.name,snapshot_locking_enabled";

            var resp = await httpClient.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var selected = await _context.SelectedNetappVolumes
                .FirstOrDefaultAsync(v => v.Uuid == volumeUuid && v.NetappControllerId == controllerId, ct);
            if (selected == null)
                throw new Exception($"SelectedNetappVolume {volumeUuid} not found for controller {controllerId}");

            // --- Map fields (safe navigation)
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
        }
        public async Task UpdateAllSelectedVolumesAsync(CancellationToken ct = default)
        {
            var selectedVolumes = await _context.SelectedNetappVolumes
                .Select(v => new { v.NetappControllerId, v.Uuid })
                .ToListAsync(ct);

            foreach (var v in selectedVolumes)
            {
                try
                {
                    await SyncSelectedVolumesAsync(v.NetappControllerId, v.Uuid, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to sync volume {Uuid} for controller {Controller}", v.Uuid, v.NetappControllerId);
                }
            }

            // ------ Helper Functions ------



        }
    }
}
