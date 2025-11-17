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
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BareProx.Controllers
{
    public class BackupController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IBackupService _backupService;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IQueryDbFactory _qdbf;
        private readonly ILogger<BackupController> _logger;

        public BackupController(
            ApplicationDbContext context,
            IBackupService backupService,
            IServiceScopeFactory scopeFactory,
            IQueryDbFactory qdbf,
            ILogger<BackupController> logger)
        {
            _context = context;
            _backupService = backupService;
            _scopeFactory = scopeFactory;
            _qdbf = qdbf;
            _logger = logger;
        }

        public IActionResult Backup()
        {
            var schedules = _context.BackupSchedules.ToList();
            return View(schedules);
        }

        // ---------- helpers ----------
        private static string? NormalizeRecipients(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var parts = raw
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return parts.Length == 0 ? null : string.Join(",", parts);
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

        private async Task<(bool configured, bool defaultOnSuccess, bool defaultOnError, string? defaultRecipients)>
            GetEmailSettingsAsync(CancellationToken ct)
        {
            var es = await _context.EmailSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            if (es == null)
                return (false, false, false, null);

            var configured = es.Enabled == true && !string.IsNullOrWhiteSpace(es.SmtpHost);
            var onSuccess = es.OnBackupSuccess == true;
            var onError = es.OnBackupFailure == true;
            var recipients = string.IsNullOrWhiteSpace(es.DefaultRecipients) ? null : NormalizeRecipients(es.DefaultRecipients);

            return (configured, onSuccess, onError, recipients);
        }

        // ---------- CREATE ----------
        [HttpGet]
        public async Task<IActionResult> CreateSchedule(CancellationToken ct)
        {
            var model = new CreateScheduleRequest();

            // Email availability + defaults
            var (emailConfigured, defSuccess, defError, defRecipients) = await GetEmailSettingsAsync(ct);
            model.EmailConfigured = emailConfigured;
            ViewBag.EmailConfigured = emailConfigured;

            model.NotificationsEnabled = emailConfigured;
            model.NotifyOnSuccess = emailConfigured && defSuccess;
            model.NotifyOnError = emailConfigured && defError;
            if (emailConfigured && !string.IsNullOrWhiteSpace(defRecipients))
                model.NotificationEmails = defRecipients;

            // Inventory from Query DB
            var (combinedStorage, volumeMeta, replicableVolumes) = await LoadInventoryForCreateEditAsync(ct);

            model.StorageOptions = combinedStorage
                .Where(kvp => kvp.Value.Any())
                .Select(kvp => kvp.Key)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .Select(s => new SelectListItem { Text = s, Value = s })
                .ToList();

            model.VmsByStorage = combinedStorage.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Select(vm => new SelectListItem
                {
                    Value = vm.Id.ToString(),
                    Text = $"{vm.Id} — {vm.Name}"
                }).ToList(),
                StringComparer.OrdinalIgnoreCase);

            model.AllVms = new List<SelectListItem>();
            model.ReplicableVolumes = replicableVolumes;
            model.VolumeMeta = volumeMeta;

            // Pick first storage by default
            if (model.StorageOptions.Count > 0 && string.IsNullOrEmpty(model.StorageName))
                model.StorageName = model.StorageOptions[0].Value;

            // Prefill ClusterId/ControllerId/SelectedNetappVolumeId/VolumeUuid from VolumeMeta (if available)
            if (!string.IsNullOrWhiteSpace(model.StorageName) &&
                volumeMeta.TryGetValue(model.StorageName, out var vmMeta))
            {
                model.ClusterId = vmMeta.ClusterId;
                model.ControllerId = vmMeta.ControllerId;
                if (vmMeta.SelectedNetappVolumeId.HasValue)
                    model.SelectedNetappVolumeId = vmMeta.SelectedNetappVolumeId;
                model.VolumeUuid = vmMeta.VolumeUuid; // carry UUID to UI
            }

            return View(model);
        }


        // ---------- EDIT ----------
        [HttpGet]
        public async Task<IActionResult> EditSchedule(int id, CancellationToken ct)
        {
            var schedule = await _context.BackupSchedules.FindAsync(id, ct);
            if (schedule == null) return NotFound();

            var (emailConfigured, _defSuccess, _defError, defRecipients) = await GetEmailSettingsAsync(ct);
            ViewBag.EmailConfigured = emailConfigured;

            var (combinedStorage, volumeMeta, replicableVolumes) = await LoadInventoryForCreateEditAsync(ct);

            // Build storage options based on the *resolved* VolumeMeta, not name matching
            var storageOptions = combinedStorage
                .Where(kvp =>
                    kvp.Value.Any() &&
                    volumeMeta.TryGetValue(kvp.Key, out var m) &&
                    m.SelectedNetappVolumeId.HasValue)
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => new SelectListItem { Text = kvp.Key, Value = kvp.Key })
                .ToList();

            // Ensure the schedule's current storage is present even if mapping changed
            if (!string.IsNullOrWhiteSpace(schedule.StorageName) &&
                !storageOptions.Any(o => o.Value.Equals(schedule.StorageName, StringComparison.OrdinalIgnoreCase)) &&
                combinedStorage.ContainsKey(schedule.StorageName))
            {
                storageOptions.Insert(0, new SelectListItem
                {
                    Text = schedule.StorageName,
                    Value = schedule.StorageName
                });
            }

            // Limit VMs-by-storage to the storages we allow in the dropdown
            var allowedStorageKeys = new HashSet<string>(
                storageOptions.Select(o => o.Value),
                StringComparer.OrdinalIgnoreCase);

            var vmsByStorage = combinedStorage
                .Where(kvp => allowedStorageKeys.Contains(kvp.Key) && kvp.Value.Any())
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

            // Pull everything we can from VolumeMeta for the currently selected storage
            VolumeMeta? vmMeta = null;
            volumeMeta.TryGetValue(schedule.StorageName, out vmMeta);

            // Safe parsing for Hourly frequency "start-end"
            int? startHour = null, endHour = null;
            if (string.Equals(schedule.Schedule, "Hourly", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(schedule.Frequency))
            {
                var parts = schedule.Frequency.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2)
                {
                    if (int.TryParse(parts[0], out var sh)) startHour = sh;
                    if (int.TryParse(parts[1], out var eh)) endHour = eh;
                }
            }

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

                NotificationsEnabled = schedule.NotificationsEnabled,
                NotifyOnSuccess = schedule.NotifyOnSuccess,
                NotifyOnError = schedule.NotifyOnError,
                NotificationEmails = string.IsNullOrWhiteSpace(schedule.NotificationEmails)
                                        ? defRecipients
                                        : schedule.NotificationEmails,
                EmailConfigured = emailConfigured,

                SelectedNetappVolumeId = schedule.SelectedNetappVolumeId,

                ExcludedVmIds = schedule.ExcludedVmIds?
                    .Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()
                    ?? new List<string>(),

                StorageOptions = storageOptions,
                VmsByStorage = vmsByStorage,
                AllVms = new List<SelectListItem>(),
                ReplicableVolumes = replicableVolumes,

                VolumeMeta = volumeMeta,

                // Prefer live VolumeMeta, fall back to values persisted on the schedule
                ClusterId = vmMeta?.ClusterId ?? schedule.ClusterId,
                ControllerId = vmMeta?.ControllerId ?? schedule.ControllerId,
                VolumeUuid = vmMeta?.VolumeUuid, // carry UUID into the edit model

                SingleSchedule = new ScheduleEntry
                {
                    Label = schedule.Name,
                    Type = schedule.Schedule,
                    DaysOfWeek = string.Equals(schedule.Schedule, "Hourly", StringComparison.OrdinalIgnoreCase)
                        ? new List<string>()
                        : (schedule.Frequency ?? string.Empty)
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .ToList(),
                    Time = schedule.TimeOfDay?.ToString(@"hh\:mm"),
                    StartHour = string.Equals(schedule.Schedule, "Hourly", StringComparison.OrdinalIgnoreCase) ? startHour : (int?)null,
                    EndHour = string.Equals(schedule.Schedule, "Hourly", StringComparison.OrdinalIgnoreCase) ? endHour : (int?)null,
                    RetentionCount = schedule.RetentionCount,
                    RetentionUnit = schedule.RetentionUnit
                }
            };

            // If locking toggle is off, null out lock settings in the view model
            if (!model.EnableLocking)
            {
                model.LockRetentionCount = null;
                model.LockRetentionUnit = null;
            }

            // If schedule didn’t store SelectedNetappVolumeId, try to derive it from VolumeMeta
            if (model.SelectedNetappVolumeId == null &&
                vmMeta?.SelectedNetappVolumeId is int selId)
            {
                model.SelectedNetappVolumeId = selId;
            }

            ViewData["ScheduleId"] = id;
            return View("EditSchedule", model);
        }



        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateSchedule(CreateScheduleRequest model, CancellationToken ct)
        {
            // Switch required fields depending on schedule type
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
                TempData["ErrorMessage"] = "Please fill in all required schedule details.";
                return View(model);
            }

            var s = model.SingleSchedule;

            // Enforce app-aware dependencies
            if (!model.IsApplicationAware)
            {
                model.EnableIoFreeze = false;
                model.UseProxmoxSnapshot = false;
                model.WithMemory = false;
                model.ExcludedVmIds = null;
            }
            else if (!model.UseProxmoxSnapshot)
            {
                model.WithMemory = false;
            }

            // Email gating
            var (emailConfigured, _, _, _) = await GetEmailSettingsAsync(ct);
            ViewBag.EmailConfigured = emailConfigured;
            if (!emailConfigured) model.NotificationsEnabled = false;

            if (!model.NotificationsEnabled)
            {
                model.NotifyOnSuccess = false;
                model.NotifyOnError = false;
                model.NotificationEmails = null;
            }

            // Ensure view bits exist for validation error round-trips
            if (model.VmsByStorage == null || model.VmsByStorage.Count == 0 ||
                model.VolumeMeta == null || model.VolumeMeta.Count == 0)
            {
                await RehydrateCreateEditViewBitsAsync(model, ct);
            }

            // Sanitize Excluded VM IDs against current storage’s VM list
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

            // Parse time of day (non-hourly)
            TimeSpan? timeOfDay = null;
            if (!string.Equals(s.Type, "Hourly", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(s.Time) &&
                TimeSpan.TryParse(s.Time, out var parsedTime))
            {
                timeOfDay = parsedTime;
            }

            // Hourly window validation (same as Edit)
            if (string.Equals(s.Type, "Hourly", StringComparison.OrdinalIgnoreCase))
            {
                bool valid = s.StartHour is >= 0 and <= 23
                          && s.EndHour is >= 0 and <= 23
                          && s.StartHour <= s.EndHour;
                if (!valid)
                {
                    ModelState.AddModelError(nameof(model.SingleSchedule), "Hourly window must be in 0–23 and StartHour ≤ EndHour.");
                    await RehydrateCreateEditViewBitsAsync(model, ct);
                    return View(model);
                }
            }

            // Locking support from VolumeMeta
            var volSupportsLocking =
                model.VolumeMeta != null &&
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
                    requestedLockHours <= (30 * 24)) // 30 days cap
                {
                    lockingEnabled = true;
                    lockCount = model.LockRetentionCount;
                    lockUnit = model.LockRetentionUnit;
                }
            }

            // Selected volume id from model or meta
            int? selectedVolumeId = model.SelectedNetappVolumeId;
            if (selectedVolumeId == null &&
                model.VolumeMeta != null &&
                model.StorageName != null &&
                model.VolumeMeta.TryGetValue(model.StorageName, out var metaForSel) &&
                metaForSel.SelectedNetappVolumeId.HasValue)
            {
                selectedVolumeId = metaForSel.SelectedNetappVolumeId;
            }

            // Volume UUID from model or meta
            string? volumeUuid = model.VolumeUuid;
            if (string.IsNullOrWhiteSpace(volumeUuid) &&
                model.VolumeMeta != null &&
                model.StorageName != null &&
                model.VolumeMeta.TryGetValue(model.StorageName, out var metaForUuid))
            {
                volumeUuid = metaForUuid.VolumeUuid; // NEW
            }

            // Notifications
            var notifEnabled = model.NotificationsEnabled;
            var notifyOnSuccessFlag = notifEnabled && (model.NotifyOnSuccess == true);
            var notifyOnErrorFlag = notifEnabled && (model.NotifyOnError == true);
            var notifyEmails = notifEnabled ? NormalizeRecipients(model.NotificationEmails) : null;

            // Build entity ONCE (no duplicate variable)
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

                EnableLocking = lockingEnabled,
                LockRetentionCount = lockCount,
                LockRetentionUnit = lockUnit,

                SelectedNetappVolumeId = selectedVolumeId,
                VolumeUuid = volumeUuid, // NEW

                NotificationsEnabled = notifEnabled,
                NotifyOnSuccess = notifyOnSuccessFlag,
                NotifyOnError = notifyOnErrorFlag,
                NotificationEmails = notifyEmails
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
            // Switch required fields depending on schedule type
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

            // Enforce app-aware dependencies
            if (!model.IsApplicationAware)
            {
                model.EnableIoFreeze = false;
                model.UseProxmoxSnapshot = false;
                model.WithMemory = false;
                model.ExcludedVmIds = null;
            }
            else if (!model.UseProxmoxSnapshot)
            {
                model.WithMemory = false;
            }

            // Email gating
            var (emailConfigured, _, _, _) = await GetEmailSettingsAsync(ct);
            ViewBag.EmailConfigured = emailConfigured;
            if (!emailConfigured) model.NotificationsEnabled = false;

            if (!model.NotificationsEnabled)
            {
                model.NotifyOnSuccess = false;
                model.NotifyOnError = false;
                model.NotificationEmails = null;
            }

            // Ensure view bits exist for validation error round-trips
            if (model.VmsByStorage == null || model.VmsByStorage.Count == 0 ||
                model.VolumeMeta == null || model.VolumeMeta.Count == 0)
            {
                await RehydrateCreateEditViewBitsAsync(model, ct);
            }

            // Sanitize Excluded VM IDs against current storage’s VM list
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

            // Parse time of day (non-hourly)
            TimeSpan? timeOfDay = null;
            if (!string.Equals(s.Type, "Hourly", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(s.Time) &&
                TimeSpan.TryParse(s.Time, out var parsedTime))
            {
                timeOfDay = parsedTime;
            }

            // Locking support from VolumeMeta
            var volSupportsLocking =
                model.VolumeMeta != null &&
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
                    requestedLockHours <= (30 * 24)) // 30 days cap
                {
                    lockingEnabled = true;
                    lockCount = model.LockRetentionCount;
                    lockUnit = model.LockRetentionUnit;
                }
            }

            // ----- Write back to entity -----
            schedule.Name = model.Name;
            schedule.StorageName = model.StorageName;
            schedule.IsEnabled = model.IsEnabled;

            // Hourly parsing & validation
            if (string.Equals(s.Type, "Hourly", StringComparison.OrdinalIgnoreCase))
            {
                int? startHour = s.StartHour;
                int? endHour = s.EndHour;

                bool valid = startHour is >= 0 and <= 23 && endHour is >= 0 and <= 23 && startHour <= endHour;
                if (!valid)
                {
                    ModelState.AddModelError(nameof(model.SingleSchedule), "Hourly window must be in 0–23 and StartHour ≤ EndHour.");
                    await RehydrateCreateEditViewBitsAsync(model, ct);
                    return View("EditSchedule", model);
                }

                schedule.Schedule = "Hourly";
                schedule.Frequency = $"{startHour}-{endHour}";
                schedule.TimeOfDay = null;
            }
            else
            {
                schedule.Schedule = s.Type;
                schedule.Frequency = string.Join(",", s.DaysOfWeek ?? Enumerable.Empty<string>());
                schedule.TimeOfDay = timeOfDay;
            }

            // Retention policy
            schedule.RetentionCount = s.RetentionCount;
            schedule.RetentionUnit = s.RetentionUnit;

            // Behavior flags
            schedule.IsApplicationAware = model.IsApplicationAware;
            schedule.EnableIoFreeze = model.EnableIoFreeze;
            schedule.UseProxmoxSnapshot = model.UseProxmoxSnapshot;
            schedule.WithMemory = model.WithMemory;

            // Replication & exclusions
            schedule.ReplicateToSecondary = model.ReplicateToSecondary;
            schedule.ExcludedVmIds = (model.ExcludedVmIds == null || model.ExcludedVmIds.Count == 0)
                ? null
                : string.Join(",", model.ExcludedVmIds.Distinct());

            // Prefer current VolumeMeta for infra fields; fall back to model values
            if (model.VolumeMeta != null &&
                model.StorageName != null &&
                model.VolumeMeta.TryGetValue(model.StorageName, out var meta))
            {
                schedule.ClusterId = meta.ClusterId;
                schedule.ControllerId = meta.ControllerId;
                schedule.VolumeUuid = meta.VolumeUuid;       // NEW: persist UUID
                if (meta.SelectedNetappVolumeId.HasValue)
                    schedule.SelectedNetappVolumeId = meta.SelectedNetappVolumeId.Value;
            }
            else
            {
                // Fall back (in case meta not present)
                schedule.ClusterId = model.ClusterId;
                schedule.ControllerId = model.ControllerId;
                if (!string.IsNullOrWhiteSpace(model.VolumeUuid))
                    schedule.VolumeUuid = model.VolumeUuid;
                if (model.SelectedNetappVolumeId.HasValue)
                    schedule.SelectedNetappVolumeId = model.SelectedNetappVolumeId.Value;
            }

            // Locking
            schedule.EnableLocking = lockingEnabled;
            schedule.LockRetentionCount = lockCount;
            schedule.LockRetentionUnit = lockUnit;

            // Notifications
            var notifEnabled = model.NotificationsEnabled;
            schedule.NotificationsEnabled = notifEnabled;
            schedule.NotifyOnSuccess = notifEnabled && (model.NotifyOnSuccess == true);
            schedule.NotifyOnError = notifEnabled && (model.NotifyOnError == true);
            schedule.NotificationEmails = notifEnabled ? NormalizeRecipients(model.NotificationEmails) : null;

            // Save with simple BUSY retry
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
                        storageName: request.StorageName,
                        selectedNetappVolumeId: request.selectedNetappVolumeId,
                        isApplicationAware: request.IsApplicationAware,
                        label: request.Label,
                        clusterId: request.ClusterId,
                        netappControllerId: request.ControllerId,
                        retentionCount: request.RetentionCount,
                        retentionUnit: request.RetentionUnit,
                        enableIoFreeze: request.EnableIoFreeze,
                        useProxmoxSnapshot: request.UseProxmoxSnapshot,
                        withMemory: request.WithMemory,
                        dontTrySuspend: request.DontTrySuspend,
                        scheduleId: request.ScheduleID,
                        replicateToSecondary: request.ReplicateToSecondary,
                        enableLocking: request.EnableLocking,
                        lockRetentionCount: request.LockRetentionCount,
                        lockRetentionUnit: request.LockRetentionUnit,
                        excludedVmIds: request.ExcludedVmIds,
                        volumeUuid: request.VolumeUuid,
                        ct: ct
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

        /// <summary>
        /// Rehydrates StorageOptions, VmsByStorage, VolumeMeta, ReplicableVolumes
        /// for POST-backs with validation errors.
        /// </summary>
        private async Task RehydrateCreateEditViewBitsAsync(CreateScheduleRequest model, CancellationToken ct)
        {
            // Email availability
            var (emailConfigured, _, _, _) = await GetEmailSettingsAsync(ct);
            model.EmailConfigured = emailConfigured;
            ViewBag.EmailConfigured = emailConfigured;

            // Inventory from Query DB
            var (combinedStorage, volumeMeta, replicableVolumes) = await LoadInventoryForCreateEditAsync(ct);

            // Rebuild dropdowns and VM lists
            model.StorageOptions = combinedStorage
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => new SelectListItem { Text = kvp.Key, Value = kvp.Key })
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

            // Carry meta back into the model (ClusterId/ControllerId/SelectedNetappVolumeId/VolumeUuid)
            if (!string.IsNullOrWhiteSpace(model.StorageName)
                && volumeMeta.TryGetValue(model.StorageName, out var vmMeta))
            {
                model.ClusterId = vmMeta.ClusterId;
                model.ControllerId = vmMeta.ControllerId;
                model.SelectedNetappVolumeId ??= vmMeta.SelectedNetappVolumeId;

                // NEW: hydrate UUID if not already provided
                if (string.IsNullOrWhiteSpace(model.VolumeUuid) && !string.IsNullOrWhiteSpace(vmMeta.VolumeUuid))
                    model.VolumeUuid = vmMeta.VolumeUuid;
            }

            // Keep AllVms empty (populated client-side as needed)
            model.AllVms = new List<SelectListItem>();
        }

        // Inventory loader (Query DB)
        private async Task<(Dictionary<string, List<ProxmoxVM>> combined,
               Dictionary<string, VolumeMeta> volumeMeta,
               HashSet<string> replicable)> LoadInventoryForCreateEditAsync(CancellationToken ct)
        {
            // ---------- Helpers (same logic as in ProxmoxController) ----------
            static string Nx(string? s) => (s ?? string.Empty).Trim();

            static string NormPath(string? p)
            {
                p = Nx(p);
                if (string.IsNullOrEmpty(p)) return p;
                var q = p.Replace('\\', '/');
                if (!q.StartsWith('/')) q = "/" + q;
                if (q.Length > 1 && q.EndsWith('/')) q = q.TrimEnd('/');
                return q;
            }

            static IEnumerable<string> SplitIps(string? ips)
                => Nx(ips).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                          .Select(x => x.Trim());

            // ---------- Selected volumes (main DB) with UUID available ----------
            var selectedVolumes = await _context.SelectedNetappVolumes
                .AsNoTracking()
                .Where(v => v.Disabled != true)
                .Select(v => new { v.NetappControllerId, v.VolumeName, v.Id, v.SnapshotLockingEnabled, v.Uuid })
                .ToListAsync(ct);

            // Fast lookups for mapping a resolved (controller, uuid/name) → SelectedNetappVolumeId
            var selByUuid = new Dictionary<(int ControllerId, string Uuid), int>();
            var selByName = new Dictionary<(int ControllerId, string VolumeName), int>();

            foreach (var r in selectedVolumes)
            {
                if (!string.IsNullOrWhiteSpace(r.Uuid))
                {
                    var k = (r.NetappControllerId, r.Uuid!);
                    if (!selByUuid.ContainsKey(k)) selByUuid[k] = r.Id;
                }
                if (!string.IsNullOrWhiteSpace(r.VolumeName))
                {
                    var k = (r.NetappControllerId, r.VolumeName);
                    if (!selByName.ContainsKey(k)) selByName[k] = r.Id;
                }
            }

            await using var qdb = await _qdbf.CreateAsync(ct);

            // ---------- Proxmox storages (from inventory) ----------
            // NOTE: we no longer filter by storage-name vs NetApp volume-name.
            // We take all image-capable storages and let the (Server, Export) + UUID mapping
            // decide which ones have a SelectedNetappVolume backing them.
            var invStorages = await qdb.InventoryStorages
                .AsNoTracking()
                .Where(s => s.IsImageCapable)
                .Select(s => new { s.ClusterId, s.StorageId, s.NetappVolumeUuid, s.Server, s.Export })
                .ToListAsync(ct);

            var storageNames = invStorages
                .Select(s => s.StorageId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // ---------- VM mapping per storage ----------
            var diskPairs = await qdb.InventoryVmDisks
                .AsNoTracking()
                .Where(d => storageNames.Contains(d.StorageId))
                .Select(d => new { d.ClusterId, d.StorageId, d.VmId })
                .ToListAsync(ct);

            var vmIds = diskPairs.Select(p => (p.ClusterId, p.VmId)).Distinct().ToList();

            var vmIdsByCluster = vmIds
                .GroupBy(x => x.ClusterId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.VmId).ToHashSet());

            var invVms = new List<InventoryVm>();
            foreach (var (clusterId, vmSet) in vmIdsByCluster)
            {
                if (vmSet.Count == 0) continue;
                foreach (var chunk in vmSet.Chunk(900))
                {
                    var slice = await qdb.InventoryVms
                        .AsNoTracking()
                        .Where(v => v.ClusterId == clusterId && chunk.Contains(v.VmId))
                        .ToListAsync(ct);
                    invVms.AddRange(slice);
                }
            }
            var invVmsByKey = invVms.ToDictionary(v => (v.ClusterId, v.VmId));

            // ---------- Build (Server, Export/Junction) → InventoryNetappVolume map ----------
            var allInvNav = await qdb.InventoryNetappVolumes
                .AsNoTracking()
                .ToListAsync(ct);

            var invNavByServerExport =
                new Dictionary<(string Server, string Export), InventoryNetappVolume>();

            foreach (var nav in allInvNav)
            {
                var junc = NormPath(nav.JunctionPath);
                if (string.IsNullOrEmpty(junc)) continue;

                foreach (var ip in SplitIps(nav.NfsIps))
                {
                    var key = (Server: ip, Export: junc);
                    if (!invNavByServerExport.ContainsKey(key))
                    {
                        invNavByServerExport[key] = nav;
                    }
                    else
                    {
                        // prefer primary on duplicates
                        var existing = invNavByServerExport[key];
                        if ((nav.IsPrimary == true) && (existing.IsPrimary != true))
                            invNavByServerExport[key] = nav;
                    }
                }
            }

            // ---------- Replication map (by UUID) ----------
            var repRows = await qdb.InventoryVolumeReplications
                .AsNoTracking()
                .Select(r => r.PrimaryVolumeUuid)
                .ToListAsync(ct);

            var replicablePrimary = new HashSet<string>(
                repRows.Where(u => !string.IsNullOrWhiteSpace(u)),
                StringComparer.OrdinalIgnoreCase);

            // ---------- Output structures ----------
            var combined = new Dictionary<string, List<ProxmoxVM>>(StringComparer.OrdinalIgnoreCase);
            var volumeMeta = new Dictionary<string, VolumeMeta>(StringComparer.OrdinalIgnoreCase);
            var replicable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ---------- Build result per storage ----------
            foreach (var s in invStorages)
            {
                // VM list per storage
                var vms = diskPairs
                    .Where(p => p.ClusterId == s.ClusterId && p.StorageId.Equals(s.StorageId, StringComparison.OrdinalIgnoreCase))
                    .Select(p => invVmsByKey.TryGetValue((p.ClusterId, p.VmId), out var vm) ? vm : null)
                    .Where(vm => vm != null)
                    .Select(vm => new ProxmoxVM
                    {
                        Id = vm!.VmId,
                        Name = string.IsNullOrWhiteSpace(vm.Name) ? $"VM {vm.VmId}" : vm.Name,
                        HostName = vm.NodeName,
                        HostAddress = null
                    })
                    .GroupBy(x => x.Id)
                    .Select(g => g.First())
                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (vms.Count == 0) continue;
                combined[s.StorageId] = vms;

                // Resolve NetApp mapping using (server, export) → (ips, junction)
                int controllerId = 0;
                bool locking = false;
                int? selectedId = null;
                string? resolvedUuid = null;
                string? resolvedVolumeName = null;

                var serverKey = Nx(s.Server);
                var exportKey = NormPath(s.Export);

                if (!string.IsNullOrEmpty(serverKey) &&
                    !string.IsNullOrEmpty(exportKey) &&
                    invNavByServerExport.TryGetValue((serverKey, exportKey), out var matchedNav))
                {
                    controllerId = matchedNav.NetappControllerId;
                    locking = matchedNav.SnapshotLockingEnabled == true;
                    resolvedUuid = matchedNav.VolumeUuid;
                    resolvedVolumeName = matchedNav.VolumeName;
                }
                else if (!string.IsNullOrWhiteSpace(s.NetappVolumeUuid))
                {
                    // fallback by UUID already on the storage row
                    var nav = allInvNav.FirstOrDefault(v =>
                        string.Equals(v.VolumeUuid, s.NetappVolumeUuid, StringComparison.OrdinalIgnoreCase));
                    if (nav is not null)
                    {
                        controllerId = nav.NetappControllerId;
                        locking = nav.SnapshotLockingEnabled == true;
                        resolvedUuid = nav.VolumeUuid;
                        resolvedVolumeName = nav.VolumeName;
                    }
                }

                // Pick SelectedNetappVolumeId: prefer UUID, then resolved name, then legacy storage name
                if (controllerId != 0)
                {
                    if (!string.IsNullOrWhiteSpace(resolvedUuid) &&
                        selByUuid.TryGetValue((controllerId, resolvedUuid), out var viaUuid))
                    {
                        selectedId = viaUuid;
                    }
                    else if (!string.IsNullOrWhiteSpace(resolvedVolumeName) &&
                             selByName.TryGetValue((controllerId, resolvedVolumeName), out var viaName))
                    {
                        selectedId = viaName;
                    }
                    else if (selByName.TryGetValue((controllerId, s.StorageId), out var viaLegacy))
                    {
                        selectedId = viaLegacy;
                    }
                }

                // Replication flag based on UUID (if we have one)
                if (!string.IsNullOrWhiteSpace(resolvedUuid) &&
                    replicablePrimary.Contains(resolvedUuid))
                {
                    replicable.Add(s.StorageId);
                }

                // Volume meta for the UI (ClusterId, ControllerId, SelectedId, Locking, UUID)
                volumeMeta[s.StorageId] = new VolumeMeta
                {
                    ClusterId = s.ClusterId,
                    ControllerId = controllerId,
                    SnapshotLockingEnabled = locking,
                    SelectedNetappVolumeId = selectedId,
                    VolumeUuid = resolvedUuid // <- carry UUID forward
                };
            }

            return (combined, volumeMeta, replicable);
        }



    }
}
