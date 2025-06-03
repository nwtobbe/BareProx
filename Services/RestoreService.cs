using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BareProx.Data;
using BareProx.Models;
using BareProx.Services.Background;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BareProx.Services
{
    public class RestoreService : IRestoreService
    {
        private readonly ApplicationDbContext _context;
        private readonly IBackgroundServiceQueue _taskQueue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<RestoreService> _logger;

        public RestoreService(
            ApplicationDbContext context,
            IBackgroundServiceQueue taskQueue,
            IServiceScopeFactory scopeFactory,
            ILogger<RestoreService> logger)
        {
            _context = context;
            _taskQueue = taskQueue;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task<bool> RunRestoreAsync(RestoreFormViewModel model)
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
            await _context.SaveChangesAsync();
            var jobId = job.Id;

            // 2) Enqueue background work
            _taskQueue.QueueBackgroundWorkItem(async ct =>
            {
                using var scope = _scopeFactory.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var netapp = scope.ServiceProvider.GetRequiredService<INetappService>();
                var proxmox = scope.ServiceProvider.GetRequiredService<ProxmoxService>();

                // Reload job in new context
                var backgroundJob = await ctx.Jobs.FindAsync(new object[] { jobId }, ct);
                backgroundJob.Status = "Running";
                backgroundJob.StartedAt = DateTime.UtcNow;
                await ctx.SaveChangesAsync(ct);

                try
                {
                    // 3) Clone volume from snapshot
                    var cloneName = $"restore_{jobId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
                    model.CloneVolumeName = cloneName;

                    var cloneResult = await netapp.CloneVolumeFromSnapshotAsync(
                        model.VolumeName,
                        model.SnapshotName,
                        cloneName,
                        model.ControllerId);
                    if (!cloneResult.Success)
                        throw new InvalidOperationException(cloneResult.Message);

                    // 4) Copy export policy
                    var policyOk = await netapp.CopyExportPolicyAsync(
                        model.VolumeName,
                        cloneName,
                        model.ControllerId);
                    if (!policyOk)
                    {
                        await netapp.DeleteVolumeAsync(cloneName, model.ControllerId);
                        throw new InvalidOperationException("Failed to apply export policy.");
                    }

                    // 5) Ensure export path is set
                    var volInfo = await netapp.LookupVolumeAsync(cloneName, model.ControllerId);
                    if (volInfo == null)
                        throw new InvalidOperationException($"UUID not found for clone '{cloneName}'.");

                    var exportPath = $"/{cloneName}";
                    var exported = await netapp.SetVolumeExportPathAsync(
                        volInfo.Uuid,
                        exportPath,
                        model.ControllerId);
                    if (!exported)
                    {
                        await netapp.DeleteVolumeAsync(cloneName, model.ControllerId);
                        throw new InvalidOperationException($"Failed to export clone '{cloneName}'.");
                    }

                    // 6) Mount on target host
                    var mounts = await netapp.GetVolumesWithMountInfoAsync(model.ControllerId);
                    var cloneMount = mounts.FirstOrDefault(m =>
                        m.VolumeName.Equals(cloneName, StringComparison.OrdinalIgnoreCase)
                    );
                    if (cloneMount == null)
                        throw new InvalidOperationException($"Mount info not found for clone '{cloneName}'.");

                    // Load the cluster with hosts
                    var clusterInfo = await ctx.ProxmoxClusters
                        .Include(c => c.Hosts)
                        .FirstOrDefaultAsync(c => c.Hosts.Any(), ct);
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
                        exportPath);
                    if (!mountSuccess)
                    {
                        await netapp.DeleteVolumeAsync(cloneName, model.ControllerId);
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
                            int.Parse(model.VmId));

                        // Restore using original VMID on target host
                        restored = await proxmox.RestoreVmFromConfigWithOriginalIdAsync(
                            model.OriginalConfig,
                            targetHost.HostAddress,
                            int.Parse(model.VmId),
                            cloneName,
                            model.StartDisconnected);
                    }
                    else
                    {
                        // Create new VM on target host
                        restored = await proxmox.RestoreVmFromConfigAsync(
                            model.OriginalConfig,
                            targetHost.HostAddress,
                            model.NewVmName,
                            cloneName,
                            model.ControllerId,
                            model.StartDisconnected);
                    }

                    if (!restored)
                        throw new InvalidOperationException("VM restore failed.");

                    // 8) Mark success
                    backgroundJob.Status = "Completed";
                    backgroundJob.CompletedAt = DateTime.UtcNow;
                    await ctx.SaveChangesAsync(ct);
                }
                catch (Exception ex)
                {
                    // 9) Mark failure
                    backgroundJob.Status = "Failed";
                    backgroundJob.ErrorMessage = ex.Message;
                    backgroundJob.CompletedAt = DateTime.UtcNow;
                    await ctx.SaveChangesAsync(ct);
                }
            });

            return true;
        }
    }
}
