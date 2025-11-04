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
using System.Net.Http;
using BareProx.Data;
using BareProx.Models;
using BareProx.Services.Netapp;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Services
{
    public class NetappSnapmirrorService : INetappSnapmirrorService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<NetappSnapmirrorService> _logger;
        private readonly IAppTimeZoneService _tz;
        private readonly INetappAuthService _authService;
        private readonly INetappVolumeService _volumeService;

        public NetappSnapmirrorService(
            ApplicationDbContext context,
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

        private static string EnsureSlash(string baseUrl) =>
            string.IsNullOrEmpty(baseUrl) || baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";

        private static int GetInt32Flexible(JsonElement e)
        {
            // ONTAP sometimes returns numbers as strings
            if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var i)) return i;
            if (e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), out i)) return i;
            return 0;
        }

        private static long? GetInt64Flexible(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var l)) return l;
            if (e.ValueKind == JsonValueKind.String && long.TryParse(e.GetString(), out l)) return l;
            return null;
        }

        private static DateTime? GetDateTimeFlexible(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(e.GetString(), out var d))
                return d;
            return null;
        }

        /// <summary>
        /// Verifies the relationship exists and returns LIVE state from ONTAP.
        /// </summary>
        public async Task<SnapMirrorRelation> GetSnapMirrorRelationAsync(string relationshipUuid, CancellationToken ct = default)
        {
            // DB check (so we can map to destination controller)
            var relationRow = await _context.SnapMirrorRelations
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Uuid == relationshipUuid, ct);

            if (relationRow == null)
                throw new InvalidOperationException($"SnapMirror relationship '{relationshipUuid}' not found in database.");

            var controller = await _context.NetappControllers
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == relationRow.DestinationControllerId, ct);

            if (controller == null)
                throw new InvalidOperationException($"NetApp controller with ID {relationRow.DestinationControllerId} not found.");

            var client = _authService.CreateAuthenticatedClient(controller, out var baseUrl);
            baseUrl = EnsureSlash(baseUrl);

            var resp = await client.GetAsync($"{baseUrl}snapmirror/relationships/{relationshipUuid}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"ONTAP returned {resp.StatusCode} fetching relationship: {body}");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var live = await System.Text.Json.JsonSerializer.DeserializeAsync<SnapMirrorRelation>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                ct);

            if (live == null)
                throw new InvalidOperationException($"No SnapMirrorRelation returned from API for UUID {relationshipUuid}");

            return live;
        }

        public async Task<List<SnapMirrorRelation>> GetSnapMirrorRelationsAsync(NetappController controller, CancellationToken ct = default)
        {
            var client = _authService.CreateAuthenticatedClient(controller, out var baseUrl);
            baseUrl = EnsureSlash(baseUrl);

            var result = new List<SnapMirrorRelation>();
            string? nextHref = "snapmirror/relationships?return_timeout=120&fields=*";

            while (!string.IsNullOrEmpty(nextHref))
            {
                var url = baseUrl + nextHref;
                var resp = await client.GetAsync(url, ct);
                var txt = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogError("Failed to fetch SnapMirror relations from controller {Id}: {Status} {Body}",
                        controller.Id, resp.StatusCode, txt);
                    break;
                }

                using var doc = JsonDocument.Parse(txt);
                var root = doc.RootElement;

                if (root.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in records.EnumerateArray())
                    {
                        // Safe-ish extraction
                        var srcPath = entry.TryGetProperty("source", out var src) && src.TryGetProperty("path", out var sp)
                            ? sp.GetString() ?? "" : "";
                        var dstPath = entry.TryGetProperty("destination", out var dst) && dst.TryGetProperty("path", out var dp)
                            ? dp.GetString() ?? "" : "";

                        var policyName = entry.TryGetProperty("policy", out var pol) && pol.TryGetProperty("name", out var pn)
                            ? pn.GetString() ?? "" : "";
                        var policyType = entry.TryGetProperty("policy", out pol) && pol.TryGetProperty("type", out var pt)
                            ? pt.GetString() ?? "" : "";
                        var policyUuid = entry.TryGetProperty("policy", out pol) && pol.TryGetProperty("uuid", out var pu)
                            ? pu.GetString() : null;

                        var state = entry.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";
                        var lagTime = entry.TryGetProperty("lag_time", out var lt) ? lt.GetString() : null;
                        var healthy = entry.TryGetProperty("healthy", out var hp) && hp.GetBoolean();
                        var uuid = entry.TryGetProperty("uuid", out var up) ? up.GetString() ?? "" : "";

                        var exportedSnapshot = entry.TryGetProperty("exported_snapshot", out var es) ? es.GetString() : null;
                        var totalTransferDuration = entry.TryGetProperty("total_transfer_duration", out var ttd) ? ttd.GetString() : null;
                        var totalTransferBytes = entry.TryGetProperty("total_transfer_bytes", out var ttb) ? GetInt64Flexible(ttb) : null;
                        var lastTransferType = entry.TryGetProperty("last_transfer_type", out var ltt) ? ltt.GetString() : null;
                        var lastTransferCompressionRatio = entry.TryGetProperty("last_transfer_network_compression_ratio", out var cr) ? cr.GetString() : null;
                        var backoffLevel = entry.TryGetProperty("backoff_level", out var bl) ? bl.GetString() : null;

                        var srcClusterName = entry.TryGetProperty("source", out src) &&
                                             src.TryGetProperty("cluster", out var sCluster) &&
                                             sCluster.TryGetProperty("name", out var scn)
                            ? scn.GetString() : null;

                        var dstClusterName = entry.TryGetProperty("destination", out dst) &&
                                             dst.TryGetProperty("cluster", out var dCluster) &&
                                             dCluster.TryGetProperty("name", out var dcn)
                            ? dcn.GetString() : null;

                        var srcSvmName = entry.TryGetProperty("source", out src) &&
                                         src.TryGetProperty("svm", out var sSvm) &&
                                         sSvm.TryGetProperty("name", out var ssvm)
                            ? ssvm.GetString() : null;

                        var dstSvmName = entry.TryGetProperty("destination", out dst) &&
                                         dst.TryGetProperty("svm", out var dSvm) &&
                                         dSvm.TryGetProperty("name", out var dsvm)
                            ? dsvm.GetString() : null;

                        string? lastTransferState = null;
                        string? lastTransferDuration = null;
                        DateTime? lastTransferEndTime = null;
                        if (entry.TryGetProperty("transfer", out var tr) && tr.ValueKind == JsonValueKind.Object)
                        {
                            lastTransferState = tr.TryGetProperty("state", out var lts) ? lts.GetString() : null;
                            lastTransferDuration = tr.TryGetProperty("total_duration", out var ltd) ? ltd.GetString() : null;
                            if (tr.TryGetProperty("end_time", out var let))
                                lastTransferEndTime = GetDateTimeFlexible(let);
                        }

                        // "cluster:volume" or sometimes full SVM paths; keep pragmatic split
                        static string ExtractVol(string path)
                            => path.Contains(':') ? path[(path.IndexOf(':') + 1)..] : path;

                        var srcVol = ExtractVol(srcPath);
                        var dstVol = ExtractVol(dstPath);

                        result.Add(new SnapMirrorRelation
                        {
                            SourceVolume = srcVol,
                            DestinationVolume = dstVol,
                            SourceControllerId = 0, // resolved during sync
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
                }

                // pagination
                if (root.TryGetProperty("_links", out var links) &&
                    links.TryGetProperty("next", out var next) &&
                    next.TryGetProperty("href", out var href))
                {
                    nextHref = href.GetString();
                }
                else
                {
                    nextHref = null;
                }
            }

            return result;
        }

        public async Task<bool> TriggerSnapMirrorUpdateAsync(string relationshipUuid, CancellationToken ct = default)
        {
            var relation = await _context.SnapMirrorRelations
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Uuid == relationshipUuid, ct);

            if (relation == null)
            {
                _logger.LogError("No SnapMirrorRelation found for UUID {Uuid}", relationshipUuid);
                return false;
            }

            var controller = await _context.NetappControllers
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == relation.DestinationControllerId, ct);
            if (controller == null)
            {
                _logger.LogError("No NetappController record for ID {Id}", relation.DestinationControllerId);
                return false;
            }

            var client = _authService.CreateAuthenticatedClient(controller, out var baseUrl);
            baseUrl = EnsureSlash(baseUrl);

            var url = $"{baseUrl}snapmirror/relationships/{relationshipUuid}/transfers";
            using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

            var resp = await client.PostAsync(url, content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if ((int)resp.StatusCode == 202)
            {
                // Accepted async job
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    string? jobUuid = null, jobHref = null;
                    if (root.TryGetProperty("job", out var job))
                    {
                        jobUuid = job.TryGetProperty("uuid", out var ju) ? ju.GetString() : null;
                        if (job.TryGetProperty("_links", out var jl) &&
                            jl.TryGetProperty("self", out var js) &&
                            js.TryGetProperty("href", out var jh))
                            jobHref = jh.GetString();
                    }
                    _logger.LogInformation("Triggered SnapMirror update (async). JobUuid={JobUuid} Link={JobHref}", jobUuid ?? "(n/a)", jobHref ?? "(n/a)");
                }
                catch { /* ignore parse errors */ }
                return true;
            }

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to trigger SnapMirror update for UUID {Uuid} on controller {Id}: {Status} {Body}",
                    relationshipUuid, controller.Id, resp.StatusCode, body);
                return false;
            }

            _logger.LogInformation("Triggered SnapMirror update for UUID {Uuid} on controller {Id}", relationshipUuid, controller.Id);
            return true;
        }

        public async Task SyncSnapMirrorRelationsAsync(CancellationToken ct = default)
        {
            // 1) Load controllers
            var controllers = await _context.NetappControllers
                .AsNoTracking()
                .ToListAsync(ct);

            var controllerIds = controllers.Select(c => c.Id).ToHashSet();

            // 2) Clean up relations pointing to non-existing controllers
            var invalidRelations = await _context.SnapMirrorRelations
                .Where(r =>
                    !controllerIds.Contains(r.SourceControllerId) ||
                    !controllerIds.Contains(r.DestinationControllerId))
                .ToListAsync(ct);

            if (invalidRelations.Any())
            {
                _logger.LogWarning("Removing {Count} SnapMirrorRelations with invalid controller references.", invalidRelations.Count);
                _context.SnapMirrorRelations.RemoveRange(invalidRelations);
                await _context.SaveChangesAsync(ct);
            }

            // 3) Only secondary controllers host destination relations here
            var secondaryControllers = controllers.Where(c => !c.IsPrimary).ToList();

            // Build a case-insensitive map of SelectedNetappVolumes -> controllerId
            var allSelectedVolumeEntries = await _context.SelectedNetappVolumes
                .AsNoTracking()
                .ToListAsync(ct);
            var selectedVolumeToController = allSelectedVolumeEntries
                .GroupBy(v => v.VolumeName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().NetappControllerId, StringComparer.OrdinalIgnoreCase);

            foreach (var secondary in secondaryControllers)
            {
                // 3a) Volumes selected for this DEST controller
                var selectedDestVolumes = await _context.SelectedNetappVolumes
                    .AsNoTracking()
                    .Where(v => v.NetappControllerId == secondary.Id)
                    .Select(v => v.VolumeName)
                    .ToListAsync(ct);

                if (!selectedDestVolumes.Any())
                {
                    // Remove all relations for this destination controller if nothing is selected
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

                // 3b) Pull LIVE relations
                var liveRelations = await GetSnapMirrorRelationsAsync(secondary, ct);

                // 3c) Filter: keep only those whose destination volume is selected (CI)
                var selectedDestSet = new HashSet<string>(selectedDestVolumes, StringComparer.OrdinalIgnoreCase);
                var filtered = liveRelations.Where(r => selectedDestSet.Contains(r.DestinationVolume)).ToList();

                // 3d) Load current DB relations for this destination controller
                var existingDbRelations = await _context.SnapMirrorRelations
                    .Where(r => r.DestinationControllerId == secondary.Id)
                    .ToListAsync(ct);

                // Key on "dest||source" case-insensitive
                static string KeyFor(string dest, string src) => $"{dest}||{src}";
                var dbLookup = new Dictionary<string, SnapMirrorRelation>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in existingDbRelations)
                    dbLookup[KeyFor(r.DestinationVolume, r.SourceVolume)] = r;

                // 3e) Upsert
                foreach (var relation in filtered)
                {
                    var key = KeyFor(relation.DestinationVolume, relation.SourceVolume);

                    // Resolve SourceControllerId from selected volume map (best effort)
                    relation.SourceControllerId = selectedVolumeToController.TryGetValue(relation.SourceVolume, out var sid) ? sid : 0;

                    if (dbLookup.TryGetValue(key, out var existing))
                    {
                        // Update existing
                        existing.Uuid = relation.Uuid;
                        existing.SourceControllerId = relation.SourceControllerId;
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
                        _context.SnapMirrorRelations.Add(relation);
                    }
                }

                // 3f) Remove stale
                var toRemove = dbLookup.Values.ToList();
                if (toRemove.Any())
                {
                    _logger.LogInformation("Removing {Count} stale SnapMirrorRelations for controller {ControllerId}.",
                        toRemove.Count, secondary.Id);
                    _context.SnapMirrorRelations.RemoveRange(toRemove);
                }

                await _context.SaveChangesAsync(ct);
            }
        }

        public async Task<SnapMirrorPolicy?> SnapMirrorPolicyGet(int controllerId, string policyUuid, CancellationToken ct = default)
        {
            var controller = await _context.NetappControllers
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == controllerId, ct);

            if (controller == null)
                throw new Exception($"NetApp controller {controllerId} not found.");

            var client = _authService.CreateAuthenticatedClient(controller, out var baseUrl);
            baseUrl = EnsureSlash(baseUrl);

            var url = $"{baseUrl}snapmirror/policies/{policyUuid}?fields=*";
            var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var entry = doc.RootElement;

            var policy = new SnapMirrorPolicy
            {
                Uuid = entry.TryGetProperty("uuid", out var u) ? u.GetString() ?? "" : "",
                Name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                Scope = entry.TryGetProperty("scope", out var sc) ? sc.GetString() ?? "" : "",
                Type = entry.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "",
                NetworkCompressionEnabled = entry.TryGetProperty("network_compression_enabled", out var nc) && nc.GetBoolean(),
                Throttle = entry.TryGetProperty("throttle", out var thr) ? GetInt32Flexible(thr) : 0,
                Retentions = new List<SnapMirrorPolicyRetention>()
            };

            if (entry.TryGetProperty("retention", out var retentionProp) && retentionProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var ret in retentionProp.EnumerateArray())
                {
                    var countVal = ret.TryGetProperty("count", out var cntEl) ? GetInt32Flexible(cntEl) : 0;
                    policy.Retentions.Add(new SnapMirrorPolicyRetention
                    {
                        Label = ret.TryGetProperty("label", out var label) ? label.GetString() ?? "" : "",
                        Count = countVal,
                        Preserve = ret.TryGetProperty("preserve", out var pres) && pres.GetBoolean(),
                        Warn = ret.TryGetProperty("warn", out var warnEl) ? GetInt32Flexible(warnEl) : 0,
                        Period = ret.TryGetProperty("period", out var per) ? per.GetString() : null
                    });
                }
            }

            return policy;
        }
    }
}
