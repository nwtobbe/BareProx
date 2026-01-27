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


using BareProx.Controllers;
using BareProx.Data;
using BareProx.Models;
using BareProx.Services.Backup;
using BareProx.Services.Netapp;
using BareProx.Services.Proxmox;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace BareProx.Services.Background
{
    /// <summary>
    /// Background service that reads all defined backup schedules
    /// and posts a BackupRequest for each schedule to the BackupController,
    /// then updates the LastRun timestamp on each schedule.
    /// Honors Hourly, Daily, and Weekly schedules based on Frequency and TimeOfDay.
    /// Runs every 30 seconds to minimize delay.
    /// </summary>
    public class ScheduledBackupService : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly IDbFactory _dbf;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<ScheduledBackupService> _logger;
        private const int IntervalSeconds = 30;
        private readonly IAppTimeZoneService _tz;
        private readonly IBackgroundServiceQueue _queue;

        public ScheduledBackupService(
            IServiceProvider services,
            IDbFactory dbf,
            IHttpClientFactory httpClientFactory,
            ILogger<ScheduledBackupService> logger,
            IAppTimeZoneService tz,
            IBackgroundServiceQueue queue)
        {
            _services = services;
            _dbf = dbf;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _tz = tz;
            _queue = queue;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await DispatchScheduledBackupsAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error dispatching scheduled backups");
                }

                await Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), stoppingToken);
            }
        }

        private async Task DispatchScheduledBackupsAsync(CancellationToken ct)
        {
            using var scope = _services.CreateScope();

            // DbContext via factory (pooled, safe for singleton)
            await using var db = await _dbf.CreateAsync(ct);

            var ctrlLogger = scope.ServiceProvider.GetRequiredService<ILogger<BackupController>>();

            var localtime = _tz.ConvertUtcToApp(DateTime.UtcNow);

            // Pull only enabled schedules (already filtered) but keep a cheap safety check below.
            var schedules = await db.BackupSchedules
                .Where(s => s.IsEnabled)
                .ToListAsync(ct);

            foreach (var sched in schedules)
            {
                if (!sched.IsEnabled || !IsDue(sched, localtime))
                    continue;

                try
                {
                    var controller = await db.NetappControllers
                        .FindAsync(new object[] { sched.ControllerId }, ct);

                    var cluster = await db.ProxmoxClusters
                        .Include(c => c.Hosts)
                        .FirstOrDefaultAsync(c => c.Id == sched.ClusterId, ct);

                    if (controller == null || cluster == null)
                    {
                        ctrlLogger.LogWarning("Missing controller or cluster for schedule {Id}", sched.Id);
                        continue;
                    }

                    // Parse excluded VM IDs from CSV (may be null/empty)
                    string[] excludedVmIds = Array.Empty<string>();
                    if (!string.IsNullOrWhiteSpace(sched.ExcludedVmIds))
                    {
                        excludedVmIds = sched.ExcludedVmIds
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Distinct()
                            .ToArray();
                    }

                    ctrlLogger.LogInformation("Queueing background backup for storage {StorageName} (ScheduleId={ScheduleId})",
                        sched.StorageName, sched.Id);

                    // IMPORTANT: capture primitive values only (do NOT capture the EF entity)
                    var storageName = sched.StorageName;
                    var selectedNetappVolumeId = sched.SelectedNetappVolumeId;
                    var volumeUuid = sched.VolumeUuid;
                    var isApplicationAware = sched.IsApplicationAware;
                    var label = (sched.Schedule ?? "").ToLowerInvariant();
                    var clusterId = sched.ClusterId;
                    var controllerId = sched.ControllerId;
                    var retentionCount = sched.RetentionCount;
                    var retentionUnit = sched.RetentionUnit;
                    var enableIoFreeze = sched.EnableIoFreeze;
                    var useProxmoxSnapshot = sched.UseProxmoxSnapshot;
                    var withMemory = sched.WithMemory;
                    var scheduleId = sched.Id;
                    var replicateToSecondary = sched.ReplicateToSecondary;
                    var enableLocking = sched.EnableLocking;
                    var lockRetentionCount = sched.EnableLocking ? sched.LockRetentionCount : null;
                    var lockRetentionUnit = sched.EnableLocking ? sched.LockRetentionUnit : null;

                    _queue.QueueBackgroundWorkItem(async workerToken =>
                    {
                        // If host is shutting down, don't start new work
                        if (workerToken.IsCancellationRequested)
                            return;

                        using var innerScope = _services.CreateScope();
                        var scopedBackupService = innerScope.ServiceProvider.GetRequiredService<IBackupService>();

                        // Decouple job token from worker token to avoid phantom "Job was cancelled"
                        using var jobCts = new CancellationTokenSource();

                        await scopedBackupService.StartBackupAsync(
                            storageName: storageName,
                            selectedNetappVolumeId: selectedNetappVolumeId,
                            volumeUuid: volumeUuid,
                            isApplicationAware: isApplicationAware,
                            label: label,
                            clusterId: clusterId,
                            netappControllerId: controllerId,
                            retentionCount: retentionCount,
                            retentionUnit: retentionUnit,
                            enableIoFreeze: enableIoFreeze,
                            useProxmoxSnapshot: useProxmoxSnapshot,
                            withMemory: withMemory,
                            dontTrySuspend: false,
                            scheduleId: scheduleId,
                            replicateToSecondary: replicateToSecondary,
                            enableLocking: enableLocking,
                            lockRetentionCount: lockRetentionCount,
                            lockRetentionUnit: lockRetentionUnit,
                            excludedVmIds: excludedVmIds.Length == 0 ? null : excludedVmIds,
                            ct: jobCts.Token
                        );
                    });

                    // Mark LastRun when queued (your existing behavior)
                    sched.LastRun = localtime;
                }
                catch (Exception ex)
                {
                    ctrlLogger.LogError(ex, "Backup failed for {StorageName}: {Message}", sched.StorageName, ex.Message);
                }
            }

            // Retry save on transient SQLITE_BUSY
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    await db.SaveChangesAsync(ct);
                    break;
                }
                catch (DbUpdateException ey) when (ey.InnerException is SqliteException se && se.SqliteErrorCode == 5)
                {
                    if (i == 2) throw;
                    await Task.Delay(500, ct);
                }
            }
        }



        private bool IsDue(BackupSchedule sched, DateTime now)
        {
            var currentDay = now.ToString("ddd", CultureInfo.InvariantCulture);

            switch (sched.Schedule)
            {
                case "Hourly":
                    // Example: interpret Frequency = "8-17" as hours of the day.
                    if (string.IsNullOrWhiteSpace(sched.Frequency))
                        return false;

                    // Parse “8-17” → startHour=8, endHour=17
                    var parts = sched.Frequency.Split('-', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2
                        || !int.TryParse(parts[0], out var startHour)
                        || !int.TryParse(parts[1], out var endHour))
                    {
                        return false;
                    }

                    // Only run on the hour (minute=0, second < IntervalSeconds window)
                    if (now.Minute != 0)
                        return false;
                    // Only run if within [startHour, endHour)
                    if (now.Hour < startHour || now.Hour > endHour)
                        return false;

                    // Only once per hour (LastRun before the top‐of‐hour)
                    var thisHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
                    return sched.LastRun == null || sched.LastRun.Value < thisHour;

                case "Daily":
                case "Weekly":
                    if (!sched.TimeOfDay.HasValue)
                        return false;

                    // if they specified particular days, enforce them
                    var days = (sched.Frequency ?? "")
                                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                                .Select(d => d.Trim())
                                .ToList();
                    if (days.Any() && !days.Contains(currentDay, StringComparer.OrdinalIgnoreCase))
                        return false;

                    // only fire in the [scheduled, scheduled+IntervalSeconds) window
                    var scheduled = now.Date + sched.TimeOfDay.Value;
                    if (now < scheduled || now >= scheduled.AddSeconds(IntervalSeconds))
                        return false;

                    // and only once in that window
                    return sched.LastRun == null
                        || sched.LastRun.Value < scheduled;

                default:
                    return false;
            }
        }

    }
}

