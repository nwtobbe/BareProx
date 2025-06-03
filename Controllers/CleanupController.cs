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
            var pageVm = new CleanupPageViewModel();

            // 1) Load all Proxmox clusters including their Hosts
            var clusters = await _context.ProxmoxClusters
                                         .Include(c => c.Hosts)
                                         .ToListAsync();

            if (clusters.Count == 0)
            {
                pageVm.WarningMessage = "No Proxmox clusters are configured.";
                return View(pageVm);
            }

            // 2) Find the single primary NetApp controller
            var primaryController = await _context.NetappControllers
                                                  .FirstOrDefaultAsync(c => c.IsPrimary);
            if (primaryController == null)
            {
                pageVm.WarningMessage = "No primary NetApp controller is configured.";
                return View(pageVm);
            }

            int controllerId = primaryController.Id;

            // 3) Fetch the full list of all FlexClones on that controller once:
            var allClones = await _netappService.ListFlexClonesAsync(controllerId);

            // 4) Build a HashSet of all backup‐recorded snapshots from the DB:
            //    We assume there is a DbSet<BackupRecord> with (VolumeName, SnapshotName) columns.
            //    Replace "BackupRecords" with whatever your actual EF Core DbSet is called.
            var backupRecords = await _context.Set<BackupRecord>()
                .AsNoTracking()
                .ToListAsync();

            var recordedSnapshotsByVolume = backupRecords
                .GroupBy(r => r.StorageName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(r => r.SnapshotName).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase
                );

            // 5) Now process each cluster separately
            foreach (var cluster in clusters)
            {
                var clusterVm = new CleanupClusterViewModel
                {
                    ClusterName = cluster.Name ?? $"Cluster {cluster.Id}"
                };

                // —————————————————————————————————————————————————————————————
                // PART A) In‐Use vs Orphaned Clones (unchanged)
                // —————————————————————————————————————————————————————————————

                var allItems = new List<CleanupItem>();

                foreach (var cloneName in allClones)
                {
                    // 5A.1) Determine mount‐IP for this clone
                    var mountInfo = (await _netappService
                                          .GetVolumesWithMountInfoAsync(controllerId))
                                    .FirstOrDefault(m => m.VolumeName == cloneName);
                    string? mountIp = mountInfo?.MountIp;

                    // 5A.2) Check if any VM on *this cluster* is using that clone
                    var attachedVMs = new List<ProxmoxVM>();
                    foreach (var host in cluster.Hosts)
                    {
                        var nodeName = host.Hostname ?? host.HostAddress!;
                        var vmsOnNode = await _proxmoxService
                            .GetVmsOnNodeAsync(cluster, nodeName, cloneName);

                        if (vmsOnNode?.Any() == true)
                            attachedVMs.AddRange(vmsOnNode);
                    }

                    allItems.Add(new CleanupItem
                    {
                        VolumeName = cloneName,
                        MountIp = mountIp,
                        IsInUse = attachedVMs.Any(),
                        AttachedVms = attachedVMs
                    });
                }

                clusterVm.InUse = allItems.Where(x => x.IsInUse).ToList();
                clusterVm.Orphaned = allItems.Where(x => !x.IsInUse).ToList();

                // —————————————————————————————————————————————————————————————
                // PART B) “Orphaned Snapshots” per Primary Volume
                // —————————————————————————————————————————————————————————————

                // 5B.1) Determine the list of “primary volumes” we care about.
                //       In this example, we treat each distinct VolumeName from BackupRecords
                //       as a primary volume. (Any other method you use to enumerate primary volumes
                //       would simply replace this block.)
                var primaryVolumes = recordedSnapshotsByVolume.Keys.ToList();

                // 5B.2) Build one PrimaryVolumeSnapshots object per volume
                foreach (var vol in primaryVolumes)
                {
                    var pvs = new PrimaryVolumeSnapshots
                    {
                        VolumeName = vol
                    };

                    // 5B.3) Fetch all snapshot names on storage for this volume
                    var allSnapshotNamesOnDisk = await _netappService.GetSnapshotsAsync(controllerId, vol)
                        ?? new List<string>();

                    // 5B.4) Which snapshots are already in BackupRecords?
                    recordedSnapshotsByVolume.TryGetValue(vol, out var recordedForThisVol);
                    recordedForThisVol ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    // 5B.5) Orphaned snapshots = (diskSnapshots − recordedSnapshots)
                    var orphanedSnapshotNames = allSnapshotNamesOnDisk
                        .Where(s =>
                            !recordedForThisVol.Contains(s) &&
                            !s.StartsWith("snapmirror", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    // 5B.6) For each orphaned snapshot, check if there’s a FlexClone built from it,
                    //        and if so, whether that clone is in‐use or mounted on *this cluster*.
                    var orphanedInfos = new List<SnapshotInfo>();
                    foreach (var snap in orphanedSnapshotNames)
                    {
                        var info = new SnapshotInfo
                        {
                            SnapshotName = snap
                        };

                        // 5B.6a) Is there a clone whose name suggests it was made from this snapshot?
                        //         We assume your clones follow some naming convention like:
                        //         "<volume>_snapshot_<snapshot>_clone_<randomSuffix>"
                        //         Adjust this filter to match your actual naming pattern.
                        var cloneForThisSnapshot = allClones
                            .FirstOrDefault(c => c.Contains(snap, StringComparison.OrdinalIgnoreCase)
                                              && c.StartsWith(vol + "_", StringComparison.OrdinalIgnoreCase));
                        if (cloneForThisSnapshot != null)
                        {
                            info.CloneName = cloneForThisSnapshot;

                            // 5B.6b) If that clone exists, is it mounted on this cluster?
                            //          Fetch mount info once again, only for this one clone.
                            var singleMount = (await _netappService
                                                    .GetVolumesWithMountInfoAsync(controllerId))
                                              .FirstOrDefault(m => m.VolumeName == cloneForThisSnapshot);
                            info.CloneMountIp = singleMount?.MountIp;

                            // 5B.6c) If it’s mounted, check if any VM on *this cluster* is using it:
                            if (info.CloneName is not null)
                            {
                                var vmsOnClone = new List<ProxmoxVM>();
                                foreach (var host in cluster.Hosts)
                                {
                                    var nodeName = host.Hostname ?? host.HostAddress!;
                                    var vms = await _proxmoxService
                                        .GetVmsOnNodeAsync(cluster, nodeName, cloneForThisSnapshot);
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

                pageVm.Clusters.Add(clusterVm);
            }

            return View(pageVm);
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Cleanup(string volumeName, string mountIp)
        {
            if (string.IsNullOrWhiteSpace(volumeName) || string.IsNullOrWhiteSpace(mountIp))
                return BadRequest(new { error = "Missing volume or mount IP." });

            // Reload cluster + hosts
            var cluster = await _context.ProxmoxClusters
                                        .Include(c => c.Hosts)
                                        .FirstOrDefaultAsync();
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
                        cluster, host.Hostname!, volumeName);
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
            var deleted = await _netappService.DeleteVolumeAsync(
                              volumeName,
                              controllerId: _context.NetappControllers
                                                 .Select(c => c.Id)
                                                 .First());

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

        // — New: DELETE a single snapshot
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CleanupSnapshot(string volumeName, string snapshotName)
        {
            if (string.IsNullOrWhiteSpace(volumeName) || string.IsNullOrWhiteSpace(snapshotName))
                return BadRequest(new { error = "Missing volume or snapshot name." });

            var controllerId = await _context.NetappControllers
                                             .Select(c => c.Id)
                                             .FirstOrDefaultAsync();
            if (controllerId == 0)
                return NotFound(new { error = "No NetApp controller configured." });

            var result = await _netappService.DeleteSnapshotAsync(
                             controllerId,
                             volumeName,
                             snapshotName);

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
