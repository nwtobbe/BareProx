using BareProx.Data;
using BareProx.Models;
using BareProx.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Controllers
{
    public class ProxmoxController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ProxmoxService _proxmoxService;
        private readonly ILogger<ProxmoxController> _logger;

        public ProxmoxController(
            ApplicationDbContext context,
            ProxmoxService proxmoxService,
            ILogger<ProxmoxController> logger)
        {
            _context = context;
            _proxmoxService = proxmoxService;
            _logger = logger;
        }

        public async Task<IActionResult> ListVMs()
        {
            // 1) Load cluster
            var cluster = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync();
            if (cluster == null)
            {
                ViewBag.Warning = "No Proxmox clusters are configured.";
                return View(new List<StorageWithVMsDto>());
            }

            // 2) Pick primary NetApp controller
            var netappController = await _context.NetappControllers
                .Where(c => c.IsPrimary)
                .FirstOrDefaultAsync();
            if (netappController == null)
            {
                ViewBag.Warning = "No primary NetApp controller is configured.";
                return View(new List<StorageWithVMsDto>());
            }

            // 3) Read selected Proxmox storages for this cluster
            var selectedStorageNames = await _context.SelectedStorages
                .Where(s => s.ClusterId == cluster.Id)
                .Select(s => s.StorageIdentifier)
                .ToListAsync();
            if (!selectedStorageNames.Any())
            {
                ViewBag.Warning = "No Proxmox storage has been selected for backup.";
                return View(new List<StorageWithVMsDto>());
            }

            // 4) Query Proxmox for VM lists, with error handling
            Dictionary<string, List<ProxmoxVM>> storageVmMap;
            try
            {
                storageVmMap = await _proxmoxService
                    .GetVmsByStorageListAsync(cluster, selectedStorageNames);
            }
            catch (ServiceUnavailableException ex)
            {
                _logger.LogWarning(ex, "Unable to reach Proxmox cluster");
                ViewBag.Warning = ex.Message;
                return View(new List<StorageWithVMsDto>());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error querying Proxmox");
                ViewBag.Warning = "An unexpected error occurred while fetching VM lists.";
                return View(new List<StorageWithVMsDto>());
            }

            // 5) Build DTOs & filter out empty storages
            var model = storageVmMap
                .Where(kvp => kvp.Value?.Any() == true)
                .Select(kvp => new StorageWithVMsDto
                {
                    StorageName = kvp.Key,
                    VMs = kvp.Value.Select(vm => new ProxmoxVM
                    {
                        Id = vm.Id,
                        Name = vm.Name,
                        HostName = vm.HostName,
                        HostAddress = vm.HostAddress
                    }).ToList(),
                    ClusterId = cluster.Id,
                    NetappControllerId = netappController.Id
                })
                .ToList();

            // 6) Compute which storages can replicate
            var selectedVolumes = await _context.SelectedNetappVolumes.ToListAsync();
            var relations = await _context.SnapMirrorRelations.ToListAsync();
            var replicable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rel in relations)
            {
                var primary = selectedVolumes.FirstOrDefault(v =>
                    v.NetappControllerId == rel.SourceControllerId &&
                    v.VolumeName == rel.SourceVolume);

                var secondary = selectedVolumes.FirstOrDefault(v =>
                    v.NetappControllerId == rel.DestinationControllerId &&
                    v.VolumeName == rel.DestinationVolume);

                if (primary != null && secondary != null)
                    replicable.Add(rel.SourceVolume);
            }

            foreach (var dto in model)
                dto.IsReplicable = replicable.Contains(dto.StorageName);

            // 7) If no DTOs made it through, warn
            if (!model.Any())
                ViewBag.Warning = "No VMs found on the selected storage.";

            // 8) Render
            return View(model);
        }
    }
}
