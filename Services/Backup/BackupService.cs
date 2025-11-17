/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 *
 * Copyright (C) 2025 Tobias Modig
 *
 * This file is part of BareProx.
 *
 * BareProx is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * BareProx is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with BareProx. If not, see <https://www.gnu.org/licenses/>.
 */


using BareProx.Data;
using BareProx.Models;
using BareProx.Repositories;
using BareProx.Services.Jobs;
using BareProx.Services.Notifications;
using BareProx.Services.Proxmox;
using BareProx.Services.Proxmox.Ops;
using BareProx.Services.Proxmox.Snapshots;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Linq;

namespace BareProx.Services.Backup
{
    public class BackupService : IBackupService
    {
        private readonly IDbContextFactory<ApplicationDbContext> _dbf;
        private readonly IQueryDbFactory _qdbf;
        private readonly ProxmoxService _proxmoxService;
        private readonly IProxmoxOpsService _proxmoxOps;
        private readonly INetappSnapmirrorService _netAppSnapmirrorService;
        private readonly INetappSnapshotService _netAppSnapshotService;
        private readonly IProxmoxSnapshotsService _proxmoxSnapshotsService;
        private readonly IBackupRepository _backupRepository;
        private readonly ILogger<BackupService> _logger;
        private readonly TimeZoneInfo _timeZoneInfo;
        private readonly string _timeZoneId;
        private readonly IAppTimeZoneService _tz;
        private readonly IProxmoxInventoryCache _invCache;
        private readonly IJobService _jobs;
        private readonly IEmailSender _email;
        private readonly IProxmoxSnapChains _snapChains; // NEW

        public BackupService(
            IDbContextFactory<ApplicationDbContext> dbf,
            ProxmoxService proxmoxService,
            IProxmoxOpsService proxmoxOps,
            INetappSnapmirrorService netAppSnapmirrorService,
            INetappSnapshotService netAppSnapshotService,
            IProxmoxSnapshotsService proxmoxSnapshotsService,
            IBackupRepository backupRepository,
            ILogger<BackupService> logger,
            IConfiguration configuration,
            IAppTimeZoneService tz,
            IProxmoxInventoryCache invCache,
            IJobService jobs,
            IEmailSender email,
            IProxmoxSnapChains snapChains,
            IQueryDbFactory qdbf) // NEW
        {
            _dbf = dbf;
            _proxmoxService = proxmoxService;
            _proxmoxOps = proxmoxOps;
            _netAppSnapmirrorService = netAppSnapmirrorService;
            _netAppSnapshotService = netAppSnapshotService;
            _proxmoxSnapshotsService = proxmoxSnapshotsService;
            _backupRepository = backupRepository;
            _logger = logger;
            _timeZoneId = configuration["AppSettings:TimeZone"] ?? "UTC";
            _timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(_timeZoneId);
            _tz = tz;
            _invCache = invCache;
            _jobs = jobs;
            _email = email;
            _snapChains = snapChains; // NEW
            _qdbf = qdbf;
        }

        public async Task<bool> StartBackupAsync(
            // identity / scope
            string storageName,
            int? selectedNetappVolumeId,
            string? volumeUuid,

            // context
            bool isApplicationAware,
            string label,
            int clusterId,
            int netappControllerId,

            // policy
            int retentionCount,
            string retentionUnit,

            // behavior
            bool enableIoFreeze,
            bool useProxmoxSnapshot,
            bool withMemory,
            bool dontTrySuspend,

            // scheduling / replication / locking
            int scheduleId,
            bool replicateToSecondary,
            bool enableLocking,
            int? lockRetentionCount,
            string? lockRetentionUnit,

            // extras
            IEnumerable<string>? excludedVmIds = null,
            CancellationToken ct = default
        )
        {
            // ---- Identity split: Proxmox vs NetApp ----
            var proxmoxStorageId = storageName;          // Proxmox storage ID (e.g. "Proxmox_DSDEVII")
            string? netappVolumeName = null;            // NetApp volume name (e.g. "proxmox_ds_dev2")
            int effectiveNetappControllerId = netappControllerId;
            string? effectiveVolumeUuid = volumeUuid;
            string? svmName = null;

            ProxmoxCluster? cluster = null;
            List<ProxmoxVM>? vms = null;
            var proxmoxSnapshotNames = new Dictionary<int, string>();
            var proxSnapCleanup = false;
            var vmsWerePaused = false;
            var hadWarnings = false;
            var vmHadWarnings = new HashSet<int>();
            var skippedCount = 0;
            string? createdSnapshotName = null;

            var excludedSet = new HashSet<int>();
            if (excludedVmIds != null)
            {
                foreach (var s in excludedVmIds)
                    if (int.TryParse(s, out var id)) excludedSet.Add(id);
            }

            int jobId = 0;

            try
            {
                jobId = await _jobs.CreateJobAsync(
                    type: "Backup",
                    relatedVm: proxmoxStorageId,
                    payloadJson: $"{{\"storageName\":\"{proxmoxStorageId}\",\"label\":\"{label}\",\"excludedCount\":{excludedSet.Count}}}",
                    ct);

                // ---- Resolve SelectedNetappVolumeId / Disabled enforcement ----
                using (var db0 = await _dbf.CreateDbContextAsync(ct))
                {
                    SelectedNetappVolumes? sel = null;

                    if (selectedNetappVolumeId is int selId && selId > 0)
                    {
                        sel = await db0.SelectedNetappVolumes
                            .AsNoTracking()
                            .FirstOrDefaultAsync(v => v.Id == selId, ct);

                        var check = ValidateSelectedVolumeRow(sel, null);
                        if (check.error is not null)
                            return await FailAndNotifyAsync(jobId, proxmoxStorageId, label, check.error, scheduleId, ct);

                        if (check.row is null)
                            return await FailAndNotifyAsync(jobId, proxmoxStorageId, label, "SelectedNetappVolumeId not found.", scheduleId, ct);

                        var row = check.row;

                        // UUID consistency guard
                        if (!string.IsNullOrWhiteSpace(effectiveVolumeUuid) &&
                            !string.IsNullOrWhiteSpace(row.Uuid) &&
                            !string.Equals(effectiveVolumeUuid, row.Uuid, StringComparison.OrdinalIgnoreCase))
                        {
                            return await FailAndNotifyAsync(
                                jobId, proxmoxStorageId, label,
                                $"Volume UUID mismatch for '{row.VolumeName}' on controller {row.NetappControllerId}: " +
                                $"param='{effectiveVolumeUuid}', db='{row.Uuid}'.",
                                scheduleId, ct);
                        }

                        // NetApp identity from SelectedNetappVolumes
                        netappVolumeName = row.VolumeName;
                        effectiveNetappControllerId = row.NetappControllerId;

                        if (string.IsNullOrWhiteSpace(effectiveVolumeUuid) && !string.IsNullOrWhiteSpace(row.Uuid))
                            effectiveVolumeUuid = row.Uuid;

                        if (string.IsNullOrWhiteSpace(svmName) && !string.IsNullOrWhiteSpace(row.Vserver))
                            svmName = row.Vserver;
                    }
                    else
                    {
                        // Legacy/manual path: try (controller, volumeName) using incoming storageName
                        sel = await db0.SelectedNetappVolumes
                            .AsNoTracking()
                            .FirstOrDefaultAsync(v =>
                                v.NetappControllerId == netappControllerId &&
                                v.VolumeName == storageName, ct);

                        var check = ValidateSelectedVolumeRow(sel, storageName);
                        if (check.error is not null)
                            return await FailAndNotifyAsync(jobId, proxmoxStorageId, label, check.error, scheduleId, ct);

                        var row = check.row;
                        if (row != null)
                        {
                            if (!string.IsNullOrWhiteSpace(effectiveVolumeUuid) &&
                                !string.IsNullOrWhiteSpace(row.Uuid) &&
                                !string.Equals(effectiveVolumeUuid, row.Uuid, StringComparison.OrdinalIgnoreCase))
                            {
                                return await FailAndNotifyAsync(
                                    jobId, proxmoxStorageId, label,
                                    $"Volume UUID mismatch for '{storageName}' on controller {netappControllerId}: " +
                                    $"param='{effectiveVolumeUuid}', db='{row.Uuid}'.",
                                    scheduleId, ct);
                            }

                            netappVolumeName = row.VolumeName;
                            effectiveNetappControllerId = row.NetappControllerId;

                            if (string.IsNullOrWhiteSpace(effectiveVolumeUuid) && !string.IsNullOrWhiteSpace(row.Uuid))
                                effectiveVolumeUuid = row.Uuid;

                            if (string.IsNullOrWhiteSpace(svmName) && !string.IsNullOrWhiteSpace(row.Vserver))
                                svmName = row.Vserver;
                        }
                    }
                }

                // ---- Fallback: use Query DB to resolve UUID + NetApp volume name ----
                if (string.IsNullOrWhiteSpace(effectiveVolumeUuid) || string.IsNullOrWhiteSpace(netappVolumeName))
                {
                    await using var qdb0 = await _qdbf.CreateAsync(ct);

                    // 1) Proxmox storage → NetApp volume UUID via InventoryStorages
                    var invUuid = await qdb0.InventoryStorages
                        .AsNoTracking()
                        .Where(s => s.ClusterId == clusterId && s.StorageId == proxmoxStorageId)
                        .Select(s => s.NetappVolumeUuid)
                        .FirstOrDefaultAsync(ct);

                    if (!string.IsNullOrWhiteSpace(invUuid))
                        effectiveVolumeUuid ??= invUuid;

                    // 2) UUID → NetApp volume name / controller / SVM via InventoryNetappVolumes
                    if (!string.IsNullOrWhiteSpace(effectiveVolumeUuid) &&
                        (string.IsNullOrWhiteSpace(netappVolumeName) || effectiveNetappControllerId == 0))
                    {
                        var nav = await qdb0.InventoryNetappVolumes
                            .AsNoTracking()
                            .FirstOrDefaultAsync(v => v.VolumeUuid == effectiveVolumeUuid, ct);

                        if (nav != null)
                        {
                            netappVolumeName ??= nav.VolumeName;
                            if (effectiveNetappControllerId == 0)
                                effectiveNetappControllerId = nav.NetappControllerId;
                            if (string.IsNullOrWhiteSpace(svmName))
                                svmName = nav.SvmName;
                        }
                    }
                }

                // Last-resort: assume NetApp volume name == Proxmox storage ID
                if (string.IsNullOrWhiteSpace(netappVolumeName))
                    netappVolumeName = proxmoxStorageId;

                using (var db = await _dbf.CreateDbContextAsync(ct))
                {
                    cluster = await db.ProxmoxClusters
                        .Include(c => c.Hosts)
                        .AsNoTracking()
                        .FirstOrDefaultAsync(c => c.Id == clusterId, ct);
                }
                if (cluster == null)
                    return await FailAndNotifyAsync(jobId, proxmoxStorageId, label, $"Cluster with ID {clusterId} not found.", scheduleId, ct);

                // Eligible VMs on this storage (Proxmox side)
                bool snapChainActive = false;
                try
                {
                    snapChainActive = await _snapChains.IsSnapshotChainActiveFromDefAsync(cluster, proxmoxStorageId, ct);
                    _logger.LogInformation("Snapshot-as-volume-chain on '{Storage}': {Active}", proxmoxStorageId, snapChainActive);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not determine snapshot-as-volume-chain state for storage '{Storage}'. Defaulting to false.", proxmoxStorageId);
                }

                var storageWithVms = await _invCache.GetEligibleBackupStorageWithVMsAsync(
                    cluster,
                    effectiveNetappControllerId,
                    new[] { proxmoxStorageId },
                    ct);

                if (!storageWithVms.TryGetValue(proxmoxStorageId, out vms) || vms is null || vms.Count == 0)
                {
                    _logger.LogWarning("No VMs returned for Proxmox storage '{Storage}' (controllerId={Controller}).",
                        proxmoxStorageId, effectiveNetappControllerId);

                    try
                    {
                        var raw = await _invCache.GetVmsByStorageListAsync(
                            cluster,
                            new[] { proxmoxStorageId },
                            ct,
                            maxAge: TimeSpan.Zero,
                            forceRefresh: true);

                        if (raw.TryGetValue(proxmoxStorageId, out var direct) && direct is { Count: > 0 })
                        {
                            _logger.LogInformation("Direct Proxmox listing found {Count} VM(s) on '{Storage}', but eligible set still empty.", direct.Count, proxmoxStorageId);
                        }
                        else
                        {
                            _logger.LogWarning("Direct Proxmox listing also found no VMs on '{Storage}'.", proxmoxStorageId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Sanity check (raw Proxmox listing) failed for storage '{Storage}'.", proxmoxStorageId);
                    }

                    return await FailAndNotifyAsync(jobId, proxmoxStorageId, label,
                        $"No VMs found in storage '{proxmoxStorageId}'. " +
                        $"(Tip: ensure the Proxmox storage name is correct and mounted on at least one online host.)",
                        scheduleId, ct);
                }

                if (cluster.Hosts == null || !cluster.Hosts.Any())
                    return await FailAndNotifyAsync(jobId, proxmoxStorageId, label, "Cluster not properly configured.", scheduleId, ct);

                // Create per-VM rows
                var vmRows = new Dictionary<int, int>(capacity: vms.Count);
                foreach (var vm in vms)
                {
                    var rowId = await _jobs.BeginVmAsync(jobId, vm.Id, vm.Name, vm.HostName, proxmoxStorageId, ct);
                    vmRows[vm.Id] = rowId;
                }

                // Power status (once)
                var statusMap = new Dictionary<int, string>(capacity: vms.Count);
                {
                    var tasks = vms.Select(async vm =>
                    {
                        var st = await _proxmoxService.GetVmStatusAsync(cluster, vm.HostName, vm.HostAddress, vm.Id, ct);
                        lock (statusMap) statusMap[vm.Id] = st ?? "";
                    });
                    await Task.WhenAll(tasks);
                }

                // Mark skipped
                foreach (var vm in vms)
                {
                    var rowId = vmRows[vm.Id];

                    if (excludedSet.Contains(vm.Id))
                    {
                        await _jobs.MarkVmSkippedAsync(rowId, "Excluded by user", ct);
                        skippedCount++;
                        continue;
                    }

                    var st = statusMap.TryGetValue(vm.Id, out var s) ? s : "";
                    var isStopped = string.Equals(st, "stopped", StringComparison.OrdinalIgnoreCase);
                    if (isStopped)
                    {
                        await _jobs.MarkVmSkippedAsync(rowId, "VM is powered off", ct);
                        skippedCount++;
                    }
                }

                // 3) Pause (IO-freeze)
                var anyPaused = false;
                if (isApplicationAware && enableIoFreeze)
                {
                    foreach (var vm in vms)
                    {
                        if (excludedSet.Contains(vm.Id)) continue;

                        var isStopped = statusMap.TryGetValue(vm.Id, out var st) &&
                                        string.Equals(st, "stopped", StringComparison.OrdinalIgnoreCase);
                        if (isStopped) continue;

                        try
                        {
                            await _proxmoxService.PauseVmAsync(cluster, vm.HostName, vm.HostAddress, vm.Id, ct);
                            anyPaused = true;
                            await _jobs.SetIoFreezeResultAsync(vmRows[vm.Id], attempted: true, succeeded: true, wasRunning: true, ct);
                            await _jobs.LogVmAsync(vmRows[vm.Id], "IO freeze requested", "Info", ct);
                        }
                        catch (Exception ex)
                        {
                            await _jobs.SetIoFreezeResultAsync(vmRows[vm.Id], attempted: true, succeeded: false, wasRunning: true, ct);
                            await _jobs.LogVmAsync(vmRows[vm.Id], $"IO freeze failed: {ex.Message}", "Warning", ct);
                            _logger.LogWarning(ex, "Pause failed for VM {VmId} on {Host}", vm.Id, vm.HostName);
                            hadWarnings = true;
                            vmHadWarnings.Add(vm.Id);
                        }
                    }

                    vmsWerePaused = anyPaused;
                    if (anyPaused)
                        await _jobs.UpdateJobStatusAsync(jobId, "Paused VMs", null, ct);
                }

                // 4) Proxmox snapshots (optional)
                var localTime = _tz.ConvertUtcToApp(DateTime.UtcNow);
                var proxmoxSnapshotName = $"BareProx-{label}_{localTime:yyyy-MM-dd-HH-mm-ss}";
                var snapshotTasks = new Dictionary<int, string>();

                if (isApplicationAware && useProxmoxSnapshot)
                {
                    await _jobs.UpdateJobStatusAsync(jobId, "Creating Proxmox snapshots", null, ct);

                    foreach (var vm in vms)
                    {
                        if (excludedSet.Contains(vm.Id)) continue;

                        var status = statusMap.TryGetValue(vm.Id, out var s) ? s : "";
                        var isStopped = string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase);
                        if (isStopped)
                        {
                            _logger.LogInformation("Skipping snapshot for stopped VM {VmId}", vm.Id);
                            continue;
                        }

                        try
                        {
                            var upid = await _proxmoxSnapshotsService.CreateSnapshotAsync(
                                cluster, vm.HostName, vm.HostAddress, vm.Id,
                                proxmoxSnapshotName,
                                "Backup created via BareProx",
                                withMemory,
                                dontTrySuspend,
                                ct);

                            await _jobs.MarkVmSnapshotRequestedAsync(vmRows[vm.Id], proxmoxSnapshotName, upid, ct);
                            await _jobs.LogVmAsync(vmRows[vm.Id], $"Snapshot requested (UPID={upid ?? "n/a"})", "Info", ct);

                            if (!string.IsNullOrWhiteSpace(upid))
                            {
                                snapshotTasks[vm.Id] = upid;
                                proxmoxSnapshotNames[vm.Id] = proxmoxSnapshotName;
                            }
                            else
                            {
                                await _jobs.LogVmAsync(vmRows[vm.Id], "Snapshot UPID was empty", "Warning", ct);
                                _logger.LogWarning("Snapshot creation returned empty UPID for VM {VmId}", vm.Id);
                                hadWarnings = true;
                                vmHadWarnings.Add(vm.Id);
                            }
                        }
                        catch (Exception ex)
                        {
                            await _jobs.LogVmAsync(vmRows[vm.Id], $"Snapshot request failed: {ex.Message}", "Error", ct);
                            _logger.LogWarning(ex, "Snapshot creation failed for VM {VmId}", vm.Id);
                            hadWarnings = true;
                            vmHadWarnings.Add(vm.Id);
                        }
                    }

                    await _jobs.UpdateJobStatusAsync(jobId, "Waiting for Proxmox snapshots", null, ct);

                    var waits = snapshotTasks.Select(kv =>
                    {
                        var vm = vms.First(v => v.Id == kv.Key);
                        return new
                        {
                            Vm = vm,
                            Task = _proxmoxOps.WaitForTaskCompletionAsync(
                                cluster, vm.HostName, vm.HostAddress, kv.Value,
                                TimeSpan.FromMinutes(20),
                                _logger, ct)
                        };
                    }).ToList();

                    await Task.WhenAll(waits.Select(w => w.Task));
                    foreach (var w in waits)
                    {
                        if (w.Task.Result)
                        {
                            await _jobs.MarkVmSnapshotTakenAsync(vmRows[w.Vm.Id], ct);
                            await _jobs.LogVmAsync(vmRows[w.Vm.Id], "Snapshot completed", "Info", ct);
                        }
                        else
                        {
                            await _jobs.LogVmAsync(vmRows[w.Vm.Id], "Snapshot wait timed out", "Warning", ct);
                            _logger.LogWarning("Snapshot task for VM {VmId} timed out in job {JobId}", w.Vm.Id, jobId);
                            hadWarnings = true;
                            vmHadWarnings.Add(w.Vm.Id);
                        }
                    }

                    await _jobs.UpdateJobStatusAsync(jobId, "Proxmox snapshots completed", null, ct);
                }

                // 5a) NetApp snapshot (storage-wide)
                LogUuidFallbackIfNeeded(effectiveVolumeUuid, effectiveNetappControllerId, netappVolumeName!);

                var snapshotResult = await _netAppSnapshotService.CreateSnapshotAsync(
                    effectiveNetappControllerId,
                    netappVolumeName!,
                    label,
                    snapLocking: enableLocking,
                    lockRetentionCount: enableLocking ? lockRetentionCount : null,
                    lockRetentionUnit: enableLocking ? lockRetentionUnit : null,
                    volumeUuid: effectiveVolumeUuid,
                    svmName: svmName,
                    ct: ct);

                if (!snapshotResult.Success)
                    return await FailAndNotifyAsync(jobId, proxmoxStorageId, label, snapshotResult.ErrorMessage, scheduleId, ct);

                await _jobs.UpdateJobStatusAsync(jobId, "NetApp snapshot created", null, ct);
                createdSnapshotName = snapshotResult.SnapshotName;

                foreach (var vm in vms)
                {
                    await _jobs.LogVmAsync(vmRows[vm.Id], $"Storage snapshot created: {snapshotResult.SnapshotName}", "Info", ct);
                }

                // 5b) Persist BackupRecord(s)
                foreach (var vm in vms)
                {
                    var cfg = await _proxmoxService.GetVmConfigAsync(cluster, vm.HostName, vm.Id, ct);

                    var isStopped = statusMap.TryGetValue(vm.Id, out var st) &&
                                    string.Equals(st, "stopped", StringComparison.OrdinalIgnoreCase);
                    var isExcluded = excludedSet.Contains(vm.Id);

                    var perVmIsAppAware = !isExcluded && !isStopped && isApplicationAware;
                    var perVmFreeze = !isExcluded && !isStopped && enableIoFreeze;
                    var perVmUseProxSnap = !isExcluded && !isStopped && useProxmoxSnapshot;
                    var perVmWithMemory = !isExcluded && !isStopped && withMemory;

                    try
                    {
                        await _backupRepository.StoreBackupInfoAsync(new BackupRecord
                        {
                            JobId = jobId,
                            VMID = vm.Id,
                            VmName = vm.Name,
                            HostName = vm.HostName,

                            // NetApp side identity
                            StorageName = netappVolumeName!,
                            VolumeUuid = effectiveVolumeUuid,
                            SelectedNetappVolumeId = selectedNetappVolumeId,
                            Label = label,
                            RetentionCount = retentionCount,
                            RetentionUnit = retentionUnit,
                            TimeStamp = DateTime.UtcNow,
                            ControllerId = effectiveNetappControllerId,
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

                        if (isExcluded || isStopped)
                        {
                            await _jobs.LogVmAsync(vmRows[vm.Id], "BackupRecord stored (VM skipped earlier)", "Info", ct);
                            continue;
                        }

                        if (vmHadWarnings.Contains(vm.Id))
                        {
                            await _jobs.MarkVmWarningAsync(vmRows[vm.Id], "Completed with warnings", ct);
                        }
                        else
                        {
                            await _jobs.MarkVmSuccessAsync(vmRows[vm.Id], backupRecordId: null, ct);
                        }

                        await _jobs.LogVmAsync(vmRows[vm.Id], "BackupRecord stored", "Info", ct);
                    }
                    catch (Exception ex)
                    {
                        await _jobs.MarkVmFailureAsync(vmRows[vm.Id], $"Failed to persist BackupRecord: {ex.Message}", ct);
                        hadWarnings = true;
                        vmHadWarnings.Add(vm.Id);
                    }
                }

                // --- Track NetApp snapshot in QUERY DB ---
                {
                    await using var qdb = await _qdbf.CreateAsync(ct);

                    qdb.NetappSnapshots.Add(new NetappSnapshot
                    {
                        JobId = jobId,
                        PrimaryVolume = netappVolumeName!,
                        SnapshotName = snapshotResult.SnapshotName,
                        CreatedAt = _tz.ConvertUtcToApp(DateTime.UtcNow),
                        PrimaryControllerId = effectiveNetappControllerId,
                        SnapmirrorLabel = label,
                        ExistsOnPrimary = true,
                        ExistsOnSecondary = false,
                        LastChecked = _tz.ConvertUtcToApp(DateTime.UtcNow),
                        IsReplicated = false
                    });

                    for (int i = 0; i < 3; i++)
                    {
                        try { await qdb.SaveChangesAsync(ct); break; }
                        catch (DbUpdateException ey) when (ey.InnerException is Microsoft.Data.Sqlite.SqliteException se && se.SqliteErrorCode == 5)
                        { if (i == 2) throw; await Task.Delay(500, ct); }
                    }
                }

                // 5c) Cleanup Proxmox snapshots (if taken)
                if (useProxmoxSnapshot)
                {
                    await CleanupProxmoxSnapshots(cluster, vms, proxmoxSnapshotNames, shouldCleanup: true, ct: ct);
                    proxSnapCleanup = true;
                    _logger.LogInformation("Proxmox snapshots deleted after storage snapshot.");

                    foreach (var vm in vms)
                    {
                        if (proxmoxSnapshotNames.ContainsKey(vm.Id))
                            await _jobs.LogVmAsync(vmRows[vm.Id], "Proxmox snapshot deleted after storage snapshot", "Info", ct);
                    }
                }

                // 5c) Index storage disks for this backup job (Proxmox side)
                try
                {
                    var anyVm = vms.FirstOrDefault();
                    var nodeName = anyVm?.HostName ?? cluster.Hosts.First().Hostname;

                    var disks = await _proxmoxService.GetStorageDisksAsync(
                        cluster,
                        nodeName,
                        proxmoxStorageId,
                        ct);

                    if (disks.Count > 0)
                    {
                        using (var db = await _dbf.CreateDbContextAsync(ct))
                        {
                            var nowUtc = DateTime.UtcNow;

                            foreach (var d in disks)
                            {
                                db.ProxmoxStorageDiskSnapshots.Add(new ProxmoxStorageDiskSnapshot
                                {
                                    JobId = jobId,
                                    ClusterId = cluster.Id,
                                    NodeName = nodeName,
                                    StorageId = proxmoxStorageId,
                                    VMID = int.TryParse(d.vmid, out var vmidVal) ? vmidVal : (int?)null,
                                    VolId = d.volid,
                                    ContentType = d.content,
                                    Format = d.format,
                                    SizeBytes = d.size,
                                    CapturedAtUtc = nowUtc
                                });
                            }

                            await db.SaveChangesAsync(ct);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to index Proxmox storage disks for job {JobId} on storage {Storage}.",
                        jobId, proxmoxStorageId);
                }

                // 6) Replicate to secondary (optional)
                if (replicateToSecondary)
                {
                    using (var db = await _dbf.CreateDbContextAsync(ct))
                    {
                        var relation = await db.SnapMirrorRelations
                            .AsNoTracking()
                            .FirstOrDefaultAsync(r =>
                                r.SourceVolume == netappVolumeName &&
                                r.SourceControllerId == effectiveNetappControllerId, ct);

                        if (relation == null)
                        {
                            _logger.LogWarning("SnapMirror: no relation found for source volume '{Volume}' (controller {ControllerId}). Replication skipped.",
                                netappVolumeName, effectiveNetappControllerId);
                            await _jobs.UpdateJobStatusAsync(jobId, "Replication skipped (no SnapMirror relation)", null, ct);
                            foreach (var vm in vms)
                                await _jobs.LogVmAsync(vmRows[vm.Id], "Replication skipped (no SnapMirror relation)", "Info", ct);
                            hadWarnings = true;
                        }
                        else
                        {
                            await _jobs.UpdateJobStatusAsync(jobId, "Triggering SnapMirror update", null, ct);

                            var triggered = await _netAppSnapmirrorService.TriggerSnapMirrorUpdateAsync(relation.Uuid, ct);
                            if (!triggered)
                            {
                                _logger.LogWarning("SnapMirror: failed to trigger update for relation {RelationUuid}. Replication skipped.", relation.Uuid);
                                await _jobs.UpdateJobStatusAsync(jobId, "Replication skipped (trigger failed)", null, ct);
                                foreach (var vm in vms)
                                    await _jobs.LogVmAsync(vmRows[vm.Id], "Replication skipped (trigger failed)", "Warning", ct);
                                hadWarnings = true;
                            }
                            else
                            {
                                await _jobs.UpdateJobStatusAsync(jobId, "Waiting for SnapMirror to catch up", null, ct);

                                var secondaryVolume = relation.DestinationVolume;
                                var secondarySnap = snapshotResult.SnapshotName;
                                var secondaryControllerId = relation.DestinationControllerId;
                                var replicated = false;

                                var sw = Stopwatch.StartNew();
                                while (sw.Elapsed < TimeSpan.FromMinutes(120))
                                {
                                    ct.ThrowIfCancellationRequested();
                                    await Task.Delay(TimeSpan.FromSeconds(10), ct);

                                    var updated = await _netAppSnapmirrorService.GetSnapMirrorRelationAsync(relation.Uuid, ct);

                                    var isMirrored = updated is not null &&
                                                     string.Equals(updated.state, "snapmirrored", StringComparison.OrdinalIgnoreCase);

                                    var isSuccess = string.Equals(updated?.transfer?.State, "success", StringComparison.OrdinalIgnoreCase);

                                    if (isMirrored && isSuccess)
                                    {
                                        var snaps = await _netAppSnapshotService.GetSnapshotsAsync(secondaryControllerId, secondaryVolume, ct)
                                                   ?? Enumerable.Empty<string>();

                                        if (!string.IsNullOrWhiteSpace(secondarySnap) &&
                                            snaps.Any(s => string.Equals(s, secondarySnap, StringComparison.OrdinalIgnoreCase)))
                                        {
                                            replicated = true;
                                            break;
                                        }
                                    }
                                }

                                if (replicated)
                                {
                                    var nowApp = _tz.ConvertUtcToApp(DateTime.UtcNow);

                                    // Update snapshot row in QUERY DB
                                    await using (var qdb = await _qdbf.CreateAsync(ct))
                                    {
                                        var snapRow = await qdb.NetappSnapshots
                                            .FirstOrDefaultAsync(s =>
                                                s.SnapshotName == secondarySnap
                                                && s.PrimaryVolume == netappVolumeName
                                                && s.PrimaryControllerId == effectiveNetappControllerId, ct);

                                        if (snapRow != null)
                                        {
                                            snapRow.ExistsOnSecondary = true;
                                            snapRow.SecondaryVolume = secondaryVolume;
                                            snapRow.SecondaryControllerId = secondaryControllerId;
                                            snapRow.IsReplicated = true;
                                            snapRow.LastChecked = nowApp;

                                            for (int i = 0; i < 3; i++)
                                            {
                                                try { await qdb.SaveChangesAsync(ct); break; }
                                                catch (DbUpdateException ey) when (ey.InnerException is Microsoft.Data.Sqlite.SqliteException se && se.SqliteErrorCode == 5)
                                                { if (i == 2) throw; await Task.Delay(500, ct); }
                                            }
                                        }
                                    }

                                    // keep BackupRecords update on MAIN DB
                                    await db.BackupRecords
                                        .Where(br => br.JobId == jobId)
                                        .ExecuteUpdateAsync(s => s.SetProperty(b => b.ReplicateToSecondary, true), ct);

                                    await _jobs.UpdateJobStatusAsync(jobId, "Replication completed", null, ct);

                                    foreach (var vm in vms)
                                    {
                                        await _jobs.LogVmAsync(vmRows[vm.Id], $"Replicated to secondary ({secondaryVolume})", "Info", ct);
                                    }
                                }
                                else
                                {
                                    await _jobs.UpdateJobStatusAsync(jobId, "Replication not confirmed (timeout/no snapshot)", null, ct);
                                    foreach (var vm in vms)
                                        await _jobs.LogVmAsync(vmRows[vm.Id], "Replication not confirmed (timeout/no snapshot)", "Warning", ct);
                                    hadWarnings = true;
                                }
                            }
                        }
                    }
                }

                // 7) Cancellation check (MAIN DB)
                using (var db = await _dbf.CreateDbContextAsync(ct))
                {
                    var jobRow = await db.Jobs.FindAsync(new object?[] { jobId }, ct);
                    if (string.Equals(jobRow?.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
                        return await FailAndNotifyAsync(jobId, proxmoxStorageId, label, "Job was cancelled.", scheduleId, ct);
                }

                // 8) Complete job
                if (hadWarnings)
                    await _jobs.CompleteJobAsync(jobId, "Warning", ct);
                else
                    await _jobs.CompleteJobAsync(jobId, ct);

                var warnedCount = vmHadWarnings.Count;
                var final = hadWarnings ? "Warning" : "Success";
                await TryNotifyAsync(
                    jobId,
                    netappVolumeName ?? proxmoxStorageId,
                    label,
                    final,
                    errorOrNote: null,
                    snapshotName: createdSnapshotName,
                    totalVms: vms?.Count ?? 0,
                    skippedVms: skippedCount,
                    warnedVms: warnedCount,
                    scheduleId: scheduleId,
                    ct: ct);

                return true;
            }
            catch (OperationCanceledException)
            {
                if (jobId > 0)
                {
                    await _jobs.FailJobAsync(jobId, "Job was cancelled.", CancellationToken.None);
                    await TryNotifyAsync(
                        jobId,
                        netappVolumeName ?? proxmoxStorageId,
                        label,
                        "Error",
                        "Job was cancelled.",
                        null,
                        0, 0, 0,
                        scheduleId,
                        CancellationToken.None);
                    return false;
                }
                throw;
            }
            catch (Exception ex)
            {
                if (jobId > 0)
                {
                    await _jobs.FailJobAsync(jobId, ex.Message, ct);
                    await TryNotifyAsync(
                        jobId,
                        netappVolumeName ?? proxmoxStorageId,
                        label,
                        "Error",
                        ex.Message,
                        createdSnapshotName,
                        0, 0, 0,
                        scheduleId,
                        ct);
                    return false;
                }

                _logger.LogError(ex, "StartBackupAsync failed before Job creation. storage={Storage}", proxmoxStorageId);
                return false;
            }
            finally
            {
                if (cluster != null && vms != null)
                {
                    try
                    {
                        await UnpauseIfNeeded(cluster, vms, isApplicationAware, enableIoFreeze, vmsWerePaused, ct);
                    }
                    catch { /* best effort */ }

                    if (useProxmoxSnapshot && !proxSnapCleanup)
                    {
                        try
                        {
                            await CleanupProxmoxSnapshots(cluster, vms, proxmoxSnapshotNames, shouldCleanup: true, ct: ct);
                        }
                        catch { /* best effort */ }
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
                    try { await _proxmoxService.UnpauseVmAsync(cluster, vm.HostName, vm.HostAddress, vm.Id, ct); }
                    catch { /* best effort */ }
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
                if (snapshotMap.TryGetValue(vm.Id, out var snap) && !string.IsNullOrWhiteSpace(snap))
                {
                    try { await _proxmoxSnapshotsService.DeleteSnapshotAsync(cluster, vm.HostName, vm.HostAddress, vm.Id, snap, ct); }
                    catch { /* best effort */ }
                }
            }
        }

        private async Task<bool> FailAndNotifyAsync(
            int jobId,
            string storageName,
            string label,
            string error,
            int scheduleId,
            CancellationToken ct)
        {
            var res = await _jobs.FailJobAsync(jobId, error, ct);
            await TryNotifyAsync(jobId, storageName, label, "Error", error, snapshotName: null,
                                 totalVms: 0, skippedVms: 0, warnedVms: 0, scheduleId: scheduleId, ct: ct);
            return res;
        }

        private async Task TryNotifyAsync(
            int jobId,
            string storageName,
            string label,
            string finalStatus,
            string? errorOrNote,
            string? snapshotName,
            int totalVms,
            int skippedVms,
            int warnedVms,
            int scheduleId,
            CancellationToken ct)
        {
            try
            {
                using var db = await _dbf.CreateDbContextAsync(ct);
                var s = await db.EmailSettings.AsNoTracking().FirstOrDefaultAsync(e => e.Id == 1, ct);
                if (s is null || !s.Enabled) return; // SMTP not configured/enabled => bail

                const int ManualScheduleId = 999;
                bool isManual = scheduleId == ManualScheduleId;

                BackupSchedule? sched = null;
                if (!isManual && scheduleId > 0)
                    sched = await db.BackupSchedules.AsNoTracking().FirstOrDefaultAsync(x => x.Id == scheduleId, ct);

                // Decide recipients + gating
                string recipientsRaw = string.Empty;
                bool sendAllowed = false;

                if (isManual)
                {
                    // Manual jobs: use GLOBAL gates + GLOBAL recipients (unchanged)
                    recipientsRaw = s.DefaultRecipients ?? string.Empty;
                    sendAllowed = finalStatus switch
                    {
                        "Success" => s.OnBackupSuccess,
                        "Warning" => s.OnBackupFailure, // treat warning like error
                        "Error" => s.OnBackupFailure,
                        _ => false
                    };
                }
                else
                {
                    // Scheduled job: require schedule to exist and notifications be enabled on it
                    if (sched is null) return;

                    // If the schedule has notifications disabled -> no notifications at all
                    if (sched.NotificationsEnabled != true) return;

                    // When notifications are enabled on the schedule:
                    // - ONLY the schedule toggles control whether to send (ignore global gates)
                    // - Recipients = schedule list if present; otherwise fallback to global default
                    var hasSchedRecipients = !string.IsNullOrWhiteSpace(sched.NotificationEmails);
                    recipientsRaw = hasSchedRecipients ? sched.NotificationEmails! : (s.DefaultRecipients ?? string.Empty);

                    sendAllowed = finalStatus switch
                    {
                        "Success" => sched.NotifyOnSuccess == true,
                        "Warning" => sched.NotifyOnError == true, // warnings treated like problems
                        "Error" => sched.NotifyOnError == true,
                        _ => false
                    };
                }

                // Normalize recipients
                var to = (recipientsRaw ?? string.Empty)
                    .Split(new[] { ';', ',' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (!sendAllowed || to.Length == 0) return;

                // Compose and send
                var nowApp = _tz.ConvertUtcToApp(DateTime.UtcNow);
                var tzName = _timeZoneId;
                var schedTag = (!isManual && sched != null) ? $" [Schedule: {sched.Name}]" : "";
                var subj = $"BareProx: Backup {finalStatus} — {storageName} ({label}){schedTag} [Job #{jobId}]";

                var html = $@"
                            <h3>BareProx Backup {finalStatus}</h3>
                            <p><b>Job:</b> #{jobId}{(!isManual && sched != null ? $"<br/><b>Schedule:</b> {System.Net.WebUtility.HtmlEncode(sched.Name)}" : "")}<br/>
                            <b>Storage:</b> {System.Net.WebUtility.HtmlEncode(storageName)}<br/>
                            <b>Label:</b> {System.Net.WebUtility.HtmlEncode(label)}<br/>
                            <b>Snapshot:</b> {(string.IsNullOrWhiteSpace(snapshotName) ? "" : System.Net.WebUtility.HtmlEncode(snapshotName))}<br/>
                            <b>When ({System.Net.WebUtility.HtmlEncode(tzName)}):</b> {nowApp:yyyy-MM-dd HH:mm:ss}<br/>
                            <table style=""border-collapse:collapse;min-width:360px"">
                              <tr><td style=""padding:4px;border:1px solid #ccc""><b>Total VMs</b></td><td style=""padding:4px;border:1px solid #ccc"">{totalVms}</td></tr>
                              <tr><td style=""padding:4px;border:1px solid #ccc""><b>Skipped</b></td><td style=""padding:4px;border:1px solid #ccc"">{skippedVms}</td></tr>
                              <tr><td style=""padding:4px;border:1px solid #ccc""><b>VMs with warnings</b></td><td style=""padding:4px;border:1px solid #ccc"">{warnedVms}</td></tr>
                            </table>
                            {(string.IsNullOrWhiteSpace(errorOrNote) ? "" : $@"<p><b>Notes:</b><br/><pre style=""white-space:pre-wrap"">{System.Net.WebUtility.HtmlEncode(errorOrNote)}</pre></p>")}
                            <p>— BareProx</p>";

                foreach (var r in to)
                    await _email.SendAsync(r, subj, html, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TryNotifyAsync failed for Job {JobId}", jobId);
            }
        }


        // ---------- Helper: validate SelectedNetappVolumes row ----------
        private static (SelectedNetappVolumes? row, string? error) ValidateSelectedVolumeRow(SelectedNetappVolumes? row, string? nameForMsg)
        {
            if (row is null) return (null, null); // no explicit row -> proceed
            if (row.Disabled == true)
            {
                var vol = string.IsNullOrWhiteSpace(nameForMsg) ? row.VolumeName : nameForMsg;
                return (null, $"Volume '{vol}' on controller #{row.NetappControllerId} is not selected.");
            }
            return (row, null);
        }

        // Prefer UUID when present; log once when we fall back to name-only.
        private void LogUuidFallbackIfNeeded(string? volumeUuid, int controllerId, string storageName)
        {
            if (string.IsNullOrWhiteSpace(volumeUuid))
                _logger.LogWarning("[No UUID] Falling back to name-based NetApp ops: volume='{Storage}' controller={ControllerId}", storageName, controllerId);
        }
    }
}
