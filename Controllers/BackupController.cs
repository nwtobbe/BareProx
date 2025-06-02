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

using BareProx.Data;
using BareProx.Models;
using BareProx.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Controllers
{
    public class BackupController : Controller
    {
        private readonly ProxmoxService _proxmoxService;
        private readonly INetappService _netappService;
        private readonly ApplicationDbContext _context;
        private readonly IBackupService _backupService;
        private readonly IServiceScopeFactory _scopeFactory;

        public BackupController(ApplicationDbContext context, ProxmoxService proxmoxService, INetappService netappService, IBackupService backupService, IServiceScopeFactory scopeFactory)
        {
            _context = context;
            _proxmoxService = proxmoxService;
            _netappService = netappService;
            _backupService = backupService;
            _scopeFactory = scopeFactory;
        }

        public IActionResult Backup()
        {
            var schedules = _context.BackupSchedules.ToList(); // List all backup schedules
            return View(schedules);
        }

        [HttpGet]
        public async Task<IActionResult> CreateSchedule()
        {
            // 1) Load all clusters (with their hosts)
            var clusters = await _context.ProxmoxClusters
                                         .Include(c => c.Hosts)
                                         .ToListAsync();
            if (!clusters.Any())
                return NotFound("No Proxmox clusters configured.");

            // 2) Load all primary NetApp controllers
            var primaryControllers = await _context.NetappControllers
                                                   .Where(n => n.IsPrimary)
                                                   .ToListAsync();
            if (!primaryControllers.Any())
                return NotFound("No primary NetApp controllers configured.");

            // 3) Build a combined map: storageName → list of VMs
            var combinedStorage = new Dictionary<string, List<ProxmoxVM>>(StringComparer.OrdinalIgnoreCase);

            // 4) Build metadata: storageName → which cluster/controller first provided it
            var volumeMeta = new Dictionary<string, VolumeMeta>(StringComparer.OrdinalIgnoreCase);

            foreach (var cluster in clusters)
            {
                foreach (var controller in primaryControllers)
                {
                    var map = await _proxmoxService
                        .GetEligibleBackupStorageWithVMsAsync(cluster, controller.Id);

                    if (map == null) continue;

                    foreach (var kv in map)
                    {
                        var storageName = kv.Key;
                        var vms = kv.Value;

                        // merge VM lists
                        if (!combinedStorage.TryGetValue(storageName, out var list))
                        {
                            list = new List<ProxmoxVM>();
                            combinedStorage[storageName] = list;
                        }
                        list.AddRange(vms);

                        // record the first cluster/controller that had this storage
                        if (!volumeMeta.ContainsKey(storageName))
                        {
                            volumeMeta[storageName] = new VolumeMeta
                            {
                                ClusterId = cluster.Id,
                                ControllerId = controller.Id
                            };
                        }
                    }
                }
            }

            // 5) Compute replicable volumes (your existing logic)
            var selectedVolumes = await _context.SelectedNetappVolumes.ToListAsync();
            var relationships = await _context.SnapMirrorRelations.ToListAsync();
            var replicableVolumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rel in relationships)
            {
                var primary = selectedVolumes.FirstOrDefault(v =>
                    v.NetappControllerId == rel.SourceControllerId &&
                    v.VolumeName == rel.SourceVolume);

                var secondary = selectedVolumes.FirstOrDefault(v =>
                    v.NetappControllerId == rel.DestinationControllerId &&
                    v.VolumeName == rel.DestinationVolume);

                if (primary != null && secondary != null)
                    replicableVolumes.Add(primary.VolumeName);
            }

            // 6a) Build a HashSet of “allowed” storage names from SelectedNetappVolumes
            var selectedVolumeNames = selectedVolumes
                .Select(v => v.VolumeName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 6b) Only include those combinedStorage keys that (1) are in selectedVolumeNames
            //      and (2) actually have at least one VM attached (Value.Any())
            var storageOptions = combinedStorage
                .Where(kvp => selectedVolumeNames.Contains(kvp.Key) && kvp.Value.Any())
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => new SelectListItem { Text = kvp.Key, Value = kvp.Key })
                .ToList();

            // 6c) Build AllVms list, but only for those same “allowed” storage names:
            var allVms = combinedStorage
                .Where(kvp => selectedVolumeNames.Contains(kvp.Key) && kvp.Value.Any())
                .SelectMany(kvp => kvp.Value.Select(vmInfo =>
                    new SelectListItem
                    {
                        Text = $"{vmInfo.Name} ({kvp.Key})",
                        Value = vmInfo.Id.ToString()
                    }))
                .ToList();

            // 7) Build the view model
            var vm = new CreateScheduleRequest
            {
                VolumeMeta = volumeMeta,
                StorageOptions = storageOptions,
                AllVms = allVms,
                ExcludedVmIds = new List<string>(),
                ReplicableVolumes = replicableVolumes
            };

            return View(vm);
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateSchedule(CreateScheduleRequest model)
        {
            // 0) Conditional ModelState cleanup for Hourly vs Daily/Weekly
            if (model.SingleSchedule != null)
            {
                var prefix = nameof(model.SingleSchedule) + ".";
                if (model.SingleSchedule.Type == "Hourly")
                {
                    ModelState.Remove(prefix + nameof(model.SingleSchedule.DaysOfWeek));
                    ModelState.Remove(prefix + nameof(model.SingleSchedule.Time));
                }
                else
                {
                    ModelState.Remove(prefix + nameof(model.SingleSchedule.StartHour));
                    ModelState.Remove(prefix + nameof(model.SingleSchedule.EndHour));
                }
            }

            // 1) If validation fails, re-populate everything and return view
            if (!ModelState.IsValid || model.SingleSchedule == null)
            {
                // a) Fetch clusters + primary controllers
                var clusters = await _context.ProxmoxClusters.Include(c => c.Hosts).ToListAsync();
                var primaryControllers = await _context.NetappControllers.Where(n => n.IsPrimary).ToListAsync();

                // b) Build combinedStorage & volumeMeta
                var combinedStorage = new Dictionary<string, List<ProxmoxVM>>(StringComparer.OrdinalIgnoreCase);
                var volumeMeta = new Dictionary<string, VolumeMeta>(StringComparer.OrdinalIgnoreCase);

                foreach (var cluster in clusters)
                {
                    foreach (var ctrl in primaryControllers)
                    {
                        var map = await _proxmoxService.GetEligibleBackupStorageWithVMsAsync(cluster, ctrl.Id);
                        if (map == null) continue;

                        foreach (var kv in map)
                        {
                            if (!combinedStorage.TryGetValue(kv.Key, out var list))
                            {
                                list = new List<ProxmoxVM>();
                                combinedStorage[kv.Key] = list;
                            }
                            list.AddRange(kv.Value);

                            if (!volumeMeta.ContainsKey(kv.Key))
                            {
                                volumeMeta[kv.Key] = new VolumeMeta
                                {
                                    ClusterId = cluster.Id,
                                    ControllerId = ctrl.Id
                                };
                            }
                        }
                    }
                }

                // c) Compute replicableVolumes
                var selectedVolumes = await _context.SelectedNetappVolumes.ToListAsync();
                var relations = await _context.SnapMirrorRelations.ToListAsync();
                var replicableVolumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var rel in relations)
                {
                    var prim = selectedVolumes.FirstOrDefault(v =>
                        v.NetappControllerId == rel.SourceControllerId &&
                        v.VolumeName == rel.SourceVolume);
                    var sec = selectedVolumes.FirstOrDefault(v =>
                        v.NetappControllerId == rel.DestinationControllerId &&
                        v.VolumeName == rel.DestinationVolume);

                    if (prim != null && sec != null)
                        replicableVolumes.Add(prim.VolumeName);
                }

                // d) Flatten storageOptions & AllVms
                model.StorageOptions = combinedStorage.Keys
                    .OrderBy(s => s)
                    .Select(s => new SelectListItem { Text = s, Value = s })
                    .ToList();

                model.AllVms = combinedStorage
                    .SelectMany(kvp => kvp.Value.Select(vmInfo =>
                        new SelectListItem
                        {
                            Text = $"{vmInfo.Name} ({kvp.Key})",
                            Value = vmInfo.Id.ToString()
                        }))
                    .ToList();

                model.ReplicableVolumes = replicableVolumes;
                model.VolumeMeta = volumeMeta;

                // e) Show error banner
                TempData["ErrorMessage"] = "Please fill in all required schedule details.";
                return View(model);
            }

            // 2) At this point, the model is valid → create the schedule
            var s = model.SingleSchedule;

            var schedule = new BackupSchedule
            {
                Name = model.Name,
                StorageName = model.StorageName,
                IsEnabled = model.IsEnabled,
                Schedule = s.Type,
                Frequency = s.Type == "Hourly"
                                          ? $"{s.StartHour}-{s.EndHour}"
                                          : string.Join(",", s.DaysOfWeek),
                TimeOfDay = s.Type == "Hourly"
                                          ? (TimeSpan?)null
                                          : TimeSpan.Parse(s.Time),
                IsApplicationAware = model.IsApplicationAware,
                EnableIoFreeze = model.EnableIoFreeze,
                UseProxmoxSnapshot = model.UseProxmoxSnapshot,
                WithMemory = model.WithMemory,
                ExcludedVmIds = model.ExcludedVmIds is null
                                          ? null
                                          : string.Join(",", model.ExcludedVmIds),
                RetentionCount = s.RetentionCount,
                RetentionUnit = s.RetentionUnit,
                LastRun = null,
                ReplicateToSecondary = model.ReplicateToSecondary,
                ClusterId = model.ClusterId,
                ControllerId = model.ControllerId
            };

            _context.BackupSchedules.Add(schedule);
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = "Backup schedule created successfully!";
            return RedirectToAction("Backup");
        }



        // GET: EditSchedule
        [HttpGet]
        public async Task<IActionResult> EditSchedule(int id)
        {
            var schedule = await _context.BackupSchedules.FindAsync(id);
            if (schedule == null) return NotFound();

            // 1) Load all clusters + primary NetApp controllers
            var clusters = await _context.ProxmoxClusters.Include(c => c.Hosts).ToListAsync();
            var primaryControllers = await _context.NetappControllers.Where(n => n.IsPrimary).ToListAsync();

            // 2) Build combinedStorage & volumeMeta
            var combinedStorage = new Dictionary<string, List<ProxmoxVM>>(StringComparer.OrdinalIgnoreCase);
            var volumeMeta = new Dictionary<string, VolumeMeta>(StringComparer.OrdinalIgnoreCase);

            foreach (var cluster in clusters)
            {
                foreach (var controller in primaryControllers)
                {
                    var map = await _proxmoxService.GetEligibleBackupStorageWithVMsAsync(cluster, controller.Id);
                    if (map == null) continue;

                    foreach (var kv in map)
                    {
                        // merge VMs
                        if (!combinedStorage.TryGetValue(kv.Key, out var list))
                        {
                            list = new List<ProxmoxVM>();
                            combinedStorage[kv.Key] = list;
                        }
                        list.AddRange(kv.Value);

                        // record first meta
                        if (!volumeMeta.ContainsKey(kv.Key))
                        {
                            volumeMeta[kv.Key] = new VolumeMeta
                            {
                                ClusterId = cluster.Id,
                                ControllerId = controller.Id
                            };
                        }
                    }
                }
            }

            // 3) replicableVolumes logic (unchanged)
            var selectedVolumes = await _context.SelectedNetappVolumes.ToListAsync();
            var relationships = await _context.SnapMirrorRelations.ToListAsync();
            var replicableVolumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rel in relationships)
            {
                var primary = selectedVolumes.FirstOrDefault(v =>
                    v.NetappControllerId == rel.SourceControllerId &&
                    v.VolumeName == rel.SourceVolume);
                var secondary = selectedVolumes.FirstOrDefault(v =>
                    v.NetappControllerId == rel.DestinationControllerId &&
                    v.VolumeName == rel.DestinationVolume);

                if (primary != null && secondary != null)
                    replicableVolumes.Add(primary.VolumeName);
            }

            // 6a) Build a HashSet of “allowed” storage names from SelectedNetappVolumes
            var selectedVolumeNames = selectedVolumes
                .Select(v => v.VolumeName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 6b) Only include those combinedStorage keys that (1) are in selectedVolumeNames
            //      and (2) actually have at least one VM attached (Value.Any())
            var storageOptions = combinedStorage
                .Where(kvp => selectedVolumeNames.Contains(kvp.Key) && kvp.Value.Any())
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => new SelectListItem { Text = kvp.Key, Value = kvp.Key })
                .ToList();

            // 6c) Build AllVms list, but only for those same “allowed” storage names:
            var allVms = combinedStorage
                .Where(kvp => selectedVolumeNames.Contains(kvp.Key) && kvp.Value.Any())
                .SelectMany(kvp => kvp.Value.Select(vmInfo =>
                    new SelectListItem
                    {
                        Text = $"{vmInfo.Name} ({kvp.Key})",
                        Value = vmInfo.Id.ToString()
                    }))
                .ToList();

            // 5) Build view model
            var model = new CreateScheduleRequest
            {
                Id = schedule.Id,
                Name = schedule.Name,
                StorageName = schedule.StorageName,
                IsEnabled = schedule.IsEnabled,
                IsApplicationAware = schedule.IsApplicationAware,
                EnableIoFreeze = schedule.EnableIoFreeze,
                UseProxmoxSnapshot = schedule.UseProxmoxSnapshot,
                WithMemory = schedule.WithMemory,
                ReplicateToSecondary = schedule.ReplicateToSecondary,

                ExcludedVmIds = schedule.ExcludedVmIds?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()
                                       ?? new List<string>(),

                StorageOptions = storageOptions,
                AllVms = allVms,
                ReplicableVolumes = replicableVolumes,

                VolumeMeta = volumeMeta,
                ClusterId = volumeMeta.TryGetValue(schedule.StorageName, out var vmMeta)
                                         ? vmMeta.ClusterId
                                         : 0,
                ControllerId = volumeMeta.TryGetValue(schedule.StorageName, out vmMeta)
                                         ? vmMeta.ControllerId
                                         : 0,

                SingleSchedule = new ScheduleEntry
                {
                    Label = schedule.Name,
                    Type = schedule.Schedule,
                    DaysOfWeek = schedule.Schedule == "Hourly"
                                      ? new List<string>()
                                      : schedule.Frequency.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
                    Time = schedule.TimeOfDay?.ToString(@"hh\:mm"),
                    StartHour = schedule.Schedule == "Hourly"
                                      ? int.Parse(schedule.Frequency.Split('-')[0])
                                      : (int?)null,
                    EndHour = schedule.Schedule == "Hourly"
                                      ? int.Parse(schedule.Frequency.Split('-')[1])
                                      : (int?)null,
                    RetentionCount = schedule.RetentionCount,
                    RetentionUnit = schedule.RetentionUnit
                }
            };

            ViewData["ScheduleId"] = id;
            return View("EditSchedule", model);
        }


        // POST: EditSchedule
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditSchedule(int id, CreateScheduleRequest model)
        {
            if (!ModelState.IsValid)
                return View("EditSchedule", model);

            var schedule = await _context.BackupSchedules.FindAsync(id);
            if (schedule == null) return NotFound();

            // 1) Update core fields
            var s = model.SingleSchedule;
            schedule.Name = s.Label ?? schedule.Name;
            schedule.StorageName = model.StorageName;
            schedule.IsEnabled = model.IsEnabled;
            schedule.Schedule = s.Type;
            schedule.Frequency = s.Type == "Hourly"
                                           ? $"{s.StartHour}-{s.EndHour}"
                                           : string.Join(",", s.DaysOfWeek);
            schedule.TimeOfDay = s.Type == "Hourly"
                                           ? null
                                           : (TimeSpan.TryParse(s.Time, out var t) ? t : (TimeSpan?)null);
            schedule.RetentionCount = s.RetentionCount;
            schedule.RetentionUnit = s.RetentionUnit;

            // 2) App-aware & snapshot options
            schedule.IsApplicationAware = model.IsApplicationAware;
            schedule.EnableIoFreeze = model.EnableIoFreeze;
            schedule.UseProxmoxSnapshot = model.UseProxmoxSnapshot;
            schedule.WithMemory = model.WithMemory;

            // 3) Replication flag & excluded VMs
            schedule.ReplicateToSecondary = model.ReplicateToSecondary;
            schedule.ExcludedVmIds = model.ExcludedVmIds != null
                                                ? string.Join(",", model.ExcludedVmIds)
                                                : null;

            // 4) Persist the chosen cluster & controller
            schedule.ClusterId = model.ClusterId;
            schedule.ControllerId = model.ControllerId;

            await _context.SaveChangesAsync();
            TempData["SuccessMessage"] = "Backup schedule updated successfully.";
            return RedirectToAction("Backup");
        }


        public IActionResult DeleteSchedule(int id)
        {
            var schedule = _context.BackupSchedules.Find(id);
            if (schedule != null)
            {
                _context.BackupSchedules.Remove(schedule);
                _context.SaveChanges();
            }

            return RedirectToAction("Backup");
        }


        [HttpPost]
        public IActionResult StartBackupNow([FromForm] BackupRequest request)
        {
            if (string.IsNullOrEmpty(request.StorageName) || request.RetentionCount < 1 || request.RetentionCount > 999)
                return BadRequest("Invalid input");

            _ = Task.Run(async () =>
            {
                using var scope = _scopeFactory.CreateScope();

                var scopedBackupService = scope.ServiceProvider.GetRequiredService<IBackupService>();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<BackupController>>();

                try
                {
                    logger.LogInformation("Starting background backup for storage {StorageName}", request.StorageName);

                    await scopedBackupService.StartBackupAsync(
                        request.StorageName,
                        request.IsApplicationAware,
                        request.Label,
                        request.ClusterId,
                        request.ControllerId,
                        request.RetentionCount,
                        request.RetentionUnit,
                        request.EnableIoFreeze,
                        request.UseProxmoxSnapshot,
                        request.WithMemory,
                        request.DontTrySuspend,
                        request.ScheduleID,
                        request.ReplicateToSecondary

                    );

                    logger.LogInformation("Backup process completed for {StorageName}", request.StorageName);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Backup failed for {StorageName}: {Message}", request.StorageName, ex.Message);
                }
            });

            TempData["SuccessMessage"] = $"Backup for '{request.StorageName}' started in the background.";
            return RedirectToAction("ListVMs", "Proxmox");
        }
    }
}
