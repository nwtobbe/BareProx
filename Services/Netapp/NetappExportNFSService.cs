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
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Polly;
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
            // Fetch controller
            var controller = await _context.NetappControllers.FindAsync(controllerId, ct);
            if (controller == null) return false;

            var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            // Lookup source export policy
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

            // Wait for clone to become available and online
            string? tgtUuid = null;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                var tgtLookupUrl = $"{baseUrl}storage/volumes" +
                    $"?name={Uri.EscapeDataString(targetCloneName)}&fields=uuid,state";

                var tgtResp = await httpClient.GetAsync(tgtLookupUrl, ct);
                tgtResp.EnsureSuccessStatusCode();

                using var tgtDoc = JsonDocument.Parse(await tgtResp.Content.ReadAsStringAsync(ct));
                var tgtRecs = tgtDoc.RootElement.GetProperty("records");

                if (tgtRecs.GetArrayLength() > 0)
                {
                    var volume = tgtRecs[0];
                    var state = volume.TryGetProperty("state", out var st) ? st.GetString() : null;

                    if (state != null && state.Equals("online", StringComparison.OrdinalIgnoreCase))
                    {
                        tgtUuid = volume.GetProperty("uuid").GetString();
                        break;
                    }
                }

                await Task.Delay(1000, ct);
            }

            if (string.IsNullOrEmpty(tgtUuid))
            {
                _logger.LogError("Clone volume '{TargetClone}' did not become available within timeout.", targetCloneName);
                return false;
            }

            // PATCH export policy
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

            // Retry PATCH up to 3 times if needed
            for (int retry = 0; retry < 3; retry++)
            {
                var request = new HttpRequestMessage(HttpMethod.Patch, patchUrl) { Content = content };
                var patchResp = await httpClient.SendAsync(request, ct);

                if (patchResp.IsSuccessStatusCode)
                    return true;

                var err = await patchResp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Attempt {Retry}/3: Export policy patch failed: {Error}", retry + 1, err);

                await Task.Delay(2000, ct); // Wait before retry
            }

            _logger.LogError("Failed to apply export policy after multiple retries for volume '{Clone}'.", targetCloneName);
            return false;
        }

        public async Task<bool> EnsureExportPolicyExistsOnSecondaryAsync(string exportPolicyName,int primaryControllerId,int secondaryControllerId,string svmName,CancellationToken ct = default)
        {
            // 1. Lookup controllers
            var primaryController = await _context.NetappControllers.FindAsync(primaryControllerId, ct);
            var secondaryController = await _context.NetappControllers.FindAsync(secondaryControllerId, ct);
            if (primaryController == null || secondaryController == null)
                throw new Exception("Controller(s) not found.");

            var primaryClient = _authService.CreateAuthenticatedClient(primaryController, out var primaryBaseUrl);
            var secondaryClient = _authService.CreateAuthenticatedClient(secondaryController, out var secondaryBaseUrl);

            // 2. Check if export policy exists on secondary
            var secPolicyUrl = $"{secondaryBaseUrl}protocols/nfs/export-policies?name={Uri.EscapeDataString(exportPolicyName)}&svm.name={Uri.EscapeDataString(svmName)}";
            var secPolicyResp = await secondaryClient.GetAsync(secPolicyUrl, ct);
            secPolicyResp.EnsureSuccessStatusCode();

            using (var secPolicyDoc = JsonDocument.Parse(await secPolicyResp.Content.ReadAsStringAsync(ct)))
            {
                if (secPolicyDoc.RootElement.TryGetProperty("records", out var secRecordsElement) &&
                    secRecordsElement.ValueKind == JsonValueKind.Array)
                {
                    var secPolicyRecord = secRecordsElement.EnumerateArray().FirstOrDefault();
                    if (secPolicyRecord.ValueKind == JsonValueKind.Object)
                    {
                        // Policy already exists on secondary, done!
                        return true;
                    }
                }
            }

            // 3. Lookup export policy (and id) on primary
            string? primaryPolicyId = null;
            JsonElement primaryPolicyRecord = default;

            // Try SVM-scoped first
            var primaryPolicyUrl = $"{primaryBaseUrl}protocols/nfs/export-policies?name={Uri.EscapeDataString(exportPolicyName)}&svm.name={Uri.EscapeDataString(svmName)}";
            var primaryPolicyResp = await primaryClient.GetAsync(primaryPolicyUrl, ct);

            bool tryClusterLevel = false;
            if (primaryPolicyResp.IsSuccessStatusCode)
            {
                using (var primaryPolicyDoc = JsonDocument.Parse(await primaryPolicyResp.Content.ReadAsStringAsync(ct)))
                {
                    if (primaryPolicyDoc.RootElement.TryGetProperty("records", out var policyArray) &&
                        policyArray.ValueKind == JsonValueKind.Array)
                    {
                        primaryPolicyRecord = policyArray.EnumerateArray().FirstOrDefault();
                        if (primaryPolicyRecord.ValueKind == JsonValueKind.Object &&
                            primaryPolicyRecord.TryGetProperty("id", out var idProp))
                        {
                            // id is a number; convert to string for URLs
                            primaryPolicyId = idProp.GetInt64().ToString();
                        }
                        else
                        {
                            tryClusterLevel = true; // No policy found at SVM level, try cluster
                        }
                    }
                    else
                    {
                        tryClusterLevel = true;
                    }
                }
            }
            else
            {
                var body = await primaryPolicyResp.Content.ReadAsStringAsync(ct);
                // Try cluster-level if SVM does not exist or policy not found (2621462)
                if (primaryPolicyResp.StatusCode == System.Net.HttpStatusCode.NotFound ||
                    body.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("\"code\": \"2621462\"", StringComparison.OrdinalIgnoreCase))
                {
                    tryClusterLevel = true;
                }
                else
                {
                    // Other error (auth, comms, etc)
                    return false;
                }
            }

            if (tryClusterLevel)
            {
                var clusterPolicyUrl = $"{primaryBaseUrl}protocols/nfs/export-policies?name={Uri.EscapeDataString(exportPolicyName)}";
                var clusterPolicyResp = await primaryClient.GetAsync(clusterPolicyUrl, ct);
                if (!clusterPolicyResp.IsSuccessStatusCode)
                    return false;
                using (var clusterPolicyDoc = JsonDocument.Parse(await clusterPolicyResp.Content.ReadAsStringAsync(ct)))
                {
                    if (clusterPolicyDoc.RootElement.TryGetProperty("records", out var clusterArray) &&
                        clusterArray.ValueKind == JsonValueKind.Array)
                    {
                        primaryPolicyRecord = clusterArray.EnumerateArray().FirstOrDefault();
                        if (primaryPolicyRecord.ValueKind == JsonValueKind.Object &&
                            primaryPolicyRecord.TryGetProperty("id", out var idProp))
                        {
                            primaryPolicyId = idProp.GetInt64().ToString();
                        }
                        else
                        {
                            _logger?.LogError("primaryPolicyRecord did not have an id property. Record: {0}", primaryPolicyRecord.ToString());
                            return false;
                        }
                    }
                    else
                    {
                        return false;
                    }
                }
            }

            if (string.IsNullOrEmpty(primaryPolicyId))
                return false;

            // 4. Get all rules for this policy from primary using the detailed policy endpoint
            var policyDetailUrl = $"{primaryBaseUrl}protocols/nfs/export-policies/{primaryPolicyId}?fields=svm,id,rules,name";
            var policyDetailResp = await primaryClient.GetAsync(policyDetailUrl, ct);
            if (!policyDetailResp.IsSuccessStatusCode)
                return false;

            var policyDetailJson = await policyDetailResp.Content.ReadAsStringAsync(ct);
            using var policyDetailDoc = JsonDocument.Parse(policyDetailJson);
            var policyDetail = policyDetailDoc.RootElement;

            // Extract the "rules" array
            if (!policyDetail.TryGetProperty("rules", out var rulesArray) || rulesArray.ValueKind != JsonValueKind.Array)
                return false;

            // 5. Create export policy on secondary
            var createBody = new
            {
                name = exportPolicyName,
                svm = new { name = svmName }
            };
            var createContent = new StringContent(System.Text.Json.JsonSerializer.Serialize(createBody), Encoding.UTF8, "application/json");
            var createResp = await secondaryClient.PostAsync($"{secondaryBaseUrl}protocols/nfs/export-policies", createContent, ct);
            if (!createResp.IsSuccessStatusCode)
                return false;

            string? createdId = null;
            using (var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync(ct)))
            {
                if (createDoc.RootElement.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                {
                    createdId = idProp.GetInt64().ToString();
                }
            }

            // Fallback: If id is not present, look up by name and svm.name
            if (string.IsNullOrEmpty(createdId))
            {
                var lookupUrl = $"{secondaryBaseUrl}protocols/nfs/export-policies?name={Uri.EscapeDataString(exportPolicyName)}&svm.name={Uri.EscapeDataString(svmName)}";
                var lookupResp = await secondaryClient.GetAsync(lookupUrl, ct);
                if (!lookupResp.IsSuccessStatusCode)
                    return false;

                using var lookupDoc = JsonDocument.Parse(await lookupResp.Content.ReadAsStringAsync(ct));
                if (lookupDoc.RootElement.TryGetProperty("records", out var recordsArray) && recordsArray.ValueKind == JsonValueKind.Array)
                {
                    var policyRec = recordsArray.EnumerateArray().FirstOrDefault();
                    if (policyRec.ValueKind == JsonValueKind.Object && policyRec.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                    {
                        createdId = idProp.GetInt64().ToString();
                    }
                }
            }

            if (string.IsNullOrEmpty(createdId))
                return false;


            // 6. Copy each rule from primary to secondary using detailed rule
            foreach (var rule in rulesArray.EnumerateArray())
            {
                // Fetch detailed rule if possible
                if (!rule.TryGetProperty("index", out var idxProp) || idxProp.ValueKind != JsonValueKind.Number)
                    continue;
                int ruleIndex = idxProp.GetInt32();

                // Fetch full details for this rule
                var ruleDetailsUrl = $"{primaryBaseUrl}protocols/nfs/export-policies/{primaryPolicyId}/rules/{ruleIndex}";
                var ruleDetailsResp = await primaryClient.GetAsync(ruleDetailsUrl, ct);
                if (!ruleDetailsResp.IsSuccessStatusCode)
                {
                    _logger?.LogWarning("Failed to fetch detailed export policy rule for index {0}", ruleIndex);
                    continue;
                }

                using var ruleDetailsDoc = JsonDocument.Parse(await ruleDetailsResp.Content.ReadAsStringAsync(ct));
                var ruleDetails = ruleDetailsDoc.RootElement;

                // Build payload ONLY with supported fields
                var payload = new Dictionary<string, object>();

                // List of NetApp export rule properties you want to copy if present:
                var knownFields = new[] {
        "clients", "protocols", "ro_rule", "rw_rule",
        "anonymous_user", "superuser", "allow_device_creation", "ntfs_unix_security",
        "chown_mode", "allow_suid"
    };

                foreach (var field in knownFields)
                {
                    if (ruleDetails.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Undefined && value.ValueKind != JsonValueKind.Null)
                    {
                        switch (value.ValueKind)
                        {
                            case JsonValueKind.String:
                                payload[field] = value.GetString();
                                break;
                            case JsonValueKind.Number:
                                if (value.TryGetInt64(out var longVal)) payload[field] = longVal;
                                else if (value.TryGetDecimal(out var decVal)) payload[field] = decVal;
                                break;
                            case JsonValueKind.True:
                            case JsonValueKind.False:
                                payload[field] = value.GetBoolean();
                                break;
                            case JsonValueKind.Array:
                                payload[field] = value.EnumerateArray().Select(x =>
                                {
                                    // for "clients" it may be array of objects, for others usually string
                                    if (x.ValueKind == JsonValueKind.String)
                                        return (object)x.GetString();
                                    else if (x.ValueKind == JsonValueKind.Object)
                                    {
                                        // for "clients", e.g. [{"match": "10.0.0.0/24"}]
                                        return x.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString());
                                    }
                                    return x.ToString();
                                }).ToList();
                                break;
                            case JsonValueKind.Object:
                                // Only needed for "clients" (array of object)
                                payload[field] = System.Text.Json.JsonSerializer.Deserialize<object>(value.GetRawText());
                                break;
                        }
                    }
                }

                if (payload.Count == 0)
                {
                    _logger?.LogWarning("Export policy rule for policy {0} (rule index {1}) is empty after filtering. Skipping.", createdId, ruleIndex);
                    continue;
                }

                var ruleContent = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var rulePostUrl = $"{secondaryBaseUrl}protocols/nfs/export-policies/{createdId}/rules";
                var ruleResp = await secondaryClient.PostAsync(rulePostUrl, ruleContent, ct);
                if (!ruleResp.IsSuccessStatusCode)
                {
                    var errText = await ruleResp.Content.ReadAsStringAsync(ct);
                    _logger?.LogError("Failed to POST export policy rule {0} to {1}: {2}", ruleIndex, rulePostUrl, errText);
                    return false;
                }
            }

            // Success!
            return true;

        }



        public async Task<bool> SetExportPolicyAsync(string volumeName,string exportPolicyName,int controllerId,CancellationToken ct = default)
        {
            var controller = await _context.NetappControllers.FindAsync(controllerId, ct);
            if (controller == null) return false;

            var client = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            // 1) Resolve volume UUID with a few tries (volume may appear a bit late)
            string? uuid = null;
            for (int i = 0; i < 15 && uuid == null; i++)
            {
                var lookupUrl = $"{baseUrl}storage/volumes?name={Uri.EscapeDataString(volumeName)}&fields=uuid,state";
                var lookupResp = await client.GetAsync(lookupUrl, ct);
                if (!lookupResp.IsSuccessStatusCode)
                {
                    await Task.Delay(500, ct);
                    continue;
                }

                using var doc = JsonDocument.Parse(await lookupResp.Content.ReadAsStringAsync(ct));
                var recs = doc.RootElement.GetProperty("records");
                if (recs.GetArrayLength() > 0)
                {
                    var v = recs[0];
                    var state = v.TryGetProperty("state", out var st) ? st.GetString() : null;
                    // Prefer to wait until online, but proceed if uuid is present.
                    if (!string.IsNullOrEmpty(state) && state.Equals("online", StringComparison.OrdinalIgnoreCase))
                        uuid = v.GetProperty("uuid").GetString();
                    else
                        uuid = v.GetProperty("uuid").GetString();
                }

                if (uuid == null) await Task.Delay(500, ct);
            }

            if (uuid == null) { _logger.LogError("SetExportPolicy: volume '{Name}' not found.", volumeName); return false; }

            // 2) PATCH with retries and verify
            var patchUrl = $"{baseUrl}storage/volumes/{uuid}";
            var payloadObj = new { nas = new { export_policy = new { name = exportPolicyName } } };

            // simple manual retry (or use Polly if you prefer)
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                using var content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(payloadObj),
                    Encoding.UTF8,
                    "application/json");

                var resp = await client.PatchAsync(patchUrl, content, ct);
                if (resp.IsSuccessStatusCode)
                {
                    // verify applied value
                    var verifyUrl = $"{baseUrl}storage/volumes/{uuid}?fields=nas.export_policy.name";
                    var vResp = await client.GetAsync(verifyUrl, ct);
                    if (vResp.IsSuccessStatusCode)
                    {
                        using var vDoc = JsonDocument.Parse(await vResp.Content.ReadAsStringAsync(ct));
                        var name = vDoc.RootElement
                                       .GetProperty("nas")
                                       .GetProperty("export_policy")
                                       .GetProperty("name")
                                       .GetString();

                        if (string.Equals(name, exportPolicyName, StringComparison.OrdinalIgnoreCase))
                            return true;

                        _logger.LogInformation("SetExportPolicy verify mismatch (expected '{Exp}', got '{Got}'), attempt {Attempt}",
                            exportPolicyName, name, attempt);
                    }
                }
                else
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    // 409/423 => busy/locked; try again
                    if ((int)resp.StatusCode == 409 || (int)resp.StatusCode == 423)
                    {
                        _logger.LogWarning("SetExportPolicy PATCH busy/locked (attempt {Attempt}): {Body}", attempt, body);
                    }
                    else
                    {
                        _logger.LogWarning("SetExportPolicy PATCH failed (attempt {Attempt}, {Status}): {Body}",
                            attempt, (int)resp.StatusCode, body);
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct);
            }

            _logger.LogError("SetExportPolicy: failed to apply '{Policy}' to volume '{Name}' after retries.", exportPolicyName, volumeName);
            return false;
        }

        public async Task<bool> SetVolumeExportPathAsync(string volumeUuid,string exportPath,int controllerId,CancellationToken ct = default)
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
    }
}
