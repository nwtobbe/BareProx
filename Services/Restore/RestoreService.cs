/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 *
 * Copyright (C) 2025-2026 Tobias Modig
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
using BareProx.Services.Background;
using BareProx.Services.Netapp;
using BareProx.Services.Notifications;
using BareProx.Services.Proxmox;
using BareProx.Services.Proxmox.Restore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace BareProx.Services.Restore
{
    public class RestoreService : IRestoreService
    {
        private readonly IDbContextFactory<ApplicationDbContext> _dbf;
        private readonly IQueryDbFactory _qdbf;
        private readonly IBackgroundServiceQueue _taskQueue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<RestoreService> _logger;
        private readonly INetappVolumeService _netappVolumeService;
        private readonly IEmailSender _email;
        private readonly IAppTimeZoneService _tz;

        public RestoreService(
            IDbContextFactory<ApplicationDbContext> dbf,
            IQueryDbFactory qdbf,
            IBackgroundServiceQueue taskQueue,
            IServiceScopeFactory scopeFactory,
            ILogger<RestoreService> logger,
            INetappVolumeService netappVolumeService,
            IEmailSender email,
            IAppTimeZoneService tz)
        {
            _dbf = dbf;
            _qdbf = qdbf;
            _taskQueue = taskQueue;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _netappVolumeService = netappVolumeService;
            _email = email;
            _tz = tz;
        }

        public async Task<bool> RunRestoreAsync(RestoreFormViewModel model, CancellationToken ct)
        {
            int jobId;

            await using (var db = await _dbf.CreateDbContextAsync(ct))
            {
                var job = new Job
                {
                    Type = "Restore",
                    Status = "Queued",
                    RelatedVm = model.VmName,
                    PayloadJson = JsonSerializer.Serialize(model),
                    StartedAt = DateTime.UtcNow
                };

                db.Jobs.Add(job);
                await db.SaveChangesAsync(ct);
                jobId = job.Id;
            }

            _taskQueue.QueueBackgroundWorkItem(async backgroundCt =>
            {
                using var scope = _scopeFactory.CreateScope();

                var ctxFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
                await using var ctx = await ctxFactory.CreateDbContextAsync(backgroundCt);
                await using var qdb = await _qdbf.CreateAsync(backgroundCt);

                var netappFlexClone = scope.ServiceProvider.GetRequiredService<INetappFlexCloneService>();
                var proxmox = scope.ServiceProvider.GetRequiredService<ProxmoxService>();
                var proxmoxRestore = scope.ServiceProvider.GetRequiredService<IProxmoxRestore>();
                var netappVolumeSvc = scope.ServiceProvider.GetRequiredService<INetappVolumeService>();
                var netappExportNfs = scope.ServiceProvider.GetRequiredService<INetappExportNFSService>();

                var backgroundJob = await ctx.Jobs.FindAsync(new object[] { jobId }, backgroundCt);
                if (backgroundJob == null)
                    return;

                int.TryParse(model.VmId, out var vmid);

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

                string? cloneName = null;
                bool restoreCompleted = false;
                bool cloneCleanupAttempted = false;

                try
                {
                    var backupMeta = await ctx.BackupRecords
                        .AsNoTracking()
                        .Where(b => b.Id == model.BackupId)
                        .Select(b => new
                        {
                            b.JobId,
                            b.SnapshotAsvolumeChain,
                            b.StorageName
                        })
                        .FirstOrDefaultAsync(backgroundCt);

                    if (backupMeta == null)
                        throw new InvalidOperationException($"Backup record #{model.BackupId} not found.");

                    cloneName = $"restore_{jobId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
                    model.CloneVolumeName = cloneName;

                    await LogVmAsync(
                        ctx,
                        vmResultId,
                        "Info",
                        $"Cloning volume '{model.VolumeName}' from snapshot '{model.SnapshotName}' → '{cloneName}'.",
                        backgroundCt);

                    var snapChainActive = backupMeta.SnapshotAsvolumeChain;

                    var cloneResult = await netappFlexClone.CloneVolumeFromSnapshotAsync(
                        model.VolumeName,
                        model.SnapshotName,
                        cloneName,
                        model.ControllerId,
                        backgroundCt);

                    if (!cloneResult.Success)
                        throw new InvalidOperationException(cloneResult.Message);

                    await LogVmAsync(ctx, vmResultId, "Info", $"Clone created: '{cloneName}'.", backgroundCt);
                    await UpdateJobStatusAsync(ctx, backgroundJob, "Cloned volume", null, backgroundCt);

                    // 4) Copy or ensure export policy
                    if (string.Equals(model.Target, "Secondary", StringComparison.OrdinalIgnoreCase))
                    {
                        await LogVmAsync(ctx, vmResultId, "Info", "Applying export policy on secondary.", backgroundCt);

                        var snap = await qdb.NetappSnapshots
                            .AsNoTracking()
                            .Where(s =>
                                s.JobId == backupMeta.JobId &&
                                s.SnapshotName == model.SnapshotName)
                            .OrderByDescending(s => s.CreatedAt)
                            .FirstOrDefaultAsync(backgroundCt);

                        var primaryCtrl = snap?.PrimaryControllerId
                            ?? await ctx.SnapMirrorRelations
                                .AsNoTracking()
                                .Where(r =>
                                    r.DestinationControllerId == model.ControllerId &&
                                    r.DestinationVolume == model.VolumeName)
                                .Select(r => r.SourceControllerId)
                                .FirstOrDefaultAsync(backgroundCt);

                        var primaryVol = snap?.PrimaryVolume
                            ?? await ctx.SnapMirrorRelations
                                .AsNoTracking()
                                .Where(r =>
                                    r.DestinationControllerId == model.ControllerId &&
                                    r.DestinationVolume == model.VolumeName)
                                .Select(r => r.SourceVolume)
                                .FirstOrDefaultAsync(backgroundCt);

                        if (primaryCtrl == 0 || string.IsNullOrWhiteSpace(primaryVol))
                            throw new InvalidOperationException(
                                $"Could not resolve primary source volume for secondary restore of {model.VolumeName}@{model.ControllerId}.");

                        var primaryMeta = await ctx.SelectedNetappVolumes
                            .AsNoTracking()
                            .Where(v => v.NetappControllerId == primaryCtrl && v.VolumeName == primaryVol)
                            .Select(v => new
                            {
                                v.ExportPolicyName,
                                v.Vserver
                            })
                            .FirstOrDefaultAsync(backgroundCt)
                            ?? throw new InvalidOperationException(
                                $"No export-policy metadata for {primaryVol}@{primaryCtrl}.");

                        var primaryPolicy = primaryMeta.ExportPolicyName
                            ?? throw new InvalidOperationException(
                                $"No export policy for {primaryVol}@{primaryCtrl}.");

                        var primarySvmName = primaryMeta.Vserver
                            ?? throw new InvalidOperationException(
                                $"Missing primary SVM for {primaryVol}@{primaryCtrl}.");

                        var secondarySvmName = await ctx.SelectedNetappVolumes
                            .AsNoTracking()
                            .Where(v => v.NetappControllerId == model.ControllerId && v.VolumeName == model.VolumeName)
                            .Select(v => v.Vserver)
                            .FirstOrDefaultAsync(backgroundCt)
                            ?? throw new InvalidOperationException("Missing SVM on secondary.");

                        _logger.LogInformation(
                            "Restore secondary export-policy resolve: policy={Policy}, primary={PrimaryCtrl}/{PrimaryVol}/{PrimarySvm}, secondary={SecondaryCtrl}/{SecondaryVol}/{SecondarySvm}, clone={Clone}",
                            primaryPolicy,
                            primaryCtrl,
                            primaryVol,
                            primarySvmName,
                            model.ControllerId,
                            model.VolumeName,
                            secondarySvmName,
                            cloneName);

                        var ensured = await netappExportNfs.EnsureExportPolicyExistsOnSecondaryAsync(
                            exportPolicyName: primaryPolicy,
                            primaryControllerId: primaryCtrl,
                            secondaryControllerId: model.ControllerId,
                            primarySvmName: primarySvmName,
                            secondarySvmName: secondarySvmName,
                            ct: backgroundCt);

                        if (!ensured)
                        {
                            cloneCleanupAttempted = true;
                            await TryDeleteCloneAsync(
                                netappVolumeSvc,
                                cloneName,
                                model.ControllerId,
                                ctx,
                                vmResultId,
                                "Failed to prepare export policy on secondary. Clone delete attempted.",
                                backgroundCt);

                            throw new InvalidOperationException("Failed to prepare export policy on secondary.");
                        }

                        var setOk = await netappExportNfs.SetExportPolicyAsync(
                            volumeName: cloneName,
                            exportPolicyName: primaryPolicy,
                            controllerId: model.ControllerId,
                            ct: backgroundCt);

                        if (!setOk)
                        {
                            cloneCleanupAttempted = true;
                            await TryDeleteCloneAsync(
                                netappVolumeSvc,
                                cloneName,
                                model.ControllerId,
                                ctx,
                                vmResultId,
                                "Failed to set export policy on cloned volume (secondary). Clone delete attempted.",
                                backgroundCt);

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
                            cloneCleanupAttempted = true;
                            await TryDeleteCloneAsync(
                                netappVolumeSvc,
                                cloneName,
                                model.ControllerId,
                                ctx,
                                vmResultId,
                                "Failed to apply export policy on primary clone. Clone delete attempted.",
                                backgroundCt);

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
                        cloneCleanupAttempted = true;
                        await TryDeleteCloneAsync(
                            netappVolumeSvc,
                            cloneName,
                            model.ControllerId,
                            ctx,
                            vmResultId,
                            $"Failed to export clone '{cloneName}'. Clone delete attempted.",
                            backgroundCt);

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
                        .FirstOrDefaultAsync(c => c.Id == model.ClusterId, backgroundCt)
                        ?? throw new InvalidOperationException($"Proxmox cluster {model.ClusterId} not found.");

                    var origHost = clusterInfo.Hosts
                        .FirstOrDefault(h => h.HostAddress == model.OriginalHostAddress);

                    var targetHost = clusterInfo.Hosts
                        .FirstOrDefault(h => h.HostAddress == model.HostAddress)
                        ?? throw new InvalidOperationException("Selected target host not found in cluster.");

                    await LogVmAsync(
                        ctx,
                        vmResultId,
                        "Info",
                        $"Mounting NFS on host '{targetHost.Hostname}' ({targetHost.HostAddress}).",
                        backgroundCt);

                    var mountSuccess = await proxmox.MountNfsStorageViaApiAsync(
                        clusterInfo,
                        targetHost.Hostname!,
                        cloneName,
                        cloneMount.MountIp,
                        exportPath,
                        snapChainActive);

                    if (!mountSuccess)
                    {
                        cloneCleanupAttempted = true;
                        await TryDeleteCloneAsync(
                            netappVolumeSvc,
                            cloneName,
                            model.ControllerId,
                            ctx,
                            vmResultId,
                            "Failed to mount clone on target host. Clone delete attempted.",
                            backgroundCt);

                        throw new InvalidOperationException("Failed to mount clone on target host.");
                    }

                    await UpdateJobStatusAsync(ctx, backgroundJob, "Mounted on target host", null, backgroundCt);

                    // 7) Perform restore
                    await LogVmAsync(
                        ctx,
                        vmResultId,
                        "Info",
                        $"Starting restore to host '{targetHost.Hostname}' (type: {model.RestoreType}).",
                        backgroundCt);

                    bool restored;
                    if (model.RestoreType == RestoreType.ReplaceOriginal && origHost != null)
                    {
                        await LogVmAsync(
                            ctx,
                            vmResultId,
                            "Info",
                            $"Shutting down and removing original VM {model.VmId} on '{origHost.Hostname}'.",
                            backgroundCt);

                        await proxmox.ShutdownAndRemoveVmAsync(
                            clusterInfo,
                            origHost.Hostname!,
                            int.Parse(model.VmId),
                            backgroundCt);

                        restored = await proxmoxRestore.RestoreVmFromConfigWithOriginalIdAsync(
                            model,
                            targetHost.HostAddress,
                            cloneName,
                            snapshotChainActive: snapChainActive,
                            backgroundCt);
                    }
                    else
                    {
                        restored = await proxmoxRestore.RestoreVmFromConfigAsync(
                            model,
                            targetHost.HostAddress,
                            cloneName,
                            snapshotChainActive: snapChainActive,
                            backgroundCt);
                    }

                    if (!restored)
                        throw new InvalidOperationException("VM restore failed.");

                    await LogVmAsync(ctx, vmResultId, "Info", "Restore completed.", backgroundCt);

                    vmResult.Status = "Success";
                    vmResult.CompletedAtUtc = DateTime.UtcNow;
                    await ctx.SaveChangesAsync(backgroundCt);

                    backgroundJob.Status = "Completed";
                    backgroundJob.CompletedAt = DateTime.UtcNow;
                    await ctx.SaveChangesAsync(backgroundCt);

                    restoreCompleted = true;

                    await TryNotifyAsync(
                        jobId: jobId,
                        finalStatus: "Success",
                        vmName: model.VmName ?? "",
                        storageName: model.VolumeName ?? "",
                        snapshotName: model.SnapshotName ?? "",
                        targetHost: targetHost.Hostname ?? model.HostAddress ?? "",
                        errorOrNote: null,
                        ct: backgroundCt);
                }
                catch (Exception ex)
                {
                    if (!restoreCompleted && !cloneCleanupAttempted && !string.IsNullOrWhiteSpace(cloneName))
                    {
                        await TryDeleteCloneAsync(
                            netappVolumeSvc,
                            cloneName,
                            model.ControllerId,
                            ctx,
                            vmResultId,
                            $"Cleanup after restore failure: attempted to delete clone '{cloneName}'.",
                            backgroundCt);
                    }

                    await LogVmAsync(ctx, vmResultId, "Error", ex.Message, backgroundCt);

                    var vmRow = await ctx.JobVmResults.FindAsync(new object[] { vmResultId }, backgroundCt);
                    if (vmRow != null)
                    {
                        vmRow.Status = "Failed";
                        vmRow.ErrorMessage = ex.Message;
                        vmRow.CompletedAtUtc = DateTime.UtcNow;
                        await ctx.SaveChangesAsync(backgroundCt);
                    }

                    backgroundJob.Status = "Failed";
                    backgroundJob.ErrorMessage = ex.Message;
                    backgroundJob.CompletedAt = DateTime.UtcNow;
                    await ctx.SaveChangesAsync(backgroundCt);

                    await TryNotifyAsync(
                        jobId: jobId,
                        finalStatus: "Error",
                        vmName: model.VmName ?? "",
                        storageName: model.VolumeName ?? "",
                        snapshotName: model.SnapshotName ?? "",
                        targetHost: model.HostAddress ?? "",
                        errorOrNote: ex.Message,
                        ct: backgroundCt);
                }
            });

            return true;
        }

        private async Task TryDeleteCloneAsync(
            INetappVolumeService netappVolumeSvc,
            string? cloneName,
            int controllerId,
            ApplicationDbContext ctx,
            int jobVmResultId,
            string infoMessage,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(cloneName))
                return;

            try
            {
                await netappVolumeSvc.DeleteVolumeAsync(cloneName, controllerId, ct);
                await LogVmAsync(ctx, jobVmResultId, "Warning", infoMessage, ct);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(
                    cleanupEx,
                    "Failed to delete clone '{CloneName}' on controller {ControllerId}.",
                    cloneName,
                    controllerId);
            }
        }

        private static async Task UpdateJobStatusAsync(
            ApplicationDbContext ctx,
            Job job,
            string status,
            string? error,
            CancellationToken ct)
        {
            job.Status = status;
            if (!string.IsNullOrWhiteSpace(error))
                job.ErrorMessage = error;

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

        private async Task TryNotifyAsync(
            int jobId,
            string finalStatus,
            string vmName,
            string storageName,
            string snapshotName,
            string targetHost,
            string? errorOrNote,
            CancellationToken ct)
        {
            try
            {
                await using var db = await _dbf.CreateDbContextAsync(ct);

                var s = await db.EmailSettings
                    .AsNoTracking()
                    .FirstOrDefaultAsync(e => e.Id == 1, ct);

                if (s is null || !s.Enabled)
                    return;

                var finalRank = finalStatus == "Error" ? 3 : 1;
                var minRank = SevRank(s.MinSeverity ?? "Info");

                var send =
                    (finalStatus == "Success" && s.OnRestoreSuccess && finalRank >= minRank) ||
                    (finalStatus == "Error" && s.OnRestoreFailure && finalRank >= minRank);

                if (!send)
                    return;

                var recipients = (s.DefaultRecipients ?? "")
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

                if (recipients.Length == 0)
                    return;

                var nowApp = _tz.ConvertUtcToApp(DateTime.UtcNow);

                var subj = $"BareProx: Restore {finalStatus} — {vmName} ({storageName}:{snapshotName}) [Job #{jobId}]";
                var html = $@"
                    <h3>BareProx Restore {finalStatus}</h3>
                    <p>
                      <b>Job:</b> #{jobId}<br/>
                      <b>VM:</b> {System.Net.WebUtility.HtmlEncode(vmName)}<br/>
                      <b>Storage:</b> {System.Net.WebUtility.HtmlEncode(storageName)}<br/>
                      <b>Snapshot:</b> {System.Net.WebUtility.HtmlEncode(snapshotName)}<br/>
                      <b>Target host:</b> {System.Net.WebUtility.HtmlEncode(targetHost)}<br/>
                      <b>When (App time):</b> {nowApp:u}
                    </p>
                    {(string.IsNullOrWhiteSpace(errorOrNote)
                        ? ""
                        : $@"<p><b>Notes:</b><br/><pre style=""white-space:pre-wrap"">{System.Net.WebUtility.HtmlEncode(errorOrNote)}</pre></p>")}
                    <p>— BareProx</p>";

                foreach (var r in recipients)
                {
                    await _email.SendAsync(r, subj, html, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TryNotifyAsync (Restore) failed for Job {JobId}", jobId);
            }
        }

        private static int SevRank(string s) => s?.ToLowerInvariant() switch
        {
            "critical" => 4,
            "error" => 3,
            "warning" => 2,
            _ => 1
        };
    }
}