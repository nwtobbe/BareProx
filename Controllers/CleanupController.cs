using BareProx.Data;
using BareProx.Models;
using BareProx.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Controllers
{
    public class CleanupController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly INetappService _netappService;
        private readonly ProxmoxService _proxmoxService;

        public CleanupController(
            ApplicationDbContext context,
            INetappService netappService,
            ProxmoxService proxmoxService)
        {
            _context = context;
            _netappService = netappService;
            _proxmoxService = proxmoxService;
        }

        public async Task<IActionResult> Index()
        {
            // 1) pick the (first) cluster
            var cluster = await _context.ProxmoxClusters
                                       .Include(c => c.Hosts)
                                       .FirstOrDefaultAsync();
            if (cluster == null)
                return NotFound("Proxmox cluster not configured.");

            // 2) find your NetApp controller
            var controllerId = await _context.NetappControllers
                                             .Select(c => c.Id)
                                             .FirstOrDefaultAsync();

            // 3) list *all* FlexClones (even if no longer exported)
            var clones = await _netappService.ListFlexClonesAsync(controllerId);

            var allItems = new List<CleanupItem>();

            foreach (var clone in clones)
            {
                // see if we can still fetch mount‐info for it (optional)
                var mountInfo = (await _netappService.GetVolumesWithMountInfoAsync(controllerId))
                                    .FirstOrDefault(m => m.VolumeName == clone);

                var mountIp = mountInfo?.MountIp;

                // 4) for *each* host, collect any VMs pointing at this clone
                var inUse = new List<ProxmoxVM>();
                foreach (var host in cluster.Hosts)
                {
                    var vms = await _proxmoxService
                        .GetVmsOnNodeAsync(cluster, host.Hostname, clone);
                    if (vms?.Any() == true)
                        inUse.AddRange(vms);
                }

                allItems.Add(new CleanupItem
                {
                    VolumeName = clone,
                    MountIp = mountIp,           // null if unmounted
                    IsInUse = inUse.Any(),
                    AttachedVms = inUse
                });
            }

            // 5) split
            var vm = new CleanupPageViewModel
            {
                InUse = allItems.Where(i => i.IsInUse).ToList(),
                Orphaned = allItems.Where(i => !i.IsInUse).ToList(),
            };

            return View(vm);
        }



        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Cleanup(string volumeName, string mountIp)
        {
            if (string.IsNullOrWhiteSpace(volumeName) || string.IsNullOrWhiteSpace(mountIp))
                return BadRequest(new { error = "Missing volume or mount IP." });

            // 1) Reload cluster + all hosts
            var cluster = await _context.ProxmoxClusters
                                        .Include(c => c.Hosts)
                                        .FirstOrDefaultAsync();
            if (cluster == null)
                return NotFound(new { error = "Proxmox cluster not configured." });

            Exception? lastException = null;
            ProxmoxHost? successfulHost = null;

            // 2) Try each host until unmount succeeds
            foreach (var host in cluster.Hosts)
            {
                try
                {
                    await _proxmoxService.UnmountNfsStorageViaApiAsync(
                        cluster,
                        host.Hostname!,
                        volumeName);

                    successfulHost = host;
                    break;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }
            }

            if (successfulHost == null)
            {
                return StatusCode(500, new
                {
                    error = $"Failed to unmount {volumeName} on all Proxmox hosts. Last error: {lastException?.Message}"
                });
            }

            // 3) Delete the NetApp flexclone
            var deleted = await _netappService.DeleteVolumeAsync(volumeName,
                controllerId: _context.NetappControllers.Select(c => c.Id).First());

            if (!deleted)
            {
                return StatusCode(500, new
                {
                    error = $"Unmounted {volumeName}, but failed to delete from NetApp."
                });
            }

            return Ok(new
            {
                message = $"Flex-clone {volumeName} unmounted from {successfulHost.Hostname} and deleted."
            });
        }



    }
}
