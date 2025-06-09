using BareProx.Data;
using BareProx.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace BareProx.Services
{
    public class NetappVolumeService : INetappVolumeService
    {
        private readonly ApplicationDbContext _context;
        private readonly INetappAuthService _authService;
        private readonly ILogger<NetappVolumeService> _logger;

        public NetappVolumeService(
            ApplicationDbContext context,
            INetappAuthService authService,
            ILogger<NetappVolumeService> logger)
        {
            _context = context;
            _authService = authService;
            _logger = logger;
        }

        public async Task<List<VserverDto>> GetVserversAndVolumesAsync(int netappControllerId, CancellationToken ct = default)
        {
            var vservers = new List<VserverDto>();

            var controller = await _context.NetappControllers.FindAsync(netappControllerId, ct);
            if (controller == null)
                throw new Exception("NetApp controller not found.");

            var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            try
            {
                var vserverUrl = $"{baseUrl}svm/svms";
                var vserverResponse = await httpClient.GetAsync(vserverUrl, ct);
                vserverResponse.EnsureSuccessStatusCode();

                var vserverJson = await vserverResponse.Content.ReadAsStringAsync(ct);
                using var vserverDoc = JsonDocument.Parse(vserverJson);
                var vserverElements = vserverDoc.RootElement.GetProperty("records").EnumerateArray();

                foreach (var vserverElement in vserverElements)
                {
                    var vserverName = vserverElement.GetProperty("name").GetString() ?? string.Empty;
                    var vserverDto = new VserverDto { Name = vserverName };

                    var volumesUrl = $"{baseUrl}storage/volumes?svm.name={Uri.EscapeDataString(vserverName)}";
                    var volumesResponse = await httpClient.GetAsync(volumesUrl, ct);
                    volumesResponse.EnsureSuccessStatusCode();

                    var volumesJson = await volumesResponse.Content.ReadAsStringAsync(ct);
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


        public async Task<List<NetappMountInfo>> GetVolumesWithMountInfoAsync(int controllerId, CancellationToken ct = default)
        {
            var controller = await _context.NetappControllers.FindAsync(controllerId, ct);
            if (controller == null)
                throw new Exception("NetApp controller not found.");

            var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            try
            {
                var interfaceUrl = $"{baseUrl}network/ip/interfaces?fields=ip.address,svm.name,services&services=data_nfs";
                var interfaceResponse = await httpClient.GetAsync(interfaceUrl, ct);
                interfaceResponse.EnsureSuccessStatusCode();

                var interfaceJson = await interfaceResponse.Content.ReadAsStringAsync(ct);
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
                var volumesResponse = await httpClient.GetAsync(volumesUrl, ct);
                volumesResponse.EnsureSuccessStatusCode();

                var volumesJson = await volumesResponse.Content.ReadAsStringAsync(ct);
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


    }
    }
