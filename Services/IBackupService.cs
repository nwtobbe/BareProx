using Azure.Core;
using BareProx.Data;
using BareProx.Models;
using BareProx.Repositories;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

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
            int ScheduleID);
    }

    public class BackupService : IBackupService
    {
        private readonly ApplicationDbContext _context;
        private readonly ProxmoxService _proxmoxService;
        private readonly INetappService _netAppService;
        private readonly IBackupRepository _backupRepository;
        private readonly ILogger<BackupService> _logger;

        public BackupService(
            ApplicationDbContext context,
            ProxmoxService proxmoxService,
            INetappService netAppService,
            IBackupRepository backupRepository,
            ILogger<BackupService> logger)
        {
            _context = context;
            _proxmoxService = proxmoxService;
            _netAppService = netAppService;
            _backupRepository = backupRepository;
            _logger = logger;
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
            int ScheduleID)
        {
            var job = new Job
            {
                Type = "Backup",
                Status = "Running",
                RelatedVm = storageName,
                PayloadJson = $"{{\"storageName\":\"{storageName}\",\"label\":\"{label}\"}}",
                StartedAt = DateTime.UtcNow
            };

            _context.Jobs.Add(job);
            await _context.SaveChangesAsync();

            ProxmoxCluster? cluster = null;
            List<ProxmoxVM>? vms = null;
            bool vmsWerePaused = false;
            var proxmoxSnapshotNames = new Dictionary<int, string>();

            try
            {
                var storageWithVMs = await _proxmoxService.GetFilteredStorageWithVMsAsync(clusterId, netappControllerId);
                if (!storageWithVMs.TryGetValue(storageName, out vms) || vms == null || !vms.Any())
                    return await FailJobAsync(job, $"No VMs found in storage '{storageName}'.");

                cluster = await _context.ProxmoxClusters.Include(c => c.Hosts).FirstOrDefaultAsync(c => c.Id == clusterId);
                if (cluster == null || string.IsNullOrWhiteSpace(cluster.ApiToken) || cluster.Hosts == null || !cluster.Hosts.Any())
                    return await FailJobAsync(job, "Cluster not properly configured.");

                // Pause VMs (if IO freeze requested)
                if (isApplicationAware && enableIoFreeze)
                {
                    foreach (var vm in vms)
                        await _proxmoxService.PauseVmAsync(cluster, vm.HostName, vm.HostAddress, vm.Id);

                    vmsWerePaused = true;
                    job.Status = "Paused VMs";
                    await _context.SaveChangesAsync();
                }

                // Proxmox Snapshots
                var snapshotName = $"{label}_{DateTime.UtcNow:yyyyMMddHHmmss}";
                var snapshotTasks = new Dictionary<int, string>();

                if (isApplicationAware && useProxmoxSnapshot)
                {
                    foreach (var vm in vms)
                    {
                        // Skip stopped VMs
                        var status = await _proxmoxService.GetVmStatusAsync(cluster, vm.HostName, vm.HostAddress, vm.Id);
                        if (status == "stopped")
                        {
                            _logger.LogWarning("Skipping snapshot for stopped VM {VmId}", vm.Id);
                            continue;
                        }

                        var upid = await _proxmoxService.CreateSnapshotAsync(
                            cluster,
                            vm.HostName,
                            vm.HostAddress,
                            vm.Id,
                            snapshotName,
                            "Backup created via BareProx",
                            withMemory,
                            dontTrySuspend
                        );

                        if (!string.IsNullOrWhiteSpace(upid))
                        {
                            snapshotTasks[vm.Id] = upid;
                            proxmoxSnapshotNames[vm.Id] = snapshotName; // Only track actual snapshot
                        }
                        else
                        {
                            _logger.LogWarning("Snapshot creation failed or returned no UPID for VM {VmId}", vm.Id);
                        }
                    }

                    job.Status = "Waiting for Proxmox snapshot tasks";
                    await _context.SaveChangesAsync();

                    foreach (var kvp in snapshotTasks)
                    {
                        var vmId = kvp.Key;
                        var upid = kvp.Value;
                        var vm = vms.First(v => v.Id == vmId);

                        var success = await _proxmoxService.WaitForTaskCompletionAsync(
                            cluster,
                            vm.HostName,
                            vm.HostAddress,
                            upid,
                            TimeSpan.FromMinutes(20),
                            _logger
                        );

                        if (!success)
                        {
                            _logger.LogWarning("Snapshot task for VM {VmId} failed or timed out in job {JobId}", vmId, job.Id);
                        }
                    }

                    job.Status = "Proxmox Snapshots completed";
                    await _context.SaveChangesAsync();
                }


                // Create NetApp snapshot
                var snapshotResult = await _netAppService.CreateSnapshotAsync(netappControllerId, storageName, label);
                if (!snapshotResult.Success)
                    return await FailJobAsync(job, snapshotResult.ErrorMessage);

                job.Status = "Snapshot created";
                await _context.SaveChangesAsync();

                // Check for cancellation
                await _context.Entry(job).ReloadAsync();
                if (job.Status == "Cancelled")
                    return await FailJobAsync(job, "Job was cancelled.");

                // Save backup info
                foreach (var vm in vms)
                {
                    var config = await _proxmoxService.GetVmConfigAsync(cluster, vm.HostName, vm.Id);
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
                        ScheduleId = ScheduleID
                    });
                }

                job.Status = "Completed";
                job.CompletedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                return true;
            }
            catch (Exception ex)
            {
                return await FailJobAsync(job, ex.Message);
            }
            finally
            {
                if (cluster != null && vms != null)
                {
                    await UnpauseIfNeeded(cluster, vms, isApplicationAware, enableIoFreeze, vmsWerePaused);
                    await CleanupProxmoxSnapshots(cluster, vms, proxmoxSnapshotNames, useProxmoxSnapshot);
                }
            }
        }

        private async Task UnpauseIfNeeded(ProxmoxCluster cluster, IEnumerable<ProxmoxVM> vms, bool isAppAware, bool ioFreeze, bool vmsWerePaused)
        {
            if (isAppAware && ioFreeze && vmsWerePaused)
            {
                foreach (var vm in vms)
                {
                    try
                    {
                        await _proxmoxService.UnpauseVmAsync(cluster, vm.HostName, vm.HostAddress, vm.Id);
                    }
                    catch { }
                }
            }
        }

        private async Task CleanupProxmoxSnapshots(
            ProxmoxCluster cluster,
            IEnumerable<ProxmoxVM> vms,
            Dictionary<int, string> snapshotMap,
            bool shouldCleanup)
        {
            if (!shouldCleanup) return;

            foreach (var vm in vms)
            {
                if (snapshotMap.TryGetValue(vm.Id, out var snap))
                {
                    try
                    {
                        await _proxmoxService.DeleteSnapshotAsync(cluster, vm.HostName, vm.HostAddress, vm.Id, snap);
                    }
                    catch { }
                }
            }
        }

        private async Task<bool> FailJobAsync(Job job, string message)
        {
            job.Status = "Failed";
            job.ErrorMessage = message;
            job.CompletedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return false;
        }
    }
}
