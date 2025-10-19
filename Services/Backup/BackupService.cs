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

namespace BareProx.Services.Backup
{

    public class BackupService : IBackupService
    {
        private readonly ApplicationDbContext _context;
        private readonly ProxmoxService _proxmoxService;
        private readonly INetappSnapmirrorService _netAppSnapmirrorService;
        private readonly INetappSnapshotService _netAppSnapshotService;
        private readonly IBackupRepository _backupRepository;
        private readonly ILogger<BackupService> _logger;
        private readonly TimeZoneInfo _timeZoneInfo;
        private readonly string _timeZoneId;
        private readonly IAppTimeZoneService _tz;
        private readonly IProxmoxInventoryCache _invCache;

        public BackupService(
            ApplicationDbContext context,
            ProxmoxService proxmoxService,
            INetappSnapmirrorService netAppSnapmirrorService,
            INetappSnapshotService netAppSnapshotService,
            IBackupRepository backupRepository,
            ILogger<BackupService> logger,
            IConfiguration configuration,
            IAppTimeZoneService tz,
            IProxmoxInventoryCache invCache)
        {
            _context = context;
            _proxmoxService = proxmoxService;
            _netAppSnapmirrorService = netAppSnapmirrorService;
            _netAppSnapshotService = netAppSnapshotService;
            _backupRepository = backupRepository;
            _logger = logger;
            _timeZoneId = configuration["AppSettings:TimeZone"] ?? "UTC";
            _timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(_timeZoneId);
            _tz = tz;
            _invCache = invCache;

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
     IEnumerable<string>? excludedVmIds,
     CancellationToken ct)
        {
            bool ProxSnapCleanup = false;

            // Build excluded set (int VMIDs)
            var excludedSet = new HashSet<int>();
            if (excludedVmIds != null)
            {
                foreach (var s in excludedVmIds)
                {
                    if (int.TryParse(s, out var id)) excludedSet.Add(id);
                }
            }

            // 1) Create and save a Job
            var job = new Job
            {
                Type = "Backup",
                Status = "Running",
                RelatedVm = storageName,
                PayloadJson = $"{{\"storageName\":\"{storageName}\",\"label\":\"{label}\",\"excludedCount\":{excludedSet.Count}}}",
                StartedAt = DateTime.UtcNow
            };
            _context.Jobs.Add(job);
            await _context.SaveChangesAsync(ct);

            // 2) Discover cluster & VMs
            var cluster = await _context.ProxmoxClusters
                                        .Include(c => c.Hosts)
                                        .FirstOrDefaultAsync(c => c.Id == clusterId, ct);
            if (cluster == null)
                return await FailJobAsync(job, $"Cluster with ID {clusterId} not found.", ct);

            List<ProxmoxVM>? vms = null;
            var proxmoxSnapshotNames = new Dictionary<int, string>();
            bool vmsWerePaused = false;
            bool anyPaused = false;
            bool snapChainActive = false;

            try
            {
                snapChainActive = await _proxmoxService.IsSnapshotChainActiveFromDefAsync(cluster, storageName, ct);
                _logger.LogInformation("Snapshot-as-volume-chain on '{Storage}': {Active}", storageName, snapChainActive);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not determine snapshot-as-volume-chain state for storage '{Storage}'. Defaulting to false.", storageName);
            }

            try
            {
                var storageWithVms = await _invCache
                    .GetEligibleBackupStorageWithVMsAsync(cluster, netappControllerId, null, ct);

                if (!storageWithVms.TryGetValue(storageName, out vms) || vms == null || !vms.Any())
                    return await FailJobAsync(job, $"No VMs found in storage '{storageName}'.", ct);

                if (cluster.Hosts == null || !cluster.Hosts.Any())
                    return await FailJobAsync(job, "Cluster not properly configured.", ct);

                // Fetch power status for all VMs once
                var statusMap = new Dictionary<int, string>(capacity: vms.Count);
                {
                    var tasks = vms.Select(async vm =>
                    {
                        var st = await _proxmoxService.GetVmStatusAsync(cluster, vm.HostName, vm.HostAddress, vm.Id, ct);
                        lock (statusMap) statusMap[vm.Id] = st ?? "";
                    });
                    await Task.WhenAll(tasks);
                }

                // 3) Pause VMs (only non-excluded, non-stopped) if IO-freeze requested
                if (isApplicationAware && enableIoFreeze)
                {
                    foreach (var vm in vms)
                    {
                        if (excludedSet.Contains(vm.Id)) continue; // ← skip excluded
                        var isStopped = statusMap.TryGetValue(vm.Id, out var st) &&
                                        string.Equals(st, "stopped", StringComparison.OrdinalIgnoreCase);
                        if (!isStopped)
                        {
                            await _proxmoxService.PauseVmAsync(cluster, vm.HostName, vm.HostAddress, vm.Id);
                            anyPaused = true;
                        }
                    }
                    vmsWerePaused = anyPaused;
                    if (anyPaused)
                    {
                        job.Status = "Paused VMs";
                        await _context.SaveChangesAsync(ct);
                    }
                }

                // 4) Proxmox snapshots (if requested) for non-excluded, non-stopped
                var localTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _timeZoneInfo);
                var ProxMoxsnapshotName = $"BareProx-{label}_{localTime:yyyy-MM-dd-HH-mm-ss}";
                var snapshotTasks = new Dictionary<int, string>();

                if (isApplicationAware && useProxmoxSnapshot)
                {
                    job.Status = "Creating Proxmox snapshots";
                    await _context.SaveChangesAsync(ct);

                    foreach (var vm in vms)
                    {
                        if (excludedSet.Contains(vm.Id)) continue; // ← skip excluded

                        var status = statusMap.TryGetValue(vm.Id, out var s) ? s : "";
                        var isStopped = string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase);
                        if (isStopped)
                        {
                            _logger.LogInformation("Skipping snapshot for stopped VM {VmId}", vm.Id);
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

                    // Wait for only the tasks we created (non-excluded, non-stopped)
                    var waitTasks = snapshotTasks.Select(kv =>
                    {
                        var vm = vms.First(v => v.Id == kv.Key);
                        return new
                        {
                            Vm = vm,
                            Task = _proxmoxService.WaitForTaskCompletionAsync(
                                cluster, vm.HostName, vm.HostAddress, kv.Value,
                                TimeSpan.FromMinutes(20),
                                _logger, ct)
                        };
                    }).ToList();

                    await Task.WhenAll(waitTasks.Select(x => x.Task));

                    foreach (var entry in waitTasks)
                    {
                        var success = await entry.Task;
                        if (!success)
                        {
                            _logger.LogWarning("Snapshot task for VM {VmId} timed out in job {JobId}", entry.Vm.Id, job.Id);
                        }
                    }

                    job.Status = "Proxmox snapshots completed";
                    await _context.SaveChangesAsync(ct);
                }

                // 5a) Create the NetApp snapshot (storage-wide)
                var snapshotResult = await _netAppSnapshotService.CreateSnapshotAsync(
                    netappControllerId,
                    storageName,
                    label,
                    snapLocking: enableLocking,
                    lockRetentionCount: enableLocking ? lockRetentionCount : null,
                    lockRetentionUnit: enableLocking ? lockRetentionUnit : null,
                    ct: ct);

                if (!snapshotResult.Success)
                    return await FailJobAsync(job, snapshotResult.ErrorMessage, ct);

                job.Status = "Snapshot created";
                await _context.SaveChangesAsync(ct);

                // 5b) Persist BackupRecord(s)
                foreach (var vm in vms)
                {
                    var cfg = await _proxmoxService.GetVmConfigAsync(cluster, vm.HostName, vm.Id, ct);

                    var isStopped = statusMap.TryGetValue(vm.Id, out var st) &&
                                    string.Equals(st, "stopped", StringComparison.OrdinalIgnoreCase);
                    var isExcluded = excludedSet.Contains(vm.Id);

                    // Per-VM flags:
                    // If excluded OR stopped => all false (as requested)
                    var perVmIsAppAware = !isExcluded && !isStopped && isApplicationAware;
                    var perVmFreeze = !isExcluded && !isStopped && enableIoFreeze;
                    var perVmUseProxSnap = !isExcluded && !isStopped && useProxmoxSnapshot;
                    var perVmWithMemory = !isExcluded && !isStopped && withMemory;

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
                        ConfigurationJson = cfg,

                        IsApplicationAware = perVmIsAppAware,
                        EnableIoFreeze = perVmFreeze,
                        UseProxmoxSnapshot = perVmUseProxSnap,
                        WithMemory = perVmWithMemory,

                        ScheduleId = scheduleId,
                        ReplicateToSecondary = replicateToSecondary,
                        SnapshotAsvolumeChain = snapChainActive
                    });
                }

                // Track snapshot in DB (include JobId)
                _context.NetappSnapshots.Add(new NetappSnapshot
                {
                    JobId = job.Id,
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

                // 5c) Delete Proxmox snapshots if used (only those created)
                if (useProxmoxSnapshot)
                {
                    await CleanupProxmoxSnapshots(
                        cluster,
                        vms,
                        proxmoxSnapshotNames, // only contains created snapshots (non-excluded/running)
                        shouldCleanup: true,
                        ct: ct
                    );
                    ProxSnapCleanup = true;
                    _logger.LogInformation("Proxmox snapshots deleted after storage snapshot.");
                }

                // 6) Replicate to secondary, if enabled
                if (replicateToSecondary)
                {
                    var relation = await _context.SnapMirrorRelations
                        .FirstOrDefaultAsync(r => r.SourceVolume == storageName, ct);
                    if (relation == null)
                        return await FailJobAsync(job, $"No SnapMirror relation for '{storageName}'.", ct);

                    job.Status = "Triggering SnapMirror update";
                    await _context.SaveChangesAsync(ct);

                    var triggered = await _netAppSnapmirrorService.TriggerSnapMirrorUpdateAsync(relation.Uuid, ct);
                    if (!triggered)
                        return await FailJobAsync(job, "Failed to trigger SnapMirror update.", ct);

                    var primaryVolume = storageName;
                    var secondaryVolume = relation.DestinationVolume;
                    var secondarySnap = snapshotResult.SnapshotName;
                    var secondaryControllerId = relation.DestinationControllerId;

                    job.Status = "Waiting for SnapMirror to catch up";
                    await _context.SaveChangesAsync(ct);

                    var sw = Stopwatch.StartNew();
                    while (sw.Elapsed < TimeSpan.FromMinutes(120))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), ct);

                        var updated = await _netAppSnapmirrorService.GetSnapMirrorRelationAsync(relation.Uuid, ct);

                        if (updated.state.Equals("snapmirrored", StringComparison.OrdinalIgnoreCase)
                            && updated.transfer?.State.Equals("success", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            var snaps = await _netAppSnapshotService.GetSnapshotsAsync(
                                secondaryControllerId,
                                secondaryVolume,
                                ct);

                            if (snaps.Any(s =>
                                string.Equals(s, secondarySnap, StringComparison.OrdinalIgnoreCase)))
                            {
                                break;
                            }
                        }
                    }

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

                    var toUpdate = await _context.BackupRecords
                        .Where(br => br.JobId == job.Id)
                        .ToListAsync(ct);
                    foreach (var rec in toUpdate) rec.ReplicateToSecondary = true;

                    job.Status = "Replication completed";
                    await _context.SaveChangesAsync(ct);
                }

                // 7) Check for cancellation
                await _context.Entry(job).ReloadAsync(ct);
                if (job.Status == "Cancelled")
                    return await FailJobAsync(job, "Job was cancelled.", ct);

                // 8) Finish job
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

    }
}
