using BareProx.Data;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using Microsoft.AspNetCore.Mvc;

namespace BareProx.Services
{
    public class JanitorService : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly TimeSpan _interval = TimeSpan.FromMinutes(5);
        private readonly ILogger<JanitorService> _logger;

        public JanitorService(IServiceProvider services, ILogger<JanitorService> logger)
        {
            _services = services;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await CleanupExpired(stoppingToken); }
                catch { /* log if desired */ }

                await Task.Delay(_interval, stoppingToken);
            }
        }

        private async Task CleanupExpired(CancellationToken ct)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var netapp = scope.ServiceProvider.GetRequiredService<INetappService>();
            var now = DateTime.UtcNow;

            // 1) Find all expired records
            var expired = await db.BackupRecords
                .Include(r => r.Job)
                .Where(r =>
                    (r.RetentionUnit == "Hours" && r.TimeStamp.AddHours(r.RetentionCount) < now) ||
                    (r.RetentionUnit == "Days" && r.TimeStamp.AddDays(r.RetentionCount) < now) ||
                    (r.RetentionUnit == "Weeks" && r.TimeStamp.AddDays(r.RetentionCount * 7) < now)
                )
                .ToListAsync(ct);

            // 2) Group by JobId (so we only delete each snapshot once)
            foreach (var grp in expired.GroupBy(r => r.JobId))
            {
                var example = grp.First();

                bool removeRecords = false;
                try
                {
                    var deleteResult = await netapp.DeleteSnapshotAsync(
                        example.ControllerId,
                        example.StorageName,
                        example.SnapshotName
                    );

                    if (deleteResult.Success)
                    {
                        removeRecords = true;
                    }
                    else if (deleteResult.ErrorMessage?.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _logger.LogInformation(
                            "Snapshot {SnapshotName} already gone; cleaning up DB records for job {JobId}.",
                            example.SnapshotName, example.JobId);
                        removeRecords = true;
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Snapshot delete failed for {SnapshotName}: {Reason}. Keeping DB records for job {JobId}.",
                            example.SnapshotName, deleteResult.ErrorMessage, example.JobId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Exception while deleting snapshot {SnapshotName} for job {JobId}: {Message}. Skipping cleanup.",
                        example.SnapshotName, example.JobId, ex.Message);
                }

                if (removeRecords)
                {
                    db.BackupRecords.RemoveRange(grp);
                    db.Jobs.Remove(example.Job);
                }
            }

            // 3) Persist all the deletions at once
            await db.SaveChangesAsync(ct);
        }


    }

}
