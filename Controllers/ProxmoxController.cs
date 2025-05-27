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

        public async Task<IActionResult> ListVMs(CancellationToken ct)
        {
            // 1) Load cluster
            var cluster = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync(ct);
            if (cluster == null)
            {
                ViewBag.Warning = "No Proxmox clusters are configured.";
                return View(new List<StorageWithVMsDto>());
            }

            // 1a) Check cluster health and skip if *all* hosts are offline
            var (quorate, onlineCount, totalCount, hostStates, summary)
                = await _proxmoxService.GetClusterStatusAsync(cluster, ct);

            // Collect the *names* of hosts that are actually up:
            var onlineHostNames = hostStates
                .Where(kvp => kvp.Value)
                .Select(kvp => kvp.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!onlineHostNames.Any())
            {
                ViewBag.Warning = "No Proxmox hosts are currently online: " + summary;
                return View(new List<StorageWithVMsDto>());
            }

            // 1b) Prune cluster.Hosts to only those that are up
            cluster.Hosts = cluster.Hosts
                .Where(h => onlineHostNames.Contains(h.Hostname ?? h.HostAddress))
                .ToList();

            // 2) Pick primary NetApp controller
            var netappController = await _context.NetappControllers
                .Where(c => c.IsPrimary)
                .FirstOrDefaultAsync(ct);
            if (netappController == null)
            {
                ViewBag.Warning = "No primary NetApp controller is configured.";
                return View(new List<StorageWithVMsDto>());
            }

            // 3) Read selected Proxmox storages for this cluster
            var selectedStorageNames = await _context.SelectedStorages
                .Where(s => s.ClusterId == cluster.Id)
                .Select(s => s.StorageIdentifier)
                .ToListAsync(ct);
            if (!selectedStorageNames.Any())
            {
                ViewBag.Warning = "No Proxmox storage has been selected for backup.";
                return View(new List<StorageWithVMsDto>());
            }

            // 4) Query Proxmox for VM lists, now only against the online hosts
            Dictionary<string, List<ProxmoxVM>> storageVmMap;
            try
            {
                storageVmMap = await _proxmoxService
                    .GetVmsByStorageListAsync(cluster, selectedStorageNames, ct);
            }
            catch (ServiceUnavailableException ex)
            {
                _logger.LogWarning(ex, "Unable to reach any Proxmox host");
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
            var selectedVolumes = await _context.SelectedNetappVolumes.ToListAsync(ct);
            var relations = await _context.SnapMirrorRelations.ToListAsync(ct);
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
            {
                // a) IsReplicable:
                dto.IsReplicable = replicable.Contains(dto.StorageName);

                // b) SnapshotLockingEnabled:
                var vol = selectedVolumes.FirstOrDefault(v =>
                    v.NetappControllerId == dto.NetappControllerId &&
                    v.VolumeName.Equals(dto.StorageName, StringComparison.OrdinalIgnoreCase));

                dto.SnapshotLockingEnabled = vol?.SnapshotLockingEnabled == true;
            }

            // 7) If no DTOs made it through, warn
            if (!model.Any())
                ViewBag.Warning = "No VMs found on the selected storage.";

            // 8) Render
            return View(model);
        }
    }
}
