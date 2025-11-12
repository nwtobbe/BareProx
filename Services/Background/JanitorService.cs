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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Services.Background
{
    /// <summary>
    /// DB-only housekeeping: orphan purges and old/stuck job pruning.
    /// NetApp snapshot retention & presence checks are enforced by CollectionService.
    /// Snapshot tracking is stored in the Query DB.
    /// </summary>
    public sealed class JanitorService : BackgroundService
    {
        private readonly IDbFactory _dbf;
        private readonly IQueryDbFactory _qdbf;
        private readonly ILogger<JanitorService> _logger;

        private static readonly TimeSpan Cadence = TimeSpan.FromMinutes(5);

        public JanitorService(
            IDbFactory dbf,
            IQueryDbFactory qdbf,
            ILogger<JanitorService> logger)
        {
            _dbf = dbf;
            _qdbf = qdbf;
            _logger = logger;
        }

        private static readonly TimeSpan WarmupMaxWait = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan WarmupPoll = TimeSpan.FromSeconds(10);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // --- Warm-up: avoid pruning before CollectionService seeds tracker after fresh boot/DB move ---
            var warmupMaxWait = TimeSpan.FromMinutes(3);
            var warmupPoll = TimeSpan.FromSeconds(10);

            try
            {
                await using var main = await _dbf.CreateAsync(stoppingToken);
                await using var qdb = await _qdbf.CreateAsync(stoppingToken);

                bool trackerEmpty = !await qdb.NetappSnapshots.AnyAsync(stoppingToken);

                // Only bother warming up if there's anything historical that we might prune
                bool hasAnythingToPrune =
                    await main.BackupRecords.AnyAsync(stoppingToken) ||
                    await main.Jobs.AnyAsync(stoppingToken);

                if (trackerEmpty && hasAnythingToPrune)
                {
                    _logger.LogInformation("Janitor: snapshot tracker empty; delaying cleanup until tracker is warm.");

                    var deadline = DateTime.UtcNow + warmupMaxWait;
                    while (DateTime.UtcNow < deadline && !stoppingToken.IsCancellationRequested)
                    {
                        // Re-check tracker or a readiness flag set by CollectionService
                        bool trackerHasRows = await qdb.NetappSnapshots.AnyAsync(stoppingToken);
                        var meta = await qdb.InventoryMetadata.FindAsync(new object[] { "SnapshotTrackerReady" }, stoppingToken);
                        bool flaggedReady = string.Equals(meta?.Value, "true", StringComparison.OrdinalIgnoreCase);

                        if (trackerHasRows || flaggedReady)
                        {
                            _logger.LogInformation("Janitor: tracker is warm; starting regular maintenance.");
                            break;
                        }

                        _logger.LogDebug("Janitor warm-up: tracker not ready; re-checking in {s}s.", warmupPoll.TotalSeconds);
                        await Task.Delay(warmupPoll, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Janitor: warm-up check failed; continuing with regular loop.");
            }

            // --- Regular periodic maintenance loop ---
            var timer = new PeriodicTimer(Cadence);
            try
            {
                do
                {
                    try
                    {
                        await CleanupOrphanedSelectedStorages(stoppingToken);
                        await CleanupExpiredDbOnly(stoppingToken); // DB-only cleanup after CollectionService retention
                        await PruneOldOrStuckJobs(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Janitor pass failed.");
                    }
                }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException) { /* timer canceled */ }
        }


        private async Task CleanupOrphanedSelectedStorages(CancellationToken ct)
        {
            await using var db = await _dbf.CreateAsync(ct);

            var orphaned = await db.SelectedStorages
                .Where(ss => !db.ProxmoxClusters.Any(pc => pc.Id == ss.ClusterId))
                .ToListAsync(ct);

            if (orphaned.Count == 0)
            {
                _logger.LogDebug("SelectedStorages cleanup: no orphaned rows.");
                return;
            }

            await WithSqliteBusyRetryAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                db.SelectedStorages.RemoveRange(orphaned);
                var affected = await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                _logger.LogInformation("SelectedStorages cleanup: removed {Count} orphaned rows.", affected);
            }, ct);
        }

        /// <summary>
        /// DB-only cleanup for expired snapshots that CollectionService has already removed from NetApp.
        /// Uses QUERY DB NetappSnapshots flags to decide if safe to delete app rows, and prunes those
        /// NetappSnapshots rows from the QUERY DB once removed.
        /// Also removes per-backup storage-content snapshots from the main DB.
        /// </summary>
        private async Task CleanupExpiredDbOnly(CancellationToken ct)
        {
            await using var main = await _dbf.CreateAsync(ct);
            await using var qdb = await _qdbf.CreateAsync(ct);

            var now = DateTime.UtcNow;

            var records = await main.BackupRecords
                .Include(r => r.Job)
                .AsNoTracking()
                .Select(r => new
                {
                    r.Id,
                    r.JobId,
                    r.ControllerId,
                    r.StorageName,
                    r.SnapshotName,
                    r.TimeStamp,
                    r.RetentionCount,
                    r.RetentionUnit,
                    JobStatus = r.Job!.Status
                })
                .ToListAsync(ct);

            static bool IsExpired(DateTime ts, int count, string? unit, DateTime nowUtc) =>
                (unit ?? "").ToLowerInvariant() switch
                {
                    "hours" => ts.AddHours(count) < nowUtc,
                    "days" => ts.AddDays(count) < nowUtc,
                    "weeks" => ts.AddDays(7 * count) < nowUtc,
                    _ => false
                };

            var jobIds = records.Select(r => r.JobId).Distinct().ToList();

            // Read presence flags from QUERY DB (new location)
            var qSnaps = await qdb.NetappSnapshots
                .Where(s => jobIds.Contains(s.JobId))
                .Select(s => new { s.JobId, s.SnapshotName, s.ExistsOnPrimary, s.ExistsOnSecondary })
                .ToListAsync(ct);

            var bothGone = qSnaps.ToDictionary(
                k => (k.JobId, k.SnapshotName),
                v => (v.ExistsOnPrimary != true) && (v.ExistsOnSecondary != true));

            var candidates = records
                .Where(r => r.JobStatus != "Running" && IsExpired(r.TimeStamp, r.RetentionCount, r.RetentionUnit, now))
                .GroupBy(r => new { r.JobId, r.SnapshotName })
                .Where(g =>
                {
                    if (bothGone.TryGetValue((g.Key.JobId, g.Key.SnapshotName), out var gone))
                        return gone;              // only when both sides are gone
                    return true;                  // missing tracker row → treat as purged
                })
                .Select(g => new { g.Key.JobId, g.Key.SnapshotName, RecordIds = g.Select(x => x.Id).ToList() })
                .ToList();

            if (candidates.Count == 0) return;

            var jobIdsToPurge = candidates.Select(c => c.JobId).Distinct().ToList();

            var vmResultIds = await main.JobVmResults
                .Where(r => jobIdsToPurge.Contains(r.JobId))
                .Select(r => r.Id)
                .ToListAsync(ct);

            await WithSqliteBusyRetryAsync(async () =>
            {
                await using var tx = await main.Database.BeginTransactionAsync(ct);

                if (vmResultIds.Count > 0)
                    await main.JobVmLogs
                        .Where(l => vmResultIds.Contains(l.JobVmResultId))
                        .ExecuteDeleteAsync(ct);

                await main.JobVmResults
                    .Where(r => jobIdsToPurge.Contains(r.JobId))
                    .ExecuteDeleteAsync(ct);

                // 🔧 NEW: remove storage content index rows for those jobs
                await main.ProxmoxStorageDiskSnapshots
                    .Where(x => jobIdsToPurge.Contains(x.JobId))
                    .ExecuteDeleteAsync(ct);

                // Remove only the expired snapshot’s BackupRecords (MAIN DB)
                foreach (var c in candidates)
                    await main.BackupRecords
                        .Where(r => r.JobId == c.JobId && r.SnapshotName == c.SnapshotName)
                        .ExecuteDeleteAsync(ct);

                // Remove orphan Jobs that no longer have any BackupRecords/VM results
                var stillUsedJobIds = await main.BackupRecords
                    .Where(r => jobIdsToPurge.Contains(r.JobId))
                    .Select(r => r.JobId)
                    .Distinct()
                    .ToListAsync(ct);

                var orphanJobs = jobIdsToPurge.Except(stillUsedJobIds).ToList();
                if (orphanJobs.Count > 0)
                    await main.Jobs
                        .Where(j => orphanJobs.Contains(j.Id))
                        .ExecuteDeleteAsync(ct);

                await tx.CommitAsync(ct);
            }, ct);

            // Also prune the associated snapshot tracker rows from QUERY DB
            foreach (var c in candidates)
            {
                await qdb.NetappSnapshots
                    .Where(s => s.JobId == c.JobId && s.SnapshotName == c.SnapshotName)
                    .ExecuteDeleteAsync(ct);
            }

            _logger.LogInformation("Janitor: DB-only retention purged {Count} snapshot group(s).", candidates.Count);
        }

        private async Task PruneOldOrStuckJobs(CancellationToken ct)
        {
            await using var db = await _dbf.CreateAsync(ct);
            await using var qdb = await _qdbf.CreateAsync(ct);

            var cutoff = DateTime.UtcNow.AddDays(-30);

            string[] failed = { "Failed", "Error", "Aborted", "Cancelled" };
            string[] active =
            {
                "Pending","Queued","Running","InProgress","Started",
                "Creating Proxmox snapshots","Waiting for Proxmox snapshots",
                "Proxmox snapshots completed","Paused VMs","NetApp snapshot created",
                "Triggering SnapMirror update"
            };

            var toDeleteIds = await db.Jobs
                .AsNoTracking()
                .Where(j => (j.CompletedAt ?? j.StartedAt) < cutoff &&
                            (j.Type == "Restore" ||
                             (j.Type == "Backup" && j.Status != null && failed.Contains(j.Status)) ||
                             (j.Status == null || active.Contains(j.Status))))
                .Select(j => j.Id)
                .ToListAsync(ct);

            if (toDeleteIds.Count == 0)
            {
                _logger.LogDebug("PruneJobs: nothing to delete (cutoff {Cutoff}).", cutoff);


                return;
            }

            await WithSqliteBusyRetryAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);

                await db.JobVmLogs.Where(l => db.JobVmResults
                        .Where(r => toDeleteIds.Contains(r.JobId))
                        .Select(r => r.Id)
                        .Contains(l.JobVmResultId))
                    .ExecuteDeleteAsync(ct);

                await db.JobVmResults.Where(r => toDeleteIds.Contains(r.JobId)).ExecuteDeleteAsync(ct);

                // NEW: prune storage content snapshot entries for those jobs
                await db.ProxmoxStorageDiskSnapshots
                    .Where(x => toDeleteIds.Contains(x.JobId))
                    .ExecuteDeleteAsync(ct);

                await db.BackupRecords.Where(r => toDeleteIds.Contains(r.JobId)).ExecuteDeleteAsync(ct);
                await db.Jobs.Where(j => toDeleteIds.Contains(j.Id)).ExecuteDeleteAsync(ct);

                await tx.CommitAsync(ct);
            }, ct);

            // Prune any remaining snapshot tracker rows for those jobs in QUERY DB
            await qdb.NetappSnapshots
                .Where(s => toDeleteIds.Contains(s.JobId))
                .ExecuteDeleteAsync(ct);

            _logger.LogInformation("Pruned {Count} old/stale jobs.", toDeleteIds.Count);
        }

        private static async Task WithSqliteBusyRetryAsync(Func<Task> action, CancellationToken ct, int attempts = 3, int delayMs = 500)
        {
            for (var i = 0; i < attempts; i++)
            {
                try { await action(); return; }
                catch (DbUpdateException ey) when (ey.InnerException is SqliteException se && se.SqliteErrorCode == 5)
                {
                    if (i == attempts - 1) throw;
                    await Task.Delay(delayMs, ct);
                }
            }
        }
    }
}
