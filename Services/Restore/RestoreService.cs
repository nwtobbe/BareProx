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

using System.Text.Json;
using BareProx.Data;
using BareProx.Models;
using BareProx.Services.Background;
using Microsoft.EntityFrameworkCore;
using BareProx.Services.Netapp;

namespace BareProx.Services.Restore
{
    public class RestoreService : IRestoreService
    {
        private readonly ApplicationDbContext _context;
        private readonly IBackgroundServiceQueue _taskQueue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<RestoreService> _logger;
        private readonly INetappVolumeService _netappVolumeService;

        public RestoreService(
            ApplicationDbContext context,
            IBackgroundServiceQueue taskQueue,
            IServiceScopeFactory scopeFactory,
            ILogger<RestoreService> logger,
            INetappVolumeService netappVolumeService)
        {
            _context = context;
            _taskQueue = taskQueue;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _netappVolumeService = netappVolumeService;
        }

        public async Task<bool> RunRestoreAsync(RestoreFormViewModel model, CancellationToken ct)
        {
            // 1) Create job record (Queued)
            var job = new Job
            {
                Type = "Restore",
                Status = "Queued",
                RelatedVm = model.VmName,
                PayloadJson = JsonSerializer.Serialize(model),
                StartedAt = DateTime.UtcNow
            };
            _context.Jobs.Add(job);
            await _context.SaveChangesAsync(ct);
            var jobId = job.Id;

            // 2) Enqueue background work
            _taskQueue.QueueBackgroundWorkItem(async backgroundCt =>
            {
                using var scope = _scopeFactory.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var netappflexclone = scope.ServiceProvider.GetRequiredService<INetappFlexCloneService>();
                var proxmox = scope.ServiceProvider.GetRequiredService<ProxmoxService>();
                var netappVolumeSvc = scope.ServiceProvider.GetRequiredService<INetappVolumeService>();
                var netappExportNfs = scope.ServiceProvider.GetRequiredService<INetappExportNFSService>();

                // Reload job in this scope
                var backgroundJob = await ctx.Jobs.FindAsync(new object[] { jobId }, backgroundCt);
                if (backgroundJob == null) return;

                // Create per-VM result row (one VM per restore)
                int vmid = 0;
                int.TryParse(model.VmId, out vmid);
                var vmResult = new JobVmResult
                {
                    JobId = jobId,
                    VMID = vmid,
                    VmName = model.VmName ?? "",
                    HostName = model.HostAddress ?? "",
                    StorageName = model.VolumeName ?? "",
                    Status = "Running",
                    Reason = "",
                    ErrorMessage = null,
                    WasRunning = false,
                    IoFreezeAttempted = false,
                    IoFreezeSucceeded = false,
                    SnapshotRequested = false,
                    SnapshotTaken = false,
                    ProxmoxSnapshotName = null,
                    SnapshotUpid = null,
                    StartedAtUtc = DateTime.UtcNow,
                    CompletedAtUtc = null
                };
                ctx.JobVmResults.Add(vmResult);
                await ctx.SaveChangesAsync(backgroundCt);
                var vmResultId = vmResult.Id;

                await UpdateJobStatusAsync(ctx, backgroundJob, "Running", null, backgroundCt);
                await LogVmAsync(ctx, vmResultId, "Info", "Restore job started.", backgroundCt);

                try
                {
                    // 3) Clone volume from snapshot
                    var cloneName = $"restore_{jobId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
                    model.CloneVolumeName = cloneName;
                    await LogVmAsync(ctx, vmResultId, "Info", $"Cloning volume '{model.VolumeName}' from snapshot '{model.SnapshotName}' → '{cloneName}'.", backgroundCt);

                    // Determine snapshot-as-volume chain flag from the originating backup
                    bool snapChainActive = await ctx.BackupRecords
                        .Where(b => b.Id == model.BackupId)
                        .AnyAsync(b => b.SnapshotAsvolumeChain, backgroundCt);

                    var cloneResult = await netappflexclone.CloneVolumeFromSnapshotAsync(
                        model.VolumeName,
                        model.SnapshotName,
                        cloneName,
                        model.ControllerId,
                        backgroundCt);

                    if (!cloneResult.Success)
                        throw new InvalidOperationException(cloneResult.Message);

                    await LogVmAsync(ctx, vmResultId, "Info", $"Clone created: '{cloneName}'.", backgroundCt);
                    await UpdateJobStatusAsync(ctx, backgroundJob, "Cloned volume", null, backgroundCt);

                    // 4) Copy/ensure export policy
                    if (model.Target.Equals("Secondary", StringComparison.OrdinalIgnoreCase))
                    {
                        await LogVmAsync(ctx, vmResultId, "Info", "Applying export policy on secondary.", backgroundCt);

                        var snap = await ctx.NetappSnapshots
                            .AsNoTracking()
                            .FirstOrDefaultAsync(s => s.JobId == model.BackupId, backgroundCt);

                        int primaryCtrl = snap?.PrimaryControllerId
                                          ?? await ctx.SnapMirrorRelations
                                              .Where(r => r.DestinationControllerId == model.ControllerId &&
                                                          r.DestinationVolume == model.VolumeName)
                                              .Select(r => r.SourceControllerId)
                                              .FirstOrDefaultAsync(backgroundCt);

                        string primaryVol = snap?.PrimaryVolume
                                            ?? await ctx.SnapMirrorRelations
                                                .Where(r => r.DestinationControllerId == model.ControllerId &&
                                                            r.DestinationVolume == model.VolumeName)
                                                .Select(r => r.SourceVolume)
                                                .FirstOrDefaultAsync(backgroundCt);

                        var primaryPolicy = await ctx.SelectedNetappVolumes
                            .Where(v => v.NetappControllerId == primaryCtrl && v.VolumeName == primaryVol)
                            .Select(v => v.ExportPolicyName)
                            .FirstOrDefaultAsync(backgroundCt)
                            ?? throw new InvalidOperationException($"No export policy for {primaryVol}@{primaryCtrl}");

                        var svmName = await ctx.SelectedNetappVolumes
                            .Where(v => v.NetappControllerId == model.ControllerId && v.VolumeName == model.VolumeName)
                            .Select(v => v.Vserver)
                            .FirstOrDefaultAsync(backgroundCt)
                            ?? throw new InvalidOperationException("Missing SVM on secondary.");

                        await netappExportNfs.EnsureExportPolicyExistsOnSecondaryAsync(
                            exportPolicyName: primaryPolicy,
                            primaryControllerId: primaryCtrl,
                            secondaryControllerId: model.ControllerId,
                            svmName: svmName,
                            ct: backgroundCt);

                        var setOk = await netappExportNfs.SetExportPolicyAsync(
                            volumeName: cloneName,
                            exportPolicyName: primaryPolicy,
                            controllerId: model.ControllerId,
                            ct: backgroundCt);

                        if (!setOk)
                        {
                            await netappVolumeSvc.DeleteVolumeAsync(cloneName, model.ControllerId, backgroundCt);
                            throw new InvalidOperationException("Failed to set export policy on cloned volume (secondary).");
                        }
                    }
                    else
                    {
                        await LogVmAsync(ctx, vmResultId, "Info", "Copying export policy on primary.", backgroundCt);

                        var policyOk = await netappExportNfs.CopyExportPolicyAsync(
                            model.VolumeName,
                            cloneName,
                            model.ControllerId,
                            backgroundCt);

                        if (!policyOk)
                        {
                            await netappVolumeSvc.DeleteVolumeAsync(cloneName, model.ControllerId, backgroundCt);
                            throw new InvalidOperationException("Failed to apply export policy on primary clone.");
                        }
                    }

                    await UpdateJobStatusAsync(ctx, backgroundJob, "Export policy applied", null, backgroundCt);

                    // 5) Ensure export path
                    var volInfo = await netappVolumeSvc.LookupVolumeAsync(cloneName, model.ControllerId, backgroundCt)
                                ?? throw new InvalidOperationException($"UUID not found for clone '{cloneName}'.");

                    var exportPath = $"/{cloneName}";
                    await LogVmAsync(ctx, vmResultId, "Info", $"Setting export path '{exportPath}'.", backgroundCt);

                    var exported = await netappExportNfs.SetVolumeExportPathAsync(
                        volInfo.Uuid,
                        exportPath,
                        model.ControllerId,
                        backgroundCt);

                    if (!exported)
                    {
                        await netappVolumeSvc.DeleteVolumeAsync(cloneName, model.ControllerId, backgroundCt);
                        throw new InvalidOperationException($"Failed to export clone '{cloneName}'.");
                    }

                    await UpdateJobStatusAsync(ctx, backgroundJob, "Export path set", null, backgroundCt);

                    // 6) Mount on target host
                    var mounts = await netappVolumeSvc.GetVolumesWithMountInfoAsync(model.ControllerId, backgroundCt);
                    var cloneMount = mounts.FirstOrDefault(m =>
                        m.VolumeName.Equals(cloneName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Mount info not found for clone '{cloneName}'.");

                    var clusterInfo = await ctx.ProxmoxClusters
                        .Include(c => c.Hosts)
                        .FirstOrDefaultAsync(c => c.Hosts.Any(), backgroundCt)
                        ?? throw new InvalidOperationException("Proxmox cluster not found.");

                    var origHost = clusterInfo.Hosts
                        .FirstOrDefault(h => h.HostAddress == model.OriginalHostAddress);

                    var targetHost = clusterInfo.Hosts
                        .FirstOrDefault(h => h.HostAddress == model.HostAddress)
                        ?? throw new InvalidOperationException("Selected target host not found in cluster.");

                    await LogVmAsync(ctx, vmResultId, "Info", $"Mounting NFS on host '{targetHost.Hostname}' ({targetHost.HostAddress}).", backgroundCt);

                    var mountSuccess = await proxmox.MountNfsStorageViaApiAsync(
                        clusterInfo,
                        targetHost.Hostname!,
                        cloneName,
                        cloneMount.MountIp,
                        exportPath,
                        snapChainActive);

                    if (!mountSuccess)
                    {
                        await netappVolumeSvc.DeleteVolumeAsync(cloneName, model.ControllerId, backgroundCt);
                        throw new InvalidOperationException("Failed to mount clone on target host.");
                    }

                    await UpdateJobStatusAsync(ctx, backgroundJob, "Mounted on target host", null, backgroundCt);

                    // 7) Perform restore
                    await LogVmAsync(ctx, vmResultId, "Info", $"Starting restore to host '{targetHost.Hostname}' (type: {model.RestoreType}).", backgroundCt);

                    bool restored;
                    if (model.RestoreType == RestoreType.ReplaceOriginal && origHost != null)
                    {
                        await LogVmAsync(ctx, vmResultId, "Info", $"Shutting down and removing original VM {model.VmId} on '{origHost.Hostname}'.", backgroundCt);

                        await proxmox.ShutdownAndRemoveVmAsync(
                            clusterInfo,
                            origHost.Hostname!,
                            int.Parse(model.VmId),
                            backgroundCt);

                        restored = await proxmox.RestoreVmFromConfigWithOriginalIdAsync(
                            model,
                            targetHost.HostAddress,
                            cloneName,
                            snapshotChainActive: snapChainActive,
                            backgroundCt);
                    }
                    else
                    {
                        restored = await proxmox.RestoreVmFromConfigAsync(
                            model,
                            targetHost.HostAddress,
                            cloneName,
                            snapshotChainActive: snapChainActive,
                            backgroundCt);
                    }

                    if (!restored)
                        throw new InvalidOperationException("VM restore failed.");

                    await LogVmAsync(ctx, vmResultId, "Info", "Restore completed.", backgroundCt);

                    // 8) Mark success (both VM row and job)
                    vmResult.Status = "Success";
                    vmResult.CompletedAtUtc = DateTime.UtcNow;
                    await ctx.SaveChangesAsync(backgroundCt);

                    backgroundJob.Status = "Completed";
                    backgroundJob.CompletedAt = DateTime.UtcNow;
                    await ctx.SaveChangesAsync(backgroundCt);
                }
                catch (Exception ex)
                {
                    // Per-VM failure log & status
                    await LogVmAsync(ctx, vmResultId, "Error", ex.Message, backgroundCt);

                    var vmRow = await ctx.JobVmResults.FindAsync(new object[] { vmResultId }, backgroundCt);
                    if (vmRow != null)
                    {
                        vmRow.Status = "Failed";
                        vmRow.ErrorMessage = ex.Message;
                        vmRow.CompletedAtUtc = DateTime.UtcNow;
                        await ctx.SaveChangesAsync(backgroundCt);
                    }

                    // Job failure
                    backgroundJob.Status = "Failed";
                    backgroundJob.ErrorMessage = ex.Message;
                    backgroundJob.CompletedAt = DateTime.UtcNow;
                    await ctx.SaveChangesAsync(backgroundCt);
                }
            });

            return true;
        }

        // ---- helpers (mirror Backup style) -----------------------------

        private static async Task UpdateJobStatusAsync(
            ApplicationDbContext ctx,
            Job job,
            string status,
            string? error,
            CancellationToken ct)
        {
            job.Status = status;
            if (!string.IsNullOrWhiteSpace(error)) job.ErrorMessage = error;
            await ctx.SaveChangesAsync(ct);
        }

        private static async Task LogVmAsync(
            ApplicationDbContext ctx,
            int jobVmResultId,
            string level,
            string message,
            CancellationToken ct)
        {
            ctx.JobVmLogs.Add(new JobVmLog
            {
                JobVmResultId = jobVmResultId,
                Level = level,
                Message = message,
                TimestampUtc = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync(ct);
        }
    }
}
