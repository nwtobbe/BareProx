/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 *
 * Copyright (C) 2025-2026 Tobias Modig
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
using Microsoft.EntityFrameworkCore;
using Polly;
using System.Net;
using System.Text;
using System.Text.Json;

namespace BareProx.Services.Netapp
{
    public class NetappExportNFSService : INetappExportNFSService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<NetappExportNFSService> _logger;
        private readonly IAppTimeZoneService _tz;
        private readonly INetappAuthService _authService;
        private readonly INetappVolumeService _volumeService;

        public NetappExportNFSService(
            ApplicationDbContext context,
            ILogger<NetappExportNFSService> logger,
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

        public async Task<List<string>> GetNfsEnabledIpsAsync(int controllerId, string vserver, CancellationToken ct = default)
        {
            var controller = await _context.NetappControllers
                .FirstOrDefaultAsync(c => c.Id == controllerId, ct);

            if (controller == null)
                throw new InvalidOperationException("NetApp controller not found.");

            var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl);
            var url = $"{baseUrl}network/ip/interfaces?svm.name={Uri.EscapeDataString(vserver)}&fields=ip.address,services";

            using var resp = await httpClient.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement
                .GetProperty("records")
                .EnumerateArray()
                .Where(e =>
                    e.TryGetProperty("services", out var services) &&
                    services.ValueKind == JsonValueKind.Array &&
                    services.EnumerateArray().Any(s => s.GetString() == "data_nfs"))
                .Select(e => e.GetProperty("ip").GetProperty("address").GetString() ?? "")
                .Where(ip => !string.IsNullOrWhiteSpace(ip))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<bool> CopyExportPolicyAsync(
            string sourceVolumeName,
            string targetCloneName,
            int controllerId,
            CancellationToken ct = default)
        {
            var controller = await _context.NetappControllers.FindAsync(new object[] { controllerId }, ct);
            if (controller == null)
            {
                _logger.LogError("CopyExportPolicy: controller {ControllerId} not found.", controllerId);
                return false;
            }

            var client = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            // Read source volume export policy live from ONTAP
            var sourceLookup = await LookupVolumeAsync(client, baseUrl, sourceVolumeName, ct);
            if (!sourceLookup.Success)
            {
                _logger.LogError("CopyExportPolicy: source volume lookup failed for '{SourceVolume}': {Message}",
                    sourceVolumeName, sourceLookup.Message);
                return false;
            }

            if (sourceLookup.RecordCount > 1)
            {
                _logger.LogWarning(
                    "CopyExportPolicy: volume name '{SourceVolume}' returned {Count} records on controller {ControllerId}; using first match.",
                    sourceVolumeName, sourceLookup.RecordCount, controllerId);
            }

            if (string.IsNullOrWhiteSpace(sourceLookup.ExportPolicyName))
            {
                _logger.LogError("CopyExportPolicy: source volume '{SourceVolume}' has no export policy.", sourceVolumeName);
                return false;
            }

            var targetUuid = await WaitForVolumeUuidAsync(
                client,
                baseUrl,
                targetCloneName,
                attempts: 60,
                delayMs: 1000,
                requireOnline: true,
                ct: ct);

            if (string.IsNullOrWhiteSpace(targetUuid))
            {
                _logger.LogError("CopyExportPolicy: clone volume '{TargetClone}' did not become available within timeout.", targetCloneName);
                return false;
            }

            var ok = await PatchExportPolicyWithVerifyAsync(
                client,
                baseUrl,
                targetUuid,
                targetCloneName,
                sourceLookup.ExportPolicyName!,
                maxAttempts: 5,
                ct: ct);

            if (!ok)
            {
                _logger.LogError("CopyExportPolicy: failed to apply export policy '{Policy}' to clone '{Clone}'.",
                    sourceLookup.ExportPolicyName, targetCloneName);
            }

            return ok;
        }

        public async Task<bool> EnsureExportPolicyExistsOnSecondaryAsync(
            string exportPolicyName,
            int primaryControllerId,
            int secondaryControllerId,
            string primarySvmName,
            string secondarySvmName,
            CancellationToken ct = default)
        {
            var primaryController = await _context.NetappControllers.FindAsync(new object[] { primaryControllerId }, ct);
            var secondaryController = await _context.NetappControllers.FindAsync(new object[] { secondaryControllerId }, ct);

            if (primaryController == null || secondaryController == null)
            {
                _logger.LogError(
                    "EnsureExportPolicyExistsOnSecondary: controller(s) not found. primary={PrimaryControllerId}, secondary={SecondaryControllerId}",
                    primaryControllerId, secondaryControllerId);
                return false;
            }

            var primaryClient = _authService.CreateAuthenticatedClient(primaryController, out var primaryBaseUrl);
            var secondaryClient = _authService.CreateAuthenticatedClient(secondaryController, out var secondaryBaseUrl);

            _logger.LogInformation(
                "EnsureExportPolicyExistsOnSecondary: policy={Policy}, primary={PrimaryControllerId}/{PrimarySvm}, secondary={SecondaryControllerId}/{SecondarySvm}",
                exportPolicyName, primaryControllerId, primarySvmName, secondaryControllerId, secondarySvmName);

            // 1) Already exists on secondary? Done.
            var existingSecondaryPolicyId = await LookupExportPolicyIdAsync(
                secondaryClient,
                secondaryBaseUrl,
                exportPolicyName,
                secondarySvmName,
                ct);

            if (!string.IsNullOrWhiteSpace(existingSecondaryPolicyId))
            {
                _logger.LogInformation(
                    "EnsureExportPolicyExistsOnSecondary: policy '{Policy}' already exists on secondary SVM '{SecondarySvm}'.",
                    exportPolicyName, secondarySvmName);
                return true;
            }

            // 2) Find the policy on primary, using PRIMARY SVM first
            var primaryPolicyId = await LookupExportPolicyIdAsync(
                primaryClient,
                primaryBaseUrl,
                exportPolicyName,
                primarySvmName,
                ct);

            var usedClusterLevelFallback = false;

            if (string.IsNullOrWhiteSpace(primaryPolicyId))
            {
                primaryPolicyId = await LookupExportPolicyIdAsync(
                    primaryClient,
                    primaryBaseUrl,
                    exportPolicyName,
                    svmName: null,
                    ct);

                usedClusterLevelFallback = !string.IsNullOrWhiteSpace(primaryPolicyId);
            }

            if (string.IsNullOrWhiteSpace(primaryPolicyId))
            {
                _logger.LogError(
                    "EnsureExportPolicyExistsOnSecondary: could not find policy '{Policy}' on primary controller {PrimaryControllerId}. primarySvm={PrimarySvm}",
                    exportPolicyName, primaryControllerId, primarySvmName);
                return false;
            }

            if (usedClusterLevelFallback)
            {
                _logger.LogWarning(
                    "EnsureExportPolicyExistsOnSecondary: policy '{Policy}' was not found in primary SVM '{PrimarySvm}', used cluster-level fallback.",
                    exportPolicyName, primarySvmName);
            }

            // 3) Read full policy on primary
            var policyDetailUrl = $"{primaryBaseUrl}protocols/nfs/export-policies/{primaryPolicyId}?fields=svm,id,rules,name";
            using var policyDetailResp = await primaryClient.GetAsync(policyDetailUrl, ct);

            if (!policyDetailResp.IsSuccessStatusCode)
            {
                var body = await policyDetailResp.Content.ReadAsStringAsync(ct);
                _logger.LogError(
                    "EnsureExportPolicyExistsOnSecondary: failed to read primary policy details for policy '{Policy}' (id {PolicyId}): {Status} {Body}",
                    exportPolicyName, primaryPolicyId, (int)policyDetailResp.StatusCode, body);
                return false;
            }

            using var policyDetailDoc = JsonDocument.Parse(await policyDetailResp.Content.ReadAsStringAsync(ct));
            var policyDetail = policyDetailDoc.RootElement;

            if (!policyDetail.TryGetProperty("rules", out var rulesArray) || rulesArray.ValueKind != JsonValueKind.Array)
            {
                _logger.LogError(
                    "EnsureExportPolicyExistsOnSecondary: primary policy '{Policy}' has no readable rules array.",
                    exportPolicyName);
                return false;
            }

            // 4) Create policy on secondary SVM
            var createBody = new
            {
                name = exportPolicyName,
                svm = new { name = secondarySvmName }
            };

            using (var createContent = new StringContent(
                JsonSerializer.Serialize(createBody),
                Encoding.UTF8,
                "application/json"))
            {
                using var createResp = await secondaryClient.PostAsync(
                    $"{secondaryBaseUrl}protocols/nfs/export-policies",
                    createContent,
                    ct);

                if (!createResp.IsSuccessStatusCode)
                {
                    var body = await createResp.Content.ReadAsStringAsync(ct);

                    // race/duplicate: try lookup before giving up
                    _logger.LogWarning(
                        "EnsureExportPolicyExistsOnSecondary: create policy returned {Status}: {Body}",
                        (int)createResp.StatusCode, body);
                }
            }

            // 5) Resolve created policy id on secondary
            var createdId = await LookupExportPolicyIdAsync(
                secondaryClient,
                secondaryBaseUrl,
                exportPolicyName,
                secondarySvmName,
                ct);

            if (string.IsNullOrWhiteSpace(createdId))
            {
                _logger.LogError(
                    "EnsureExportPolicyExistsOnSecondary: policy '{Policy}' was not found on secondary after create attempt.",
                    exportPolicyName);
                return false;
            }

            // 6) Copy rules from primary to secondary
            foreach (var rule in rulesArray.EnumerateArray())
            {
                if (!rule.TryGetProperty("index", out var idxProp) || idxProp.ValueKind != JsonValueKind.Number)
                    continue;

                var ruleIndex = idxProp.GetInt32();

                var ruleDetailsUrl = $"{primaryBaseUrl}protocols/nfs/export-policies/{primaryPolicyId}/rules/{ruleIndex}";
                using var ruleDetailsResp = await primaryClient.GetAsync(ruleDetailsUrl, ct);

                if (!ruleDetailsResp.IsSuccessStatusCode)
                {
                    var body = await ruleDetailsResp.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning(
                        "EnsureExportPolicyExistsOnSecondary: failed to fetch primary export policy rule {RuleIndex}: {Status} {Body}",
                        ruleIndex, (int)ruleDetailsResp.StatusCode, body);
                    continue;
                }

                using var ruleDetailsDoc = JsonDocument.Parse(await ruleDetailsResp.Content.ReadAsStringAsync(ct));
                var ruleDetails = ruleDetailsDoc.RootElement;

                var payload = BuildExportRulePayload(ruleDetails);
                if (payload.Count == 0)
                {
                    _logger.LogWarning(
                        "EnsureExportPolicyExistsOnSecondary: export policy rule {RuleIndex} became empty after filtering; skipping.",
                        ruleIndex);
                    continue;
                }

                using var ruleContent = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json");

                var rulePostUrl = $"{secondaryBaseUrl}protocols/nfs/export-policies/{createdId}/rules";
                using var ruleResp = await secondaryClient.PostAsync(rulePostUrl, ruleContent, ct);

                if (!ruleResp.IsSuccessStatusCode)
                {
                    var errText = await ruleResp.Content.ReadAsStringAsync(ct);
                    _logger.LogError(
                        "EnsureExportPolicyExistsOnSecondary: failed to POST export rule {RuleIndex} to secondary policy {PolicyId}: {Status} {Body}",
                        ruleIndex, createdId, (int)ruleResp.StatusCode, errText);
                    return false;
                }
            }

            return true;
        }

        public async Task<bool> SetExportPolicyAsync(
            string volumeName,
            string exportPolicyName,
            int controllerId,
            CancellationToken ct = default)
        {
            var controller = await _context.NetappControllers.FindAsync(new object[] { controllerId }, ct);
            if (controller == null)
            {
                _logger.LogError("SetExportPolicy: controller {ControllerId} not found.", controllerId);
                return false;
            }

            var client = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            var uuid = await WaitForVolumeUuidAsync(
                client,
                baseUrl,
                volumeName,
                attempts: 60,
                delayMs: 1000,
                requireOnline: true,
                ct: ct);

            if (string.IsNullOrWhiteSpace(uuid))
            {
                _logger.LogError("SetExportPolicy: volume '{Name}' not found or not ready.", volumeName);
                return false;
            }

            var ok = await PatchExportPolicyWithVerifyAsync(
                client,
                baseUrl,
                uuid,
                volumeName,
                exportPolicyName,
                maxAttempts: 5,
                ct: ct);

            if (!ok)
            {
                _logger.LogError(
                    "SetExportPolicy: failed to apply '{Policy}' to volume '{Name}' after retries.",
                    exportPolicyName, volumeName);
            }

            return ok;
        }

        public async Task<bool> SetVolumeExportPathAsync(
            string volumeUuid,
            string exportPath,
            int controllerId,
            CancellationToken ct = default)
        {
            var controller = await _context.NetappControllers
                .FirstOrDefaultAsync(c => c.Id == controllerId, ct);

            if (controller == null)
                return false;

            var client = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            var patchUrl = $"{baseUrl}storage/volumes/{volumeUuid}";
            var getUrl = $"{patchUrl}?fields=nas.path";
            var payloadObj = new { nas = new { path = exportPath } };
            var payloadJson = JsonSerializer.Serialize(payloadObj);

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
                            _logger.LogWarning(
                                outcome.Exception,
                                "[ExportPath:{Attempt}] HTTP error; retrying in {Delay}s",
                                attempt, delay.TotalSeconds);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "[ExportPath:{Attempt}] verification failed; retrying in {Delay}s",
                                attempt, delay.TotalSeconds);
                        }
                    });

            return await retryPolicy.ExecuteAsync(
                async pollyCt =>
                {
                    using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

                    using var patchResp = await client.PatchAsync(patchUrl, content, pollyCt);
                    if (!patchResp.IsSuccessStatusCode)
                    {
                        _logger.LogError("[ExportPath] PATCH failed: {Status}", patchResp.StatusCode);
                        return false;
                    }

                    using var getResp = await client.GetAsync(getUrl, pollyCt);
                    if (!getResp.IsSuccessStatusCode)
                    {
                        _logger.LogError("[ExportPath] GET failed: {Status}", getResp.StatusCode);
                        return false;
                    }

                    var text = await getResp.Content.ReadAsStringAsync(pollyCt);

                    JsonDocument doc;
                    try
                    {
                        doc = JsonDocument.Parse(text);
                    }
                    catch (JsonException je)
                    {
                        _logger.LogError(je, "[ExportPath] Invalid JSON: {Json}", text);
                        return false;
                    }

                    if (!doc.RootElement.TryGetProperty("nas", out var nasElem) ||
                        !nasElem.TryGetProperty("path", out var pathElem))
                    {
                        _logger.LogWarning("[ExportPath] missing 'nas.path' in response: {Json}", text);
                        return false;
                    }

                    var actual = pathElem.GetString();
                    if (!string.Equals(actual, exportPath, StringComparison.Ordinal))
                    {
                        _logger.LogInformation(
                            "[ExportPath] path not yet applied: expected={Expected} actual={Actual}",
                            exportPath, actual);
                        return false;
                    }

                    return true;
                },
                ct);
        }

        // --------------------------------------------------------------------
        // Helpers
        // --------------------------------------------------------------------

        private sealed class VolumeLookupResult
        {
            public bool Success { get; init; }
            public string? Message { get; init; }
            public int RecordCount { get; init; }
            public string? Uuid { get; init; }
            public string? State { get; init; }
            public string? SvmName { get; init; }
            public string? ExportPolicyName { get; init; }
        }

        private async Task<VolumeLookupResult> LookupVolumeAsync(
            HttpClient client,
            string baseUrl,
            string volumeName,
            CancellationToken ct)
        {
            var url = $"{baseUrl}storage/volumes?name={Uri.EscapeDataString(volumeName)}&fields=uuid,state,svm.name,nas.export_policy.name";
            using var resp = await client.GetAsync(url, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return new VolumeLookupResult
                {
                    Success = false,
                    Message = $"HTTP {(int)resp.StatusCode}: {body}"
                };
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
            {
                return new VolumeLookupResult
                {
                    Success = false,
                    Message = "No records array in response."
                };
            }

            var count = records.GetArrayLength();
            if (count == 0)
            {
                return new VolumeLookupResult
                {
                    Success = false,
                    Message = $"Volume '{volumeName}' not found.",
                    RecordCount = 0
                };
            }

            var first = records[0];

            string? uuid = first.TryGetProperty("uuid", out var uuidProp) ? uuidProp.GetString() : null;
            string? state = first.TryGetProperty("state", out var stateProp) ? stateProp.GetString() : null;

            string? svmName = null;
            if (first.TryGetProperty("svm", out var svmProp) &&
                svmProp.ValueKind == JsonValueKind.Object &&
                svmProp.TryGetProperty("name", out var svmNameProp))
            {
                svmName = svmNameProp.GetString();
            }

            string? exportPolicyName = null;
            if (first.TryGetProperty("nas", out var nasProp) &&
                nasProp.ValueKind == JsonValueKind.Object &&
                nasProp.TryGetProperty("export_policy", out var epProp) &&
                epProp.ValueKind == JsonValueKind.Object &&
                epProp.TryGetProperty("name", out var epNameProp))
            {
                exportPolicyName = epNameProp.GetString();
            }

            return new VolumeLookupResult
            {
                Success = true,
                RecordCount = count,
                Uuid = uuid,
                State = state,
                SvmName = svmName,
                ExportPolicyName = exportPolicyName
            };
        }

        private async Task<string?> WaitForVolumeUuidAsync(
            HttpClient client,
            string baseUrl,
            string volumeName,
            int attempts,
            int delayMs,
            bool requireOnline,
            CancellationToken ct)
        {
            string? anyUuid = null;

            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                var lookup = await LookupVolumeAsync(client, baseUrl, volumeName, ct);
                if (lookup.Success && !string.IsNullOrWhiteSpace(lookup.Uuid))
                {
                    anyUuid = lookup.Uuid;

                    var isOnline = string.Equals(lookup.State, "online", StringComparison.OrdinalIgnoreCase);
                    if (!requireOnline || isOnline)
                        return lookup.Uuid;
                }

                await Task.Delay(delayMs, ct);
            }

            if (!string.IsNullOrWhiteSpace(anyUuid))
            {
                _logger.LogWarning(
                    "WaitForVolumeUuid: volume '{Volume}' was found but never reached online state; proceeding with UUID anyway.",
                    volumeName);
            }

            return anyUuid;
        }

        private async Task<bool> PatchExportPolicyWithVerifyAsync(
            HttpClient client,
            string baseUrl,
            string volumeUuid,
            string volumeNameForLogs,
            string exportPolicyName,
            int maxAttempts,
            CancellationToken ct)
        {
            var patchUrl = $"{baseUrl}storage/volumes/{volumeUuid}";
            var verifyUrl = $"{baseUrl}storage/volumes/{volumeUuid}?fields=nas.export_policy.name";
            var payloadObj = new
            {
                nas = new
                {
                    export_policy = new { name = exportPolicyName }
                }
            };

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                using (var content = new StringContent(
                    JsonSerializer.Serialize(payloadObj),
                    Encoding.UTF8,
                    "application/json"))
                {
                    using var resp = await client.PatchAsync(patchUrl, content, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        var body = await resp.Content.ReadAsStringAsync(ct);

                        if ((int)resp.StatusCode == 409 || (int)resp.StatusCode == 423)
                        {
                            _logger.LogWarning(
                                "PatchExportPolicyWithVerify: PATCH busy/locked for '{Volume}' (attempt {Attempt}/{MaxAttempts}): {Body}",
                                volumeNameForLogs, attempt, maxAttempts, body);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "PatchExportPolicyWithVerify: PATCH failed for '{Volume}' (attempt {Attempt}/{MaxAttempts}, {Status}): {Body}",
                                volumeNameForLogs, attempt, maxAttempts, (int)resp.StatusCode, body);
                        }

                        await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
                        continue;
                    }
                }

                using var verifyResp = await client.GetAsync(verifyUrl, ct);
                if (verifyResp.IsSuccessStatusCode)
                {
                    using var verifyDoc = JsonDocument.Parse(await verifyResp.Content.ReadAsStringAsync(ct));

                    if (verifyDoc.RootElement.TryGetProperty("nas", out var nasProp) &&
                        nasProp.ValueKind == JsonValueKind.Object &&
                        nasProp.TryGetProperty("export_policy", out var epProp) &&
                        epProp.ValueKind == JsonValueKind.Object &&
                        epProp.TryGetProperty("name", out var nameProp))
                    {
                        var actual = nameProp.GetString();
                        if (string.Equals(actual, exportPolicyName, StringComparison.OrdinalIgnoreCase))
                            return true;

                        _logger.LogInformation(
                            "PatchExportPolicyWithVerify: verify mismatch for '{Volume}' (attempt {Attempt}/{MaxAttempts}). expected='{Expected}', actual='{Actual}'",
                            volumeNameForLogs, attempt, maxAttempts, exportPolicyName, actual);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "PatchExportPolicyWithVerify: verify response missing nas.export_policy.name for '{Volume}' (attempt {Attempt}/{MaxAttempts}).",
                            volumeNameForLogs, attempt, maxAttempts);
                    }
                }
                else
                {
                    var body = await verifyResp.Content.ReadAsStringAsync(ct);
                    _logger.LogInformation(
                        "PatchExportPolicyWithVerify: verify GET failed for '{Volume}' (attempt {Attempt}/{MaxAttempts}, {Status}): {Body}",
                        volumeNameForLogs, attempt, maxAttempts, (int)verifyResp.StatusCode, body);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
            }

            return false;
        }

        private async Task<string?> LookupExportPolicyIdAsync(
            HttpClient client,
            string baseUrl,
            string exportPolicyName,
            string? svmName,
            CancellationToken ct)
        {
            var url = string.IsNullOrWhiteSpace(svmName)
                ? $"{baseUrl}protocols/nfs/export-policies?name={Uri.EscapeDataString(exportPolicyName)}"
                : $"{baseUrl}protocols/nfs/export-policies?name={Uri.EscapeDataString(exportPolicyName)}&svm.name={Uri.EscapeDataString(svmName)}";

            using var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

            if (!doc.RootElement.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var rec in records.EnumerateArray())
            {
                if (rec.ValueKind != JsonValueKind.Object)
                    continue;

                if (rec.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                    return idProp.GetInt64().ToString();
            }

            return null;
        }

        private static Dictionary<string, object> BuildExportRulePayload(JsonElement ruleDetails)
        {
            var payload = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            var knownFields = new[]
            {
                "clients",
                "protocols",
                "ro_rule",
                "rw_rule",
                "anonymous_user",
                "superuser",
                "allow_device_creation",
                "ntfs_unix_security",
                "chown_mode",
                "allow_suid"
            };

            foreach (var field in knownFields)
            {
                if (!ruleDetails.TryGetProperty(field, out var value))
                    continue;

                if (value.ValueKind == JsonValueKind.Undefined || value.ValueKind == JsonValueKind.Null)
                    continue;

                payload[field] = ConvertJson(value)!;
            }

            return payload;
        }

        private static object? ConvertJson(JsonElement value)
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.TryGetInt64(out var l) ? l :
                                        value.TryGetDecimal(out var d) ? d :
                                        value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Array => value.EnumerateArray().Select(ConvertJson).ToList(),
                JsonValueKind.Object => value.EnumerateObject()
                    .ToDictionary(p => p.Name, p => ConvertJson(p.Value)),
                _ => null
            };
        }
    }
}