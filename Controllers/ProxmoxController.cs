using BareProx.Data;
using BareProx.Models;
using BareProx.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BareProx.Controllers
{
    public class ProxmoxController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ProxmoxService _proxmoxService;
        private readonly INetappService _netappService;

        public ProxmoxController(ApplicationDbContext context, ProxmoxService proxmoxService, INetappService netappService)
        {
            _context = context;
            _proxmoxService = proxmoxService;
            _netappService = netappService;
        }

        // GET: Backup/ListVMs
        public async Task<IActionResult> ListVMs()
        {
            var cluster = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync();

            if (cluster == null)
                return NotFound("No Proxmox clusters configured.");

            var netappController = await _context.NetappControllers.FirstOrDefaultAsync();
            if (netappController == null)
                return NotFound("No NetApp controllers configured.");

            var selectedStorageNames = await _context.SelectedStorages
                .Select(s => s.StorageIdentifier)
                .ToListAsync();
            if (selectedStorageNames == null)
                return NotFound("No Selected Storageconfigured.");

            var storageVmMap = await _proxmoxService.GetVmsByStorageListAsync(cluster, selectedStorageNames);


            var model = storageVmMap
                // exclude any storage whose name contains “backup” or “_restore” (case-insensitive)
                .Where(kvp =>
                    !kvp.Key.Contains("backup", StringComparison.OrdinalIgnoreCase) &&
                    !kvp.Key.Contains("restore_", StringComparison.OrdinalIgnoreCase))
                .Select(kvp => new StorageWithVMsDto
                {
                    StorageName = kvp.Key,
                    VMs = kvp.Value
                                             .Select(vm => new ProxmoxVM
                                             {
                                                 Id = vm.Id,
                                                 Name = vm.Name,
                                                 HostName = vm.HostName,
                                                 HostAddress = vm.HostAddress
                                             })
                                             .ToList(),
                    ClusterId = cluster.Id,
                    NetappControllerId = netappController.Id
                })
                .ToList();

            return View(model); // Now matches @model List<StorageWithVMsDto> in the view
        }

    }
}
