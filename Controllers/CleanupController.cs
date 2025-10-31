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
using BareProx.Services.Netapp;
using BareProx.Services.Proxmox;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Controllers
{
    public class CleanupController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly INetappFlexCloneService _netappFlexCloneService;
        private readonly ProxmoxService _proxmoxService;
        private readonly INetappVolumeService _netappVolumeService;
        private readonly INetappSnapshotService _netappSnapshotService;

        public CleanupController(
            ApplicationDbContext context,
            INetappFlexCloneService netappFlexCloneService,
            ProxmoxService proxmoxService,
            INetappVolumeService netappVolumeService,
            INetappSnapshotService netappSnapshotService)
        {
            _context = context;
            _netappFlexCloneService = netappFlexCloneService;
            _proxmoxService = proxmoxService;
            _netappVolumeService = netappVolumeService;
            _netappSnapshotService = netappSnapshotService;
        }

        public async Task<IActionResult> Index(CancellationToken ct)
        {
            var pageVm = new CleanupPageViewModel();

            // 1) Load Proxmox clusters including their hosts (no tracking)
            var clusters = await _context.ProxmoxClusters
                                         .Include(c => c.Hosts)
                                         .AsNoTracking()
                                         .ToListAsync(ct);
            if (clusters.Count == 0)
            {
                pageVm.WarningMessage = "No Proxmox clusters are configured.";
                return View(pageVm);
            }

            // 2) Load all NetApp controllers (primary & secondary) (no tracking)
            var netappControllers = await _context.NetappControllers
                                                  .AsNoTracking()
                                                  .ToListAsync(ct);
            if (netappControllers.Count == 0)
            {
                pageVm.WarningMessage = "No NetApp controllers are configured.";
                return View(pageVm);
            }

            // 3) Process each cluster separately
            foreach (var cluster in clusters)
            {
                var clusterVm = new CleanupClusterViewModel
                {
                    ClusterName = cluster.Name ?? $"Cluster {cluster.Id}"
                };

                var allItems = new List<CleanupItem>();

                // Process each NetApp controller separately
                foreach (var controller in netappControllers)
                {
                    var controllerId = controller.Id;

                    // FlexClones for this controller
                    var allClones = await _netappFlexCloneService.ListFlexClonesAsync(controllerId, ct);

                    // Selected volumes from database (no tracking)
                    var selectedVolumes = await _context.Set<SelectedNetappVolume>()
                                                        .Where(v => v.NetappControllerId == controllerId)
                                                        .AsNoTracking()
                                                        .ToListAsync(ct);

                    var selectedVolumeNames = selectedVolumes
                        .Select(v => v.VolumeName)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    // Mount info once per controller
                    var mountInfos = await _netappVolumeService.GetVolumesWithMountInfoAsync(controllerId, ct);

                    // PART A: FlexClones In-use vs Orphaned
                    foreach (var cloneName in allClones)
                    {
                        var mountInfo = mountInfos.FirstOrDefault(m => string.Equals(m.VolumeName, cloneName, StringComparison.OrdinalIgnoreCase));
                        var mountIp = mountInfo?.MountIp;

                        // Check attached VMs per host
                        var attachedVMs = new List<ProxmoxVM>();
                        foreach (var host in cluster.Hosts)
                        {
                            var nodeName = host.Hostname ?? host.HostAddress!;
                            var vmsOnNode = await _proxmoxService.GetVmsOnNodeAsync(cluster, nodeName, cloneName, ct);
                            if (vmsOnNode?.Any() == true)
                                attachedVMs.AddRange(vmsOnNode);
                        }

                        allItems.Add(new CleanupItem
                        {
                            VolumeName = cloneName,
                            MountIp = mountIp,
                            ControllerName = controller.Hostname,
                            ControllerId = controllerId,
                            ClusterId = cluster.Id,
                            IsInUse = attachedVMs.Any(),
                            AttachedVms = attachedVMs,
                            IsSelectedVolume = selectedVolumeNames.Contains(cloneName)
                        });
                    }

                    // PART B: Orphaned Snapshots per selected volume (only for primaries)
                    var primaryControllerIds = netappControllers
                        .Where(c => c.IsPrimary)
                        .Select(c => c.Id)
                        .ToHashSet();

                    foreach (var selectedVolume in selectedVolumes.Where(v => primaryControllerIds.Contains(v.NetappControllerId)))
                    {
                        var pvs = new PrimaryVolumeSnapshots
                        {
                            VolumeName = selectedVolume.VolumeName,
                            ControllerName = controller.Hostname,
                            ControllerId = controllerId
                        };

                        var snapshotsOnDisk = await _netappSnapshotService.GetSnapshotsAsync(controllerId, selectedVolume.VolumeName, ct);

                        // EF: fetch then build HashSet with comparer in-memory
                        var recordedSnapshotsList = await _context.Set<BackupRecord>()
                            .Where(r => r.StorageName == selectedVolume.VolumeName)
                            .Select(r => r.SnapshotName)
                            .ToListAsync(ct);

                        var recordedSnapshots = recordedSnapshotsList
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

                        var orphanedSnapshots = snapshotsOnDisk
                            .Where(s => !recordedSnapshots.Contains(s) &&
                                        !s.StartsWith("snapmirror", StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        var orphanedInfos = new List<SnapshotInfo>();
                        foreach (var snap in orphanedSnapshots)
                        {
                            var info = new SnapshotInfo { SnapshotName = snap };

                            var cloneForSnapshot = allClones.FirstOrDefault(c =>
                                c.Contains(snap, StringComparison.OrdinalIgnoreCase) &&
                                c.StartsWith(selectedVolume.VolumeName + "_", StringComparison.OrdinalIgnoreCase));

                            if (cloneForSnapshot != null)
                            {
                                info.CloneName = cloneForSnapshot;
                                var cloneMount = mountInfos.FirstOrDefault(m => string.Equals(m.VolumeName, cloneForSnapshot, StringComparison.OrdinalIgnoreCase));
                                info.CloneMountIp = cloneMount?.MountIp;

                                if (info.CloneName is not null)
                                {
                                    var vmsOnClone = new List<ProxmoxVM>();
                                    foreach (var host in cluster.Hosts)
                                    {
                                        var nodeName = host.Hostname ?? host.HostAddress!;
                                        var vms = await _proxmoxService.GetVmsOnNodeAsync(cluster, nodeName, cloneForSnapshot, ct);
                                        if (vms?.Any() == true)
                                            vmsOnClone.AddRange(vms);
                                    }
                                    info.CloneAttachedVms = vmsOnClone;
                                }
                            }
                            orphanedInfos.Add(info);
                        }

                        pvs.OrphanedSnapshots = orphanedInfos;
                        clusterVm.Volumes.Add(pvs);
                    }
                }

                clusterVm.InUse = allItems.Where(x => x.IsInUse).ToList();
                clusterVm.Orphaned = allItems.Where(x => !x.IsInUse).ToList();

                pageVm.Clusters.Add(clusterVm);
            }

            return View(pageVm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Cleanup(string volumeName, string mountIp, int controllerId,
            int clusterId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(volumeName) || string.IsNullOrWhiteSpace(mountIp))
                return BadRequest(new { error = "Missing volume or mount IP." });

            // Reload cluster + hosts
            var cluster = await _context.ProxmoxClusters
                                        .Include(c => c.Hosts)
                                        .AsNoTracking()
                                        .FirstOrDefaultAsync(c => c.Id == clusterId, ct);
            if (cluster == null)
                return NotFound(new { error = "Proxmox cluster not configured." });

            Exception? lastException = null;
            ProxmoxHost? successfulHost = null;

            // Try unmount on each host
            foreach (var host in cluster.Hosts)
            {
                try
                {
                    await _proxmoxService.UnmountNfsStorageViaApiAsync(
                        cluster, host.Hostname!, volumeName, ct);
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

            // Delete the NetApp flexclone
            var deleted = await _netappVolumeService.DeleteVolumeAsync(
                              volumeName,
                              controllerId,
                              ct);

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

        // — Delete a single snapshot
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CleanupSnapshot(string volumeName, string snapshotName, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(volumeName) || string.IsNullOrWhiteSpace(snapshotName))
                return BadRequest(new { error = "Missing volume or snapshot name." });

            // Prefer the controller that owns this volume (SelectedNetappVolumes), then any primary, then any controller.
            var controllerId = await _context.SelectedNetappVolumes
                                             .Where(v => v.VolumeName == volumeName)
                                             .Select(v => v.NetappControllerId)
                                             .FirstOrDefaultAsync(ct);

            if (controllerId == 0)
            {
                controllerId = await _context.NetappControllers
                                             .Where(c => c.IsPrimary)
                                             .Select(c => c.Id)
                                             .FirstOrDefaultAsync(ct);
            }
            if (controllerId == 0)
            {
                controllerId = await _context.NetappControllers
                                             .Select(c => c.Id)
                                             .FirstOrDefaultAsync(ct);
            }
            if (controllerId == 0)
                return NotFound(new { error = "No NetApp controller configured." });

            var result = await _netappSnapshotService.DeleteSnapshotAsync(
                             controllerId,
                             volumeName,
                             snapshotName,
                             ct);

            if (!result.Success)
            {
                return StatusCode(500, new
                {
                    error = $"Failed to delete snapshot {snapshotName} on volume {volumeName}: {result.ErrorMessage}"
                });
            }

            return Ok(new
            {
                message = $"Snapshot {snapshotName} on {volumeName} deleted."
            });
        }
    }
}
