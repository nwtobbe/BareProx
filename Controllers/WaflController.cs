using BareProx.Data;
using BareProx.Models;
using BareProx.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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

    }
}
