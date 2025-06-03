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
                    // await CleanupOrphanPrimarySnapshots(stoppingToken);
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

            foreach (var grp in expired.GroupBy(r => new {
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
            var relations = await db.SnapMirrorRelations
                .ToListAsync(ct);

            // 2) Load every snapshot row you’re tracking
            var snaps = await db.NetappSnapshots.ToListAsync(ct);

            foreach (var s in snaps)
            {
                // — primary side —
                var primaryList = await netapp.GetSnapshotsAsync(
                    s.PrimaryControllerId,
                    s.PrimaryVolume);
                s.ExistsOnPrimary = primaryList
                    .Any(x => x.Equals(s.SnapshotName, StringComparison.OrdinalIgnoreCase));

                // — find the relation for this primary volume (if any) —
                var rel = relations.FirstOrDefault(r =>
                    r.SourceControllerId == s.PrimaryControllerId &&
                    r.SourceVolume == s.PrimaryVolume);

                if (rel != null)
                {
                    // 3) List snapshots on the secondary volume
                    var secList = await netapp.GetSnapshotsAsync(
                        rel.DestinationControllerId,
                        rel.DestinationVolume);

                    var foundOnSecondary = secList
                        .Any(x => x.Equals(s.SnapshotName, StringComparison.OrdinalIgnoreCase));

                    if (foundOnSecondary)
                    {
                        // 4) Update your DB row with the secondary details
                        s.ExistsOnSecondary = true;
                        s.SecondaryControllerId = rel.DestinationControllerId;
                        s.SecondaryVolume = rel.DestinationVolume;
                        s.IsReplicated = true;
                    }
                    else
                    {
                        s.ExistsOnSecondary = false;
                    }
                }
                else
                {
                    // no SnapMirror relation → no secondary
                    s.ExistsOnSecondary = false;
                }

                // 5) Always bump your timestamp
                s.LastChecked = now;
            }

            await db.SaveChangesAsync(ct);
        }


        /// <summary>
        /// 3) Delete any orphan snapshots on primary (BP_*) not tracked in NetappSnapshots.
        /// </summary>
        private async Task CleanupOrphanPrimarySnapshots(CancellationToken ct)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var netapp = scope.ServiceProvider.GetRequiredService<INetappService>();

            var primaries = await db.NetappControllers
                .Where(c => c.IsPrimary)
                .ToListAsync(ct);

            foreach (var ctrl in primaries)
            {
                var trackedVolumes = await db.NetappSnapshots
                    .Where(s => s.PrimaryControllerId == ctrl.Id)
                    .Select(s => s.PrimaryVolume)
                    .Distinct()
                    .ToListAsync(ct);

                foreach (var vol in trackedVolumes)
                {
                    var actual = await netapp.GetSnapshotsAsync(ctrl.Id, vol);
                    var expected = new HashSet<string>(
                        await db.NetappSnapshots
                            .Where(s =>
                                s.PrimaryControllerId == ctrl.Id &&
                                s.PrimaryVolume == vol)
                            .Select(s => s.SnapshotName)
                            .ToListAsync(ct),
                        StringComparer.OrdinalIgnoreCase);

                    foreach (var snap in actual.Where(n => n.StartsWith("BP_")))
                    {
                        if (!expected.Contains(snap))
                        {
                            try
                            {
                                var del = await netapp.DeleteSnapshotAsync(ctrl.Id, vol, snap);
                                if (del.Success)
                                {
                                    _logger.LogInformation(
                                        "Deleted orphan primary snapshot {snap} on vol {vol}, ctrl {ctrl}",
                                        snap, vol, ctrl.Id);
                                }
                                else
                                {
                                    _logger.LogWarning(
                                        "Failed deleting orphan {snap} on {vol}, ctrl {ctrl}: {err}",
                                        snap, vol, ctrl.Id, del.ErrorMessage);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(
                                    ex,
                                    "Error deleting orphan snapshot {snap} on {vol}, ctrl {ctrl}",
                                    snap, vol, ctrl.Id);
                            }
                        }
                    }
                }
            }
        }
    }
}
