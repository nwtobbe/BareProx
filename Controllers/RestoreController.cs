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
using BareProx.Services;
using BareProx.Services.Backup;
using BareProx.Services.Proxmox;
using BareProx.Services.Restore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Controllers
{
    public class RestoreController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IBackupService _backupService;
        private readonly ProxmoxService _proxmoxService;
        private readonly IRestoreService _restoreService;
        private readonly IAppTimeZoneService _tz;

        public RestoreController(
            ApplicationDbContext context,
            IBackupService backupService,
            ProxmoxService proxmoxService,
            IRestoreService restoreService,
            IAppTimeZoneService tz)
        {
            _context = context;
            _backupService = backupService;
            _proxmoxService = proxmoxService;
            _restoreService = restoreService;
            _tz = tz;
        }

        public async Task<IActionResult> Index(CancellationToken ct)
        {
            // 1) All backup records (newest first)
            var backups = await _context.BackupRecords
                .AsNoTracking()
                .OrderByDescending(r => r.TimeStamp)
                .ToListAsync(ct);

            if (backups.Count == 0)
                return View(new List<RestoreVmGroupViewModel>());

            // 2) Latest NetApp snapshot row per JobId (CreatedAt desc)
            var snaps = await _context.NetappSnapshots
                .AsNoTracking()
                .ToListAsync(ct);

            var latestSnapByJob = snaps
                .GroupBy(s => s.JobId)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(s => s.CreatedAt).First()
                );

            // 3) Hostname -> Cluster mapping
            var hostRows = await _context.ProxmoxHosts
                .AsNoTracking()
                .Include(h => h.Cluster)
                .Select(h => new { h.Hostname, h.ClusterId, ClusterName = h.Cluster.Name })
                .ToListAsync(ct);

            var hostToCluster = hostRows
                .GroupBy(h => h.Hostname, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => new { ClusterId = g.First().ClusterId, ClusterName = g.First().ClusterName },
                    StringComparer.OrdinalIgnoreCase
                );

            // 4) Flatten for view
            var flat = new List<RestoreViewModel>(backups.Count);
            foreach (var r in backups)
            {
                latestSnapByJob.TryGetValue(r.JobId, out var snap);

                var clusterInfo = hostToCluster.TryGetValue(r.HostName, out var hc)
                    ? hc
                    : new { ClusterId = 0, ClusterName = "(Unknown cluster)" };

                flat.Add(new RestoreViewModel
                {
                    BackupId = r.Id,
                    JobId = r.JobId,
                    VmName = r.VmName,
                    VmId = r.VMID.ToString(),
                    SnapshotName = r.SnapshotName,
                    VolumeName = r.StorageName,
                    StorageName = r.StorageName,
                    ClusterName = clusterInfo.ClusterName,
                    ClusterId = clusterInfo.ClusterId,
                    TimeStamp = _tz.ConvertUtcToApp(r.TimeStamp),
                    IsOnPrimary = snap?.ExistsOnPrimary ?? false,
                    PrimaryControllerId = snap?.PrimaryControllerId ?? 0,
                    IsOnSecondary = snap?.ExistsOnSecondary ?? false,
                    SecondaryControllerId = snap?.SecondaryControllerId
                });
            }

            // 5) Group by Cluster -> VM
            var grouped = flat
                .GroupBy(x => new { x.ClusterId, x.ClusterName, x.VmId, x.VmName })
                .Select(g => new RestoreVmGroupViewModel
                {
                    VmId = g.Key.VmId,
                    VmName = g.Key.VmName,
                    ClusterId = g.Key.ClusterId,
                    ClusterName = g.Key.ClusterName,
                    RestorePoints = g.OrderByDescending(x => x.TimeStamp).ToList()
                })
                .OrderBy(g => g.ClusterName)
                .ThenBy(g => g.VmName)
                .ToList();

            return View(grouped);
        }

        public async Task<IActionResult> Restore(
            int backupId,
            int clusterId,
            int controllerId,
            string target,
            CancellationToken ct)
        {
            var record = await _context.BackupRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == backupId, ct);
            if (record == null) return NotFound();

            var cluster = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync(c => c.Id == clusterId, ct);
            if (cluster == null)
                return StatusCode(500, "Cluster not configured");

            // latest snapshot for this job
            var snap = await _context.NetappSnapshots
                .AsNoTracking()
                .Where(s => s.JobId == record.JobId)
                .OrderByDescending(s => s.CreatedAt)
                .FirstOrDefaultAsync(ct);

            // We’ll need enabled SelectedNetappVolumes for lookups/fallbacks
            var selectedVolumes = await _context.SelectedNetappVolumes
                .AsNoTracking()
                .Where(v => v.Disabled != true)
                .ToListAsync(ct);

            // Helper: resolve volume + controller for target preference
            (string volume, int resolvedControllerId) ResolveSource()
            {
                var wantSecondary = string.Equals(target, "Secondary", StringComparison.OrdinalIgnoreCase);

                if (wantSecondary)
                {
                    // Prefer NetappSnapshots info if present & exists
                    if (snap?.ExistsOnSecondary == true &&
                        snap.SecondaryControllerId.HasValue &&
                        !string.IsNullOrWhiteSpace(snap.SecondaryVolume))
                    {
                        return (snap.SecondaryVolume!, snap.SecondaryControllerId!.Value);
                    }

                    // Fallback: SnapMirror relation for this primary volume
                    var rel = _context.SnapMirrorRelations
                        .AsNoTracking()
                        .FirstOrDefault(r =>
                            r.SourceVolume == record.StorageName);

                    if (rel != null)
                    {
                        // use destination end (if enabled)
                        var enabled = selectedVolumes.Any(v =>
                            v.NetappControllerId == rel.DestinationControllerId &&
                            v.VolumeName == rel.DestinationVolume);

                        if (enabled)
                            return (rel.DestinationVolume, rel.DestinationControllerId);
                    }
                }
                else
                {
                    // PRIMARY target
                    if (snap?.ExistsOnPrimary == true &&
                        snap.PrimaryControllerId > 0 &&
                        !string.IsNullOrWhiteSpace(record.StorageName))
                    {
                        return (record.StorageName, snap.PrimaryControllerId);
                    }

                    // Fallback: SelectedNetappVolumes row for this volume on a primary controller
                    var sv = selectedVolumes.FirstOrDefault(v =>
                        v.VolumeName == record.StorageName);

                    if (sv != null)
                    {
                        return (record.StorageName, sv.NetappControllerId);
                    }
                }

                // Final fallback: original volume + incoming controllerId (if >0) else 0
                return (record.StorageName, controllerId > 0 ? controllerId : 0);
            }

            var (actualVolume, resolvedControllerId) = ResolveSource();

            // If we still lack a controller, try any enabled row for the chosen volume
            if (resolvedControllerId == 0)
            {
                resolvedControllerId = selectedVolumes
                    .FirstOrDefault(v => v.VolumeName == actualVolume)?.NetappControllerId ?? 0;
            }

            var originalHost = cluster.Hosts
                .FirstOrDefault(h => string.Equals(h.Hostname, record.HostName, StringComparison.OrdinalIgnoreCase));

            var vm = new RestoreFormViewModel
            {
                BackupId = record.Id,
                ClusterId = clusterId,
                ControllerId = resolvedControllerId > 0 ? resolvedControllerId : controllerId, // prefer resolved
                Target = target,
                VmId = record.VMID.ToString(),
                VmName = record.VmName,
                SnapshotName = record.SnapshotName,
                VolumeName = actualVolume,
                OriginalConfig = record.ConfigurationJson,
                CloneVolumeName = $"clone_{record.VMID}_{_tz.ConvertUtcToApp(DateTime.UtcNow):yyyy-MM-dd-HH-mm}",
                StartDisconnected = false,
                OriginalHostAddress = originalHost?.HostAddress,
                OriginalHostName = originalHost?.Hostname,
                UsedProxmoxSnapshot = record.UseProxmoxSnapshot,
                VmState = record.WithMemory
            };

            vm.HostOptions = cluster.Hosts
                .Select(h => new SelectListItem
                {
                    Value = h.HostAddress,
                    Text = $"{h.Hostname} ({h.HostAddress})"
                })
                .ToList();

            // Soft warning if chosen end isn’t actually present
            if (string.Equals(target, "Secondary", StringComparison.OrdinalIgnoreCase))
            {
                if (!(snap?.ExistsOnSecondary ?? false))
                    TempData["Warning"] = "No secondary snapshot found; using best-effort mapping via SnapMirror/SelectedVolumes.";
            }
            else
            {
                if (!(snap?.ExistsOnPrimary ?? false))
                    TempData["Warning"] = "No primary snapshot flag found; using SelectedVolumes/controller mapping.";
            }

            return View(vm);
        }

        [HttpPost]
        public async Task<IActionResult> PerformRestore(
            RestoreFormViewModel model,
            CancellationToken ct)
        {
            var backup = await _context.BackupRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == model.BackupId, ct);
            if (backup == null)
                return RedirectToAction(nameof(Index));

            // Load the selected cluster explicitly
            var cluster = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync(c => c.Id == model.ClusterId, ct);

            if (cluster == null)
            {
                TempData["Error"] = "Cluster not configured";
                return RedirectToAction(nameof(Index));
            }

            var origHost = cluster.Hosts
                .FirstOrDefault(h => h.HostAddress == model.OriginalHostAddress);

            bool origExists = false;
            if (origHost != null && int.TryParse(model.VmId, out var vmIdParsed))
            {
                origExists = await _proxmoxService.CheckIfVmExistsAsync(
                    cluster,
                    origHost,
                    vmIdParsed,
                    ct);
            }

            if (model.RestoreType == RestoreType.ReplaceOriginal && !origExists)
                model.RestoreType = RestoreType.CreateNew;

            var targetHost = cluster.Hosts
                .FirstOrDefault(h => h.HostAddress == model.HostAddress);

            if (targetHost == null)
            {
                ModelState.AddModelError(nameof(model.HostAddress), "Select a valid host");
                model.HostOptions = cluster.Hosts
                    .Select(h => new SelectListItem
                    {
                        Value = h.HostAddress,
                        Text = $"{h.Hostname} ({h.HostAddress})"
                    })
                    .ToList();

                return View("Restore", model);
            }

            if (model.RestoreType == RestoreType.CreateNew &&
                string.IsNullOrWhiteSpace(model.NewVmName))
            {
                ModelState.AddModelError(nameof(model.NewVmName), "Enter a name for the new VM");
                model.HostOptions = cluster.Hosts
                    .Select(h => new SelectListItem
                    {
                        Value = h.HostAddress,
                        Text = $"{h.Hostname} ({h.HostAddress})"
                    })
                    .ToList();

                return View("Restore", model);
            }

            var success = await _restoreService.RunRestoreAsync(model, ct);
            TempData["Message"] = success ? "Restore queued" : "Restore failed";
            return RedirectToAction(nameof(Index));
        }
    }
}
