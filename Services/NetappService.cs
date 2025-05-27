using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;
using BareProx.Data;
using BareProx.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Net;
using Polly;
using Polly.Retry;
using static System.Net.Mime.MediaTypeNames;


namespace BareProx.Services
{
    public class NetappService : INetappService
    {
        private readonly ApplicationDbContext _context;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<NetappService> _logger;
        private readonly IEncryptionService _encryptionService;
        private readonly IRemoteApiClient _remoteApiClient;
        public NetappService(ApplicationDbContext context, IHttpClientFactory httpClientFactory, ILogger<NetappService> logger, IEncryptionService encryptionService, IRemoteApiClient remoteApiClient)
        {
            _context = context;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _encryptionService = encryptionService;
            _remoteApiClient = remoteApiClient;

        }

        private AuthenticationHeaderValue GetEncryptedAuthHeader(string username, string encryptedPassword)
        {
            var decrypted = _encryptionService.Decrypt(encryptedPassword);
            var base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{decrypted}"));
            return new AuthenticationHeaderValue("Basic", base64);
        }
        private HttpClient CreateAuthenticatedClient(NetappController controller, out string baseUrl)
        {
            var httpClient = _httpClientFactory.CreateClient("NetappClient");
            httpClient.DefaultRequestHeaders.Authorization =
                GetEncryptedAuthHeader(controller.Username, controller.PasswordHash);

            baseUrl = $"https://{controller.IpAddress}/api/";
            return httpClient;
        }
        public async Task<List<SelectedNetappVolume>> GetSelectedVolumesAsync(int controllerId)
        {
            return await _context.SelectedNetappVolumes
                .Where(v => v.NetappControllerId == controllerId)
                .ToListAsync();
        }
        public async Task<List<VserverDto>> GetVserversAndVolumesAsync(int netappControllerId)
        {
            var vservers = new List<VserverDto>();

            var controller = await _context.NetappControllers.FindAsync(netappControllerId);
            if (controller == null)
                throw new Exception("NetApp controller not found.");

            var httpClient = CreateAuthenticatedClient(controller, out var baseUrl);

            try
            {
                var vserverUrl = $"{baseUrl}svm/svms";
                var vserverResponse = await httpClient.GetAsync(vserverUrl);
                vserverResponse.EnsureSuccessStatusCode();

                var vserverJson = await vserverResponse.Content.ReadAsStringAsync();
                using var vserverDoc = JsonDocument.Parse(vserverJson);
                var vserverElements = vserverDoc.RootElement.GetProperty("records").EnumerateArray();

                foreach (var vserverElement in vserverElements)
                {
                    var vserverName = vserverElement.GetProperty("name").GetString() ?? string.Empty;
                    var vserverDto = new VserverDto { Name = vserverName };

                    var volumesUrl = $"{baseUrl}storage/volumes?svm.name={Uri.EscapeDataString(vserverName)}";
                    var volumesResponse = await httpClient.GetAsync(volumesUrl);
                    volumesResponse.EnsureSuccessStatusCode();

                    var volumesJson = await volumesResponse.Content.ReadAsStringAsync();
                    using var volumesDoc = JsonDocument.Parse(volumesJson);
                    var volumeElements = volumesDoc.RootElement.GetProperty("records").EnumerateArray();

                    foreach (var volumeElement in volumeElements)
                    {
                        var volumeName = volumeElement.GetProperty("name").GetString() ?? string.Empty;
                        var uuid = volumeElement.TryGetProperty("uuid", out var uuidProp) ? uuidProp.GetString() : string.Empty;

                        var mountIp = volumeElement.TryGetProperty("nas", out var nasProp) &&
                                      nasProp.TryGetProperty("export_policy", out var exportPolicyProp) &&
                                      exportPolicyProp.TryGetProperty("rules", out var rulesProp) &&
                                      rulesProp.GetArrayLength() > 0 &&
                                      rulesProp[0].TryGetProperty("clients", out var clientsProp) &&
                                      clientsProp.GetArrayLength() > 0
                                      ? clientsProp[0].GetString()
                                      : string.Empty;

                        var volume = new NetappVolumeDto
                        {
                            VolumeName = volumeName,
                            Uuid = uuid,
                            MountIp = mountIp,
                            ClusterId = controller.Id
                        };

                        vserverDto.Volumes.Add(volume);
                    }

                    vservers.Add(vserverDto);
                }

                return vservers;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP error while fetching NetApp vservers or volumes.");
                throw new Exception("Failed to retrieve data from NetApp API.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in NetApp service.");
                throw;
            }
        }




        public async Task<List<NetappMountInfo>> GetVolumesWithMountInfoAsync(int controllerId)
        {
            var controller = await _context.NetappControllers.FindAsync(controllerId);
            if (controller == null)
                throw new Exception("NetApp controller not found.");

            var httpClient = CreateAuthenticatedClient(controller, out var baseUrl);

            try
            {
                var interfaceUrl = $"{baseUrl}network/ip/interfaces?fields=ip.address,svm.name,services&services=data_nfs";
                var interfaceResponse = await httpClient.GetAsync(interfaceUrl);
                interfaceResponse.EnsureSuccessStatusCode();

                var interfaceJson = await interfaceResponse.Content.ReadAsStringAsync();
                using var interfaceDoc = JsonDocument.Parse(interfaceJson);
                var interfaceData = interfaceDoc.RootElement.GetProperty("records");

                var svmToIps = new Dictionary<string, List<string>>();
                foreach (var iface in interfaceData.EnumerateArray())
                {
                    var ip = iface.GetProperty("ip").GetProperty("address").GetString();
                    var svm = iface.GetProperty("svm").GetProperty("name").GetString();

                    if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(svm))
                        continue;

                    if (!svmToIps.ContainsKey(svm))
                        svmToIps[svm] = new List<string>();

                    svmToIps[svm].Add(ip);
                }

                var volumesUrl = $"{baseUrl}storage/volumes?fields=name,svm.name";
                var volumesResponse = await httpClient.GetAsync(volumesUrl);
                volumesResponse.EnsureSuccessStatusCode();

                var volumesJson = await volumesResponse.Content.ReadAsStringAsync();
                using var volumesDoc = JsonDocument.Parse(volumesJson);
                var volumeData = volumesDoc.RootElement.GetProperty("records");

                var result = new List<NetappMountInfo>();
                foreach (var volume in volumeData.EnumerateArray())
                {
                    var volumeName = volume.GetProperty("name").GetString();
                    var svmName = volume.GetProperty("svm").GetProperty("name").GetString();

                    if (string.IsNullOrWhiteSpace(volumeName) || string.IsNullOrWhiteSpace(svmName))
                        continue;

                    if (!svmToIps.TryGetValue(svmName, out var mountIps) || !mountIps.Any())
                        continue;

                    foreach (var mountIp in mountIps)
                    {
                        result.Add(new NetappMountInfo
                        {
                            VolumeName = volumeName,
                            VserverName = svmName,
                            MountPath = $"{mountIp}:/{volumeName}",
                            MountIp = mountIp
                        });
                    }
                }

                return result;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP error while fetching mount info from NetApp.");
                throw new Exception("Failed to retrieve volume mount info from NetApp API.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while building mount info.");
                throw;
            }
        }


        public async Task<SnapshotResult> CreateSnapshotAsync(int clusterId, string storageName, string snapmirrorLabel)
        {
            try
            {
                var volumes = await GetVolumesWithMountInfoAsync(clusterId);
                var volume = volumes.FirstOrDefault(v => v.VolumeName.Equals(storageName, StringComparison.OrdinalIgnoreCase));

                if (volume == null)
                {
                    return new SnapshotResult
                    {
                        Success = false,
                        ErrorMessage = $"No matching NetApp volume for storage name '{storageName}'."
                    };
                }

                var snapshotName = $"{snapmirrorLabel}-{DateTime.UtcNow:yyyyMMddHHmmss}";
                await SendSnapshotRequestAsync(volume.VolumeName, snapshotName, snapmirrorLabel);

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

        public async Task<DeleteSnapshotResult> DeleteSnapshotAsync(int controllerId, string volumeName, string snapshotName)
        {
            var result = new DeleteSnapshotResult();

            var controller = await _context.NetappControllers.FirstOrDefaultAsync(c => c.Id == controllerId);
            if (controller == null)
            {
                result.ErrorMessage = $"NetApp controller #{controllerId} not found.";
                return result;
            }

            var httpClient = CreateAuthenticatedClient(controller, out var baseUrl);

            try
            {
                // 1. Get volume UUID
                var volResp = await httpClient.GetAsync($"{baseUrl}storage/volumes?name={Uri.EscapeDataString(volumeName)}");
                volResp.EnsureSuccessStatusCode();
                using var volDoc = JsonDocument.Parse(await volResp.Content.ReadAsStringAsync());
                var volRecs = volDoc.RootElement.GetProperty("records");
                if (volRecs.GetArrayLength() == 0)
                {
                    result.ErrorMessage = $"Volume '{volumeName}' not found.";
                    return result;
                }
                var volumeUuid = volRecs[0].GetProperty("uuid").GetString()!;

                // 2. Get snapshot UUID
                var snapResp = await httpClient.GetAsync($"{baseUrl}storage/volumes/{volumeUuid}/snapshots?fields=name,uuid");
                snapResp.EnsureSuccessStatusCode();
                using var snapDoc = JsonDocument.Parse(await snapResp.Content.ReadAsStringAsync());
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
                var deleteResp = await httpClient.DeleteAsync($"{baseUrl}storage/volumes/{volumeUuid}/snapshots/{snapshotUuid}");
                if (deleteResp.IsSuccessStatusCode)
                {
                    result.Success = true;
                }
                else
                {
                    var body = await deleteResp.Content.ReadAsStringAsync();
                    result.ErrorMessage = $"Failed to delete snapshot: {deleteResp.StatusCode} - {body}";
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Exception: {ex.Message}";
            }

            return result;
        }


        private async Task SendSnapshotRequestAsync(string volumeName, string snapshotName, string snapmirrorLabel)
        {
            var controller = await _context.NetappControllers.FirstOrDefaultAsync();
            if (controller == null)
                throw new Exception("NetApp controller not found.");

            var httpClient = CreateAuthenticatedClient(controller, out var baseUrl);

            // 🔍 Step 1: Lookup volume UUID by name
            var lookupUrl = $"{baseUrl}storage/volumes?name={Uri.EscapeDataString(volumeName)}";
            var lookupResponse = await httpClient.GetAsync(lookupUrl);
            lookupResponse.EnsureSuccessStatusCode();

            var lookupJson = await lookupResponse.Content.ReadAsStringAsync();
            using var lookupDoc = JsonDocument.Parse(lookupJson);
            var records = lookupDoc.RootElement.GetProperty("records");

            if (records.GetArrayLength() == 0)
                throw new Exception($"Volume '{volumeName}' not found in NetApp API.");

            var uuid = records[0].GetProperty("uuid").GetString();

            // ✅ Step 2: Create snapshot using UUID
            var snapshotUrl = $"{baseUrl}storage/volumes/{uuid}/snapshots";
            var payload = new
            {
                name = snapshotName,
                snapmirror_label = snapmirrorLabel
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(snapshotUrl, content);
            response.EnsureSuccessStatusCode();
        }

        public async Task<FlexCloneResult> CloneVolumeFromSnapshotAsync(
            string volumeName,
            string snapshotName,
            string cloneName,
            int controllerId)
        {
            // 1) Fetch controller
            var controller = await _context.NetappControllers.FindAsync(controllerId);
            if (controller == null)
                return new FlexCloneResult { Success = false, Message = "Controller not found." };

            var httpClient = CreateAuthenticatedClient(controller, out var baseUrl);

            // 2) Lookup volume UUID + SVM name
            var volLookupUrl = $"{baseUrl}storage/volumes?name={Uri.EscapeDataString(volumeName)}&fields=uuid,svm.name";
            var volResp = await httpClient.GetAsync(volLookupUrl);
            if (!volResp.IsSuccessStatusCode)
            {
                var err = await volResp.Content.ReadAsStringAsync();
                return new FlexCloneResult { Success = false, Message = $"Volume lookup failed: {err}" };
            }

            using var volDoc = JsonDocument.Parse(await volResp.Content.ReadAsStringAsync());
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
                var snapResp = await httpClient.GetAsync(snapLookupUrl);
                if (!snapResp.IsSuccessStatusCode)
                {
                    var err = await snapResp.Content.ReadAsStringAsync();
                    return new FlexCloneResult { Success = false, Message = $"Snapshot lookup failed: {err}" };
                }

                using var snapDoc = JsonDocument.Parse(await snapResp.Content.ReadAsStringAsync());
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
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );
            var cloneResp = await httpClient.PostAsync($"{baseUrl}storage/volumes", content);

            // 5) Handle the 202 Accepted and parse the Job UUID
            if (cloneResp.StatusCode == HttpStatusCode.Accepted)
            {
                using var respDoc = JsonDocument.Parse(await cloneResp.Content.ReadAsStringAsync());
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
            var body = await cloneResp.Content.ReadAsStringAsync();
            return new FlexCloneResult
            {
                Success = false,
                Message = $"Clone failed ({(int)cloneResp.StatusCode}): {body}"
            };
        }

        public async Task<List<string>> GetNfsEnabledIpsAsync(string vserver)
        {
            var controller = await _context.NetappControllers.FirstOrDefaultAsync();
            if (controller == null)
                throw new Exception("NetApp controller not found.");

            var httpClient = CreateAuthenticatedClient(controller, out var baseUrl);

            var url = $"{baseUrl}network/ip/interfaces?svm.name={Uri.EscapeDataString(vserver)}&fields=ip.address,services";

            var resp = await httpClient.GetAsync(url);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
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



        public async Task<bool> DeleteVolumeAsync(string volumeName, int controllerId)
        {
            // 1) Fetch controller
            var controller = await _context.NetappControllers.FindAsync(controllerId);
            if (controller == null) return false;

            // 🔐 Prepare HTTP client + base URL
            var httpClient = CreateAuthenticatedClient(controller, out var baseUrl); // baseUrl = https://<ip>/api/

            // 2) Lookup UUID by name
            var lookupUrl = $"{baseUrl}storage/volumes?name={Uri.EscapeDataString(volumeName)}&fields=uuid";
            var lookupResp = await httpClient.GetAsync(lookupUrl);
            if (!lookupResp.IsSuccessStatusCode) return false;

            using var lookupDoc = JsonDocument.Parse(await lookupResp.Content.ReadAsStringAsync());
            var records = lookupDoc.RootElement.GetProperty("records");
            if (records.GetArrayLength() == 0) return false;

            var uuid = records[0].GetProperty("uuid").GetString();
            if (string.IsNullOrEmpty(uuid)) return false;

            // 3) Unexport by PATCHing nas.path = ""
            var patchUrl = $"{baseUrl}storage/volumes/{uuid}";
            var unexportPayload = new { nas = new { path = "" } };
            var patchContent = new StringContent(JsonSerializer.Serialize(unexportPayload), Encoding.UTF8, "application/json");
            var patchResp = await httpClient.PatchAsync(patchUrl, patchContent);
            if (!patchResp.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Failed to unexport volume {Name} (uuid={Uuid}): {Code}", volumeName, uuid, patchResp.StatusCode);
                // Proceed to delete anyway
            }

            // 4) Delete by UUID
            var deleteUrl = $"{baseUrl}storage/volumes/{uuid}";
            var deleteResp = await httpClient.DeleteAsync(deleteUrl);
            return deleteResp.IsSuccessStatusCode;
        }



        public async Task<bool> CopyExportPolicyAsync(
            string sourceVolumeName,
            string targetCloneName,
            int controllerId)
        {
            // 1) Fetch controller
            var controller = await _context.NetappControllers.FindAsync(controllerId);
            if (controller == null) return false;

            var httpClient = CreateAuthenticatedClient(controller, out var baseUrl);

            // 2) Lookup source export policy *name*
            var srcLookupUrl = $"{baseUrl}storage/volumes" +
                $"?name={Uri.EscapeDataString(sourceVolumeName)}" +
                "&fields=nas.export_policy.name";
            var srcResp = await httpClient.GetAsync(srcLookupUrl);
            srcResp.EnsureSuccessStatusCode();

            using var srcDoc = JsonDocument.Parse(await srcResp.Content.ReadAsStringAsync());
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
            var tgtResp = await httpClient.GetAsync(tgtLookupUrl);
            tgtResp.EnsureSuccessStatusCode();

            using var tgtDoc = JsonDocument.Parse(await tgtResp.Content.ReadAsStringAsync());
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
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );
            var request = new HttpRequestMessage(HttpMethod.Patch, patchUrl) { Content = content };
            var patchResp = await httpClient.SendAsync(request);

            if (!patchResp.IsSuccessStatusCode)
            {
                var err = await patchResp.Content.ReadAsStringAsync();
                _logger.LogError("Export policy patch failed: {0}", err);
            }

            return patchResp.IsSuccessStatusCode;
        }


        public async Task<List<string>> ListVolumesByPrefixAsync(string prefix, int controllerId)
        {
            var controller = await _context.NetappControllers.FindAsync(controllerId);
            if (controller == null) return new List<string>();

            var client = CreateAuthenticatedClient(controller, out var baseUrl);

            var url = $"{baseUrl}storage/volumes?fields=name";
            var resp = await client.GetAsync(url);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement
                      .GetProperty("records")
                      .EnumerateArray()
                      .Select(r => r.GetProperty("name").GetString()!)
                      .Where(n => n != null && n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                      .ToList();
        }

        public async Task<List<string>> ListFlexClonesAsync(int controllerId)
        {
            var controller = await _context.NetappControllers.FindAsync(controllerId);
            if (controller == null)
                throw new InvalidOperationException("Controller not found");

            var client = CreateAuthenticatedClient(controller, out var baseUrl);

            var url = $"{baseUrl}storage/volumes?name=restore_*";
            var resp = await client.GetAsync(url);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
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
           int controllerId)
        {
            // 1) Fetch controller
            var controller = await _context.NetappControllers
                .FirstOrDefaultAsync(c => c.Id == controllerId);
            if (controller == null)
                return false;

            // 2) Use helper to get HttpClient and base URL
            var client = CreateAuthenticatedClient(controller, out var baseUrl);

            var patchUrl = $"{baseUrl}storage/volumes/{volumeUuid}";
            var geturl = $"{patchUrl}?fields=nas.path";
            var payloadObj = new { nas = new { path = exportPath } };
            var payloadJson = JsonSerializer.Serialize(payloadObj);

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
                            _logger?.LogWarning(outcome.Exception,
                                "[ExportPath:{Attempt}] HTTP error; retrying in {Delay}s",
                                attempt, delay.TotalSeconds);
                        else
                            _logger?.LogWarning(
                                "[ExportPath:{Attempt}] verification failed; retrying in {Delay}s",
                                attempt, delay.TotalSeconds);
                    }
                );

            // 4) Execute PATCH + GET/verify under policy
            return await retryPolicy.ExecuteAsync(async () =>
            {
                // a) PATCH
                using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
                var patchResp = await client.PatchAsync(patchUrl, content);
                if (!patchResp.IsSuccessStatusCode)
                {
                    _logger?.LogError("[ExportPath] PATCH failed: {Status}", patchResp.StatusCode);
                    return false;
                }

                // b) GET and capture raw JSON
                var getResp = await client.GetAsync(geturl);
                if (!getResp.IsSuccessStatusCode)
                {
                    _logger?.LogError("[ExportPath] GET failed: {Status}", getResp.StatusCode);
                    return false;
                }

                string text = await getResp.Content.ReadAsStringAsync();
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(text);
                }
                catch (JsonException je)
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
            });
        }


        public async Task<VolumeInfo?> LookupVolumeAsync(string volumeName, int controllerId)
        {
            var controller = await _context.NetappControllers
                .FirstOrDefaultAsync(c => c.Id == controllerId);
            if (controller == null) return null;

            // 🔐 Use helper for encrypted auth and base URL
            var client = CreateAuthenticatedClient(controller, out var baseUrl);
            var url = $"{baseUrl}storage/volumes?name={Uri.EscapeDataString(volumeName)}&fields=uuid";

            var resp = await client.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var records = doc.RootElement.GetProperty("records");
            if (records.GetArrayLength() == 0) return null;

            var uuid = records[0].GetProperty("uuid").GetString();
            if (string.IsNullOrEmpty(uuid)) return null;

            return new VolumeInfo { Uuid = uuid };
        }


        public async Task<List<string>> GetSnapshotsAsync(string vserver, string volumeName)
        {
            var controller = await _context.NetappControllers.FirstOrDefaultAsync();
            if (controller == null)
                throw new Exception("NetApp controller not found.");

            // 🔐 Use encrypted credentials + helper
            var client = CreateAuthenticatedClient(controller, out var baseUrl);

            // Step 1: Lookup the volume UUID using the volume name
            var volLookupUrl = $"{baseUrl}storage/volumes?name={Uri.EscapeDataString(volumeName)}";
            var volResp = await client.GetAsync(volLookupUrl);
            volResp.EnsureSuccessStatusCode();

            using var volDoc = JsonDocument.Parse(await volResp.Content.ReadAsStringAsync());
            var volRecs = volDoc.RootElement.GetProperty("records");
            if (volRecs.GetArrayLength() == 0)
                return new List<string>(); // Volume not found

            var volumeUuid = volRecs[0].GetProperty("uuid").GetString();

            // Step 2: Fetch snapshots for that volume
            var snapUrl = $"{baseUrl}storage/volumes/{volumeUuid}/snapshots?fields=name";
            var snapResp = await client.GetAsync(snapUrl);
            snapResp.EnsureSuccessStatusCode();

            using var snapDoc = JsonDocument.Parse(await snapResp.Content.ReadAsStringAsync());
            var snapshotNames = snapDoc.RootElement
                .GetProperty("records")
                .EnumerateArray()
                .Select(e => e.GetProperty("name").GetString() ?? string.Empty)
                .Where(name => !string.IsNullOrEmpty(name))
                .ToList();

            return snapshotNames;
        }




        public async Task<List<VolumeSnapshotTreeDto>> GetSnapshotsForVolumesAsync(HashSet<string> volumeNames)
        {
            var controller = await _context.NetappControllers.FirstOrDefaultAsync();
            if (controller == null)
                throw new Exception("No NetApp controller found.");

            // 🔐 Use encrypted credentials and base URL helper
            var client = CreateAuthenticatedClient(controller, out var baseUrl);

            var volumesUrl = $"{baseUrl}storage/volumes?fields=name,uuid,svm.name";
            var volumesResp = await client.GetAsync(volumesUrl);
            volumesResp.EnsureSuccessStatusCode();

            using var volumesDoc = JsonDocument.Parse(await volumesResp.Content.ReadAsStringAsync());
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
                var snapResp = await client.GetAsync(snapUrl);
                if (!snapResp.IsSuccessStatusCode)
                    continue;

                using var snapDoc = JsonDocument.Parse(await snapResp.Content.ReadAsStringAsync());
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



    }
}
