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
                return View(new List<VolumeSnapshotTreeDto>());
            }

            var netappController = await _context.NetappControllers.FirstOrDefaultAsync();
            if (netappController == null)
            {
                ViewBag.Warning = "No NetApp controllers are configured.";
                return View(new List<VolumeSnapshotTreeDto>());
            }

            var storageVmMap = await _proxmoxService.GetFilteredStorageWithVMsAsync(cluster.Id, netappController.Id);

            var storageNames = storageVmMap.Keys
                .Where(name =>
                    !name.Contains("backup", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("restore_", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var result = await _netappService.GetSnapshotsForVolumesAsync(storageNames);

            return View(result ?? new List<VolumeSnapshotTreeDto>());
        }


        [HttpGet]
        public async Task<IActionResult> GetSnapshotsForVolume(string volume, string vserver)
        {
            var snapshots = await _netappService.GetSnapshotsAsync(vserver, volume);
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
