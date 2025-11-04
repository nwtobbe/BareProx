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
using BareProx.Services.Backup;
using BareProx.Services.Proxmox;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Controllers
{
    public class BackupController : Controller
    {
        private readonly ProxmoxService _proxmoxService;
        private readonly ApplicationDbContext _context;
        private readonly IBackupService _backupService;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IProxmoxInventoryCache _invCache;

        public BackupController(
            ApplicationDbContext context,
            ProxmoxService proxmoxService,
            IBackupService backupService,
            IServiceScopeFactory scopeFactory,
            IProxmoxInventoryCache invCache)
        {
            _context = context;
            _proxmoxService = proxmoxService;
            _backupService = backupService;
            _scopeFactory = scopeFactory;
            _invCache = invCache;
        }

        public IActionResult Backup()
        {
            var schedules = _context.BackupSchedules.ToList();
            return View(schedules);
        }

        // ---------- CREATE ----------
        [HttpGet]
        public async Task<IActionResult> CreateSchedule(CancellationToken ct)
        {
            var model = new CreateScheduleRequest();

            // a) Fetch clusters + primary controllers
            var clusters = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .AsNoTracking()
                .ToListAsync(ct);

            var primaryControllers = await _context.NetappControllers
                .AsNoTracking()
                .Where(n => n.IsPrimary)
                .ToListAsync(ct);

            // b) Selected volumes: only enabled rows
            var selectedVolumes = await _context.SelectedNetappVolumes
                .AsNoTracking()
                .Where(v => v.Disabled != true)
                .ToListAsync(ct);

            // quick lookups
            var enabledKey = new HashSet<(int C, string V)>(
                selectedVolumes.Select(v => (v.NetappControllerId, v.VolumeName)),
                EqualityComparer<(int, string)>.Default);

            var combinedStorage = new Dictionary<string, List<ProxmoxVM>>(StringComparer.OrdinalIgnoreCase);
            var volumeMeta = new Dictionary<string, VolumeMeta>(StringComparer.OrdinalIgnoreCase);

            foreach (var cluster in clusters)
            {
                foreach (var ctrl in primaryControllers)
                {
                    // returns candidate storages for this controller
                    var map = await _invCache.GetEligibleBackupStorageWithVMsAsync(cluster, ctrl.Id, null, ct);
                    if (map == null) continue;

                    foreach (var kv in map)
                    {
                        var storage = kv.Key;

                        // ✅ keep only storages explicitly enabled for this controller
                        if (!enabledKey.Contains((ctrl.Id, storage))) continue;

                        if (!combinedStorage.TryGetValue(storage, out var list))
                        {
                            list = new List<ProxmoxVM>();
                            combinedStorage[storage] = list;
                        }
                        list.AddRange(kv.Value);

                        if (!volumeMeta.ContainsKey(storage))
                        {
                            var volInfo = selectedVolumes.FirstOrDefault(
                                v => v.VolumeName == storage && v.NetappControllerId == ctrl.Id);

                            volumeMeta[storage] = new VolumeMeta
                            {
                                ClusterId = cluster.Id,
                                ControllerId = ctrl.Id,
                                SnapshotLockingEnabled = volInfo?.SnapshotLockingEnabled == true,
                                SelectedNetappVolumeId = volInfo?.Id
                            };
                        }
                    }
                }
            }

            // Deduplicate VMs per storage (by Id) and sort by Name
            foreach (var key in combinedStorage.Keys.ToList())
            {
                combinedStorage[key] = combinedStorage[key]
                    .GroupBy(vm => vm.Id)
                    .Select(g => g.First())
                    .OrderBy(vm => vm.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            // c) Replicable volumes via SnapMirror (only if BOTH ends enabled)
            var relations = await _context.SnapMirrorRelations
                .AsNoTracking()
                .ToListAsync(ct);

            var replicableVolumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rel in relations)
            {
                if (enabledKey.Contains((rel.SourceControllerId, rel.SourceVolume)) &&
                    enabledKey.Contains((rel.DestinationControllerId, rel.DestinationVolume)))
                {
                    replicableVolumes.Add(rel.SourceVolume);
                }
            }

            // d) Storage options (only those that actually have VMs)
            model.StorageOptions = combinedStorage
                .Where(kvp => kvp.Value.Any())
                .Select(kvp => kvp.Key)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .Select(s => new SelectListItem { Text = s, Value = s })
                .ToList();

            // e) Per-storage VM lists for checkbox UI
            model.VmsByStorage = combinedStorage.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value
                    .Select(vm => new SelectListItem
                    {
                        Value = vm.Id.ToString(),
                        Text = $"{vm.Id} — {vm.Name}"
                    })
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

            model.AllVms = new List<SelectListItem>(); // legacy not used
            model.ReplicableVolumes = replicableVolumes;
            model.VolumeMeta = volumeMeta;

            // Preselect first storage for immediate list
            if (model.StorageOptions.Count > 0 && string.IsNullOrEmpty(model.StorageName))
                model.StorageName = model.StorageOptions[0].Value;

            // Initialize SelectedNetappVolumeId in the model from meta (if we have a preselected storage)
            if (!string.IsNullOrWhiteSpace(model.StorageName)
                && volumeMeta.TryGetValue(model.StorageName, out var vmMeta)
                && vmMeta.SelectedNetappVolumeId.HasValue)
            {
                model.SelectedNetappVolumeId = vmMeta.SelectedNetappVolumeId;
                model.ClusterId = vmMeta.ClusterId;
                model.ControllerId = vmMeta.ControllerId;
            }

            return View(model);
        }

        // ---------- EDIT ----------
        [HttpGet]
        public async Task<IActionResult> EditSchedule(int id, CancellationToken ct)
        {
            var schedule = await _context.BackupSchedules.FindAsync(id, ct);
            if (schedule == null) return NotFound();

            var clusters = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .AsNoTracking()
                .ToListAsync(ct);

            var primaryControllers = await _context.NetappControllers
                .AsNoTracking()
                .Where(n => n.IsPrimary)
                .ToListAsync(ct);

            // Only enabled rows
            var selectedVolumes = await _context.SelectedNetappVolumes
                .AsNoTracking()
                .Where(v => v.Disabled != true)
                .ToListAsync(ct);

            var enabledKey = new HashSet<(int C, string V)>(
                selectedVolumes.Select(v => (v.NetappControllerId, v.VolumeName)),
                EqualityComparer<(int, string)>.Default);

            var combinedStorage = new Dictionary<string, List<ProxmoxVM>>(StringComparer.OrdinalIgnoreCase);
            var volumeMeta = new Dictionary<string, VolumeMeta>(StringComparer.OrdinalIgnoreCase);

            foreach (var cluster in clusters)
            {
                foreach (var controller in primaryControllers)
                {
                    var map = await _invCache.GetEligibleBackupStorageWithVMsAsync(cluster, controller.Id, null, ct);
                    if (map == null) continue;

                    foreach (var kv in map)
                    {
                        var storage = kv.Key;

                        // ✅ only enabled volumes for this controller
                        if (!enabledKey.Contains((controller.Id, storage))) continue;

                        if (!combinedStorage.TryGetValue(storage, out var list))
                        {
                            list = new List<ProxmoxVM>();
                            combinedStorage[storage] = list;
                        }
                        list.AddRange(kv.Value);

                        if (!volumeMeta.ContainsKey(storage))
                        {
                            var volInfo = selectedVolumes.FirstOrDefault(
                                v => v.VolumeName == storage && v.NetappControllerId == controller.Id);

                            volumeMeta[storage] = new VolumeMeta
                            {
                                ClusterId = cluster.Id,
                                ControllerId = controller.Id,
                                SnapshotLockingEnabled = volInfo?.SnapshotLockingEnabled == true,
                                SelectedNetappVolumeId = volInfo?.Id
                            };
                        }
                    }
                }
            }

            // Replicable volumes (both ends enabled)
            var relationships = await _context.SnapMirrorRelations
                .AsNoTracking()
                .ToListAsync(ct);

            var replicableVolumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rel in relationships)
            {
                if (enabledKey.Contains((rel.SourceControllerId, rel.SourceVolume)) &&
                    enabledKey.Contains((rel.DestinationControllerId, rel.DestinationVolume)))
                {
                    replicableVolumes.Add(rel.SourceVolume);
                }
            }

            // Allowed storage names (must be enabled AND have VMs)
            var enabledVolumeNames = selectedVolumes
                .Select(v => v.VolumeName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var storageOptions = combinedStorage
                .Where(kvp => enabledVolumeNames.Contains(kvp.Key) && kvp.Value.Any())
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => new SelectListItem { Text = kvp.Key, Value = kvp.Key })
                .ToList();

            // Per-storage VM lists
            var vmsByStorage = combinedStorage
                .Where(kvp => enabledVolumeNames.Contains(kvp.Key) && kvp.Value.Any())
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value
                        .OrderBy(vm => vm.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(vm => new SelectListItem
                        {
                            Value = vm.Id.ToString(),
                            Text = $"{vm.Id} — {vm.Name}"
                        })
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);

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
                EnableLocking = schedule.EnableLocking,
                LockRetentionCount = schedule.LockRetentionCount,
                LockRetentionUnit = schedule.LockRetentionUnit,

                // carry current DB value (may be null); view posts it back as hidden
                SelectedNetappVolumeId = schedule.SelectedNetappVolumeId,

                ExcludedVmIds = schedule.ExcludedVmIds?
                    .Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()
                    ?? new List<string>(),

                StorageOptions = storageOptions,
                VmsByStorage = vmsByStorage,
                AllVms = new List<SelectListItem>(), // legacy cleared
                ReplicableVolumes = replicableVolumes,

                VolumeMeta = volumeMeta,
                ClusterId = volumeMeta.TryGetValue(schedule.StorageName, out var vmMeta) ? vmMeta.ClusterId : schedule.ClusterId,
                ControllerId = volumeMeta.TryGetValue(schedule.StorageName, out vmMeta) ? vmMeta.ControllerId : schedule.ControllerId,

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

            if (!model.EnableLocking)
            {
                model.LockRetentionCount = null;
                model.LockRetentionUnit = null;
            }

            // If DB was null, pre-fill model from meta to allow a one-time populate on save
            if (model.SelectedNetappVolumeId == null
                && volumeMeta.TryGetValue(model.StorageName, out var metaFromStorage)
                && metaFromStorage.SelectedNetappVolumeId.HasValue)
            {
                model.SelectedNetappVolumeId = metaFromStorage.SelectedNetappVolumeId;
            }

            ViewData["ScheduleId"] = id;
            return View("EditSchedule", model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateSchedule(CreateScheduleRequest model, CancellationToken ct)
        {
            // 0) Normalize/cleanup ModelState based on schedule type
            if (model.SingleSchedule != null)
            {
                var prefix = nameof(model.SingleSchedule) + ".";
                if (string.Equals(model.SingleSchedule.Type, "Hourly", StringComparison.OrdinalIgnoreCase))
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

            // 1) If invalid, rehydrate dropdowns/lists and return
            if (!ModelState.IsValid || model.SingleSchedule == null)
            {
                await RehydrateCreateEditViewBitsAsync(model, ct);
                TempData["ErrorMessage"] = "Please fill in all required schedule details.";
                return View(model); // default CreateSchedule.cshtml
            }

            var s = model.SingleSchedule;

            // 2) Server-side enforcement for App-Aware + Excludes
            if (!model.IsApplicationAware)
            {
                model.EnableIoFreeze = false;
                model.UseProxmoxSnapshot = false;
                model.WithMemory = false;
                model.ExcludedVmIds = null; // ignore excludes when not app-aware
            }
            else
            {
                if (!model.UseProxmoxSnapshot) model.WithMemory = false;
            }

            // 🔁 REHYDRATE (for excludes/locking/meta inference)
            if (model.VmsByStorage == null || model.VmsByStorage.Count == 0 ||
                model.VolumeMeta == null || model.VolumeMeta.Count == 0)
            {
                await RehydrateCreateEditViewBitsAsync(model, ct);
            }

            // 3) Keep excludes only from selected storage's VM set
            if (model.VmsByStorage != null &&
                !string.IsNullOrWhiteSpace(model.StorageName) &&
                model.VmsByStorage.TryGetValue(model.StorageName, out var validSelectList))
            {
                var validIds = new HashSet<string>(validSelectList.Select(i => i.Value));
                model.ExcludedVmIds = (model.ExcludedVmIds ?? new List<string>())
                    .Where(validIds.Contains)
                    .Distinct()
                    .ToList();
            }
            else
            {
                model.ExcludedVmIds = null;
            }

            // 4) Parse time for non-hourly
            TimeSpan? timeOfDay = null;
            if (!string.Equals(s.Type, "Hourly", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(s.Time) &&
                TimeSpan.TryParse(s.Time, out var parsedTime))
            {
                timeOfDay = parsedTime;
            }

            // 5) Locking validation
            var volSupportsLocking = model.VolumeMeta != null &&
                                     model.VolumeMeta.TryGetValue(model.StorageName ?? string.Empty, out var vmeta) &&
                                     vmeta.SnapshotLockingEnabled;

            var lockingEnabled = false;
            int? lockCount = null;
            string? lockUnit = null;

            if (model.EnableLocking && volSupportsLocking)
            {
                var totalRetentionHours = ToHours(s.RetentionCount, s.RetentionUnit);
                var requestedLockHours = ToHours(model.LockRetentionCount ?? 0, model.LockRetentionUnit ?? "Hours");

                if (requestedLockHours > 0 &&
                    requestedLockHours < totalRetentionHours &&
                    requestedLockHours <= (30 * 24))
                {
                    lockingEnabled = true;
                    lockCount = model.LockRetentionCount;
                    lockUnit = model.LockRetentionUnit;
                }
            }

            // Infer SelectedNetappVolumeId if not posted
            int? selectedVolumeId = model.SelectedNetappVolumeId;
            if (selectedVolumeId == null &&
                model.VolumeMeta != null &&
                model.StorageName != null &&
                model.VolumeMeta.TryGetValue(model.StorageName, out var meta) &&
                meta.SelectedNetappVolumeId.HasValue)
            {
                selectedVolumeId = meta.SelectedNetappVolumeId;
            }

            // 6) Build entity
            var schedule = new BackupSchedule
            {
                Name = model.Name,
                StorageName = model.StorageName,
                IsEnabled = model.IsEnabled,

                Schedule = s.Type,
                Frequency = string.Equals(s.Type, "Hourly", StringComparison.OrdinalIgnoreCase)
                                ? $"{s.StartHour}-{s.EndHour}"
                                : string.Join(",", s.DaysOfWeek ?? Enumerable.Empty<string>()),
                TimeOfDay = string.Equals(s.Type, "Hourly", StringComparison.OrdinalIgnoreCase) ? (TimeSpan?)null : timeOfDay,

                IsApplicationAware = model.IsApplicationAware,
                EnableIoFreeze = model.EnableIoFreeze,
                UseProxmoxSnapshot = model.UseProxmoxSnapshot,
                WithMemory = model.WithMemory,

                ExcludedVmIds = (model.ExcludedVmIds == null || model.ExcludedVmIds.Count == 0)
                                ? null
                                : string.Join(",", model.ExcludedVmIds.Distinct()),

                RetentionCount = s.RetentionCount,
                RetentionUnit = s.RetentionUnit,
                LastRun = null,

                ReplicateToSecondary = model.ReplicateToSecondary,
                ClusterId = model.ClusterId,
                ControllerId = model.ControllerId,

                // Locking
                EnableLocking = lockingEnabled,
                LockRetentionCount = lockCount,
                LockRetentionUnit = lockUnit,

                // Selected volume link
                SelectedNetappVolumeId = selectedVolumeId
            };

            _context.BackupSchedules.Add(schedule);
            await _context.SaveChangesAsync(ct);

            TempData["SuccessMessage"] = "Backup schedule created successfully!";
            return RedirectToAction("Backup");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditSchedule(int id, CreateScheduleRequest model, CancellationToken ct)
        {
            // 0) Normalize/cleanup ModelState based on schedule type
            if (model.SingleSchedule != null)
            {
                var prefix = nameof(model.SingleSchedule) + ".";
                if (string.Equals(model.SingleSchedule.Type, "Hourly", StringComparison.OrdinalIgnoreCase))
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

            if (!ModelState.IsValid || model.SingleSchedule == null)
            {
                await RehydrateCreateEditViewBitsAsync(model, ct);
                return View("EditSchedule", model);
            }

            var schedule = await _context.BackupSchedules.FindAsync(new object?[] { id }, ct);
            if (schedule == null) return NotFound();

            var s = model.SingleSchedule;

            // 1) App-aware + Excludes enforcement
            if (!model.IsApplicationAware)
            {
                model.EnableIoFreeze = false;
                model.UseProxmoxSnapshot = false;
                model.WithMemory = false;
                model.ExcludedVmIds = null;
            }
            else
            {
                if (!model.UseProxmoxSnapshot) model.WithMemory = false;
            }

            // 🔁 REHYDRATE lists/meta if needed
            if (model.VmsByStorage == null || model.VmsByStorage.Count == 0 ||
                model.VolumeMeta == null || model.VolumeMeta.Count == 0)
            {
                await RehydrateCreateEditViewBitsAsync(model, ct);
            }

            // 2) Keep excludes only from selected storage's VM set
            if (model.VmsByStorage != null &&
                !string.IsNullOrWhiteSpace(model.StorageName) &&
                model.VmsByStorage.TryGetValue(model.StorageName, out var validSelectList))
            {
                var validIds = new HashSet<string>(validSelectList.Select(i => i.Value));
                model.ExcludedVmIds = (model.ExcludedVmIds ?? new List<string>())
                    .Where(validIds.Contains)
                    .Distinct()
                    .ToList();
            }
            else
            {
                model.ExcludedVmIds = null;
            }

            // 3) Parse time for non-hourly
            TimeSpan? timeOfDay = null;
            if (!string.Equals(s.Type, "Hourly", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(s.Time) &&
                TimeSpan.TryParse(s.Time, out var parsedTime))
            {
                timeOfDay = parsedTime;
            }

            // 4) Locking validation (needs VolumeMeta)
            var volSupportsLocking = model.VolumeMeta != null &&
                                     model.VolumeMeta.TryGetValue(model.StorageName ?? string.Empty, out var vmeta) &&
                                     vmeta.SnapshotLockingEnabled;

            var lockingEnabled = false;
            int? lockCount = null;
            string? lockUnit = null;

            if (model.EnableLocking && volSupportsLocking)
            {
                var totalRetentionHours = ToHours(s.RetentionCount, s.RetentionUnit);
                var requestedLockHours = ToHours(model.LockRetentionCount ?? 0, model.LockRetentionUnit ?? "Hours");

                if (requestedLockHours > 0 &&
                    requestedLockHours < totalRetentionHours &&
                    requestedLockHours <= (30 * 24))
                {
                    lockingEnabled = true;
                    lockCount = model.LockRetentionCount;
                    lockUnit = model.LockRetentionUnit;
                }
            }

            // 5) Update entity fields
            schedule.Name = model.Name;
            schedule.StorageName = model.StorageName;
            schedule.IsEnabled = model.IsEnabled;

            schedule.Schedule = s.Type;
            schedule.Frequency = string.Equals(s.Type, "Hourly", StringComparison.OrdinalIgnoreCase)
                                    ? $"{s.StartHour}-{s.EndHour}"
                                    : string.Join(",", s.DaysOfWeek ?? Enumerable.Empty<string>());
            schedule.TimeOfDay = string.Equals(s.Type, "Hourly", StringComparison.OrdinalIgnoreCase) ? (TimeSpan?)null : timeOfDay;

            schedule.RetentionCount = s.RetentionCount;
            schedule.RetentionUnit = s.RetentionUnit;

            schedule.IsApplicationAware = model.IsApplicationAware;
            schedule.EnableIoFreeze = model.EnableIoFreeze;
            schedule.UseProxmoxSnapshot = model.UseProxmoxSnapshot;
            schedule.WithMemory = model.WithMemory;

            schedule.ReplicateToSecondary = model.ReplicateToSecondary;
            schedule.ExcludedVmIds = (model.ExcludedVmIds == null || model.ExcludedVmIds.Count == 0)
                                        ? null
                                        : string.Join(",", model.ExcludedVmIds.Distinct());

            schedule.ClusterId = model.ClusterId;
            schedule.ControllerId = model.ControllerId;

            schedule.EnableLocking = lockingEnabled;
            schedule.LockRetentionCount = lockCount;
            schedule.LockRetentionUnit = lockUnit;

            // 5b) Only populate SelectedNetappVolumeId if DB is missing it
            if (schedule.SelectedNetappVolumeId == null)
            {
                int? candidate = model.SelectedNetappVolumeId;

                if (candidate == null &&
                    model.VolumeMeta != null &&
                    model.StorageName != null &&
                    model.VolumeMeta.TryGetValue(model.StorageName, out var meta) &&
                    meta.SelectedNetappVolumeId.HasValue)
                {
                    candidate = meta.SelectedNetappVolumeId;
                }

                if (candidate.HasValue)
                {
                    schedule.SelectedNetappVolumeId = candidate.Value;
                }
            }

            // 6) Save with transient retry (SQLite "database is locked")
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    await _context.SaveChangesAsync(ct);
                    break;
                }
                catch (DbUpdateException ex) when (ex.InnerException is SqliteException se && se.SqliteErrorCode == 5 /* SQLITE_BUSY */)
                {
                    if (i == 2) throw;
                    await Task.Delay(500, ct);
                }
            }

            TempData["SuccessMessage"] = "Backup schedule updated successfully.";
            return RedirectToAction("Backup");
        }

        // ---------- DELETE ----------
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

        // ---------- START NOW ----------
        [HttpPost]
        public IActionResult StartBackupNow([FromForm] BackupRequest request, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(request.StorageName) || request.RetentionCount < 1 || request.RetentionCount > 999)
                return BadRequest("Invalid input");

            _ = Task.Run(async () =>
            {
                using var scope = _scopeFactory.CreateScope();

                var scopedBackupService = scope.ServiceProvider.GetRequiredService<IBackupService>();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<BackupController>>();

                if (!request.EnableLocking)
                {
                    request.LockRetentionCount = null;
                    request.LockRetentionUnit = null;
                }

                try
                {
                    logger.LogInformation("Starting background backup for storage {StorageName}", request.StorageName);

                    await scopedBackupService.StartBackupAsync(
                        request.StorageName,
                        request.selectedNetappVolumeId,
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
                        request.ReplicateToSecondary,
                        request.EnableLocking,
                        request.LockRetentionCount,
                        request.LockRetentionUnit,
                        request.ExcludedVmIds,
                        ct
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

        private static int ToHours(int? count, string? unit)
        {
            var c = count.GetValueOrDefault(0);
            if (c <= 0) return 0;

            return unit switch
            {
                "Hours" => c,
                "Days" => c * 24,
                "Weeks" => c * 7 * 24,
                _ => 0
            };
        }

        /// <summary>
        /// Rehydrates StorageOptions, VmsByStorage, VolumeMeta (incl. SelectedNetappVolumeId), ReplicableVolumes
        /// so the view can render on POST-back with errors.
        /// </summary>
        private async Task RehydrateCreateEditViewBitsAsync(CreateScheduleRequest model, CancellationToken ct)
        {
            var clusters = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .AsNoTracking()
                .ToListAsync(ct);

            var primaryControllers = await _context.NetappControllers
                .AsNoTracking()
                .Where(n => n.IsPrimary)
                .ToListAsync(ct);

            // Only enabled rows
            var selectedVolumes = await _context.SelectedNetappVolumes
                .AsNoTracking()
                .Where(v => v.Disabled != true)
                .ToListAsync(ct);

            var enabledKey = new HashSet<(int C, string V)>(
                selectedVolumes.Select(v => (v.NetappControllerId, v.VolumeName)),
                EqualityComparer<(int, string)>.Default);

            var combinedStorage = new Dictionary<string, List<ProxmoxVM>>(StringComparer.OrdinalIgnoreCase);
            var volumeMeta = new Dictionary<string, VolumeMeta>(StringComparer.OrdinalIgnoreCase);

            foreach (var cluster in clusters)
            {
                foreach (var ctrl in primaryControllers)
                {
                    var map = await _invCache.GetEligibleBackupStorageWithVMsAsync(cluster, ctrl.Id, null, ct);
                    if (map == null) continue;

                    foreach (var kv in map)
                    {
                        var storage = kv.Key;

                        // ✅ only enabled volumes for this controller
                        if (!enabledKey.Contains((ctrl.Id, storage))) continue;

                        if (!combinedStorage.TryGetValue(storage, out var list))
                        {
                            list = new List<ProxmoxVM>();
                            combinedStorage[storage] = list;
                        }
                        list.AddRange(kv.Value);

                        if (!volumeMeta.ContainsKey(storage))
                        {
                            var volInfo = selectedVolumes.FirstOrDefault(
                                v => v.VolumeName == storage && v.NetappControllerId == ctrl.Id);

                            volumeMeta[storage] = new VolumeMeta
                            {
                                ClusterId = cluster.Id,
                                ControllerId = ctrl.Id,
                                SnapshotLockingEnabled = volInfo?.SnapshotLockingEnabled == true,
                                SelectedNetappVolumeId = volInfo?.Id
                            };
                        }
                    }
                }
            }

            // Replicable volumes (SnapMirror, both ends enabled)
            var relations = await _context.SnapMirrorRelations
                .AsNoTracking()
                .ToListAsync(ct);

            var replicableVolumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rel in relations)
            {
                if (enabledKey.Contains((rel.SourceControllerId, rel.SourceVolume)) &&
                    enabledKey.Contains((rel.DestinationControllerId, rel.DestinationVolume)))
                {
                    replicableVolumes.Add(rel.SourceVolume);
                }
            }

            model.StorageOptions = combinedStorage.Keys
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .Select(s => new SelectListItem { Text = s, Value = s })
                .ToList();

            model.VmsByStorage = combinedStorage.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value
                    .OrderBy(vm => vm.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(vm => new SelectListItem
                    {
                        Value = vm.Id.ToString(),
                        Text = $"{vm.Id} — {vm.Name}"
                    })
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

            model.VolumeMeta = volumeMeta;
            model.ReplicableVolumes = replicableVolumes;

            // If we know the storage, fill cluster/controller + selected volume id from meta
            if (!string.IsNullOrWhiteSpace(model.StorageName)
                && volumeMeta.TryGetValue(model.StorageName, out var vmMeta))
            {
                model.ClusterId = vmMeta.ClusterId;
                model.ControllerId = vmMeta.ControllerId;
                model.SelectedNetappVolumeId ??= vmMeta.SelectedNetappVolumeId;
            }

            // legacy
            model.AllVms = new List<SelectListItem>();
        }
    }
}
