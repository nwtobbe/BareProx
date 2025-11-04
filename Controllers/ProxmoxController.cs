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
using BareProx.Services.Proxmox;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Controllers
{
    public class ProxmoxController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ProxmoxService _proxmoxService;
        private readonly IProxmoxInventoryCache _invCache;
        private readonly ILogger<ProxmoxController> _logger;

        public ProxmoxController(
            ApplicationDbContext context,
            ProxmoxService proxmoxService,
            IProxmoxInventoryCache invCache,
            ILogger<ProxmoxController> logger)
        {
            _context = context;
            _proxmoxService = proxmoxService;
            _invCache = invCache;
            _logger = logger;
        }

        public async Task<IActionResult> ListVMs(CancellationToken ct)
        {
            // 1) Load the first configured cluster (no tracking)
            var cluster = await _context.ProxmoxClusters
                .AsNoTracking()
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync(ct);

            if (cluster == null)
            {
                ViewBag.Warning = "No Proxmox clusters are configured.";
                return View(new List<StorageWithVMsDto>());
            }

            // 2) Primary NetApp controller (no tracking)
            var netappController = await _context.NetappControllers
                .AsNoTracking()
                .Where(c => c.IsPrimary)
                .Select(c => new { c.Id })
                .FirstOrDefaultAsync(ct);

            if (netappController == null)
            {
                ViewBag.Warning = "No primary NetApp controller is configured.";
                return View(new List<StorageWithVMsDto>());
            }

            // 3) Selected storages (filter set)
            var selectedStorageNames = await _context.SelectedStorages
                .AsNoTracking()
                .Where(s => s.ClusterId == cluster.Id)
                .Select(s => s.StorageIdentifier)
                .ToListAsync(ct);

            if (selectedStorageNames.Count == 0)
            {
                ViewBag.Warning = "No Proxmox storage has been selected for backup.";
                return View(new List<StorageWithVMsDto>());
            }

            var selectedStorageSet = new HashSet<string>(selectedStorageNames, StringComparer.OrdinalIgnoreCase);

            // 4) Cluster/host status (skip entirely if all offline)
            var (_, _, _, hostStates, summary) = await _proxmoxService.GetClusterStatusAsync(cluster, ct);
            var onlineHostNames = hostStates.Where(kv => kv.Value).Select(kv => kv.Key)
                                           .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (onlineHostNames.Count == 0)
            {
                ViewBag.Warning = "No Proxmox hosts are currently online: " + summary;
                return View(new List<StorageWithVMsDto>());
            }

            // Local list of *online* hosts (don’t mutate EF entity)
            var onlineHosts = cluster.Hosts
                .Where(h => onlineHostNames.Contains(h.Hostname ?? h.HostAddress))
                .ToList();

            if (onlineHosts.Count == 0)
            {
                ViewBag.Warning = "No online hosts matched the configured cluster hosts.";
                return View(new List<StorageWithVMsDto>());
            }

            // 5) Kick off VM inventory (uses cache internally)
            Dictionary<string, List<ProxmoxVM>> storageVmMap;
            try
            {
                storageVmMap = await _invCache.GetVmsByStorageListAsync(cluster, selectedStorageNames, ct);
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

            // 6) Fetch SnapMirror relations + SelectedNetappVolumes limited to relevant storages
            var relationsTask = _context.SnapMirrorRelations
                .AsNoTracking()
                .Where(r => selectedStorageSet.Contains(r.SourceVolume))
                .Select(r => new { r.SourceControllerId, r.SourceVolume, r.DestinationControllerId, r.DestinationVolume })
                .ToListAsync(ct);

            var selectedVolumesTask = _context.SelectedNetappVolumes
                .AsNoTracking()
                .Where(v => selectedStorageSet.Contains(v.VolumeName) && v.Disabled != true) // << skip disabled, include null
                .Select(v => new
                {
                    v.Id,
                    v.NetappControllerId,
                    v.VolumeName,
                    v.SnapshotLockingEnabled
                })
                .ToListAsync(ct);

            await Task.WhenAll(relationsTask, selectedVolumesTask);

            var relations = relationsTask.Result;
            var selectedVolumes = selectedVolumesTask.Result;

            // Quick lookup: (controllerId, volumeName) -> SelectedNetappVolume.Id
            // If multiple rows exist for the same key, we keep the first encountered (stable enough for UI purposes).
            var selectedVolumeIdByKey = new Dictionary<(int ControllerId, string VolumeName), int>(EqualityComparer<(int, string)>.Default);
            var lockByKey = new Dictionary<(int ControllerId, string VolumeName), bool>(EqualityComparer<(int, string)>.Default);
            foreach (var v in selectedVolumes)
            {
                var key = (v.NetappControllerId, v.VolumeName);
                if (!selectedVolumeIdByKey.ContainsKey(key))
                    selectedVolumeIdByKey[key] = v.Id;

                // Coalesce nullable -> bool
                if (v.SnapshotLockingEnabled == true)
                    lockByKey[key] = true;              // any true sets the key to true
                else if (!lockByKey.ContainsKey(key))
                    lockByKey[key] = false;             // initialize to false if not present
            }

            // 7) Build DTOs (skip empty storages early)
            var model = new List<StorageWithVMsDto>(capacity: storageVmMap.Count);
            foreach (var kv in storageVmMap)
            {
                var storage = kv.Key;
                var vms = kv.Value;
                if (vms == null || vms.Count == 0) continue;

                // Map the SelectedNetappVolumeId for the *primary* controller
                selectedVolumeIdByKey.TryGetValue((netappController.Id, storage), out var maybeSelectedId);
                var hasLocking = lockByKey.TryGetValue((netappController.Id, storage), out var enabled) && enabled;

                model.Add(new StorageWithVMsDto
                {
                    StorageName = storage,
                    VMs = vms.Select(vm => new ProxmoxVM
                    {
                        Id = vm.Id,
                        Name = vm.Name,
                        HostName = vm.HostName,
                        HostAddress = vm.HostAddress
                    }).ToList(),
                    ClusterId = cluster.Id,
                    NetappControllerId = netappController.Id,
                    SelectedNetappVolumeId = maybeSelectedId == 0 ? (int?)null : maybeSelectedId, // ✅ expose to the view
                    SnapshotLockingEnabled = hasLocking
                });
            }

            if (model.Count == 0)
            {
                ViewBag.Warning = "No VMs found on the selected storage.";
                return View(model);
            }

            // 8) Compute replicability: show checkbox if there is a SnapMirror relation for this storage.
            //    (Do NOT require destination volume to be selected/enabled in settings.)
            var replicableSources = new HashSet<string>(
                relations.Select(r => r.SourceVolume),
                StringComparer.OrdinalIgnoreCase
            );

            foreach (var dto in model)
            {
                dto.IsReplicable = replicableSources.Contains(dto.StorageName);
                // SnapshotLockingEnabled already set above from lockByKey
            }

            return View(model);
        }
    }
}
