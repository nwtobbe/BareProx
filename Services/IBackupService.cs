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
using BareProx.Repositories;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;

namespace BareProx.Services
{
    public interface IBackupService
    {
        Task<bool> StartBackupAsync(
            string storageName,
            bool isApplicationAware,
            string label,
            int clusterId,
            int netappControllerId,
            int retentionCount,
            string retentionUnit,
            bool enableIoFreeze,
            bool useProxmoxSnapshot,
            bool withMemory,
            bool dontTrySuspend,
            int ScheduleID,
            bool ReplicateToSecondary,

        // ← NEW: locking parameters
        bool enableLocking,
        int? lockRetentionCount,
        string? lockRetentionUnit,
            CancellationToken ct);
    }

    public class BackupService : IBackupService
    {
        private readonly ApplicationDbContext _context;
        private readonly ProxmoxService _proxmoxService;
        private readonly INetappService _netAppService;
        private readonly INetappSnapmirrorService _netAppSnapmirrorService;
        private readonly INetappSnapshotService _netAppSnapshotService;
        private readonly IBackupRepository _backupRepository;
        private readonly ILogger<BackupService> _logger;
        private readonly TimeZoneInfo _timeZoneInfo;
        private readonly string _timeZoneId;
        private readonly IAppTimeZoneService _tz;

        public BackupService(
            ApplicationDbContext context,
            ProxmoxService proxmoxService,
            INetappService netAppService,
            INetappSnapmirrorService netAppSnapmirrorService,
            INetappSnapshotService netAppSnapshotService,
            IBackupRepository backupRepository,
            ILogger<BackupService> logger,
            IConfiguration configuration,
            IAppTimeZoneService tz)
        {
            _context = context;
            _proxmoxService = proxmoxService;
            _netAppService = netAppService;
            _netAppSnapmirrorService = netAppSnapmirrorService;
            _netAppSnapshotService = netAppSnapshotService;
            _backupRepository = backupRepository;
            _logger = logger;
            _timeZoneId = configuration["AppSettings:TimeZone"] ?? "UTC";
            _timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(_timeZoneId);
            _tz = tz;

        }

        public async Task<bool> StartBackupAsync(
        string storageName,
        bool isApplicationAware,
        string label,
        int clusterId,
        int netappControllerId,
        int retentionCount,
        string retentionUnit,
        bool enableIoFreeze,
        bool useProxmoxSnapshot,
        bool withMemory,
        bool dontTrySuspend,
        int scheduleId,
        bool replicateToSecondary,
            bool enableLocking,
            int? lockRetentionCount,
            string? lockRetentionUnit,
        CancellationToken ct)

        {
            bool ProxSnapCleanup = false; // A checker to use if cleanup has run.

            // 1) Create and save a Job
            var job = new Job
            {
                Type = "Backup",
                Status = "Running",
                RelatedVm = storageName,
                PayloadJson = $"{{\"storageName\":\"{storageName}\",\"label\":\"{label}\"}}",
                StartedAt = DateTime.UtcNow
            };
            _context.Jobs.Add(job);
            await _context.SaveChangesAsync(ct);

            // 2) Discover cluster & VMs
            var cluster = await _context.ProxmoxClusters
                                        .Include(c => c.Hosts)      // so you have host info
                                        .FirstOrDefaultAsync(c => c.Id == clusterId, ct);
            if (cluster == null)
                return await FailJobAsync(job, $"Cluster with ID {clusterId} not found.");

            List<ProxmoxVM>? vms = null;
            bool vmsWerePaused = false;
            var proxmoxSnapshotNames = new Dictionary<int, string>();

            try
            {
                var storageWithVms = await _proxmoxService
                    .GetEligibleBackupStorageWithVMsAsync(cluster, netappControllerId, null, ct);

                if (!storageWithVms.TryGetValue(storageName, out vms) || vms == null || !vms.Any())
                    return await FailJobAsync(job, $"No VMs found in storage '{storageName}'.", ct);

                if (cluster.Hosts == null || !cluster.Hosts.Any())
                    return await FailJobAsync(job, "Cluster not properly configured.", ct);

                // 3) Pause VMs if IO-freeze requested
                if (isApplicationAware && enableIoFreeze)
                {
                    foreach (var vm in vms)
                        await _proxmoxService.PauseVmAsync(cluster, vm.HostName, vm.HostAddress, vm.Id);

                    vmsWerePaused = true;
                    job.Status = "Paused VMs";
                    await _context.SaveChangesAsync(ct);
                }

                // 4) Proxmox snapshots (if requested)
                var localTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _timeZoneInfo);
                var ProxMoxsnapshotName = $"{label}_{localTime:yyyy-MM-dd-HH-mm-ss}";
                var snapshotTasks = new Dictionary<int, string>();

                if (isApplicationAware && useProxmoxSnapshot)
                {
                    job.Status = "Creating Proxmox snapshots";
                    await _context.SaveChangesAsync(ct);

                    foreach (var vm in vms)
                    {
                        var status = await _proxmoxService.GetVmStatusAsync(
                            cluster, vm.HostName, vm.HostAddress, vm.Id, ct);

                        if (status == "stopped")
                        {
                            _logger.LogWarning("Skipping snapshot for stopped VM {VmId}", vm.Id);
                            continue;
                        }

                        var upid = await _proxmoxService.CreateSnapshotAsync(
                            cluster, vm.HostName, vm.HostAddress, vm.Id,
                            ProxMoxsnapshotName,
                            "Backup created via BareProx",
                            withMemory,
                            dontTrySuspend, ct
                        );

                        if (!string.IsNullOrWhiteSpace(upid))
                        {
                            snapshotTasks[vm.Id] = upid;
                            proxmoxSnapshotNames[vm.Id] = ProxMoxsnapshotName;
                        }
                        else
                        {
                            _logger.LogWarning("Snapshot creation failed for VM {VmId}", vm.Id);
                        }
                    }

                    job.Status = "Waiting for Proxmox snapshots";
                    await _context.SaveChangesAsync(ct);

                    // Build a list of tasks with their associated VM IDs
                    var waitTasks = snapshotTasks.Select(kv =>
                    {
                        var vm = vms.First(v => v.Id == kv.Key);
                        // Start the wait task and package the result with the VM info
                        return new
                        {
                            Vm = vm,
                            Task = _proxmoxService.WaitForTaskCompletionAsync(
                                cluster, vm.HostName, vm.HostAddress, kv.Value,
                                TimeSpan.FromMinutes(20),
                                _logger, ct)
                        };
                    }).ToList();

                    // Await all tasks in parallel
                    await Task.WhenAll(waitTasks.Select(x => x.Task));

                    // Check and log failures
                    foreach (var entry in waitTasks)
                    {
                        var success = await entry.Task; // Already completed, this is a synchronous read at this point
                        if (!success)
                        {
                            _logger.LogWarning(
                                "Snapshot task for VM {VmId} timed out in job {JobId}", entry.Vm.Id, job.Id);
                        }
                    }

                    // Update job status
                    job.Status = "Proxmox snapshots completed";
                    await _context.SaveChangesAsync(ct);
                }

                // 5a) Create the NetApp snapshot
                var snapshotResult = await _netAppSnapshotService.CreateSnapshotAsync(
                    netappControllerId,
                    storageName,
                    label,
                    snapLocking: enableLocking,
                    lockRetentionCount: enableLocking ? lockRetentionCount : (int?)null,
                    lockRetentionUnit: enableLocking ? lockRetentionUnit : null,
                    ct: ct);

                if (!snapshotResult.Success)
                    return await FailJobAsync(job, snapshotResult.ErrorMessage, ct);

                job.Status = "Snapshot created";
                await _context.SaveChangesAsync(ct);

                // 5b) Persist BackupRecord(s)
                foreach (var vm in vms)
                {
                    var config = await _proxmoxService.GetVmConfigAsync(cluster, vm.HostName, vm.Id, ct);

                    await _backupRepository.StoreBackupInfoAsync(new BackupRecord
                    {
                        JobId = job.Id,
                        VMID = vm.Id,
                        VmName = vm.Name,
                        HostName = vm.HostName,
                        StorageName = storageName,
                        Label = label,
                        RetentionCount = retentionCount,
                        RetentionUnit = retentionUnit,
                        TimeStamp = DateTime.UtcNow,
                        ControllerId = netappControllerId,
                        SnapshotName = snapshotResult.SnapshotName,
                        ConfigurationJson = config,
                        IsApplicationAware = isApplicationAware,
                        EnableIoFreeze = enableIoFreeze,
                        UseProxmoxSnapshot = useProxmoxSnapshot,
                        WithMemory = withMemory,
                        ScheduleId = scheduleId,
                        ReplicateToSecondary = replicateToSecondary
                    });
                }


                // ─────────────── populate JobId here ───────────────
                _context.NetappSnapshots.Add(new NetappSnapshot
                {
                    JobId = job.Id,               // ← set the JobId
                    PrimaryVolume = storageName,
                    SnapshotName = snapshotResult.SnapshotName,
                    CreatedAt = _tz.ConvertUtcToApp(DateTime.UtcNow),
                    PrimaryControllerId = netappControllerId,
                    SnapmirrorLabel = label,
                    ExistsOnPrimary = true,
                    ExistsOnSecondary = false,
                    LastChecked = _tz.ConvertUtcToApp(DateTime.UtcNow),
                    IsReplicated = false
                });
                await _context.SaveChangesAsync(ct);

                // 5b) Delete Proxmox Snapshots if used
                if (useProxmoxSnapshot)
                {
                    await CleanupProxmoxSnapshots(
                        cluster,
                        vms,
                        proxmoxSnapshotNames,
                        shouldCleanup: true,
                        ct: ct
                    );
                    ProxSnapCleanup = true; // Mark cleanup as done
                    _logger.LogInformation(
                        "Proxmox snapshots deleted after storage snapshot."
                    );
                }

                // 6) Conditionally replicate to secondary via SnapMirror
                if (replicateToSecondary)
                {
                    // 6a) Lookup the SnapMirror relation
                    var relation = await _context.SnapMirrorRelations
                        .FirstOrDefaultAsync(r => r.SourceVolume == storageName, ct);
                    if (relation == null)
                        return await FailJobAsync(job, $"No SnapMirror relation for '{storageName}'.", ct);

                    // 6b) Trigger an update on the relation
                    job.Status = "Triggering SnapMirror update";
                    await _context.SaveChangesAsync(ct);

                    var triggered = await _netAppSnapmirrorService.TriggerSnapMirrorUpdateAsync(relation.Uuid, ct);
                    if (!triggered)
                        return await FailJobAsync(job, "Failed to trigger SnapMirror update.", ct);

                    // a) Remember names & volumes for the check
                    var primaryVolume = storageName;
                    var secondaryVolume = relation.DestinationVolume;
                    var secondarySnap = snapshotResult.SnapshotName;
                    var secondaryControllerId = relation.DestinationControllerId;

                    job.Status = "Waiting for SnapMirror to catch up";
                    await _context.SaveChangesAsync(ct);

                    var sw = Stopwatch.StartNew();
                    SnapMirrorRelation updated;

                    // 6c) Poll until idle/transferred, then confirm snapshot on secondary
                    while (sw.Elapsed < TimeSpan.FromMinutes(120))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10));
                         updated = await _netAppSnapmirrorService.GetSnapMirrorRelationAsync(relation.Uuid, ct);

                        // 1) Did SnapMirror say “success”?
                        if (updated.state.Equals("snapmirrored", StringComparison.OrdinalIgnoreCase)
                            && updated.transfer?.State.Equals("success", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            // 2) Now confirm *your* snapshot
                            var snaps = await _netAppSnapshotService.GetSnapshotsAsync(
                                secondaryControllerId,
                                secondaryVolume,
                                ct);

                            if (snaps.Any(s =>
                                string.Equals(s, secondarySnap, StringComparison.OrdinalIgnoreCase)))
                            {
                                // Found the exact snapshot you created → done
                                break;
                            }
                        }
                    }
                    // 6d) Mark the snapshot in the DB as replicated
                    var nowApp = _tz.ConvertUtcToApp(DateTime.UtcNow);
                    var netappSnap = await _context.NetappSnapshots
                        .FirstOrDefaultAsync(s =>
                            s.SnapshotName == secondarySnap
                         && s.PrimaryVolume == storageName
                         && s.PrimaryControllerId == netappControllerId, ct);

                    if (netappSnap != null)
                    {
                        netappSnap.ExistsOnSecondary = true;
                        netappSnap.SecondaryVolume = secondaryVolume;
                        netappSnap.SecondaryControllerId = secondaryControllerId;
                        netappSnap.IsReplicated = true;
                        netappSnap.LastChecked = nowApp;

                        await _context.SaveChangesAsync(ct);
                    }

                    // 6e) Final verification & finish
                    var toUpdate = await _context.BackupRecords
                        .Where(br => br.JobId == job.Id)
                        .ToListAsync(ct);
                    foreach (var rec in toUpdate)
                    {
                        rec.ReplicateToSecondary = true;                                                                 
                    }

                    job.Status = "Replication completed";
                    await _context.SaveChangesAsync(ct);
                }

                // 7) Check for cancellation
                await _context.Entry(job).ReloadAsync(ct);
                if (job.Status == "Cancelled")
                    return await FailJobAsync(job, "Job was cancelled.", ct);



                // 9) Finish job
                job.Status = "Completed";
                job.CompletedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);
                return true;
            }
            catch (Exception ex)
            {
                return await FailJobAsync(job, ex.Message, ct);
            }
            finally
            {
                if (cluster != null && vms != null)
                {
                    await UnpauseIfNeeded(cluster, vms, isApplicationAware, enableIoFreeze, vmsWerePaused);
                    if (useProxmoxSnapshot && ProxSnapCleanup == false)
                    {
                        // Cleanup Proxmox snapshots if they were created
                        await CleanupProxmoxSnapshots(cluster, vms, proxmoxSnapshotNames, useProxmoxSnapshot);
                    }
                }
            }
        }



        private async Task UnpauseIfNeeded(ProxmoxCluster cluster, IEnumerable<ProxmoxVM> vms, bool isAppAware, bool ioFreeze, bool vmsWerePaused, CancellationToken ct = default)
        {
            if (isAppAware && ioFreeze && vmsWerePaused)
            {
                foreach (var vm in vms)
                {
                    try
                    {
                        await _proxmoxService.UnpauseVmAsync(cluster, vm.HostName, vm.HostAddress, vm.Id,ct);
                    }
                    catch { }
                }
            }
        }

        private async Task CleanupProxmoxSnapshots(
            ProxmoxCluster cluster,
            IEnumerable<ProxmoxVM> vms,
            Dictionary<int, string> snapshotMap,
            bool shouldCleanup,
            CancellationToken ct = default)
        {
            if (!shouldCleanup) return;

            foreach (var vm in vms)
            {
                if (snapshotMap.TryGetValue(vm.Id, out var snap))
                {
                    try
                    {
                        await _proxmoxService.DeleteSnapshotAsync(cluster, vm.HostName, vm.HostAddress, vm.Id, snap, ct);
                    }
                    catch { }
                }
            }
        }

        private async Task<bool> FailJobAsync(Job job, string message, CancellationToken ct = default)
        {
            job.Status = "Failed";
            job.ErrorMessage = message;
            job.CompletedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);
            return false;
        }
        private async Task<bool> CheckJobCancellationAsync(Job job, CancellationToken ct)
        {
            // Reload the latest row from the database
            await _context.Entry(job).ReloadAsync(ct);

            // If somebody set Status = "Cancelled", we abort
            if (job.Status == "Cancelled")
            {
                // Mark as failed (or whatever you prefer)
                job.Status = "Failed";
                job.ErrorMessage = "Job was cancelled.";
                job.CompletedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);
                return false;
            }

            return true;
        }
    }
}
