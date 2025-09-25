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
using System.Text.Json;

namespace BareProx.Services
{
    public class NetappSnapmirrorService : INetappSnapmirrorService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<NetappSnapmirrorService> _logger;
        private readonly IAppTimeZoneService _tz;
        private readonly INetappAuthService _authService;
        private readonly INetappVolumeService _volumeService;


        public NetappSnapmirrorService(ApplicationDbContext context,
            ILogger<NetappSnapmirrorService> logger,
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

        /// <summary>
        /// Verifies the snapshot exists, then fetches the SnapMirror relation by UUID.
        /// </summary>
        public async Task<SnapMirrorRelation> GetSnapMirrorRelationAsync(string relationshipUuid, CancellationToken ct = default)
        {
            // 1) Lookup the relation record directly in the database
            var relation = await _context.SnapMirrorRelations
                .FirstOrDefaultAsync(r => r.Uuid == relationshipUuid, ct);
            if (relation == null)
            {
                throw new InvalidOperationException(
                    $"SnapMirror relationship '{relationshipUuid}' not found in database.");
            }

            // 2) Lookup the controller entity
            var controller = await _context.NetappControllers
                .FirstOrDefaultAsync(c => c.Id == relation.DestinationControllerId, ct);

            if (controller == null)
            {
                throw new InvalidOperationException(
                    $"NetApp controller with ID {relation.DestinationControllerId} not found.");
            }

            // 3) Optionally re-fetch live state from the API:
            //    (you can remove this if you trust your DB copy)
            var client = _authService.CreateAuthenticatedClient(controller, out var baseUrl);
            var resp = await client.GetAsync(
                $"{baseUrl}snapmirror/relationships/{relationshipUuid}", ct);
            resp.EnsureSuccessStatusCode();


            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var live = await System.Text.Json.JsonSerializer.DeserializeAsync<SnapMirrorRelation>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);

            if (live == null)
            {
                throw new InvalidOperationException(
                    $"No SnapMirrorRelation returned from API for UUID {relationshipUuid}");
            }

            // 4) Merge the live state back into our DB record if you like, or just return it:
            //    Here we return the live object
            return live;
        }

        public async Task<List<SnapMirrorRelation>> GetSnapMirrorRelationsAsync(NetappController controller, CancellationToken ct = default)
        {
            var client = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            var url = $"{baseUrl}snapmirror/relationships?return_timeout=120" +
          "&fields=*";

            var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to fetch SnapMirror relations from controller {0}", controller.Id);
                return new List<SnapMirrorRelation>();
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var records = doc.RootElement.GetProperty("records");

            var result = new List<SnapMirrorRelation>();

            foreach (var entry in records.EnumerateArray())
            {
                var src = entry.GetProperty("source");
                var dst = entry.GetProperty("destination");
                var policy = entry.GetProperty("policy");

                var srcPath = src.GetProperty("path").GetString() ?? "";
                var dstPath = dst.GetProperty("path").GetString() ?? "";
                var policyName = policy.GetProperty("name").GetString() ?? "";
                var policyType = policy.GetProperty("type").GetString() ?? "";
                var policyUuid = policy.TryGetProperty("uuid", out var polUuid) ? polUuid.GetString() : null;

                var state = entry.GetProperty("state").GetString() ?? "";
                var lagTime = entry.TryGetProperty("lag_time", out var lagProp) ? lagProp.GetString() : null;
                var healthy = entry.TryGetProperty("healthy", out var healthyProp) && healthyProp.GetBoolean();
                var uuid = entry.GetProperty("uuid").GetString() ?? "";

                var exportedSnapshot = entry.TryGetProperty("exported_snapshot", out var expSnapProp) ? expSnapProp.GetString() : null;
                var totalTransferDuration = entry.TryGetProperty("total_transfer_duration", out var totTrDur) ? totTrDur.GetString() : null;
                var totalTransferBytes = entry.TryGetProperty("total_transfer_bytes", out var totTrBytes) ? totTrBytes.GetInt64() : (long?)null;
                var lastTransferType = entry.TryGetProperty("last_transfer_type", out var ltType) ? ltType.GetString() : null;
                var lastTransferCompressionRatio = entry.TryGetProperty("last_transfer_network_compression_ratio", out var ltRatio) ? ltRatio.GetString() : null;
                var backoffLevel = entry.TryGetProperty("backoff_level", out var backoffProp) ? backoffProp.GetString() : null;

                // Nested objects
                var srcClusterName = src.TryGetProperty("cluster", out var srcCluster) && srcCluster.TryGetProperty("name", out var scn) ? scn.GetString() : null;
                var dstClusterName = dst.TryGetProperty("cluster", out var dstCluster) && dstCluster.TryGetProperty("name", out var dcn) ? dcn.GetString() : null;
                var srcSvmName = src.TryGetProperty("svm", out var srcSvm) && srcSvm.TryGetProperty("name", out var ssvm) ? ssvm.GetString() : null;
                var dstSvmName = dst.TryGetProperty("svm", out var dstSvm) && dstSvm.TryGetProperty("name", out var dsvm) ? dsvm.GetString() : null;

                // Transfer details (optional, just store the string fields in DB)
                string? lastTransferState = null;
                string? lastTransferDuration = null;
                DateTime? lastTransferEndTime = null;
                if (entry.TryGetProperty("transfer", out var transferProp) && transferProp.ValueKind == JsonValueKind.Object)
                {
                    lastTransferState = transferProp.TryGetProperty("state", out var ls) ? ls.GetString() : null;
                    lastTransferDuration = transferProp.TryGetProperty("total_duration", out var ld) ? ld.GetString() : null;
                    if (transferProp.TryGetProperty("end_time", out var le))
                    {
                        var endTimeStr = le.GetString();
                        if (DateTime.TryParse(endTimeStr, out var parsed))
                            lastTransferEndTime = parsed;   // lastTransferEndTime is DateTime?
                    }
                }

                // Extract volume names from paths: "clustername:volumename"
                var srcVol = srcPath.Contains(':') ? srcPath.Split(':')[1] : srcPath;
                var dstVol = dstPath.Contains(':') ? dstPath.Split(':')[1] : dstPath;

                result.Add(new SnapMirrorRelation
                {
                    SourceVolume = srcVol,
                    DestinationVolume = dstVol,
                    SourceControllerId = 0, // Will be resolved elsewhere
                    DestinationControllerId = controller.Id,
                    RelationshipType = policyType,
                    SnapMirrorPolicy = policyName,
                    state = state,
                    lag_time = lagTime,
                    healthy = healthy,
                    Uuid = uuid,

                    SourceClusterName = srcClusterName,
                    DestinationClusterName = dstClusterName,
                    SourceSvmName = srcSvmName,
                    DestinationSvmName = dstSvmName,
                    PolicyUuid = policyUuid,
                    PolicyType = policyType,
                    ExportedSnapshot = exportedSnapshot,
                    TotalTransferDuration = totalTransferDuration,
                    TotalTransferBytes = totalTransferBytes,
                    LastTransferType = lastTransferType,
                    LastTransferCompressionRatio = lastTransferCompressionRatio,
                    BackoffLevel = backoffLevel,
                    LastTransferState = lastTransferState,
                    LastTransferEndTime = lastTransferEndTime,
                    LastTransferDuration = lastTransferDuration
                });
            }

            return result;
        }

        public async Task<bool> TriggerSnapMirrorUpdateAsync(string relationshipUuid, CancellationToken ct = default)
        {
            // 1) Lookup the relation record
            var relation = await _context.SnapMirrorRelations
                .FirstOrDefaultAsync(r => r.Uuid == relationshipUuid, ct);
            if (relation == null)
            {
                _logger.LogError(
                    "No SnapMirrorRelation found for UUID {Uuid}", relationshipUuid);
                return false;
            }

            // 2) Lookup the Destination-controller entity
            var controller = await _context.NetappControllers
                .FirstOrDefaultAsync(c => c.Id == relation.DestinationControllerId, ct);
            if (controller == null)
            {
                _logger.LogError(
                    "No NetappController record for ID {Id}", relation.DestinationControllerId);
                return false;
            }

            // 3) (Optional) verify the snapshot exists on the source volume
            //    If you still want that guard, uncomment the next lines:
            //
            // if (!await SnapshotExistsOnControllerAsync(
            //         relation.SourceVolume, 
            //         /* your snapshotName here */, 
            //         controller))
            // {
            //     _logger.LogWarning(
            //         "Snapshot not found on volume {Vol} — skipping update", 
            //         relation.SourceVolume);
            //     return false;
            // }

            // 4) Trigger the SnapMirror update via HTTP
            var client = _authService.CreateAuthenticatedClient(controller, out var baseUrl);
            var url = $"{baseUrl}snapmirror/relationships/{relationshipUuid}/transfers";
            var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

            var resp = await client.PostAsync(url, content, ct);
            // debug var raw = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Failed to trigger SnapMirror update for UUID {Uuid} on controller {Id}: {Status}",
                    relationshipUuid, controller.Id, resp.StatusCode);
                return false;
            }

            _logger.LogInformation(
                "Triggered SnapMirror update for UUID {Uuid} on controller {Id}",
                relationshipUuid, controller.Id);
            return true;
        }

        public async Task SyncSnapMirrorRelationsAsync(CancellationToken ct = default)
        {
            // 1) Load all controllers in memory
            var controllers = await _context.NetappControllers
                .AsNoTracking()
                .ToListAsync(ct);

            var controllerIds = controllers.Select(c => c.Id).ToHashSet();

            // 2) Find SnapMirrorRelations with missing controllers
            var invalidRelations = await _context.SnapMirrorRelations
                .Where(r =>
                    !controllerIds.Contains(r.SourceControllerId) ||
                    !controllerIds.Contains(r.DestinationControllerId))
                .ToListAsync(ct);

            if (invalidRelations.Any())
            {
                _logger.LogWarning(
                    "Removing {Count} SnapMirrorRelations with invalid controller references.",
                    invalidRelations.Count);

                _context.SnapMirrorRelations.RemoveRange(invalidRelations);
                await _context.SaveChangesAsync(ct);
            }

            // 3) Proceed with regular sync for secondary controllers
            var secondaryControllers = controllers.Where(c => !c.IsPrimary).ToList();

            foreach (var secondary in secondaryControllers)
            {
                // 3a) Find selected destination volumes for this controller
                var selectedDestVolumes = await _context.SelectedNetappVolumes
                    .Where(v => v.NetappControllerId == secondary.Id)
                    .Select(v => v.VolumeName)
                    .ToListAsync(ct);

                if (!selectedDestVolumes.Any())
                {
                    // If no volumes are selected, remove all relations for this controller
                    var toRemoveAll = await _context.SnapMirrorRelations
                        .Where(r => r.DestinationControllerId == secondary.Id)
                        .ToListAsync(ct);

                    if (toRemoveAll.Count > 0)
                    {
                        _logger.LogInformation(
                            "Removing {Count} SnapMirrorRelations because no selected volumes remain for controller {ControllerId}.",
                            toRemoveAll.Count, secondary.Id);

                        _context.SnapMirrorRelations.RemoveRange(toRemoveAll);
                        await _context.SaveChangesAsync(ct);
                    }
                    continue;
                }

                // 3b) Pull live SnapMirror relationships
                var liveRelations = await GetSnapMirrorRelationsAsync(secondary, ct);

                // 3c) Filter by selected destination volumes
                var filtered = liveRelations
                    .Where(r => selectedDestVolumes
                        .Contains(r.DestinationVolume, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                // 3d) Load current DB relations for this controller
                var existingDbRelations = await _context.SnapMirrorRelations
                    .Where(r => r.DestinationControllerId == secondary.Id)
                    .ToListAsync(ct);

                var dbLookup = existingDbRelations
                    .ToDictionary(
                        r => (r.DestinationVolume.ToLowerInvariant(), r.SourceVolume.ToLowerInvariant())
                    );

                var allSelectedVolumeEntries = await _context.SelectedNetappVolumes.ToListAsync(ct);

                // 3e) Update or insert
                foreach (var relation in filtered)
                {
                    var key = (relation.DestinationVolume.ToLowerInvariant(), relation.SourceVolume.ToLowerInvariant());

                    if (dbLookup.TryGetValue(key, out var existing))
                    {
                        // Update existing record
                        existing.Uuid = relation.Uuid;
                        existing.SourceControllerId = allSelectedVolumeEntries
                            .FirstOrDefault(c => c.VolumeName.Equals(relation.SourceVolume, StringComparison.OrdinalIgnoreCase))
                            ?.NetappControllerId ?? 0;
                        existing.DestinationControllerId = relation.DestinationControllerId;
                        existing.RelationshipType = relation.RelationshipType;
                        existing.SnapMirrorPolicy = relation.SnapMirrorPolicy;
                        existing.state = relation.state;
                        existing.lag_time = relation.lag_time;
                        existing.healthy = relation.healthy;
                        existing.SourceClusterName = relation.SourceClusterName;
                        existing.DestinationClusterName = relation.DestinationClusterName;
                        existing.SourceSvmName = relation.SourceSvmName;
                        existing.DestinationSvmName = relation.DestinationSvmName;
                        existing.LastTransferState = relation.LastTransferState;
                        existing.LastTransferEndTime = relation.LastTransferEndTime;
                        existing.LastTransferDuration = relation.LastTransferDuration;
                        existing.PolicyUuid = relation.PolicyUuid;
                        existing.PolicyType = relation.PolicyType;
                        existing.ExportedSnapshot = relation.ExportedSnapshot;
                        existing.TotalTransferDuration = relation.TotalTransferDuration;
                        existing.TotalTransferBytes = relation.TotalTransferBytes;
                        existing.LastTransferType = relation.LastTransferType;
                        existing.LastTransferCompressionRatio = relation.LastTransferCompressionRatio;
                        existing.BackoffLevel = relation.BackoffLevel;

                        dbLookup.Remove(key);
                    }
                    else
                    {
                        // Insert new relation
                        relation.SourceControllerId = allSelectedVolumeEntries
                            .FirstOrDefault(c => c.VolumeName.Equals(relation.SourceVolume, StringComparison.OrdinalIgnoreCase))
                            ?.NetappControllerId ?? 0;
                        _context.SnapMirrorRelations.Add(relation);
                    }
                }

                // 3f) Remove stale relations
                var toRemove = dbLookup.Values.ToList();
                if (toRemove.Any())
                {
                    _logger.LogInformation(
                        "Removing {Count} stale SnapMirrorRelations for controller {ControllerId}.",
                        toRemove.Count, secondary.Id);

                    _context.SnapMirrorRelations.RemoveRange(toRemove);
                }

                await _context.SaveChangesAsync(ct);
            }
        }


        public async Task<SnapMirrorPolicy?> SnapMirrorPolicyGet(int controllerId, string policyUuid, CancellationToken ct = default)
        {
            var controller = await _context.NetappControllers.FindAsync(controllerId, ct);
            if (controller == null)
                throw new Exception($"NetApp controller {controllerId} not found.");

            var client = _authService.CreateAuthenticatedClient(controller, out var baseUrl);
            var url = $"{baseUrl}snapmirror/policies/{policyUuid}?fields=*";
            var resp = await client.GetAsync(url, ct);

            if (!resp.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var entry = doc.RootElement;

            var policy = new SnapMirrorPolicy
            {
                Uuid = entry.GetProperty("uuid").GetString() ?? "",
                Name = entry.GetProperty("name").GetString() ?? "",
                Scope = entry.TryGetProperty("scope", out var scopeProp) ? scopeProp.GetString() ?? "" : "",
                Type = entry.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "" : "",
                NetworkCompressionEnabled = entry.TryGetProperty("network_compression_enabled", out var ncProp) && ncProp.GetBoolean(),
                Throttle = entry.TryGetProperty("throttle", out var thrProp) ? thrProp.GetInt32() : 0,
                Retentions = new List<SnapMirrorPolicyRetention>()
            };

            if (entry.TryGetProperty("retention", out var retentionProp) && retentionProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var ret in retentionProp.EnumerateArray())
                {
                    policy.Retentions.Add(new SnapMirrorPolicyRetention
                    {
                        Label = ret.TryGetProperty("label", out var label) ? label.GetString() ?? "" : "",
                        Count = int.TryParse(ret.TryGetProperty("count", out var countProp) ? countProp.GetString() : "0", out var cntVal) ? cntVal : 0,
                        Preserve = ret.TryGetProperty("preserve", out var pres) && pres.GetBoolean(),
                        Warn = ret.TryGetProperty("warn", out var warn) ? warn.GetInt32() : 0,
                        Period = ret.TryGetProperty("period", out var per) ? per.GetString() : null
                    });
                }
            }
            return policy;
        }
    }
}
