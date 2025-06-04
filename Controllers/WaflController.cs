using BareProx.Data;
using BareProx.Models;
using BareProx.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace BareProx.Controllers
{
    public class WaflController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly INetappService _netappService;
        private readonly ProxmoxService _proxmoxService;

        public WaflController(
                ApplicationDbContext context,
                INetappService netappService,
                ProxmoxService proxmoxService)
        {
            _context = context;
            _netappService = netappService;
            _proxmoxService = proxmoxService;
        }
        public async Task<IActionResult> Snapshots()
        {
            var cluster = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync();

            if (cluster == null)
            {
                ViewBag.Warning = "No Proxmox clusters are configured.";
                return View(new List<NetappControllerTreeDto>());
            }

            var netappControllers = await _context.NetappControllers.ToListAsync();
            if (!netappControllers.Any())
            {
                ViewBag.Warning = "No NetApp controllers are configured.";
                return View(new List<NetappControllerTreeDto>());
            }

            var selectedVolumes = await _context.SelectedNetappVolumes.ToListAsync();
            var volumeLookup = selectedVolumes.ToLookup(v => v.NetappControllerId);

            var result = new List<NetappControllerTreeDto>();

            foreach (var controller in netappControllers)
            {
                var controllerDto = new NetappControllerTreeDto
                {
                    ControllerName = controller.Hostname,
                    Svms = new List<NetappSvmDto>()
                };

                var groupedBySvm = volumeLookup[controller.Id].GroupBy(v => v.Vserver);
                foreach (var svmGroup in groupedBySvm)
                {
                    var svmDto = new NetappSvmDto
                    {
                        Name = svmGroup.Key,
                        Volumes = new List<NetappVolumeDto>()
                    };

                    foreach (var vol in svmGroup)
                    {
                        var volumeDto = new NetappVolumeDto
                        {
                            VolumeName = vol.VolumeName,
                            Vserver = vol.Vserver,
                            MountIp = vol.MountIp,
                            Uuid = vol.Uuid,
                            ClusterId = vol.ClusterId,
                            IsSelected = true
                        };

                        var snapshots = await _netappService.GetSnapshotsAsync(vol.ClusterId, vol.VolumeName);
                        volumeDto.Snapshots = snapshots;

                        svmDto.Volumes.Add(volumeDto);
                    }

                    controllerDto.Svms.Add(svmDto);
                }

                result.Add(controllerDto);
            }

            return View(result);
        }
        public async Task<IActionResult> SnapMirrorGraph()
        {
            // Pull everything to memory first
            var snapshotList = await _context.NetappSnapshots.ToListAsync();
            var policies = await _context.SnapMirrorPolicies.ToListAsync();
            var retentions = await _context.SnapMirrorPolicyRetentions.ToListAsync();
            var relationsRaw = await _context.SnapMirrorRelations.ToListAsync();

            // Build lookup dictionaries for speed
            var policyByUuid = policies.ToDictionary(p => p.Uuid, p => p);
            var retentionsByPolicyId = retentions
                .GroupBy(r => r.SnapMirrorPolicyId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var relations = new List<SnapMirrorRelationGraphDto>();

            foreach (var rel in relationsRaw)
            {
                // Try to get the policy by UUID
                SnapMirrorPolicy? policy = null;
                policyByUuid.TryGetValue(rel.PolicyUuid, out policy);

                int policyId = policy?.Id ?? 0;
                var relRetentions = retentionsByPolicyId.ContainsKey(policyId) ? retentionsByPolicyId[policyId] : new List<SnapMirrorPolicyRetention>();

                int getRetention(string label) => relRetentions.FirstOrDefault(r => r.Label == label)?.Count ?? 0;
                string? lockedPeriod = relRetentions.FirstOrDefault(r => r.Period != null)?.Period;

                var dto = new SnapMirrorRelationGraphDto
                {
                    SourceController = rel.SourceClusterName,
                    DestinationController = rel.DestinationClusterName,
                    SourceVolume = rel.SourceVolume,
                    DestinationVolume = rel.DestinationVolume,
                    SourceControllerId = rel.SourceControllerId,
                    DestinationControllerId = rel.DestinationControllerId,
                    Health = rel.healthy ? "Healthy" : "Unhealthy",
                    LagTime = FormatIso8601Duration(rel.lag_time),
                    RelationUuid = rel.Uuid,
                    PolicyName = policy?.Name ?? "",
                    PolicyType = policy?.Type ?? "",
                    HourlyRetention = getRetention("hourly"),
                    DailyRetention = getRetention("daily"),
                    WeeklyRetention = getRetention("weekly"),
                    LockedPeriod = FormatIso8601Duration(lockedPeriod),

                    HourlySnapshotsPrimary = 0,
                    DailySnapshotsPrimary = 0,
                    WeeklySnapshotsPrimary = 0,
                    HourlySnapshotsSecondary = 0,
                    DailySnapshotsSecondary = 0,
                    WeeklySnapshotsSecondary = 0
                };

                relations.Add(dto);
            }

            // For each relation, count snapshots in the snapshotList
            foreach (var rel in relations)
            {
                // PRIMARY: Snapshots where PrimaryControllerId/PrimaryVolume matches
                var primarySnaps = snapshotList
                    .Where(s => s.PrimaryControllerId == rel.SourceControllerId && s.PrimaryVolume == rel.SourceVolume && s.ExistsOnPrimary == true);

                rel.HourlySnapshotsPrimary = primarySnaps.Count(s => s.SnapmirrorLabel == "hourly");
                rel.DailySnapshotsPrimary = primarySnaps.Count(s => s.SnapmirrorLabel == "daily");
                rel.WeeklySnapshotsPrimary = primarySnaps.Count(s => s.SnapmirrorLabel == "weekly");

                // SECONDARY: Snapshots where SecondaryControllerId/DestinationVolume matches
                var secondarySnaps = snapshotList
                    .Where(s => s.SecondaryControllerId == rel.DestinationControllerId && s.SecondaryVolume == rel.DestinationVolume && s.ExistsOnSecondary == true);

                rel.HourlySnapshotsSecondary = secondarySnaps.Count(s => s.SnapmirrorLabel == "hourly");
                rel.DailySnapshotsSecondary = secondarySnaps.Count(s => s.SnapmirrorLabel == "daily");
                rel.WeeklySnapshotsSecondary = secondarySnaps.Count(s => s.SnapmirrorLabel == "weekly");
            }

            return View(relations);
        }


        [HttpGet]
        public async Task<IActionResult> GetSnapshotsForVolume(string volume, int ClusterId)
        {
            var snapshots = await _netappService.GetSnapshotsAsync(ClusterId, volume);
            return Json(new { snapshots });
        }

        [HttpGet]
        public async Task<IActionResult> GetNfsIps(string vserver)
        {
            var ips = await _netappService.GetNfsEnabledIpsAsync(vserver);
            return Json(new { ips });
        }

        [HttpPost]
        public async Task<IActionResult> MountSnapshot(MountSnapshotViewModel model)
        {
            if (!ModelState.IsValid)
                return BadRequest("Invalid input.");

            try
            {
                // 1. Generate a clone name
                var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                var cloneName = $"restore_{model.VolumeName}_{timestamp}";

                // 2. Clone the snapshot
                var result = await _netappService.CloneVolumeFromSnapshotAsync(
                    volumeName: model.VolumeName,
                    snapshotName: model.SnapshotName,
                    cloneName: cloneName,
                    controllerId: _context.NetappControllers.Select(c => c.Id).First());

                if (!result.Success)
                    return BadRequest("Failed to create FlexClone: " + result.Message);

                // 3. Copy export policy
                await _netappService.CopyExportPolicyAsync(model.VolumeName, cloneName,
                    controllerId: _context.NetappControllers.Select(c => c.Id).First());

                // 4. Set export path (nas.path)
                var controllerId = _context.NetappControllers.Select(c => c.Id).First();

                // 🔍 Lookup UUID of the clone volume
                var volumeInfo = await _netappService.LookupVolumeAsync(result.CloneVolumeName!, controllerId);
                if (volumeInfo == null)
                    return StatusCode(500, $"Failed to find UUID for cloned volume '{result.CloneVolumeName}'.");

                // ✅ Set the export path using UUID
                await _netappService.SetVolumeExportPathAsync(volumeInfo.Uuid, $"/{cloneName}", controllerId);


                // 5. Mount to Proxmox
                var cluster = await _context.ProxmoxClusters.Include(c => c.Hosts).FirstOrDefaultAsync();
                if (cluster == null)
                    return NotFound("Proxmox cluster not found.");

                var host = cluster.Hosts.FirstOrDefault();
                if (host == null)
                    return NotFound("No Proxmox hosts available in the cluster.");

                var mountSuccess = await _proxmoxService.MountNfsStorageViaApiAsync(
                    cluster,
                    node: host.Hostname!,
                    storageName: cloneName,
                    serverIp: model.MountIp,
                    exportPath: $"/{cloneName}");

                if (!mountSuccess)
                    return StatusCode(500, "Failed to mount clone on Proxmox.");

                TempData["Message"] = $"Snapshot {model.SnapshotName} cloned and mounted as {cloneName}.";
                return RedirectToAction("Snapshots");
            }
            catch (Exception ex)
            {
                return StatusCode(500, "Mount failed: " + ex.Message);
            }
        }
        [HttpPost]
        public async Task<IActionResult> UpdateSnapMirror(string relationUuid)
        {
            try
            {
                var result = await _netappService.TriggerSnapMirrorUpdateAsync(relationUuid);
                if (result)
                    return Json(new { success = true, message = "Update triggered." });

                return Json(new { success = false, message = "Failed to update SnapMirror relationship." });
            }
            catch (Exception ex)
            {
                // log ex if needed
                return Json(new { success = false, message = ex.Message });
            }
        }

        public static string FormatIso8601Duration(string isoDuration)
        {
            if (string.IsNullOrEmpty(isoDuration))
                return "";

            // Example: P1DT1H30M13S or PT53M59S
            var regex = new Regex(@"^P((?<days>\d+)D)?(T((?<hours>\d+)H)?((?<minutes>\d+)M)?((?<seconds>\d+)S)?)?$");
            var match = regex.Match(isoDuration);
            if (!match.Success)
                return isoDuration; // fallback

            var parts = new List<string>();
            if (int.TryParse(match.Groups["days"].Value, out var days) && days > 0)
                parts.Add($"{days}d");
            if (int.TryParse(match.Groups["hours"].Value, out var hours) && hours > 0)
                parts.Add($"{hours}h");
            if (int.TryParse(match.Groups["minutes"].Value, out var minutes) && minutes > 0)
                parts.Add($"{minutes}m");
            if (int.TryParse(match.Groups["seconds"].Value, out var seconds) && seconds > 0)
                parts.Add($"{seconds}s");

            return string.Join(" ", parts);
        }

    }
}
