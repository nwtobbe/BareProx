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
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
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
        private readonly IProxmoxSnapChains _snapChains;
        private readonly INodeSnapshotGateManager _gateManager;
        private readonly IOptionsMonitor<BackupThrottlesOptions> _throttles;

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
            INodeSnapshotGateManager gateManager,
            IOptionsMonitor<BackupThrottlesOptions> throttles,
            IQueryDbFactory qdbf)
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
            _snapChains = snapChains;
            _gateManager = gateManager;
            _throttles = throttles;
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
            var proxmoxStorageId = storageName;
            string? netappVolumeName = null;
            int effectiveNetappControllerId = netappControllerId;
            string? effectiveVolumeUuid = volumeUuid;
            string? svmName = null;

            ProxmoxCluster? cluster = null;
            List<ProxmoxVM>? vms = null;

            // For cleanup we ONLY store snapshots that actually completed OK.
            var proxmoxSnapshotNames = new ConcurrentDictionary<int, string>();
            var completedSnapshotVmIds = new ConcurrentDictionary<int, byte>();

            var proxSnapCleanup = false;
            var vmsWerePaused = false;
            var hadWarnings = false;
            var vmHadWarnings = new ConcurrentDictionary<int, byte>();
            var skippedCount = 0;
            string? createdSnapshotName = null;

            var excludedSet = new HashSet<int>();
            if (excludedVmIds != null)
            {
                foreach (var s in excludedVmIds)
                    if (int.TryParse(s, out var id)) excludedSet.Add(id);
            }

            int jobId = 0;

            // Finalization tracking
            string? finalStatus = null;          // e.g. "Completed", "Completed with warnings", "Failed"
            string? finalErrorMessage = null;
            bool success = false;

            // Helper: detect transient Proxmox snapshot errors
            static bool IsTransientProxmoxSnapshotError(Exception ex)
            {
                var msg = ex.ToString();
                return msg.Contains("got no worker upid", StringComparison.OrdinalIgnoreCase) ||
                       msg.Contains("start worker failed", StringComparison.OrdinalIgnoreCase) ||
                       msg.Contains("VM is locked (snapshot)", StringComparison.OrdinalIgnoreCase) ||
                       msg.Contains("VM is locked", StringComparison.OrdinalIgnoreCase) ||
                       (msg.Contains("snapshot", StringComparison.OrdinalIgnoreCase) && msg.Contains("locked", StringComparison.OrdinalIgnoreCase));
            }

            // Helper: retry wrapper
            static async Task<T> RetryAsync<T>(
                Func<Task<T>> action,
                Func<Exception, bool> isTransient,
                CancellationToken token,
                int maxAttempts = 5,
                int maxDelayMs = 15000)
            {
                var delayMs = 1000;

                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    token.ThrowIfCancellationRequested();

                    try
                    {
                        return await action();
                    }
                    catch (Exception ex) when (attempt < maxAttempts && isTransient(ex))
                    {
                        var jitter = Random.Shared.Next(0, 250);
                        await Task.Delay(delayMs + jitter, token);
                        delayMs = Math.Min(delayMs * 2, maxDelayMs);
                    }
                }

                // last attempt (throws if fails)
                return await action();
            }

            // Helper: safely mark a VM warning
            void MarkVmWarning(int vmid)
            {
                hadWarnings = true;
                vmHadWarnings.TryAdd(vmid, 1);
            }

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
                             await FailAndThrowAsync(jobId, proxmoxStorageId, label, check.error, scheduleId, ct);

                        if (check.row is null)
                             await FailAndThrowAsync(jobId, proxmoxStorageId, label, "SelectedNetappVolumeId not found.", scheduleId, ct);

                        var row = check.row;

                        if (!string.IsNullOrWhiteSpace(effectiveVolumeUuid) &&
                            !string.IsNullOrWhiteSpace(row.Uuid) &&
                            !string.Equals(effectiveVolumeUuid, row.Uuid, StringComparison.OrdinalIgnoreCase))
                        {
                             await FailAndThrowAsync(
                                jobId, proxmoxStorageId, label,
                                $"Volume UUID mismatch for '{row.VolumeName}' on controller {row.NetappControllerId}: param='{effectiveVolumeUuid}', db='{row.Uuid}'.",
                                scheduleId, ct);
                        }

                        netappVolumeName = row.VolumeName;
                        effectiveNetappControllerId = row.NetappControllerId;

                        if (string.IsNullOrWhiteSpace(effectiveVolumeUuid) && !string.IsNullOrWhiteSpace(row.Uuid))
                            effectiveVolumeUuid = row.Uuid;

                        if (string.IsNullOrWhiteSpace(svmName) && !string.IsNullOrWhiteSpace(row.Vserver))
                            svmName = row.Vserver;
                    }
                    else
                    {
                        sel = await db0.SelectedNetappVolumes
                            .AsNoTracking()
                            .FirstOrDefaultAsync(v =>
                                v.NetappControllerId == netappControllerId &&
                                v.VolumeName == storageName, ct);

                        var check = ValidateSelectedVolumeRow(sel, storageName);
                        if (check.error is not null)
                             await FailAndThrowAsync(jobId, proxmoxStorageId, label, check.error, scheduleId, ct);

                        var row = check.row;
                        if (row != null)
                        {
                            if (!string.IsNullOrWhiteSpace(effectiveVolumeUuid) &&
                                !string.IsNullOrWhiteSpace(row.Uuid) &&
                                !string.Equals(effectiveVolumeUuid, row.Uuid, StringComparison.OrdinalIgnoreCase))
                            {
                                 await FailAndThrowAsync(
                                    jobId, proxmoxStorageId, label,
                                    $"Volume UUID mismatch for '{storageName}' on controller {netappControllerId}: param='{effectiveVolumeUuid}', db='{row.Uuid}'.",
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

                    var invUuid = await qdb0.InventoryStorages
                        .AsNoTracking()
                        .Where(s => s.ClusterId == clusterId && s.StorageId == proxmoxStorageId)
                        .Select(s => s.NetappVolumeUuid)
                        .FirstOrDefaultAsync(ct);

                    if (!string.IsNullOrWhiteSpace(invUuid))
                        effectiveVolumeUuid ??= invUuid;

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
                     await FailAndThrowAsync(jobId, proxmoxStorageId, label, $"Cluster with ID {clusterId} not found.", scheduleId, ct);

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
                     await FailAndThrowAsync(jobId, proxmoxStorageId, label,
                        $"No VMs found in storage '{proxmoxStorageId}'. (Tip: ensure storage name is correct and mounted on at least one online host.)",
                        scheduleId, ct);
                }

                if (cluster.Hosts == null || !cluster.Hosts.Any())
                     await FailAndThrowAsync(jobId, proxmoxStorageId, label, "Cluster not properly configured.", scheduleId, ct);

                // Create per-VM rows
                var vmRows = new Dictionary<int, int>(capacity: vms.Count);
                foreach (var vm in vms)
                {
                    var rowId = await _jobs.BeginVmAsync(jobId, vm.Id, vm.Name, vm.HostName, proxmoxStorageId, ct);
                    vmRows[vm.Id] = rowId;
                }

                // Power status (throttled)
                var statusMap = new ConcurrentDictionary<int, string>();
                await Parallel.ForEachAsync(
                    vms,
                    new ParallelOptions { MaxDegreeOfParallelism = _throttles.CurrentValue.MaxParallelVmStatus, CancellationToken = ct },

                    async (vm, token) =>
                    {
                        var st = await _proxmoxService.GetVmStatusAsync(cluster, vm.HostName, vm.HostAddress, vm.Id, token);
                        statusMap[vm.Id] = st ?? "";
                    });

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
                    if (string.Equals(st, "stopped", StringComparison.OrdinalIgnoreCase))
                    {
                        await _jobs.MarkVmSkippedAsync(rowId, "VM is powered off", ct);
                        skippedCount++;
                    }
                }

                // 3) Pause (IO-freeze) — sequential; if one fails, continue
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
                            MarkVmWarning(vm.Id);
                        }
                    }

                    vmsWerePaused = anyPaused;
                    if (anyPaused)
                        await _jobs.UpdateJobStatusAsync(jobId, "Paused VMs", null, ct);
                }

                // 4) Proxmox snapshots (optional)
                var localTime = _tz.ConvertUtcToApp(DateTime.UtcNow);
                var proxmoxSnapshotName = $"BareProx-{label}_{localTime:yyyy-MM-dd-HH-mm-ss}";

                if (isApplicationAware && useProxmoxSnapshot)
                {
                    await _jobs.UpdateJobStatusAsync(jobId, "Creating Proxmox snapshots", null, ct);

                    var eligible = vms.Where(vm =>
                    {
                        if (excludedSet.Contains(vm.Id)) return false;
                        var st = statusMap.TryGetValue(vm.Id, out var s) ? s : "";
                        if (string.Equals(st, "stopped", StringComparison.OrdinalIgnoreCase)) return false;
                        return true;
                    }).ToList();

                    await Parallel.ForEachAsync(
                        eligible,
                        new ParallelOptions { MaxDegreeOfParallelism = _throttles.CurrentValue.MaxParallelProxmoxSnapshotCreates, CancellationToken = ct },
                        async (vm, token) =>
                        {
                            // Per-node serialize create+wait (this is the big win)
                            using var nodeGate = await _gateManager.AcquireCreateAsync(vm.HostName, token);
                            try
                            {
                                // VM lock poll (best-effort)
                                try
                                {
                                    await WaitForVmUnlockedAsync(
                                        cluster!,
                                        vm,
                                        timeout: TimeSpan.FromMinutes(5),
                                        pollInterval: TimeSpan.FromMilliseconds(750),
                                        ct: token);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogDebug(ex, "Lock polling failed for VM {VmId} (continuing).", vm.Id);
                                }

                                // Create snapshot with retry for "worker failed"/"locked"
                                var upid = await RetryAsync(
                                    async () => await _proxmoxSnapshotsService.CreateSnapshotAsync(
                                        cluster!, vm.HostName, vm.HostAddress, vm.Id,
                                        proxmoxSnapshotName,
                                        "Backup created via BareProx",
                                        withMemory,
                                        dontTrySuspend,
                                        token),
                                    IsTransientProxmoxSnapshotError,
                                    token);

                                await _jobs.MarkVmSnapshotRequestedAsync(vmRows[vm.Id], proxmoxSnapshotName, upid, token);
                                await _jobs.LogVmAsync(vmRows[vm.Id], $"Snapshot requested (UPID={upid ?? "n/a"})", "Info", token);

                                if (string.IsNullOrWhiteSpace(upid))
                                {
                                    await _jobs.LogVmAsync(vmRows[vm.Id], "Snapshot UPID was empty", "Warning", token);
                                    _logger.LogWarning("Snapshot creation returned empty UPID for VM {VmId}", vm.Id);
                                    MarkVmWarning(vm.Id);
                                    return;
                                }

                                // Wait ONCE (hard cap = configured timeout)
                                var ok = await _proxmoxOps.WaitForTaskCompletionAsync(
                                    cluster!, vm.HostName, vm.HostAddress, upid,
                                    TimeSpan.FromMinutes(_throttles.CurrentValue.ProxmoxSnapshotWaitTimeoutMinutes),
                                    _logger, token);

                                if (!ok)
                                {
                                    // Post-check (FAST) instead of waiting again.
                                    bool snapExists = false;
                                    string? lockState = null;

                                    try
                                    {
                                        snapExists = await _proxmoxSnapshotsService.SnapshotExistsAsync(
                                            cluster!, vm.HostName, vm.HostAddress, vm.Id, proxmoxSnapshotName, token);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogDebug(ex, "Post-check SnapshotExistsAsync failed for VM {VmId}", vm.Id);
                                    }

                                    try
                                    {
                                        lockState = await _proxmoxService.GetVmLockAsync(
                                            cluster!, vm.HostName, vm.HostAddress, vm.Id, token);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogDebug(ex, "Post-check GetVmLockAsync failed for VM {VmId}", vm.Id);
                                    }

                                    if (snapExists)
                                    {
                                        // Task wait timed out/not-ok, but snapshot exists => treat as success with warning
                                        MarkVmWarning(vm.Id);
                                        await _jobs.LogVmAsync(vmRows[vm.Id],
                                            "Snapshot wait timed out/not-OK, but snapshot exists (treating as success).",
                                            "Warning", token);

                                        ok = true;
                                    }
                                    else if (!string.IsNullOrWhiteSpace(lockState))
                                    {
                                        // Unknown/transient-ish state (still locked) => warning, keep ok=false
                                        MarkVmWarning(vm.Id);
                                        await _jobs.LogVmAsync(vmRows[vm.Id],
                                            $"Snapshot wait timed out/not-OK and VM is still locked ({lockState}).",
                                            "Warning", token);

                                        ok = false;
                                    }
                                    else
                                    {
                                        // Real failure: no snapshot, no lock
                                        await _jobs.LogVmAsync(vmRows[vm.Id],
                                            "Snapshot failed: task not OK / timed out (snapshot not found).",
                                            "Error", token);

                                        ok = false;
                                    }
                                }

                                if (ok)
                                {
                                    completedSnapshotVmIds.TryAdd(vm.Id, 1);
                                    proxmoxSnapshotNames[vm.Id] = proxmoxSnapshotName;

                                    await _jobs.MarkVmSnapshotTakenAsync(vmRows[vm.Id], token);
                                    await _jobs.LogVmAsync(vmRows[vm.Id], "Snapshot completed", "Info", token);
                                }
                            }
                            catch (Exception ex)
                            {
                                await _jobs.LogVmAsync(vmRows[vm.Id], $"Snapshot failed: {ex.Message}", "Error", token);
                                _logger.LogWarning(ex, "Snapshot create/wait failed for VM {VmId}", vm.Id);
                                MarkVmWarning(vm.Id);
                            }
                        });

                    await _jobs.UpdateJobStatusAsync(jobId, "Proxmox snapshot phase completed", null, ct);
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
                     await FailAndThrowAsync(jobId, proxmoxStorageId, label, snapshotResult.ErrorMessage, scheduleId, ct);

                await _jobs.UpdateJobStatusAsync(jobId, "NetApp snapshot created", null, ct);
                createdSnapshotName = snapshotResult.SnapshotName;

                foreach (var vm in vms)
                    await _jobs.LogVmAsync(vmRows[vm.Id], $"Storage snapshot created: {snapshotResult.SnapshotName}", "Info", ct);

                // 5b) Persist BackupRecord(s)
                foreach (var vm in vms)
                {
                    var cfg = await _proxmoxService.GetVmConfigAsync(cluster!, vm.HostName, vm.Id, ct);

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

                        if (vmHadWarnings.ContainsKey(vm.Id))
                            await _jobs.MarkVmWarningAsync(vmRows[vm.Id], "Completed with warnings", ct);
                        else
                            await _jobs.MarkVmSuccessAsync(vmRows[vm.Id], backupRecordId: null, ct);

                        await _jobs.LogVmAsync(vmRows[vm.Id], "BackupRecord stored", "Info", ct);
                    }
                    catch (Exception ex)
                    {
                        await _jobs.MarkVmFailureAsync(vmRows[vm.Id], $"Failed to persist BackupRecord: {ex.Message}", ct);
                        MarkVmWarning(vm.Id);
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

                // 5c) Cleanup Proxmox snapshots
                if (useProxmoxSnapshot)
                {
                    var cleanupMap = proxmoxSnapshotNames.ToDictionary(kv => kv.Key, kv => kv.Value);

                    if (cleanupMap.Count == 0)
                    {
                        _logger.LogWarning("No Proxmox snapshots eligible for cleanup (none confirmed completed). Job {JobId}", jobId);
                        proxSnapCleanup = true;
                    }
                    else
                    {
                        using var cleanupTokenSource = new CancellationTokenSource(TimeSpan.FromMinutes(_throttles.CurrentValue.CleanupBudgetMinutes));
                        var cleanupToken = cleanupTokenSource.Token;

                        await _jobs.UpdateJobStatusAsync(jobId, "Cleaning up Proxmox snapshots", null, CancellationToken.None);

                        var deleted = await CleanupProxmoxSnapshotsWithResultsAsync(
                            cluster!,
                            vms,
                            cleanupMap,
                            cleanupToken,
                            maxDegreeOfParallelism: _throttles.CurrentValue.MaxParallelProxmoxSnapshotDeletes,
                            delayBetweenDeletesMs: _throttles.CurrentValue.DelayBetweenDeletesMs,
                            isTransient: IsTransientProxmoxSnapshotError,
                            retry: (Func<Func<Task<bool>>, CancellationToken, Task<bool>>)(async (op, tok) =>
                                await RetryAsync(op, IsTransientProxmoxSnapshotError, tok)));

                        proxSnapCleanup = true;

                        _logger.LogInformation("Proxmox snapshot cleanup done. Requested={Requested} Deleted={Deleted} Job={JobId}",
                            cleanupMap.Count, deleted.Count, jobId);

                        foreach (var vm in vms)
                        {
                            if (deleted.TryGetValue(vm.Id, out var snapName))
                                await _jobs.LogVmAsync(vmRows[vm.Id], $"Proxmox snapshot deleted: {snapName}", "Info", ct);
                            else if (cleanupMap.ContainsKey(vm.Id))
                            {
                                await _jobs.LogVmAsync(vmRows[vm.Id], "Proxmox snapshot delete failed (see logs)", "Warning", ct);
                                MarkVmWarning(vm.Id);
                            }
                        }
                    }
                }

                // 5d) Index storage disks — unchanged
                try
                {
                    var anyVm = vms.FirstOrDefault();
                    var nodeName = anyVm?.HostName ?? cluster.Hosts.First().Hostname;

                    var disks = await _proxmoxService.GetStorageDisksAsync(cluster, nodeName, proxmoxStorageId, ct);
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
                    _logger.LogWarning(ex, "Failed to index Proxmox storage disks for job {JobId} on storage {Storage}.", jobId, proxmoxStorageId);
                }

                // 6) Replicate — unchanged
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

                                    await db.BackupRecords
                                        .Where(br => br.JobId == jobId)
                                        .ExecuteUpdateAsync(s => s.SetProperty(b => b.ReplicateToSecondary, true), ct);

                                    await _jobs.UpdateJobStatusAsync(jobId, "Replication completed", null, ct);

                                    foreach (var vm in vms)
                                        await _jobs.LogVmAsync(vmRows[vm.Id], $"Replicated to secondary ({secondaryVolume})", "Info", ct);
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

                // 7) Cancellation check (MAIN DB) — keep, but don’t prevent cleanup
                using (var db = await _dbf.CreateDbContextAsync(ct))
                {
                    var jobRow = await db.Jobs.FindAsync(new object?[] { jobId }, ct);
                    if (string.Equals(jobRow?.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
                         await FailAndThrowAsync(jobId, proxmoxStorageId, label, "Job was cancelled.", scheduleId, ct);
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
                // IMPORTANT: cancellation should NOT prevent final state + completedAt from being written.
                if (jobId > 0)
                {
                    finalStatus = "Cancelled";
                    finalErrorMessage = "Job was cancelled.";

                    try
                    {
                        await _jobs.FailJobAsync(jobId, finalErrorMessage, CancellationToken.None);
                    }
                    catch (Exception ex2)
                    {
                        _logger.LogWarning(ex2, "FailJobAsync failed for cancelled Job {JobId}", jobId);
                    }

                    try
                    {
                        await TryNotifyAsync(
                            jobId,
                            netappVolumeName ?? proxmoxStorageId,
                            label,
                            "Error",
                            finalErrorMessage,
                            snapshotName: createdSnapshotName,
                            totalVms: vms?.Count ?? 0,
                            skippedVms: skippedCount,
                            warnedVms: vmHadWarnings.Count,
                            scheduleId: scheduleId,
                            ct: CancellationToken.None);
                    }
                    catch (Exception ex2)
                    {
                        _logger.LogWarning(ex2, "TryNotifyAsync failed for cancelled Job {JobId}", jobId);
                    }

                    return false;
                }

                throw;
            }
            catch (Exception ex)
            {
                if (jobId > 0)
                {
                    finalStatus = "Failed";
                    finalErrorMessage = ex.Message;

                    // Mark failed (best-effort; DO NOT let this throw and skip finally)
                    try
                    {
                        await _jobs.FailJobAsync(jobId, finalErrorMessage, ct);
                    }
                    catch (Exception ex2)
                    {
                        _logger.LogWarning(ex2, "FailJobAsync failed for Job {JobId}", jobId);
                    }

                    // Notify (best-effort)
                    try
                    {
                        await TryNotifyAsync(
                            jobId,
                            netappVolumeName ?? proxmoxStorageId,
                            label,
                            "Error",
                            finalErrorMessage,
                            snapshotName: createdSnapshotName,
                            totalVms: vms?.Count ?? 0,
                            skippedVms: skippedCount,
                            warnedVms: vmHadWarnings.Count,
                            scheduleId: scheduleId,
                            ct: ct);
                    }
                    catch (Exception ex2)
                    {
                        _logger.LogWarning(ex2, "TryNotifyAsync failed for Job {JobId}", jobId);
                    }

                    return false;
                }

                _logger.LogError(ex, "StartBackupAsync failed before Job creation. storage={Storage}", storageName);
                return false;
            }
            finally
            {
                if (cluster != null && vms != null)
                {
                    // Unpause should be best-effort and ignore job cancellation
                    try
                    {
                        using var unpauseCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                        await UnpauseIfNeeded(cluster, vms, isApplicationAware, enableIoFreeze, vmsWerePaused, unpauseCts.Token);
                    }
                    catch { /* best effort */ }

                    // If we didn't complete cleanup in the main flow, attempt again with cleanup token (best-effort)
                    if (useProxmoxSnapshot && !proxSnapCleanup && proxmoxSnapshotNames.Count > 0)
                    {
                        try
                        {
                            using var cleanupCts = new CancellationTokenSource(TimeSpan.FromMinutes(_throttles.CurrentValue.CleanupBudgetMinutes));
                            var cleanupToken = cleanupCts.Token;

                            var cleanupMap = proxmoxSnapshotNames.ToDictionary(kv => kv.Key, kv => kv.Value);

                            await CleanupProxmoxSnapshotsWithResultsAsync(
                                cluster,
                                vms,
                                cleanupMap,
                                cleanupToken,
                                maxDegreeOfParallelism: _throttles.CurrentValue.MaxParallelProxmoxSnapshotDeletes,
                                delayBetweenDeletesMs: _throttles.CurrentValue.DelayBetweenDeletesMs,
                                isTransient: IsTransientProxmoxSnapshotError,
                                retry: (Func<Func<Task<bool>>, CancellationToken, Task<bool>>)(async (op, tok) =>
                                    await RetryAsync(op, IsTransientProxmoxSnapshotError, tok)));
                        }
                        catch { /* best effort */ }
                    }
                }

                // HARD FINALIZER: force CompletedAt + final Status even if cancelled/cleanup failed.
                if (jobId > 0)
                {
                    try
                    {
                        // If success path never set these, infer something safe.
                        var statusToWrite =
                            !string.IsNullOrWhiteSpace(finalStatus)
                                ? finalStatus
                                : (hadWarnings ? "Completed with warnings" : "Completed");

                        await FinalizeJobNoCancelAsync(jobId, statusToWrite, finalErrorMessage);
                    }
                    catch (Exception ex2)
                    {
                        _logger.LogError(ex2, "FinalizeJobNoCancelAsync failed for Job {JobId}", jobId);
                    }
                }
            }

        }
        

        // ---------- Helper: validate SelectedNetappVolumes row ----------
        private static (SelectedNetappVolumes? row, string? error) ValidateSelectedVolumeRow(SelectedNetappVolumes? row, string? nameForMsg)
        {
            if (row is null) return (null, null);
            if (row.Disabled == true)
            {
                var vol = string.IsNullOrWhiteSpace(nameForMsg) ? row.VolumeName : nameForMsg;
                return (null, $"Volume '{vol}' on controller #{row.NetappControllerId} is not selected.");
            }
            return (row, null);
        }

        private void LogUuidFallbackIfNeeded(string? volumeUuid, int controllerId, string storageName)
        {
            if (string.IsNullOrWhiteSpace(volumeUuid))
                _logger.LogWarning("[No UUID] Falling back to name-based NetApp ops: volume='{Storage}' controller={ControllerId}", storageName, controllerId);
        }

        private async Task<Dictionary<int, string>> CleanupProxmoxSnapshotsWithResultsAsync(
            ProxmoxCluster cluster,
            List<ProxmoxVM> vms,
            Dictionary<int, string> cleanupMap,              // vmid -> snapshotName
            CancellationToken ct,                            // GLOBAL cleanup budget token
            int maxDegreeOfParallelism,
            int delayBetweenDeletesMs,
            Func<Exception, bool> isTransient,
            Func<Func<Task<bool>>, CancellationToken, Task<bool>> retry
        )
        {
            // Tracks snapshots that were actually confirmed deleted (task finished OK)
            var deleted = new ConcurrentDictionary<int, string>();

            // Deterministic order helps debugging/log correlation
            var ordered = cleanupMap.OrderBy(kv => kv.Key).ToList();

            // Compute an absolute deadline ONCE, based on the configured cleanup budget.
            // We use it to cap *each* per-snapshot wait to the remaining time in the budget.
            // NOTE: ct will still cancel everything when the budget expires; this just avoids silly timeouts.
            var cleanupDeadlineUtc = DateTime.UtcNow.AddMinutes(_throttles.CurrentValue.CleanupBudgetMinutes);

            await Parallel.ForEachAsync(
                ordered,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxDegreeOfParallelism,
                    CancellationToken = ct // global budget cancels the whole parallel loop
                },
                async (kv, token) =>
                {
                    var vmid = kv.Key;
                    var snapName = kv.Value;

                    var vm = vms.FirstOrDefault(x => x.Id == vmid);
                    if (vm == null) return;

                    // Per-node serialization of deletes:
                    // Keeps Proxmox happier and reduces lock contention.
                    using var nodeGate = await _gateManager.AcquireDeleteAsync(vm.HostName, token);

                    try
                    {
                        // Best-effort: wait for VM to be unlocked before delete
                        // (If it stays locked, we still attempt delete; Proxmox may reject it.)
                        try
                        {
                            await WaitForVmUnlockedAsync(
                                cluster,
                                vm,
                                timeout: TimeSpan.FromMinutes(5),
                                pollInterval: TimeSpan.FromMilliseconds(750),
                                ct: token);
                        }
                        catch { /* best effort */ }

                        bool ok = await retry(async () =>
                        {
                            try
                            {
                                // 1) Issue delete => Proxmox returns a UPID (async task)
                                var delUpid = await _proxmoxSnapshotsService.DeleteSnapshotAsync(
                                    cluster, vm.HostName, vm.HostAddress, vmid, snapName, token);

                                if (string.IsNullOrWhiteSpace(delUpid))
                                {
                                    // No UPID => we can't confirm delete completion.
                                    _logger.LogWarning(
                                        "DeleteSnapshotAsync returned empty UPID for VM {VmId} snap '{Snap}' (host={Host})",
                                        vmid, snapName, vm.HostName);
                                    return false;
                                }

                                // 2) Wait for the delete task to finish.
                                // OPTION B: cap the wait by *remaining global cleanup budget*.
                                var remaining = cleanupDeadlineUtc - DateTime.UtcNow;

                                // If we're out of budget, bail out quickly.
                                // Returning false means "not deleted/confirmed".
                                if (remaining <= TimeSpan.Zero)
                                {
                                    _logger.LogWarning(
                                        "Cleanup budget exhausted before waiting for delete task. VM {VmId} snap '{Snap}' (host={Host})",
                                        vmid, snapName, vm.HostName);
                                    return false;
                                }

                                var okWait = await _proxmoxOps.WaitForTaskCompletionAsync(
                                    cluster,
                                    vm.HostName,
                                    vm.HostAddress,
                                    delUpid,
                                    timeout: remaining,
                                    logger: _logger,
                                    ct: token); // global budget token cancels polling immediately

                                if (!okWait)
                                    throw new Exception("Snapshot delete task not OK / timed out");

                                return true;
                            }
                            catch (Exception ex) when (isTransient(ex))
                            {
                                // Bubble transient errors to RetryAsync
                                throw;
                            }
                        }, token);

                        if (ok)
                            deleted[vmid] = snapName;

                        // Optional pacing between deletes (still under global token)
                        if (delayBetweenDeletesMs > 0)
                            await Task.Delay(delayBetweenDeletesMs, token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        // Global cleanup budget expired (or caller cancelled).
                        _logger.LogWarning(
                            "Cleanup cancelled (budget/caller) while deleting Proxmox snapshot '{Snap}' for VM {VmId} (host={Host})",
                            snapName, vmid, vm.HostName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Failed to delete Proxmox snapshot '{Snap}' for VM {VmId} (host={Host})",
                            snapName, vmid, vm.HostName);
                    }
                });

            return deleted.ToDictionary(kv => kv.Key, kv => kv.Value);
        }


        private async Task<bool> WaitForVmUnlockedAsync(
            ProxmoxCluster cluster,
            ProxmoxVM vm,
            TimeSpan timeout,
            TimeSpan pollInterval,
            CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();

            while (sw.Elapsed < timeout)
            {
                ct.ThrowIfCancellationRequested();

                var lockState = await _proxmoxService.GetVmLockAsync(
                    cluster,
                    vm.HostName,
                    vm.HostAddress,
                    vm.Id,
                    ct);

                if (string.IsNullOrWhiteSpace(lockState))
                    return true;

                _logger.LogDebug(
                    "VM {VmId} on {Node} locked ({Lock}); waiting…",
                    vm.Id, vm.HostName, lockState);

                await Task.Delay(pollInterval, ct);
            }

            _logger.LogWarning(
                "VM {VmId} on {Node} still locked after {Timeout}",
                vm.Id, vm.HostName, timeout);

            return false;
        }

        private async Task FailAndThrowAsync(
            int jobId,
            string storageName,
            string label,
            string error,
            int scheduleId,
            CancellationToken ct)
        {
            try { await _jobs.FailJobAsync(jobId, error, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "FailJobAsync failed for Job {JobId}", jobId); }

            try
            {
                await TryNotifyAsync(
                    jobId, storageName, label,
                    finalStatus: "Error",
                    errorOrNote: error,
                    snapshotName: null,
                    totalVms: 0, skippedVms: 0, warnedVms: 0,
                    scheduleId: scheduleId,
                    ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TryNotifyAsync failed for Job {JobId}", jobId);
            }

            throw new InvalidOperationException(error);
        }


        private async Task TryNotifyAsync(
            int jobId,
            string storageName,
            string label,
            string finalStatus,          // "Success" | "Warning" | "Error"
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

                var settings = await db.EmailSettings
                    .AsNoTracking()
                    .FirstOrDefaultAsync(e => e.Id == 1, ct);

                if (settings == null || !settings.Enabled)
                    return;

                const int ManualScheduleId = 999;
                bool isManual = scheduleId == ManualScheduleId;

                BackupSchedule? schedule = null;
                if (!isManual && scheduleId > 0)
                {
                    schedule = await db.BackupSchedules
                        .AsNoTracking()
                        .FirstOrDefaultAsync(s => s.Id == scheduleId, ct);

                    if (schedule == null || schedule.NotificationsEnabled != true)
                        return;
                }

                // Decide recipients
                string recipientsRaw;
                bool sendAllowed;

                if (isManual)
                {
                    recipientsRaw = settings.DefaultRecipients ?? string.Empty;
                    sendAllowed = finalStatus switch
                    {
                        "Success" => settings.OnBackupSuccess,
                        "Warning" => settings.OnBackupFailure,
                        "Error" => settings.OnBackupFailure,
                        _ => false
                    };
                }
                else
                {
                    recipientsRaw = !string.IsNullOrWhiteSpace(schedule!.NotificationEmails)
                        ? schedule.NotificationEmails!
                        : (settings.DefaultRecipients ?? string.Empty);

                    sendAllowed = finalStatus switch
                    {
                        "Success" => schedule.NotifyOnSuccess == true,
                        "Warning" => schedule.NotifyOnError == true,
                        "Error" => schedule.NotifyOnError == true,
                        _ => false
                    };
                }

                if (!sendAllowed)
                    return;

                var recipients = (recipientsRaw ?? string.Empty)
                    .Split(new[] { ';', ',' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (recipients.Length == 0)
                    return;

                var nowApp = _tz.ConvertUtcToApp(DateTime.UtcNow);
                var tzName = _timeZoneId;
                var schedTag = (!isManual && schedule != null) ? $" [Schedule: {schedule.Name}]" : "";

                var subject =
                    $"BareProx: Backup {finalStatus} — {storageName} ({label}){schedTag} [Job #{jobId}]";

                var html = $@"
                            <h3>BareProx Backup {finalStatus}</h3>
                            <p>
                            <b>Job:</b> #{jobId}<br/>
                            {(!isManual && schedule != null ? $"<b>Schedule:</b> {System.Net.WebUtility.HtmlEncode(schedule.Name)}<br/>" : "")}
                            <b>Storage:</b> {System.Net.WebUtility.HtmlEncode(storageName)}<br/>
                            <b>Label:</b> {System.Net.WebUtility.HtmlEncode(label)}<br/>
                            <b>Snapshot:</b> {(string.IsNullOrWhiteSpace(snapshotName) ? "-" : System.Net.WebUtility.HtmlEncode(snapshotName))}<br/>
                            <b>When ({System.Net.WebUtility.HtmlEncode(tzName)}):</b> {nowApp:yyyy-MM-dd HH:mm:ss}
                            </p>
                            
                            <table style=""border-collapse:collapse;min-width:360px"">
                            <tr><td style=""padding:4px;border:1px solid #ccc""><b>Total VMs</b></td><td style=""padding:4px;border:1px solid #ccc"">{totalVms}</td></tr>
                            <tr><td style=""padding:4px;border:1px solid #ccc""><b>Skipped</b></td><td style=""padding:4px;border:1px solid #ccc"">{skippedVms}</td></tr>
                            <tr><td style=""padding:4px;border:1px solid #ccc""><b>VMs with warnings</b></td><td style=""padding:4px;border:1px solid #ccc"">{warnedVms}</td></tr>
                            </table>
                            
                            {(string.IsNullOrWhiteSpace(errorOrNote)
                                        ? ""
                                        : $@"<p><b>Notes:</b><br/>
                            <pre style=""white-space:pre-wrap"">{System.Net.WebUtility.HtmlEncode(errorOrNote)}</pre></p>")}
                            
                            <p>— BareProx</p>";

                foreach (var r in recipients)
                    await _email.SendAsync(r, subject, html, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TryNotifyAsync failed for Job {JobId}", jobId);
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
        private async Task FinalizeJobNoCancelAsync(int jobId, string finalStatus, string? finalError)
        {
            await using var db = await _dbf.CreateDbContextAsync(CancellationToken.None);

            // Adjust DbSet name if not "Jobs"
            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, CancellationToken.None);
            if (job == null) return;

            if (job.CompletedAt == null)
                job.CompletedAt = DateTime.UtcNow;

            // Always override the "stuck" status
            job.Status = finalStatus;

            if (!string.IsNullOrWhiteSpace(finalError))
                job.ErrorMessage = finalError;

            await db.SaveChangesAsync(CancellationToken.None);
        }


    }
}
