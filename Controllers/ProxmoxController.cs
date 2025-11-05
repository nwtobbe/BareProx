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

        // Optional clusterId avoids accidentally binding to "first" cluster.
        public async Task<IActionResult> ListVMs(int? clusterId, CancellationToken ct)
        {
            // 1) Load target cluster (no tracking)
            var clusterQuery = _context.ProxmoxClusters
                .AsNoTracking()
                .Include(c => c.Hosts)
                .AsQueryable();

            var cluster = clusterId is int requestedClusterId
                ? await clusterQuery.FirstOrDefaultAsync(c => c.Id == requestedClusterId, ct)
                : await clusterQuery.FirstOrDefaultAsync(ct);

            if (cluster == null)
            {
                ViewBag.Warning = "No Proxmox clusters are configured.";
                return View(new List<StorageWithVMsDto>());
            }

            // 2) Selected storages for THIS cluster (authoritative binding)
            var selectedStorageNames = await _context.SelectedStorages
                .AsNoTracking()
                .Where(s => s.ClusterId == cluster.Id)
                .Select(s => s.StorageIdentifier)
                .ToListAsync(ct);

            if (selectedStorageNames.Count == 0)
            {
                ViewBag.Warning = "No Proxmox storage has been selected for backup for this cluster.";
                return View(new List<StorageWithVMsDto>());
            }

            var selectedStorageSet = new HashSet<string>(selectedStorageNames, StringComparer.OrdinalIgnoreCase);

            // 3) Cluster/host status (skip entirely if all offline)
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

            // 4) Kick off VM inventory (uses cache internally)
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

            // 5) Load SelectedNetappVolumes for those storages
            // DO NOT filter by SelectedNetappVolumes.ClusterId (by design).
            var selectedVolumes = await _context.SelectedNetappVolumes
                .AsNoTracking()
                .Where(v => selectedStorageSet.Contains(v.VolumeName) && v.Disabled != true) // skip disabled, include null
                .Select(v => new
                {
                    v.Id,
                    v.NetappControllerId,
                    v.VolumeName,
                    v.SnapshotLockingEnabled
                })
                .ToListAsync(ct);

            // Index: (controllerId, volumeName) -> SelectedNetappVolume.Id / Locking
            var selectedVolumeIdByKey = new Dictionary<(int ControllerId, string Volume), int>();
            var lockByKey = new Dictionary<(int ControllerId, string Volume), bool>();
            foreach (var v in selectedVolumes)
            {
                var key = (v.NetappControllerId, v.VolumeName);
                if (!selectedVolumeIdByKey.ContainsKey(key))
                    selectedVolumeIdByKey[key] = v.Id;

                // Any 'true' across duplicates yields true
                lockByKey[key] = lockByKey.TryGetValue(key, out var prev)
                    ? (prev || v.SnapshotLockingEnabled == true)
                    : (v.SnapshotLockingEnabled == true);
            }

            // Map: storage -> candidate controllers that actually have a mapping entry
            var controllerIdsByStorage = selectedVolumes
                .GroupBy(v => v.VolumeName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => x.NetappControllerId).Distinct().ToList(),
                    StringComparer.OrdinalIgnoreCase
                );

            // Optional: choose a "primary" only among candidates to act as tie-breaker
            var allCandidateControllerIds = controllerIdsByStorage.Values.SelectMany(x => x).Distinct().ToHashSet();
            var fallbackPrimary = await _context.NetappControllers
                .AsNoTracking()
                .Where(c => c.IsPrimary && allCandidateControllerIds.Contains(c.Id))
                .Select(c => new { c.Id })
                .FirstOrDefaultAsync(ct);

            // 6) Build DTOs (skip empty storages early) with a controller per storage
            var model = new List<StorageWithVMsDto>(capacity: storageVmMap.Count);
            foreach (var kv in storageVmMap)
            {
                var storage = kv.Key;
                var vms = kv.Value;
                if (vms == null || vms.Count == 0) continue;

                // Choose controller for this storage (prefer a primary if it’s a candidate)
                int? controllerForStorage = null;
                if (controllerIdsByStorage.TryGetValue(storage, out var candidates) && candidates.Count > 0)
                {
                    controllerForStorage = (fallbackPrimary != null && candidates.Contains(fallbackPrimary.Id))
                        ? fallbackPrimary.Id
                        : candidates[0]; // deterministic, first candidate
                }

                int? selectedId = null;
                bool hasLocking = false;
                if (controllerForStorage is int controllerId)
                {
                    if (selectedVolumeIdByKey.TryGetValue((controllerId, storage), out var id))
                        selectedId = id;
                    hasLocking = lockByKey.TryGetValue((controllerId, storage), out var lk) && lk;
                }

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
                    ClusterId = cluster.Id, // authoritative binding
                    NetappControllerId = controllerForStorage ?? fallbackPrimary?.Id ?? 0,
                    SelectedNetappVolumeId = selectedId,
                    SnapshotLockingEnabled = hasLocking
                });
            }

            if (model.Count == 0)
            {
                ViewBag.Warning = "No VMs found on the selected storage.";
                return View(model);
            }

            // 7) Compute replicability: only consider relations for controllers actually used
            var usedControllerIds = model
                .Select(m => m.NetappControllerId)
                .Where(id => id != 0)
                .Distinct()
                .ToList();

            var relations = await _context.SnapMirrorRelations
                .AsNoTracking()
                .Where(r => selectedStorageSet.Contains(r.SourceVolume)
                         && usedControllerIds.Contains(r.SourceControllerId))
                .Select(r => new { r.SourceControllerId, r.SourceVolume, r.DestinationControllerId, r.DestinationVolume })
                .ToListAsync(ct);

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
