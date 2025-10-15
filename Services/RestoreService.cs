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


namespace BareProx.Services
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
            // 1) Create job record
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
                var netapp = scope.ServiceProvider.GetRequiredService<INetappService>();
                var proxmox = scope.ServiceProvider.GetRequiredService<ProxmoxService>();
                var netappVolumeSvc = scope.ServiceProvider.GetRequiredService<INetappVolumeService>();

                // Reload job in new context
                var backgroundJob = await ctx.Jobs.FindAsync(new object[] { jobId }, backgroundCt);
                backgroundJob.Status = "Running";
                backgroundJob.StartedAt = DateTime.UtcNow;
                await ctx.SaveChangesAsync(backgroundCt);

                try
                {
                    // 3) Clone volume from snapshot
                    var cloneName = $"restore_{jobId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
                    model.CloneVolumeName = cloneName;

                    // ✅ Derive snapshot-as-volume-chain from the backup run (JobId == BackupId)
                    bool snapChainActive = await ctx.BackupRecords
                        .Where(b => b.Id == model.BackupId)
                        .AnyAsync(b => b.SnapshotAsvolumeChain, backgroundCt);

                    var cloneResult = await netapp.CloneVolumeFromSnapshotAsync(
                        model.VolumeName,
                        model.SnapshotName,
                        cloneName,
                        model.ControllerId,
                        backgroundCt);

                    if (!cloneResult.Success)
                        throw new InvalidOperationException(cloneResult.Message);

                    // 4) Copy export policy (branch if restoring from secondary)
                    if (model.Target.Equals("Secondary", StringComparison.OrdinalIgnoreCase))
                    {
                        // a) figure out which primary controller & volume to copy from
                        var snap = await ctx.NetappSnapshots
                            .FirstOrDefaultAsync(s => s.JobId == model.BackupId, backgroundCt);

                        int primaryCtrl = snap?.PrimaryControllerId
                                           ?? (await ctx.SnapMirrorRelations
                                                .Where(r =>
                                                    r.DestinationControllerId == model.ControllerId &&
                                                    r.DestinationVolume == model.VolumeName)
                                                .Select(r => r.SourceControllerId)
                                                .FirstOrDefaultAsync(backgroundCt));

                        string primaryVol = snap?.PrimaryVolume
                                            ?? (await ctx.SnapMirrorRelations
                                                 .Where(r =>
                                                     r.DestinationControllerId == model.ControllerId &&
                                                     r.DestinationVolume == model.VolumeName)
                                                 .Select(r => r.SourceVolume)
                                                 .FirstOrDefaultAsync(backgroundCt));

                        // b) read the export‐policy name from SelectedNetappVolumes on primary
                        var primaryPolicy = await ctx.SelectedNetappVolumes
                            .Where(v => v.NetappControllerId == primaryCtrl &&
                                        v.VolumeName == primaryVol)
                            .Select(v => v.ExportPolicyName)
                            .FirstOrDefaultAsync(backgroundCt)
                            ?? throw new InvalidOperationException($"No policy for {primaryVol}@{primaryCtrl}");

                        // c) ensure it exists on secondary
                        await netapp.EnsureExportPolicyExistsOnSecondaryAsync(
                            exportPolicyName: primaryPolicy,
                            primaryControllerId: primaryCtrl,
                            secondaryControllerId: model.ControllerId,
                            svmName: (await ctx.SelectedNetappVolumes
                                                       .Where(v => v.NetappControllerId == model.ControllerId &&
                                                                   v.VolumeName == model.VolumeName)
                                                       .Select(v => v.Vserver)
                                                       .FirstOrDefaultAsync(backgroundCt))
                                                    ?? throw new InvalidOperationException("Missing SVM"),
                            ct: backgroundCt);

                        // d) assign it to the new clone
                        var setOk = await netapp.SetExportPolicyAsync(
                            volumeName: cloneName,
                            exportPolicyName: primaryPolicy,
                            controllerId: model.ControllerId,
                            ct: backgroundCt);

                        if (!setOk)
                        {
                            await netappVolumeSvc.DeleteVolumeAsync(cloneName, model.ControllerId, backgroundCt);
                            throw new InvalidOperationException("Failed to set export policy on cloned volume.");
                        }
                    }
                    else
                    {
                        // Primary: just copy the policy in‐place
                        var policyOk = await netapp.CopyExportPolicyAsync(
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


                    // 5) Ensure export path is set
                    var volInfo = await netappVolumeSvc.LookupVolumeAsync(cloneName, model.ControllerId, backgroundCt);
                    if (volInfo == null)
                        throw new InvalidOperationException($"UUID not found for clone '{cloneName}'.");

                    var exportPath = $"/{cloneName}";
                    var exported = await netapp.SetVolumeExportPathAsync(
                        volInfo.Uuid,
                        exportPath,
                        model.ControllerId,
                        backgroundCt);

                    if (!exported)
                    {
                        await netappVolumeSvc.DeleteVolumeAsync(cloneName, model.ControllerId, backgroundCt);
                        throw new InvalidOperationException($"Failed to export clone '{cloneName}'.");
                    }

                    // 6) Mount on target host
                    var mounts = await netappVolumeSvc.GetVolumesWithMountInfoAsync(model.ControllerId, backgroundCt);
                    var cloneMount = mounts.FirstOrDefault(m =>
                        m.VolumeName.Equals(cloneName, StringComparison.OrdinalIgnoreCase));

                    if (cloneMount == null)
                        throw new InvalidOperationException($"Mount info not found for clone '{cloneName}'.");

                    // Load the cluster with hosts
                    var clusterInfo = await ctx.ProxmoxClusters
                        .Include(c => c.Hosts)
                        .FirstOrDefaultAsync(c => c.Hosts.Any(), backgroundCt);

                    if (clusterInfo == null)
                        throw new InvalidOperationException("Proxmox cluster not found.");

                    // Identify original and target hosts
                    var origHost = clusterInfo.Hosts
                        .FirstOrDefault(h => h.HostAddress == model.OriginalHostAddress);

                    var targetHost = clusterInfo.Hosts
                        .FirstOrDefault(h => h.HostAddress == model.HostAddress);

                    if (targetHost == null)
                        throw new InvalidOperationException("Selected target host not found in cluster.");

                    // Perform NFS mount on the target host
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

                    // 7) Perform restore
                    bool restored;
                    if (model.RestoreType == RestoreType.ReplaceOriginal && origHost != null)
                    {
                        // Remove existing VM on original host
                        await proxmox.ShutdownAndRemoveVmAsync(
                            clusterInfo,
                            origHost.Hostname!,
                            int.Parse(model.VmId),
                            backgroundCt);

                        // Restore using original VMID on target host
                        restored = await proxmox.RestoreVmFromConfigWithOriginalIdAsync(
                            model.OriginalConfig,
                            targetHost.HostAddress,
                            int.Parse(model.VmId),
                            cloneName,
                            model.StartDisconnected,
                            snapshotChainActive: snapChainActive,
                            backgroundCt);
                    }
                    else
                    {
                        // Create new VM on target host
                        restored = await proxmox.RestoreVmFromConfigAsync(
                            model.OriginalConfig,
                            targetHost.HostAddress,
                            model.NewVmName!,
                            cloneName,
                            model.ControllerId,
                            model.StartDisconnected,
                            snapshotChainActive: snapChainActive,
                            backgroundCt);
                    }

                    if (!restored)
                        throw new InvalidOperationException("VM restore failed.");

                    // 8) Mark success
                    backgroundJob.Status = "Completed";
                    backgroundJob.CompletedAt = DateTime.UtcNow;
                    await ctx.SaveChangesAsync(backgroundCt);
                }
                catch (Exception ex)
                {
                    // 9) Mark failure
                    backgroundJob.Status = "Failed";
                    backgroundJob.ErrorMessage = ex.Message;
                    backgroundJob.CompletedAt = DateTime.UtcNow;
                    await ctx.SaveChangesAsync(backgroundCt);
                }
            });

            return true;
        }
    }
}
