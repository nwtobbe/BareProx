using BareProx.Data;
using BareProx.Models;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Services.Background
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
                try
                {
                    await CleanupExpired(stoppingToken);
                    await TrackNetappSnapshots(stoppingToken);

                    //await CleanupOrphanedBackupRecords(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Janitor pass failed");
                }

                await Task.Delay(_interval, stoppingToken);
            }
        }

        /// <summary>
        /// 1) Delete primary snapshots past retention,
        ///    verify deletion by re-listing, then remove DB rows only if truly gone.
        /// </summary>
        private async Task CleanupExpired(CancellationToken ct)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var netapp = scope.ServiceProvider.GetRequiredService<INetappService>();
            var now = DateTime.UtcNow;

            // preload all SnapMirror relations
            var relations = await db.SnapMirrorRelations.ToListAsync(ct);
            var relLookup = relations.ToDictionary(
                r => (r.SourceControllerId, r.SourceVolume),
                r => r);

            // find expired BackupRecords by retention
            var expired = await db.BackupRecords
                .Include(r => r.Job)
                .Where(r =>
                    (r.RetentionUnit == "Hours" && r.TimeStamp.AddHours(r.RetentionCount) < now) ||
                    (r.RetentionUnit == "Days" && r.TimeStamp.AddDays(r.RetentionCount) < now) ||
                    (r.RetentionUnit == "Weeks" && r.TimeStamp.AddDays(r.RetentionCount * 7) < now)
                )
                .ToListAsync(ct);

            foreach (var grp in expired.GroupBy(r => new
            {
                r.JobId,
                r.StorageName,
                r.SnapshotName,
                r.ControllerId
            }))
            {
                var ex = grp.First();

                // 1) Delete on primary
                bool primaryDeleted = false;
                try
                {
                    var res = await netapp.DeleteSnapshotAsync(
                        ex.ControllerId, ex.StorageName, ex.SnapshotName);

                    primaryDeleted = res.Success
                        || res.ErrorMessage?.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error deleting primary snapshot {snap}", ex.SnapshotName);
                    continue;
                }

                if (!primaryDeleted)
                    continue;

                // 2) Verify it’s truly gone on primary
                var stillOnPrimary = (await netapp.GetSnapshotsAsync(
                        ex.ControllerId, ex.StorageName))
                    .Any(n => n.Equals(ex.SnapshotName, StringComparison.OrdinalIgnoreCase));

                if (stillOnPrimary)
                    continue;

                // 3) Check for any live secondary copy
                bool hasSecondary = false;
                if (relLookup.TryGetValue((ex.ControllerId, ex.StorageName), out var rel))
                {
                    var secList = await netapp.GetSnapshotsAsync(
                        rel.DestinationControllerId,
                        rel.DestinationVolume);

                    hasSecondary = secList
                        .Any(n => n.Equals(ex.SnapshotName, StringComparison.OrdinalIgnoreCase));

                    if (hasSecondary)
                    {
                        // 4a) Update the existing NetappSnapshot row instead of deleting it
                        var snap = await db.NetappSnapshots.FirstOrDefaultAsync(s =>
                            s.JobId == ex.JobId &&
                            s.SnapshotName == ex.SnapshotName, ct);

                        if (snap != null)
                        {
                            snap.ExistsOnPrimary = false;            // primary is gone
                            snap.ExistsOnSecondary = true;
                            snap.SecondaryControllerId = rel.DestinationControllerId;
                            snap.SecondaryVolume = rel.DestinationVolume;
                            snap.IsReplicated = true;
                            snap.LastChecked = now;
                        }

                        // keep the BackupRecord + Job around so you can restore from secondary
                        _logger.LogInformation(
                            "Primary snapshot expired but secondary copy exists for {snap}, preserving record",
                            ex.SnapshotName);

                        await db.SaveChangesAsync(ct);
                        continue;
                    }
                }

                // 4b) No secondary copy → remove NetappSnapshot + BackupRecord + Job
                db.NetappSnapshots.RemoveRange(
                    db.NetappSnapshots.Where(s =>
                        s.JobId == ex.JobId &&
                        s.SnapshotName == ex.SnapshotName));

                db.BackupRecords.RemoveRange(grp);
                db.Jobs.Remove(ex.Job);

                _logger.LogInformation(
                    "Removed all DB rows for snapshot {snap}, job {job}",
                    ex.SnapshotName, ex.JobId);

                await db.SaveChangesAsync(ct);
            }
        }


        /// <summary>
        /// 2) Refresh ExistsOnPrimary/Secondary flags and LastChecked for each tracked NetappSnapshot.
        /// </summary>
        private async Task TrackNetappSnapshots(CancellationToken ct)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var netapp = scope.ServiceProvider.GetRequiredService<INetappService>();
            var now = DateTime.UtcNow;

            // 1) Load all SnapMirror relations up‐front
            var relations = await db.SnapMirrorRelations.ToListAsync(ct);

            // 2) Load every snapshot row you’re currently tracking
            var trackedSnaps = await db.NetappSnapshots
                .AsTracking()
                .ToListAsync(ct);

            // Build a lookup keyed by (JobId, SnapshotName)
      var trackedLookup = trackedSnaps
    .ToDictionary(
        s => new JobSnapKey(s.JobId, s.SnapshotName),
        s => s,
        new JobSnapKeyComparer()
    );

            // 3) For each SnapMirror relation (primary→secondary):
            foreach (var rel in relations)
            {
                // 3a) List all snapshots on the secondary side
                var secList = await netapp.GetSnapshotsAsync(
                    rel.DestinationControllerId,
                    rel.DestinationVolume);

                // 3b) List all snapshots on the primary side (to set ExistsOnPrimary)
                var primaryList = await netapp.GetSnapshotsAsync(
                    rel.SourceControllerId,
                    rel.SourceVolume);

                // 3c) For every snapshot on the secondary volume…
                foreach (var snapName in secList)
                {
                    // Look up the JobId by querying BackupRecords (which store StorageName+SnapshotName)
                    var matchingJobId = await db.BackupRecords
                        .Where(r =>
                            r.StorageName == rel.SourceVolume
                            && r.SnapshotName == snapName)
                        .Select(r => r.JobId)
                        .FirstOrDefaultAsync(ct);

                    if (matchingJobId == 0)
                        continue; // no matching BackupRecord → skip

                    // Build a JobSnapKey for dictionary lookup
                    var key = new JobSnapKey(matchingJobId, snapName);

                    if (trackedLookup.TryGetValue(key, out var existingSnap))
                    {
                        // 4a) Already tracked → update flags
                        existingSnap.ExistsOnSecondary = true;
                        existingSnap.LastChecked = now;
                        existingSnap.ExistsOnPrimary = primaryList
                            .Any(x => x.Equals(snapName, StringComparison.OrdinalIgnoreCase));

                        existingSnap.SecondaryControllerId = rel.DestinationControllerId;
                        existingSnap.SecondaryVolume = rel.DestinationVolume;
                        existingSnap.IsReplicated = true;
                    }
                    else
                    {
                        // 4b) Not yet tracked → insert a new NetappSnapshot
                        var label = await db.BackupRecords
                            .Where(r =>
                                r.JobId == matchingJobId &&
                                r.StorageName == rel.SourceVolume &&
                                r.SnapshotName == snapName)
                            .Select(r => r.RetentionUnit.ToLower())
                            .FirstOrDefaultAsync(ct)
                            ?? "not_found";

                        var newSnap = new NetappSnapshot
                        {
                            CreatedAt = now,
                            ExistsOnPrimary = primaryList
                                .Any(x => x.Equals(snapName, StringComparison.OrdinalIgnoreCase)),
                            ExistsOnSecondary = true,
                            IsReplicated = true,
                            JobId = matchingJobId,
                            LastChecked = now,
                            PrimaryControllerId = rel.SourceControllerId,
                            PrimaryVolume = rel.SourceVolume,
                            SecondaryControllerId = rel.DestinationControllerId,
                            SecondaryVolume = rel.DestinationVolume,
                            SnapmirrorLabel = label,
                            SnapshotName = snapName
                        };

                        db.NetappSnapshots.Add(newSnap);
                        trackedLookup[key] = newSnap;
                    }
                }
            }

            // 5) Save all updates/inserts at once
            await db.SaveChangesAsync(ct);
        }

      


        private async Task CleanupOrphanedBackupRecords(CancellationToken ct)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var netapp = scope.ServiceProvider.GetRequiredService<INetappService>();
            var now = DateTime.UtcNow;

            // 1) Load all SnapMirror relations into a lookup
            var relations = await db.SnapMirrorRelations.ToListAsync(ct);
            var relLookup = relations.ToDictionary(r => (r.SourceControllerId, r.SourceVolume), r => r);

            // 2) Load all BackupRecords whose retention is still in the future
            var activeRecs = await db.BackupRecords
                .Where(r =>
                    (r.RetentionUnit == "Hours" && r.TimeStamp.AddHours(r.RetentionCount) >= now) ||
                    (r.RetentionUnit == "Days" && r.TimeStamp.AddDays(r.RetentionCount) >= now) ||
                    (r.RetentionUnit == "Weeks" && r.TimeStamp.AddDays(r.RetentionCount * 7) >= now)
                )
                .ToListAsync(ct);

            var toDeleteRecords = new List<BackupRecord>();
            var toDeleteJobs = new List<Job>();

            foreach (var rec in activeRecs)
            {
                // a) Check primary
                var primaryList = await netapp.GetSnapshotsAsync(rec.ControllerId, rec.StorageName);
                bool existsOnPrimary = primaryList
                    .Any(n => n.Equals(rec.SnapshotName, StringComparison.OrdinalIgnoreCase));

                // b) If not on primary, check secondary (if a relation exists)
                bool existsOnSecondary = false;
                if (!existsOnPrimary && relLookup.TryGetValue((rec.ControllerId, rec.StorageName), out var rel))
                {
                    var secList = await netapp.GetSnapshotsAsync(
                        rel.DestinationControllerId,
                        rel.DestinationVolume);

                    existsOnSecondary = secList
                        .Any(n => n.Equals(rec.SnapshotName, StringComparison.OrdinalIgnoreCase));
                }

                // c) If neither primary nor secondary has this snapshot, mark for deletion
                if (!existsOnPrimary && !existsOnSecondary)
                {
                    toDeleteRecords.Add(rec);

                    // If this Job has no other BackupRecords, schedule the Job for deletion too
                    bool hasOther = await db.BackupRecords
                        .AnyAsync(r => r.JobId == rec.JobId && r.Id != rec.Id, ct);

                    if (!hasOther)
                    {
                        var job = await db.Jobs.FindAsync(new object[] { rec.JobId }, ct);
                        if (job != null) toDeleteJobs.Add(job);
                    }
                }
            }

            // 3) Delete them all in one batch
            if (toDeleteRecords.Count > 0)
            {
                db.BackupRecords.RemoveRange(toDeleteRecords);
                db.Jobs.RemoveRange(toDeleteJobs);
                await db.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "Removed {RecordCount} orphaned BackupRecords (and {JobCount} Jobs) not found on primary or secondary",
                    toDeleteRecords.Count, toDeleteJobs.Count);
            }
        }

        private readonly struct JobSnapKey
        {
            public int JobId { get; }
            public string SnapshotName { get; }

            public JobSnapKey(int jobId, string snapshotName)
            {
                JobId = jobId;
                SnapshotName = snapshotName;
            }

            public override bool Equals(object? obj)
            {
                if (obj is not JobSnapKey other) return false;
                return JobId == other.JobId
                    && string.Equals(SnapshotName, other.SnapshotName, StringComparison.OrdinalIgnoreCase);
            }

            public override int GetHashCode()
                => HashCode.Combine(JobId, SnapshotName?.ToLowerInvariant());
        }


        private class JobSnapKeyComparer : IEqualityComparer<JobSnapKey>
        {
            public bool Equals(JobSnapKey x, JobSnapKey y)
                => x.JobId == y.JobId
                && string.Equals(x.SnapshotName, y.SnapshotName, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode(JobSnapKey obj)
                => HashCode.Combine(obj.JobId, obj.SnapshotName?.ToLowerInvariant());
        }
    }
}
