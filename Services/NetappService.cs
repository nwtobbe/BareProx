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
        public async Task<bool> CopyExportPolicyFromPrimaryAsync(
    string exportPolicyName,
    int primaryControllerId,
    int secondaryControllerId,
    string svmName, // SVM context required for export policies
    CancellationToken ct = default)
        {
            // 1. Get export policy (and its rules) from primary
            var primaryController = await _context.NetappControllers.FindAsync(primaryControllerId, ct);
            var secondaryController = await _context.NetappControllers.FindAsync(secondaryControllerId, ct);
            if (primaryController == null || secondaryController == null)
                throw new Exception("Controller(s) not found.");

            var primaryClient = _authService.CreateAuthenticatedClient(primaryController, out var primaryBaseUrl);
            var secondaryClient = _authService.CreateAuthenticatedClient(secondaryController, out var secondaryBaseUrl);

            // a. Get policy on primary
            var policyUrl = $"{primaryBaseUrl}protocols/nfs/export-policies?name={Uri.EscapeDataString(exportPolicyName)}&svm.name={Uri.EscapeDataString(svmName)}";
            var policyResp = await primaryClient.GetAsync(policyUrl, ct);
            if (!policyResp.IsSuccessStatusCode)
                throw new Exception($"Failed to get export policy '{exportPolicyName}' from primary.");
            var policyDoc = JsonDocument.Parse(await policyResp.Content.ReadAsStringAsync(ct));
            var policyRecord = policyDoc.RootElement.GetProperty("records").EnumerateArray().FirstOrDefault();
            if (policyRecord.ValueKind != JsonValueKind.Object)
                throw new Exception($"Export policy '{exportPolicyName}' not found on primary.");
            var primaryPolicyUuid = policyRecord.GetProperty("uuid").GetString();

            // b. Get rules from primary
            var rulesUrl = $"{primaryBaseUrl}protocols/nfs/export-policies/{primaryPolicyUuid}/rules";
            var rulesResp = await primaryClient.GetAsync(rulesUrl, ct);
            if (!rulesResp.IsSuccessStatusCode)
                throw new Exception("Failed to get rules for export policy.");
            var rulesDoc = JsonDocument.Parse(await rulesResp.Content.ReadAsStringAsync(ct));
            var rulesRecords = rulesDoc.RootElement.GetProperty("records").EnumerateArray().ToList();

            // 2. Check if policy exists on secondary
            var secPolicyUrl = $"{secondaryBaseUrl}protocols/nfs/export-policies?name={Uri.EscapeDataString(exportPolicyName)}&svm.name={Uri.EscapeDataString(svmName)}";
            var secPolicyResp = await secondaryClient.GetAsync(secPolicyUrl, ct);
            if (!secPolicyResp.IsSuccessStatusCode)
                throw new Exception($"Failed to query export policies on secondary.");
            var secPolicyDoc = JsonDocument.Parse(await secPolicyResp.Content.ReadAsStringAsync(ct));
            var secPolicyRecord = secPolicyDoc.RootElement.GetProperty("records").EnumerateArray().FirstOrDefault();

            string? secondaryPolicyUuid = null;

            if (secPolicyRecord.ValueKind == JsonValueKind.Object)
            {
                // Policy already exists, get its uuid
                secondaryPolicyUuid = secPolicyRecord.GetProperty("uuid").GetString();
            }
            else
            {
                // Policy does not exist, create it
                var createBody = new
                {
                    name = exportPolicyName,
                    svm = new { name = svmName }
                };
                var createContent = new StringContent(System.Text.Json.JsonSerializer.Serialize(createBody), Encoding.UTF8, "application/json");
                var createResp = await secondaryClient.PostAsync($"{secondaryBaseUrl}protocols/nfs/export-policies", createContent, ct);
                if (!createResp.IsSuccessStatusCode)
                    throw new Exception("Failed to create export policy on secondary.");
                var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync(ct));
                secondaryPolicyUuid = createDoc.RootElement.GetProperty("records")[0].GetProperty("uuid").GetString();

                // Now add all rules from primary to secondary
                foreach (var rule in rulesRecords)
                {
                    // Pass through all properties of the rule EXCEPT "policy" and "uuid"
                    var ruleObj = new Dictionary<string, object>();
                    foreach (var prop in rule.EnumerateObject())
                    {
                        if (prop.Name != "policy" && prop.Name != "uuid")
                        {
                            ruleObj[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() :
                                prop.Value.ValueKind == JsonValueKind.Number ? prop.Value.GetInt32() : (object)prop.Value.ToString();
                        }
                    }

                    var ruleContent = new StringContent(System.Text.Json.JsonSerializer.Serialize(ruleObj), Encoding.UTF8, "application/json");
                    var ruleResp = await secondaryClient.PostAsync($"{secondaryBaseUrl}protocols/nfs/export-policies/{secondaryPolicyUuid}/rules", ruleContent, ct);
                    if (!ruleResp.IsSuccessStatusCode)
                        throw new Exception("Failed to create export policy rule on secondary.");
                }
            }

            return true;
        }
        public async Task<bool> EnsureExportPolicyExistsOnSecondaryAsync(
       string exportPolicyName,
       int primaryControllerId,
       int secondaryControllerId,
       string svmName,
       CancellationToken ct = default)
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

        public async Task<bool> SetExportPolicyAsync(
            string volumeName,
            string exportPolicyName,
            int controllerId,
            CancellationToken ct = default)
        {
            var controller = await _context.NetappControllers.FindAsync(controllerId, ct);
            if (controller == null) return false;

            var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            // Lookup UUID for the clone
            var lookupUrl = $"{baseUrl}storage/volumes?name={Uri.EscapeDataString(volumeName)}&fields=uuid";
            var lookupResp = await httpClient.GetAsync(lookupUrl, ct);
            lookupResp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await lookupResp.Content.ReadAsStringAsync(ct));
            var recs = doc.RootElement.GetProperty("records");
            if (recs.GetArrayLength() == 0) return false;
            var uuid = recs[0].GetProperty("uuid").GetString()!;

            var patchUrl = $"{baseUrl}storage/volumes/{uuid}";
            var payload = new
            {
                nas = new
                {
                    export_policy = new { name = exportPolicyName }
                }
            };
            var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var patchResp = await httpClient.PatchAsync(patchUrl, content, ct);

            return patchResp.IsSuccessStatusCode;
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


///// <summary>
///// Recursively moves and renames all files under /images/{oldvmid} to /images/{newvmid} on the specified NetApp volume.
///// Only files with oldvmid in their name are renamed; directory structure is preserved.
///// </summary>
//public async Task<bool> MoveAndRenameAllVmFilesAsync(
//   string volumeName,
//   int controllerId,
//   string oldvmid,
//   string newvmid,
//   CancellationToken ct = default)
//{
//    // 1. Lookup controller and volume UUID
//    var controller = await _context.NetappControllers.FindAsync(controllerId, ct);
//    if (controller == null)
//    {
//        _logger.LogError("NetApp controller not found for ID {ControllerId}", controllerId);
//        return false;
//    }
//    var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

//    var lookupUrl = $"{baseUrl}storage/volumes?name={Uri.EscapeDataString(volumeName)}";
//    var lookupResp = await httpClient.GetAsync(lookupUrl, ct);
//    if (!lookupResp.IsSuccessStatusCode)
//    {
//        _logger.LogError("Failed to lookup volume {VolumeName} for rename: {Status}", volumeName, lookupResp.StatusCode);
//        return false;
//    }

//    using var lookupDoc = JsonDocument.Parse(await lookupResp.Content.ReadAsStringAsync(ct));
//    var lookupRecords = lookupDoc.RootElement.GetProperty("records");
//    if (lookupRecords.GetArrayLength() == 0)
//    {
//        _logger.LogError("Volume '{VolumeName}' not found on controller.", volumeName);
//        return false;
//    }
//    var volEntry = lookupRecords[0];
//    var volumeUuid = volEntry.GetProperty("uuid").GetString()!;


//    // 2. Ensure the destination directory exists: /images/{newvmid}
//    var folderPath = $"images/{newvmid}";
//    var encodedFolderPath = Uri.EscapeDataString(folderPath); // e.g. images%2F113
//    var createDirUrl = $"{baseUrl}storage/volumes/{volumeUuid}/files/{encodedFolderPath}";
//    var createDirBody = new { type = "directory", unix_permissions = 0 };
//    var createDirContent = new StringContent(System.Text.Json.JsonSerializer.Serialize(createDirBody), Encoding.UTF8, "application/json");

//    var createDirResp = await httpClient.PostAsync(createDirUrl, createDirContent, ct);
//    if (createDirResp.IsSuccessStatusCode)
//    {
//        _logger.LogInformation("Created directory {FolderPath}", folderPath);
//    }
//    else
//    {
//        var err = await createDirResp.Content.ReadAsStringAsync(ct);
//        // If it already exists, just log and continue
//        if (err.Contains("\"error\"", StringComparison.OrdinalIgnoreCase) && !err.Contains("already exists", StringComparison.OrdinalIgnoreCase))
//        {
//            _logger.LogWarning("Failed to create directory {FolderPath}: {Status} {Body}", folderPath, createDirResp.StatusCode, err);
//        }
//    }

//    // 3. Collect expected filenames and move files recursively
//    var expectedFiles = new List<string>();

//    async Task<bool> MoveFilesRecursive(string oldFolder)
//    {
//        var listUrl = $"{baseUrl}storage/volumes/{volumeUuid}/files/{Uri.EscapeDataString(oldFolder.TrimStart('/'))}";
//        var listResp = await httpClient.GetAsync(listUrl, ct);
//        if (!listResp.IsSuccessStatusCode)
//        {
//            _logger.LogError("Failed to list NetApp folder {Folder}: {Status}", oldFolder, listResp.StatusCode);
//            return false;
//        }

//        var json = await listResp.Content.ReadAsStringAsync(ct);
//        using var doc = JsonDocument.Parse(json);
//        if (!doc.RootElement.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
//            return true; // nothing to do

//        foreach (var rec in records.EnumerateArray())
//        {
//            var name = rec.GetProperty("name").GetString();
//            var type = rec.GetProperty("type").GetString(); // "file" or "directory"
//            if (string.IsNullOrEmpty(name) || name == "." || name == "..")
//                continue;

//            if (type == "directory")
//            {
//                var subOldPath = $"{oldFolder}/{name}";
//                var ok = await MoveFilesRecursive(subOldPath);
//                if (!ok)
//                    return false;
//            }
//            else if (type == "file")
//            {
//                if (!name.Contains(oldvmid))
//                    continue;

//                // Skip CD-ROM/ISO
//                if (name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
//                    continue;

//                // 1) Compute new names
//                var newFileName = name.Replace(oldvmid, newvmid);

//                // 2) Build the raw link path inside the volume (no leading slash)
//                var rawLinkPath = $"images/{newvmid}/{newFileName}";

//                // 3) Percent-encode '/' → '%2F' and '.' → '%2E'
//                var encodedLinkPath = rawLinkPath
//                    .Replace("/", "%2F")
//                    .Replace(".", "%2E");

//                // 4) Lookup the clone’s UUID once (you should have done this earlier):
//                //    var volumeUuid = (await _netappVolumeService.LookupVolumeAsync(cloneName, controllerId, ct)).Uuid;

//                // 5) Build the symlink-create URL (baseUrl already ends with "/api/")
//                var symlinkUrl = $"{baseUrl}storage/volumes/{volumeUuid}/files/{encodedLinkPath}";

//                // 6) Build a relative target so NFS clients resolve it under the same mount
//                //    From images/{newvmid}/, go up one and into images/{oldvmid}/
//                var relativeTarget = $"../{oldvmid}/{name}";

//                // 7) POST the symlink request
//                var payload = new { target = relativeTarget };
//                var content = new StringContent(
//                    System.Text.Json.JsonSerializer.Serialize(payload),
//                    Encoding.UTF8,
//                    "application/json"
//                );

//                var resp = await httpClient.PostAsync(symlinkUrl, content, ct);
//                var respText = await resp.Content.ReadAsStringAsync(ct);

//                if (!resp.IsSuccessStatusCode)
//                {
//                    _logger.LogError(
//                        "Symlink create failed for {Link} → {Target}: {Status} {Body}",
//                        rawLinkPath, relativeTarget, resp.StatusCode, respText
//                    );
//                    return false;
//                }

//                _logger.LogInformation("Created symlink {Link} → {Target}", rawLinkPath, relativeTarget);

//            }
//        }
//        return true;
//    }

//    var moveResult = await MoveFilesRecursive($"/images/{oldvmid}");

//    // 4. Poll for the files to appear in the destination folder
//    async Task<bool> WaitForFilesInDestinationAsync(string destFolder, List<string> filesToCheck, int timeoutSeconds = 20)
//    {
//        var encodedDestFolder = Uri.EscapeDataString(destFolder);
//        var url = $"{baseUrl}storage/volumes/{volumeUuid}/files/{encodedDestFolder}";

//        for (int i = 0; i < timeoutSeconds; i++)
//        {
//            var resp = await httpClient.GetAsync(url, ct);
//            if (resp.IsSuccessStatusCode)
//            {
//                var content = await resp.Content.ReadAsStringAsync(ct);
//                using var doc = JsonDocument.Parse(content);
//                if (doc.RootElement.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array)
//                {
//                    var foundFiles = new HashSet<string>(
//                        records.EnumerateArray()
//                               .Where(r => r.GetProperty("type").GetString() == "file")
//                               .Select(r => r.GetProperty("name").GetString() ?? "")
//                    );
//                    if (filesToCheck.All(f => foundFiles.Contains(f)))
//                    {
//                        _logger.LogInformation("All files found in destination after {Seconds}s", i + 1);
//                        return true;
//                    }
//                }
//            }
//            await Task.Delay(1000);
//        }

//        _logger.LogWarning(
//            "Not all files appeared in destination folder {DestFolder} after {Timeout}s: {Files}",
//            destFolder, timeoutSeconds, string.Join(",", filesToCheck));
//        return false;
//    }

//    var waitResult = await WaitForFilesInDestinationAsync($"images/{newvmid}", expectedFiles);
//    return moveResult && waitResult;
//}