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
            {
                ViewBag.Warning = "No Proxmox clusters are configured.";
                return View(new List<StorageWithVMsDto>());
            }

            var netappController = await _context.NetappControllers.FirstOrDefaultAsync();
            if (netappController == null)
            {
                ViewBag.Warning = "No NetApp controllers are configured.";
                return View(new List<StorageWithVMsDto>());
            }

            var selectedStorageNames = await _context.SelectedStorages
                .Select(s => s.StorageIdentifier)
                .ToListAsync();

            if (selectedStorageNames == null || !selectedStorageNames.Any())
            {
                ViewBag.Warning = "No storage has been selected for backup.";
                return View(new List<StorageWithVMsDto>());
            }

            var storageVmMap = await _proxmoxService.GetVmsByStorageListAsync(cluster, selectedStorageNames);

            var model = storageVmMap
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

            return View(model);
        }


    }
}
