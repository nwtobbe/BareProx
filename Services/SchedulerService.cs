using BareProx.Controllers;
using BareProx.Data;
using BareProx.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BareProx.Services
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
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<ScheduledBackupService> _logger;
        private const int IntervalSeconds = 30;

        public ScheduledBackupService(
            IServiceProvider services,
            IHttpClientFactory httpClientFactory,
            ILogger<ScheduledBackupService> logger)
        {
            _services = services;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
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
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var backupService = scope.ServiceProvider.GetRequiredService<IBackupService>();
            var schedLogger = scope.ServiceProvider.GetRequiredService<ILogger<ScheduledBackupService>>();
            var ctrlLogger = scope.ServiceProvider.GetRequiredService<ILogger<BackupController>>();

            // Retrieve IDs for cluster and NetApp controller (assuming single entries)
            var clusterId = await db.ProxmoxClusters.Select(c => c.Id).FirstOrDefaultAsync(ct);
            var netappControllerId = await db.NetappControllers.Select(n => n.Id).FirstOrDefaultAsync(ct);

            var utcNow = DateTime.UtcNow;

            // Load schedules with tracking so we can update LastRun
            var schedules = await db.BackupSchedules.ToListAsync(ct);

            foreach (var sched in schedules)
            {
                if (!IsDue(sched, utcNow))
                    continue;

                try
                {
                    // Log start of backup (using controller's logger for consistency)
                    ctrlLogger.LogInformation("Starting background backup for storage {StorageName}", sched.StorageName);

                    await backupService.StartBackupAsync(
                        sched.StorageName,
                        sched.IsApplicationAware,
                        sched.Name,                // schedule label as backup label
                        clusterId,
                        netappControllerId,
                        sched.RetentionCount,
                        sched.RetentionUnit,
                        sched.EnableIoFreeze,
                        sched.UseProxmoxSnapshot,
                        sched.WithMemory,
                        dontTrySuspend: false,
                        ScheduleID: sched.Id
                    );

                    // Log completion
                    ctrlLogger.LogInformation("Backup process completed for {StorageName}", sched.StorageName);

                    // update LastRun
                    sched.LastRun = utcNow;
                    schedLogger.LogInformation("Updated LastRun for schedule {ScheduleId}", sched.Id);
                }
                catch (Exception ex)
                {
                    ctrlLogger.LogError(ex, "Backup failed for {StorageName}: {Message}", sched.StorageName, ex.Message);
                }
            }

            await db.SaveChangesAsync(ct);
        }


        private bool IsDue(BackupSchedule sched, DateTime utcNow)
        {
            var currentDay = utcNow.ToString("ddd", CultureInfo.InvariantCulture);
            var currentHour = utcNow.Hour;
            var currentMinute = utcNow.Minute;
            var currentSecond = utcNow.Second;

            switch (sched.Schedule)
            {
                case "Hourly":
                    var parts = sched.Frequency.Split('-');
                    if (parts.Length != 2) return false;
                    if (!int.TryParse(parts[0], out var startH)) return false;
                    if (!int.TryParse(parts[1], out var endH)) return false;

                    // Only run within the first IntervalSeconds of each hour
                    if (currentHour < startH || currentHour > endH) return false;
                    if (currentMinute != 0 || currentSecond >= IntervalSeconds) return false;

                    return sched.LastRun == null ||
                           sched.LastRun.Value.Hour != currentHour ||
                           sched.LastRun.Value.Date != utcNow.Date;

                case "Daily":
                    if (!sched.TimeOfDay.HasValue) return false;
                    var days = sched.Frequency.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (days.Length > 0 && !days.Contains(currentDay, StringComparer.OrdinalIgnoreCase))
                        return false;

                    var scheduledDaily = utcNow.Date + sched.TimeOfDay.Value;
                    return utcNow >= scheduledDaily &&
                           (sched.LastRun == null || sched.LastRun.Value.Date < utcNow.Date);

                case "Weekly":
                    if (!sched.TimeOfDay.HasValue) return false;
                    var weekDays = sched.Frequency.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (!weekDays.Contains(currentDay, StringComparer.OrdinalIgnoreCase))
                        return false;

                    var scheduledWeekly = utcNow.Date + sched.TimeOfDay.Value;
                    return utcNow >= scheduledWeekly &&
                           (sched.LastRun == null || sched.LastRun.Value.Date < utcNow.Date);

                default:
                    return false;
            }
        }
    }
}
