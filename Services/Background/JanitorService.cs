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
using BareProx.Services.Netapp;
using Microsoft.Data.Sqlite;
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
                    await PruneOldOrStuckJobs(stoppingToken);

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
            var netapp = scope.ServiceProvider.GetRequiredService<INetappFlexCloneService>();
            var netappSnapshotService = scope.ServiceProvider.GetRequiredService<INetappSnapshotService>();
            var now = DateTime.UtcNow;

            var relations = await db.SnapMirrorRelations.AsNoTracking().ToListAsync(ct);
            var relLookup = relations.ToDictionary(
                r => (r.SourceControllerId, r.SourceVolume),
                r => r);

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

                using var transaction = await db.Database.BeginTransactionAsync(ct);

                try
                {
                    var deleteRes = await netappSnapshotService.DeleteSnapshotAsync(
                        ex.ControllerId, ex.StorageName, ex.SnapshotName, ct);

                    if (!deleteRes.Success && !(deleteRes.ErrorMessage?.Contains("not found", StringComparison.OrdinalIgnoreCase) ?? false))
                    {
                        _logger.LogWarning("Failed to delete snapshot {snap}: {error}", ex.SnapshotName, deleteRes.ErrorMessage);
                        continue;
                    }

                    // Verify snapshot deletion explicitly
                    var primarySnapshots = await netappSnapshotService.GetSnapshotsAsync(ex.ControllerId, ex.StorageName, ct);
                    if (primarySnapshots.Any(n => n.Equals(ex.SnapshotName, StringComparison.OrdinalIgnoreCase)))
                    {
                        _logger.LogWarning("Snapshot {snap} still exists on primary after deletion attempt", ex.SnapshotName);
                        continue;
                    }

                    if (relLookup.TryGetValue((ex.ControllerId, ex.StorageName), out var rel))
                    {
                        var secondarySnapshots = await netappSnapshotService.GetSnapshotsAsync(rel.DestinationControllerId, rel.DestinationVolume, ct);
                        bool existsOnSecondary = secondarySnapshots.Any(n => n.Equals(ex.SnapshotName, StringComparison.OrdinalIgnoreCase));

                        var snapRecord = await db.NetappSnapshots.FirstOrDefaultAsync(s =>
                            s.JobId == ex.JobId &&
                            s.SnapshotName == ex.SnapshotName, ct);

                        if (existsOnSecondary)
                        {
                            if (snapRecord != null)
                            {
                                snapRecord.ExistsOnPrimary = false;
                                snapRecord.ExistsOnSecondary = true;
                                snapRecord.SecondaryControllerId = rel.DestinationControllerId;
                                snapRecord.SecondaryVolume = rel.DestinationVolume;
                                snapRecord.IsReplicated = true;
                                snapRecord.LastChecked = now;
                            }

                            _logger.LogInformation("Snapshot {snap} exists on secondary, preserving record.", ex.SnapshotName);
                            await db.SaveChangesAsync(ct);
                            await transaction.CommitAsync(ct);
                            continue;
                        }
                    }

                    // Remove related records if no secondary exists
                    db.NetappSnapshots.RemoveRange(db.NetappSnapshots.Where(s => s.JobId == ex.JobId && s.SnapshotName == ex.SnapshotName));
                    db.BackupRecords.RemoveRange(grp);
                    db.Jobs.Remove(ex.Job);

                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);

                    _logger.LogInformation("Successfully removed snapshot {snap} and related records.", ex.SnapshotName);
                }
                catch (Exception e)
                {
                    await transaction.RollbackAsync(ct);
                    _logger.LogError(e, "Error processing expired snapshot {snap}", ex.SnapshotName);
                }
            }
        }


        /// <summary>
        /// 2) Refresh ExistsOnPrimary/Secondary flags and LastChecked for each tracked NetappSnapshot.
        /// </summary>
        private async Task TrackNetappSnapshots(CancellationToken ct)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var netapp = scope.ServiceProvider.GetRequiredService<INetappFlexCloneService>();
            var netappSnapshotService = scope.ServiceProvider.GetRequiredService<INetappSnapshotService>();
            var now = DateTime.UtcNow;

            // 0) Preload all valid NetappController IDs
            var validControllerIds = await db.NetappControllers
                .Select(n => n.Id)
                .ToListAsync(ct);
            var validSet = new HashSet<int>(validControllerIds);

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
                // ─── Skip if either controller ID is invalid ────────────────────────────
                if (!validSet.Contains(rel.SourceControllerId)
                    || !validSet.Contains(rel.DestinationControllerId))
                {
                    _logger.LogWarning(
                        "Skipping relation {Uuid} because source or destination controller is invalid ({src} → {dst})",
                        rel.Uuid,
                        rel.SourceControllerId,
                        rel.DestinationControllerId
                    );
                    continue;
                }

                // 3a) List all snapshots on the secondary side
                var secList = await netappSnapshotService.GetSnapshotsAsync(
                    rel.DestinationControllerId,
                    rel.DestinationVolume, ct);

                // 3b) List all snapshots on the primary side (to set ExistsOnPrimary)
                var primaryList = await netappSnapshotService.GetSnapshotsAsync(
                    rel.SourceControllerId,
                    rel.SourceVolume, ct);

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
            // Retry save on error
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    await db.SaveChangesAsync(ct);
                    break;
                }
                catch (DbUpdateException ey)
                    when (ey.InnerException is SqliteException se && se.SqliteErrorCode == 5)
                {
                    if (i == 2) throw;
                    await Task.Delay(500, ct);
                }
            }
        }

        private async Task PruneOldOrStuckJobs(CancellationToken ct)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var now = DateTime.UtcNow;
            var cutoff = now.AddDays(-30);

            // Age pivot for a job is CompletedAt if present, otherwise StartedAt
            var toDeleteIds = await db.Jobs
                .AsNoTracking()
                .Where(j =>
                    (j.CompletedAt ?? j.StartedAt) < cutoff &&
                    (
                        // 1) Any job with Status != "Completed"
                        (j.Status != null && !EF.Functions.Like(j.Status, "Completed"))
                        ||
                        // 2) Any Restore job (any status)
                        (j.Type != null && EF.Functions.Like(j.Type, "Restore"))
                    )
                )
                .Select(j => j.Id)
                .ToListAsync(ct);

            if (toDeleteIds.Count == 0)
            {
                _logger.LogDebug("PruneJobs: nothing to delete.");
                return;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                using var tx = await db.Database.BeginTransactionAsync(ct);
                try
                {
                    // Remove dependents first (adjust if you have cascades)
                    db.NetappSnapshots.RemoveRange(
                        db.NetappSnapshots.Where(s => toDeleteIds.Contains(s.JobId)));

                    db.BackupRecords.RemoveRange(
                        db.BackupRecords.Where(r => toDeleteIds.Contains(r.JobId)));

                    // Finally remove the jobs
                    db.Jobs.RemoveRange(
                        db.Jobs.Where(j => toDeleteIds.Contains(j.Id)));

                    var affected = await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);

                    _logger.LogInformation(
                        "Pruned {JobCount} old jobs (>30d): non-completed and all Restore. Rows affected: {Rows}.",
                        toDeleteIds.Count, affected);
                    break;
                }
                catch (DbUpdateException ey) when (ey.InnerException is Microsoft.Data.Sqlite.SqliteException se && se.SqliteErrorCode == 5)
                {
                    await tx.RollbackAsync(ct);
                    if (attempt == 2) throw;
                    await Task.Delay(500, ct);
                }
                catch (Exception ex)
                {
                    await tx.RollbackAsync(ct);
                    _logger.LogError(ex, "Failed pruning jobs.");
                    throw;
                }
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
