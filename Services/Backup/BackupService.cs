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
            // -------------------------
            // TUNABLE THROTTLES (start conservative for big storages)
            // -------------------------
            const int MaxParallelVmStatus = 12;                  // status reads
            const int MaxParallelProxmoxSnapshotCreates = 3;     // snapshot create+wait
            const int MaxParallelProxmoxSnapshotDeletes = 1;     // snapshot delete (slow)
            const int DelayBetweenDeletesMs = 2000;              // slow delete waves
            static TimeSpan ProxmoxSnapshotWaitTimeout() => TimeSpan.FromMinutes(30);
            static TimeSpan CleanupBudget() => TimeSpan.FromMinutes(30);

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

            // Helper: best-effort token for cleanup/unpause (ignore job cancellation)
            static CancellationToken CreateCleanupToken(TimeSpan budget, out CancellationTokenSource cts)
            {
                cts = new CancellationTokenSource(budget);
                return cts.Token;
            }

            // Helper: detect transient Proxmox snapshot errors
            static bool IsTransientProxmoxSnapshotError(Exception ex)
            {
                var msg = ex.ToString();
                return msg.Contains("got no worker upid", StringComparison.OrdinalIgnoreCase) ||
                       msg.Contains("start worker failed", StringComparison.OrdinalIgnoreCase) ||
                       msg.Contains("VM is locked (snapshot)", StringComparison.OrdinalIgnoreCase) ||
                       msg.Contains("VM is locked", StringComparison.OrdinalIgnoreCase);
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
                            return await FailAndNotifyAsync(jobId, proxmoxStorageId, label, check.error, scheduleId, ct);

                        if (check.row is null)
                            return await FailAndNotifyAsync(jobId, proxmoxStorageId, label, "SelectedNetappVolumeId not found.", scheduleId, ct);

                        var row = check.row;

                        if (!string.IsNullOrWhiteSpace(effectiveVolumeUuid) &&
                            !string.IsNullOrWhiteSpace(row.Uuid) &&
                            !string.Equals(effectiveVolumeUuid, row.Uuid, StringComparison.OrdinalIgnoreCase))
                        {
                            return await FailAndNotifyAsync(
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
                    return await FailAndNotifyAsync(jobId, proxmoxStorageId, label, $"Cluster with ID {clusterId} not found.", scheduleId, ct);

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
                    return await FailAndNotifyAsync(jobId, proxmoxStorageId, label,
                        $"No VMs found in storage '{proxmoxStorageId}'. (Tip: ensure storage name is correct and mounted on at least one online host.)",
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

                // Power status (throttled)
                var statusMap = new ConcurrentDictionary<int, string>();
                await Parallel.ForEachAsync(
                    vms,
                    new ParallelOptions { MaxDegreeOfParallelism = MaxParallelVmStatus, CancellationToken = ct },
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

                // 3) Pause (IO-freeze) — keep sequential; if one fails, continue
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

                // 4) Proxmox snapshots (optional) — COMPLETE FIX:
                // - Throttled create+wait per VM
                // - Retry transient errors
                // - One VM failure never aborts job
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
                        new ParallelOptions { MaxDegreeOfParallelism = MaxParallelProxmoxSnapshotCreates, CancellationToken = ct },
                        async (vm, token) =>
                        {
                            try
                            {
                                // Create snapshot with retry for "worker failed"/"locked"
                                var upid = await RetryAsync(
                                    async () => await _proxmoxSnapshotsService.CreateSnapshotAsync(
                                        cluster, vm.HostName, vm.HostAddress, vm.Id,
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

                                // Wait for completion (no parallel explosion — one per worker)
                                var ok = await _proxmoxOps.WaitForTaskCompletionAsync(
                                    cluster, vm.HostName, vm.HostAddress, upid,
                                    ProxmoxSnapshotWaitTimeout(),
                                    _logger, token);

                                if (ok)
                                {
                                    completedSnapshotVmIds.TryAdd(vm.Id, 1);
                                    proxmoxSnapshotNames[vm.Id] = proxmoxSnapshotName;

                                    await _jobs.MarkVmSnapshotTakenAsync(vmRows[vm.Id], token);
                                    await _jobs.LogVmAsync(vmRows[vm.Id], "Snapshot completed", "Info", token);
                                }
                                else
                                {
                                    await _jobs.LogVmAsync(vmRows[vm.Id], "Snapshot task timed out / not OK", "Warning", token);
                                    _logger.LogWarning("Snapshot task for VM {VmId} not OK/timed out in job {JobId}", vm.Id, jobId);
                                    MarkVmWarning(vm.Id);
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
                    return await FailAndNotifyAsync(jobId, proxmoxStorageId, label, snapshotResult.ErrorMessage, scheduleId, ct);

                await _jobs.UpdateJobStatusAsync(jobId, "NetApp snapshot created", null, ct);
                createdSnapshotName = snapshotResult.SnapshotName;

                foreach (var vm in vms)
                    await _jobs.LogVmAsync(vmRows[vm.Id], $"Storage snapshot created: {snapshotResult.SnapshotName}", "Info", ct);

                // 5b) Persist BackupRecord(s) — unchanged, but keep “one VM fail” as warning
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

                // 5c) Cleanup Proxmox snapshots — COMPLETE FIX:
                // - Only delete snapshots that completed OK
                // - Very low parallelism
                // - Delay between deletes
                // - Uses a cleanup token (ignores job cancellation)
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
                        using var cleanupTokenSource = new CancellationTokenSource(CleanupBudget());
                        var cleanupToken = cleanupTokenSource.Token;

                        await _jobs.UpdateJobStatusAsync(jobId, "Cleaning up Proxmox snapshots", null, ct);

                        var deleted = await CleanupProxmoxSnapshotsWithResultsAsync(
                            cluster,
                            vms,
                            cleanupMap,
                            cleanupToken,
                            maxDegreeOfParallelism: MaxParallelProxmoxSnapshotDeletes,
                            delayBetweenDeletesMs: DelayBetweenDeletesMs,
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

                    // If we didn't complete cleanup in the main flow, attempt again with cleanup token
                    if (useProxmoxSnapshot && !proxSnapCleanup && proxmoxSnapshotNames.Count > 0)
                    {
                        try
                        {
                            using var cleanupCts = new CancellationTokenSource(CleanupBudget());
                            var cleanupToken = cleanupCts.Token;

                            var cleanupMap = proxmoxSnapshotNames.ToDictionary(kv => kv.Key, kv => kv.Value);

                            await CleanupProxmoxSnapshotsWithResultsAsync(
                                cluster,
                                vms,
                                cleanupMap,
                                cleanupToken,
                                maxDegreeOfParallelism: MaxParallelProxmoxSnapshotDeletes,
                                delayBetweenDeletesMs: DelayBetweenDeletesMs,
                                isTransient: IsTransientProxmoxSnapshotError,
                                retry: (Func<Func<Task<bool>>, CancellationToken, Task<bool>>)(async (op, tok) =>
                                    await RetryAsync(op, IsTransientProxmoxSnapshotError, tok)));
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

        //private async Task CleanupProxmoxSnapshots(
        //    ProxmoxCluster cluster,
        //    IEnumerable<ProxmoxVM> vms,
        //    Dictionary<int, string> snapshotMap,
        //    bool shouldCleanup,
        //    CancellationToken ct = default)
        //{
        //    if (!shouldCleanup) return;

        //    foreach (var vm in vms)
        //    {
        //        if (snapshotMap.TryGetValue(vm.Id, out var snap) && !string.IsNullOrWhiteSpace(snap))
        //        {
        //            try { await _proxmoxSnapshotsService.DeleteSnapshotAsync(cluster, vm.HostName, vm.HostAddress, vm.Id, snap, ct); }
        //            catch { /* best effort */ }
        //        }
        //    }
        //}

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

        private async Task<Dictionary<int, string>> CleanupProxmoxSnapshotsWithResultsAsync(
     ProxmoxCluster cluster,
     List<ProxmoxVM> vms,
     Dictionary<int, string> cleanupMap,              // vmid -> snapshotName
     CancellationToken ct,
     int maxDegreeOfParallelism,
     int delayBetweenDeletesMs,
     Func<Exception, bool> isTransient,
     Func<Func<Task<bool>>, CancellationToken, Task<bool>> retry
 )
        {
            var deleted = new ConcurrentDictionary<int, string>();

            // Keep deletes in a deterministic order (optional)
            var ordered = cleanupMap.OrderBy(kv => kv.Key).ToList();

            await Parallel.ForEachAsync(
                ordered,
                new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism, CancellationToken = ct },
                async (kv, token) =>
                {
                    var vmid = kv.Key;
                    var snapName = kv.Value;

                    var vm = vms.FirstOrDefault(x => x.Id == vmid);
                    if (vm == null) return;

                    try
                    {
                        bool ok = await retry(async () =>
                        {
                            try
                            {
                                await _proxmoxSnapshotsService.DeleteSnapshotAsync(
                                    cluster, vm.HostName, vm.HostAddress, vmid, snapName, token);

                                return true;
                            }
                            catch (Exception ex) when (isTransient(ex))
                            {
                                // signal retry
                                throw;
                            }
                        }, token);

                        if (ok)
                            deleted[vmid] = snapName;

                        if (delayBetweenDeletesMs > 0)
                            await Task.Delay(delayBetweenDeletesMs, token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete Proxmox snapshot '{Snap}' for VM {VmId} (host={Host})",
                            snapName, vmid, vm.HostName);
                    }
                });

            return deleted.ToDictionary(kv => kv.Key, kv => kv.Value);
        }


    }
}
