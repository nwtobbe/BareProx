using BareProx.Data;
using BareProx.Models;
using BareProx.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
using System.Linq;
using System;

namespace BareProx.Controllers
{
    public class RestoreController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IBackupService _backupService;
        private readonly INetappService _netappService;
        private readonly ProxmoxService _proxmoxService;
        private readonly IRestoreService _restoreService;
        private readonly IAppTimeZoneService _tz;

        public RestoreController(
            ApplicationDbContext context,
            IBackupService backupService,
            INetappService netappService,
            ProxmoxService proxmoxService,
            IRestoreService restoreService,
            IAppTimeZoneService tz)
        {
            _context = context;
            _backupService = backupService;
            _netappService = netappService;
            _proxmoxService = proxmoxService;
            _restoreService = restoreService;
            _tz = tz;
        }

        public async Task<IActionResult> Index()
        {
            // 1) Load all backup‐points
            var backups = await _context.BackupRecords
                                        .OrderByDescending(r => r.TimeStamp)
                                        .ToListAsync();

            // 2) Load all snapshots, keyed by their JobId
            var snapsByJob = await _context.NetappSnapshots
                .ToDictionaryAsync(s => s.JobId);

            // 3) Get the Proxmox cluster info
            var proxCluster = await _context.ProxmoxClusters
                .Select(c => new { c.Id, c.Name })
                .FirstOrDefaultAsync();
            if (proxCluster == null)
                return StatusCode(500, "Cluster not configured");

            // 4) Build your list
            var vmList = backups.Select(r =>
            {
                // find the snapshot row for this record’s JobId (if any)
                snapsByJob.TryGetValue(r.JobId, out var snap);

                return new RestoreViewModel
                {
                    BackupId = r.Id,
                    JobId = r.JobId,                   // carry it if you need it
                    VmName = r.VmName.ToString(),
                    VmId = r.VMID.ToString(),
                    SnapshotName = r.SnapshotName,
                    VolumeName = r.StorageName,
                    StorageName = r.StorageName,
                    ClusterName = proxCluster.Name,
                    ClusterId = proxCluster.Id,
                    TimeStamp = _tz.ConvertUtcToApp(r.TimeStamp),

                    // these come straight from that one NetappSnapshot row
                    IsOnPrimary = snap?.ExistsOnPrimary ?? false,
                    PrimaryControllerId = snap?.PrimaryControllerId ?? 0,
                    IsOnSecondary = snap?.ExistsOnSecondary ?? false,
                    SecondaryControllerId = snap?.SecondaryControllerId
                };
            })
            .ToList();

            return View(vmList);
        }




        public async Task<IActionResult> Restore(int backupId, int clusterId, int controllerId, string target)
        {
            var record = await _context.BackupRecords.FindAsync(backupId);
            if (record == null) return NotFound();

            var cluster = await _context.ProxmoxClusters
            .Include(c => c.Hosts)
            .FirstOrDefaultAsync(c => c.Id == clusterId);
            if (cluster == null) return StatusCode(500, "Cluster not configured");

            var originalHost = cluster.Hosts.FirstOrDefault(h => h.Hostname == record.HostName);

            var vm = new RestoreFormViewModel
            {
                BackupId = record.Id,
                ClusterId = clusterId,
                ControllerId = controllerId,
                Target = target,
                VmId = record.VMID.ToString(),
                VmName = record.VmName.ToString(),
                SnapshotName = record.SnapshotName,
                VolumeName = record.StorageName,
                OriginalConfig = record.ConfigurationJson,
                CloneVolumeName = $"clone_{record.VMID}_{_tz.ConvertUtcToApp(DateTime.UtcNow):yyyy-MM-dd-HH-mm}",
                StartDisconnected = false,
                OriginalHostAddress = originalHost?.HostAddress,
                OriginalHostName = originalHost?.Hostname
            };
            vm.HostOptions = cluster.Hosts
                .Select(h => new SelectListItem { Value = h.HostAddress, Text = $"{h.Hostname} ({h.HostAddress})" })
                .ToList();

            return View(vm);
        }

        [HttpPost]
        public async Task<IActionResult> PerformRestore(RestoreFormViewModel model)
        {
            var backup = await _context.BackupRecords.FindAsync(model.BackupId);
            if (backup == null) return RedirectToAction(nameof(Index));

            var cluster = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync();
            if (cluster == null)
            {
                TempData["Error"] = "Cluster not configured";
                return RedirectToAction(nameof(Index));
            }

            var origHost = cluster.Hosts.FirstOrDefault(h => h.HostAddress == model.OriginalHostAddress);
            bool origExists = origHost != null &&
                await _proxmoxService.CheckIfVmExistsAsync(cluster, origHost, int.Parse(model.VmId));

            if (model.RestoreType == RestoreType.ReplaceOriginal && !origExists)
                model.RestoreType = RestoreType.CreateNew;

            var targetHost = cluster.Hosts.FirstOrDefault(h => h.HostAddress == model.HostAddress);
            if (targetHost == null)
            {
                ModelState.AddModelError(nameof(model.HostAddress), "Select a valid host");
                model.HostOptions = cluster.Hosts
                    .Select(h => new SelectListItem { Value = h.HostAddress, Text = $"{h.Hostname} ({h.HostAddress})" })
                    .ToList();
                return View("Restore", model);
            }

            if (model.RestoreType == RestoreType.CreateNew && string.IsNullOrWhiteSpace(model.NewVmName))
            {
                ModelState.AddModelError(nameof(model.NewVmName), "Enter a name for the new VM");
                model.HostOptions = cluster.Hosts
                    .Select(h => new SelectListItem { Value = h.HostAddress, Text = $"{h.Hostname} ({h.HostAddress})" })
                    .ToList();
                return View("Restore", model);
            }

            //model.ControllerId = backup.ControllerId;

            var success = await _restoreService.RunRestoreAsync(model);
            TempData["Message"] = success ? "Restore queued" : "Restore failed";
            return RedirectToAction(nameof(Index));
        }
    }
}
