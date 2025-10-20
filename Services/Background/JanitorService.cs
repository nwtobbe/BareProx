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
        /// Refresh ExistsOnPrimary/Secondary flags and LastChecked for each tracked NetappSnapshot,
        /// upsert rows when a snapshot is found on primary or secondary (only if a matching BackupRecord exists),
        /// and REMOVE rows that are gone from BOTH primary and secondary (manual delete / never created).
        /// </summary>
        private async Task TrackNetappSnapshots(CancellationToken ct)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var netappSnapshotService = scope.ServiceProvider.GetRequiredService<INetappSnapshotService>();
            var now = DateTime.UtcNow;

            // ---- Load base data ------------------------------------------------------
            var validControllerIds = await db.NetappControllers.Select(n => n.Id).ToListAsync(ct);
            var valid = new HashSet<int>(validControllerIds);

            var relations = await db.SnapMirrorRelations.AsNoTracking().ToListAsync(ct);

            // All tracked rows (to update/remove). Key by lower-case snapshot name to avoid custom comparers.
            var tracked = await db.NetappSnapshots.AsTracking().ToListAsync(ct);
            var trackedByKey = tracked.ToDictionary(
                s => (s.JobId, (s.SnapshotName ?? string.Empty).ToLowerInvariant()),
                s => s
            );

            // Map (source volume, snapshot name) -> JobId from existing BackupRecords (no guessing).
            var volumesOfInterest = relations
                .Select(r => r.SourceVolume)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var backupRecords = await db.BackupRecords
                .Where(r => volumesOfInterest.Contains(r.StorageName))
                .Select(r => new { r.JobId, r.StorageName, r.SnapshotName })
                .ToListAsync(ct);

            var jobByVolumeSnap = backupRecords
                .GroupBy(x => (Vol: (x.StorageName ?? "").ToLowerInvariant(),
                               Snap: (x.SnapshotName ?? "").ToLowerInvariant()))
                .ToDictionary(
                    g => (g.Key.Vol, g.Key.Snap),
                    g => g.Select(x => x.JobId).First()
                );

            // For cleanup: quick lookup for relation by (srcControllerId, srcVolume lower-cased)
            var relBySrc = relations.ToDictionary(
                r => (r.SourceControllerId, (r.SourceVolume ?? "").ToLowerInvariant()),
                r => r
            );

            // Store discovered sets per relation so we can use them again during cleanup
            var primarySets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var secondarySets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            // ---- Upsert/update based on what actually exists now ---------------------
            foreach (var rel in relations)
            {
                if (!valid.Contains(rel.SourceControllerId) || !valid.Contains(rel.DestinationControllerId))
                {
                    _logger.LogWarning("TrackSnapshots: Skip relation {Uuid} due to invalid controllers ({Src}->{Dst})",
                        rel.Uuid, rel.SourceControllerId, rel.DestinationControllerId);
                    continue;
                }

                // Query once per relation (primary + secondary)
                var primList = await netappSnapshotService.GetSnapshotsAsync(rel.SourceControllerId, rel.SourceVolume, ct);
                var secList = await netappSnapshotService.GetSnapshotsAsync(rel.DestinationControllerId, rel.DestinationVolume, ct);

                var primSet = new HashSet<string>(primList, StringComparer.OrdinalIgnoreCase);
                var secSet = new HashSet<string>(secList, StringComparer.OrdinalIgnoreCase);

                primarySets[$"{rel.SourceControllerId}|{rel.SourceVolume}"] = primSet;
                secondarySets[$"{rel.DestinationControllerId}|{rel.DestinationVolume}"] = secSet;

                // ---- Upsert for SECONDARY: ensure we track snapshots that exist on secondary
                foreach (var snap in secSet)
                {
                    var keyVol = (rel.SourceVolume ?? "").ToLowerInvariant();
                    var keySnap = (snap ?? "").ToLowerInvariant();

                    if (!jobByVolumeSnap.TryGetValue((keyVol, keySnap), out var jobId))
                        continue; // no matching BackupRecord → skip

                    if (trackedByKey.TryGetValue((jobId, keySnap), out var row))
                    {
                        row.ExistsOnSecondary = true;
                        row.ExistsOnPrimary = primSet.Contains(snap);
                        row.IsReplicated = true;
                        row.SecondaryControllerId = rel.DestinationControllerId;
                        row.SecondaryVolume = rel.DestinationVolume;
                        row.PrimaryControllerId = rel.SourceControllerId;
                        row.PrimaryVolume = rel.SourceVolume;
                        row.LastChecked = now;
                    }
                    else
                    {
                        var newRow = new NetappSnapshot
                        {
                            JobId = jobId,
                            SnapshotName = snap,
                            CreatedAt = now, // internal bookkeeping; NOT backup timestamp
                            LastChecked = now,
                            ExistsOnSecondary = true,
                            ExistsOnPrimary = primSet.Contains(snap),
                            IsReplicated = true,
                            PrimaryControllerId = rel.SourceControllerId,
                            PrimaryVolume = rel.SourceVolume,
                            SecondaryControllerId = rel.DestinationControllerId,
                            SecondaryVolume = rel.DestinationVolume,
                            SnapmirrorLabel = null
                        };
                        db.NetappSnapshots.Add(newRow);
                        trackedByKey[(jobId, keySnap)] = newRow;
                    }
                }

                // ---- Upsert for PRIMARY: track snapshots that exist on primary even if missing on secondary
                foreach (var snap in primSet)
                {
                    var keyVol = (rel.SourceVolume ?? "").ToLowerInvariant();
                    var keySnap = (snap ?? "").ToLowerInvariant();

                    if (!jobByVolumeSnap.TryGetValue((keyVol, keySnap), out var jobId))
                        continue; // only act when a matching BackupRecord exists

                    if (trackedByKey.ContainsKey((jobId, keySnap)))
                        continue; // already tracked (from secondary pass or earlier)

                    var existsOnSecondary = secSet.Contains(snap);

                    var newRow = new NetappSnapshot
                    {
                        JobId = jobId,
                        SnapshotName = snap,
                        CreatedAt = now, // internal bookkeeping; NOT backup timestamp
                        LastChecked = now,
                        ExistsOnPrimary = true,
                        ExistsOnSecondary = existsOnSecondary,
                        IsReplicated = existsOnSecondary,
                        PrimaryControllerId = rel.SourceControllerId,
                        PrimaryVolume = rel.SourceVolume,
                        SecondaryControllerId = rel.DestinationControllerId,
                        SecondaryVolume = rel.DestinationVolume,
                        SnapmirrorLabel = null
                    };
                    db.NetappSnapshots.Add(newRow);
                    trackedByKey[(jobId, keySnap)] = newRow;
                }

                // Refresh ExistsOnPrimary for any tracked row that belongs to this (source) relation
                foreach (var row in tracked.Where(t =>
                             t.PrimaryControllerId == rel.SourceControllerId &&
                             string.Equals(t.PrimaryVolume, rel.SourceVolume, StringComparison.OrdinalIgnoreCase)))
                {
                    row.ExistsOnPrimary = primSet.Contains(row.SnapshotName);
                    row.LastChecked = now;
                }
            }

            // ---- Cleanup: remove rows that are gone from BOTH sides ------------------
            var grace = TimeSpan.FromMinutes(15);
            var toRemove = new List<NetappSnapshot>();

            foreach (var row in tracked)
            {
                // Existence on primary
                bool onPrimary = false;
                if (row.PrimaryControllerId > 0 && !string.IsNullOrWhiteSpace(row.PrimaryVolume))
                {
                    var key = $"{row.PrimaryControllerId}|{row.PrimaryVolume}";
                    if (primarySets.TryGetValue(key, out var primSet))
                        onPrimary = primSet.Contains(row.SnapshotName);
                }

                // Existence on secondary
                bool onSecondary = false;
                if (row.SecondaryControllerId > 0 && !string.IsNullOrWhiteSpace(row.SecondaryVolume))
                {
                    var key = $"{row.SecondaryControllerId}|{row.SecondaryVolume}";
                    if (secondarySets.TryGetValue(key, out var secSet))
                        onSecondary = secSet.Contains(row.SnapshotName);
                }

                // Update flags from cached sets
                row.ExistsOnPrimary = onPrimary;
                row.ExistsOnSecondary = onSecondary;
                row.IsReplicated = onSecondary;
                row.LastChecked = now;

                // If missing on both sides and it's not "too fresh", delete the tracking row
                var createdAt = row.CreatedAt == default ? now : row.CreatedAt;
                var tooFresh = (now - createdAt) < grace;
                if (!onPrimary && !onSecondary && !tooFresh)
                {
                    toRemove.Add(row);
                }
            }

            if (toRemove.Count > 0)
            {
                _logger.LogInformation(
                    "TrackSnapshots: removing {Count} NetappSnapshot rows that are missing on both primary/secondary.",
                    toRemove.Count);
                db.NetappSnapshots.RemoveRange(toRemove);
            }

            // ---- Save with retry on SQLITE_BUSY --------------------------------------
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
