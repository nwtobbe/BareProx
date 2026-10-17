/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 *
 * Copyright (C) 2025-2026 Tobias Modig
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

namespace BareProx.Services.Migration
{
    using System;
    using BareProx.Data;
    using BareProx.Models;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using System.Text.Json;
    using System.Collections.Generic;
    using System.Collections.Concurrent;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    public interface IMigrationQueueRunner
    {
        bool IsRunning { get; }
        Task<bool> StartAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Service that creates required datastore snapshots for migrations.
    /// If not registered, snapshotting is skipped with a warning.
    /// </summary>
    public interface IStorageSnapshotCoordinator
    {
        /// <summary>
        /// Ensure a snapshot exists for each storage before we mutate anything.
        /// SHOULD throw on failure to abort the run safely.
        /// </summary>
        Task EnsureSnapshotsAsync(IReadOnlyCollection<string> storages, CancellationToken ct);
        Task EnsureSnapshotsByVolumeIdsAsync(IReadOnlyCollection<int> selectedNetappVolumeIds, CancellationToken ct);
        // VM-specific snapshots for each manually queued migration. Original overloads
        // remain for existing callers and non-VMID batch snapshots.
        Task EnsureSnapshotsAsync(IReadOnlyCollection<string> storages, CancellationToken ct, int? migrationVmId);
        Task EnsureSnapshotsByVolumeIdsAsync(IReadOnlyCollection<int> selectedNetappVolumeIds, CancellationToken ct, int? migrationVmId);
    }

    /// <summary>
    /// Bounded-concurrency queue runner: takes a snapshot per VM before importing, then
    /// picks the next "Queued" item, marks it "Processing",
    /// calls IMigrationExecutor, then "Done"/"Failed".
    /// </summary>
    public sealed class MigrationQueueRunner : IMigrationQueueRunner
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<MigrationQueueRunner> _log;

        private readonly SemaphoreSlim _gate = new(1, 1);
        private Task? _runTask;
        private CancellationTokenSource? _cts;

        public bool IsRunning => _runTask is { IsCompleted: false };

        public MigrationQueueRunner(IServiceScopeFactory scopeFactory, ILogger<MigrationQueueRunner> log)
        {
            _scopeFactory = scopeFactory;
            _log = log;
        }

        public async Task<bool> StartAsync(CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (IsRunning) return false;
                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _runTask = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
                return true;
            }
            finally { _gate.Release(); }
        }

        private async Task RunAsync(CancellationToken ct)
        {
            var runId = Guid.NewGuid().ToString("N");
            _log.LogInformation("Migration queue: start (run {RunId})", runId);

            try
            {
                List<int> candidateIds; // IDs frozen at run start
                List<MigrationQueueItem> itemsAtStart;
                int maxParallel = 1;
                List<int> selectedVolIds = new();
                string? selectedStorageName = null;

                // ===== 1) PLAN THE RUN (snapshots are per-VM, inside worker) =====
                using (var planScope = _scopeFactory.CreateScope())
                {
                    var db = planScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var selection = await db.MigrationSelections.AsNoTracking().FirstOrDefaultAsync(ct);
                    if (selection == null)
                        throw new InvalidOperationException("No MigrationSelection found. Configure cluster/host/storage first.");

                    // The settings are frozen for this run, so changing Settings cannot
                    // unexpectedly change the number of active workers midway through.
                    maxParallel = Math.Clamp(selection.MaxParallelMigrations, 1, 8);

                    selectedStorageName = selection.StorageIdentifier;
                    selectedVolIds = await db.MigrationSelections.AsNoTracking()
                        .Where(ms => ms.SelectedNetappVolumeId != null)
                        .Select(ms => ms.SelectedNetappVolumeId!.Value)
                        .Distinct().ToListAsync(ct);

                    itemsAtStart = await db.Set<MigrationQueueItem>()
                        .Where(x => x.Status == "Queued")
                        .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id)
                        .ToListAsync(ct);

                    if (selectedVolIds.Count == 0 && itemsAtStart.Count == 0)
                    {
                        var deadline = DateTime.UtcNow.AddSeconds(60);
                        _log.LogInformation("Run {RunId}: waiting for queued items to derive storages (up to 60s).", runId);
                        while (DateTime.UtcNow < deadline && itemsAtStart.Count == 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            await Task.Delay(1000, ct);
                            itemsAtStart = await db.Set<MigrationQueueItem>()
                                .Where(x => x.Status == "Queued")
                                .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToListAsync(ct);
                        }
                    }
                    candidateIds = itemsAtStart.Select(x => x.Id).ToList();

                    // A snapshot must be created immediately before each individual import,
                    // not once per datastore for the entire batch. The VMID is known at
                    // that point, and a failure blocks only that VM's import.
                    _log.LogInformation("Run {RunId}: planned {Count} queued VM(s); snapshots will be created per VM.",
                        runId, candidateIds.Count);
                }

                // ===== 2) PROCESS FROZEN SET WITH BOUNDED CONCURRENCY =====
                // Each worker uses a fresh DI scope and DbContext. Worker slots are
                // refilled immediately when an item finishes (not fixed-size batches).
                var nextIndex = -1;
                var vmidGates = new ConcurrentDictionary<int, SemaphoreSlim>();

                async Task ProcessItemAsync(int id, int workerNumber)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var exec = scope.ServiceProvider.GetRequiredService<IMigrationExecutor>();

                    // Conditional UPDATE is atomic: no two workers may claim the same row.
                    // Retry only a short DB operation, never a potentially partial import.
                    int claimed = await RetryDatabaseAsync(() => db.Set<MigrationQueueItem>()
                        .Where(x => x.Id == id && x.Status == "Queued")
                        .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, "Processing"), ct), ct);
                    if (claimed != 1) return;

                    // From this point a worker owns the item. Any interruption must leave
                    // it non-Queued, because imports can have already written VM files.
                    try
                    {
                        var item = await db.Set<MigrationQueueItem>().FirstAsync(x => x.Id == id, ct);
                        db.MigrationQueueLogs.Add(new MigrationQueueLog
                        {
                            ItemId = id, Step = "Item", Level = "Info",
                            Message = $"Processing start (run {runId}, worker {workerNumber})",
                            Utc = DateTime.UtcNow
                        });
                        await RetryDatabaseAsync(() => db.SaveChangesAsync(ct), ct);

                        // Serialize imports sharing a VMID within this run. Proxmox's
                        // independent availability check remains authoritative.
                        var vmidLock = vmidGates.GetOrAdd(item.VmId ?? -id,
                            _ => new SemaphoreSlim(1, 1));
                        await vmidLock.WaitAsync(ct);
                        try
                        {
                            if (item.VmId is null or < 100)
                                throw new InvalidOperationException("Cannot create a VMID-specific snapshot: destination Proxmox VMID is missing/invalid.");

                            // Create the exact per-VM snapshot BEFORE importing this VM.
                            // Always use the existing selected NetApp volume when configured;
                            // otherwise derive source storage(s) as in the original runner.
                            var snapper = scope.ServiceProvider.GetRequiredService<IStorageSnapshotCoordinator>();
                            if (selectedVolIds.Count > 0)
                            {
                                await snapper.EnsureSnapshotsByVolumeIdsAsync(selectedVolIds, ct, item.VmId.Value);
                            }
                            else
                            {
                                var storages = ExtractStoragesFromItem(item)
                                    .Where(x => !string.IsNullOrWhiteSpace(x))
                                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                                if (storages.Count == 0 && !string.IsNullOrWhiteSpace(selectedStorageName))
                                {
                                    var mapped = await db.SelectedNetappVolumes.AsNoTracking()
                                        .Where(v => v.Disabled != true && v.VolumeName == selectedStorageName)
                                        .Select(v => v.VolumeName).Distinct().ToListAsync(ct);
                                    storages = mapped.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                                }
                                // Fail closed: never import without the required snapshot.
                                if (storages.Count == 0)
                                    throw new InvalidOperationException("No mapped NetApp source storage found for VMID " + item.VmId + "; import blocked.");
                                await snapper.EnsureSnapshotsAsync(storages, ct, item.VmId.Value);
                            }
                            db.MigrationQueueLogs.Add(new MigrationQueueLog
                            {
                                ItemId = item.Id, Step = "Snapshot", Level = "Info",
                                Message = "VMID " + item.VmId + ": NetApp migration snapshot created successfully.",
                                Utc = DateTime.UtcNow
                            });
                            await RetryDatabaseAsync(() => db.SaveChangesAsync(ct), ct);
                            await exec.ExecuteAsync(item, ct);
                        }
                        finally { vmidLock.Release(); }

                        // The executor can save using this scoped context; clear its
                        // tracker before writing the final status so it cannot be undone.
                        db.ChangeTracker.Clear();
                        await SetFinalStatusAsync(db, id, "Done", "Info",
                            $"Processing done (run {runId})");
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        await RecordFailedSafelyAsync(db, id,
                            $"Interrupted (run {runId}); check destination before retry.");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Run {RunId} item {ItemId} failed.", runId, id);
                        await RecordFailedSafelyAsync(db, id,
                            $"Processing failed (run {runId}): {ex.Message}");
                    }
                }

                async Task SetFinalStatusAsync(ApplicationDbContext db, int id,
                    string status, string level, string message)
                {
                    await RetryDatabaseAsync(async () =>
                    {
                        // Keep writes short and do not retry the Proxmox operation itself.
                        await db.Set<MigrationQueueItem>()
                            .Where(x => x.Id == id && x.Status == "Processing")
                            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, status), CancellationToken.None);
                        db.MigrationQueueLogs.Add(new MigrationQueueLog
                        {
                            ItemId = id, Step = "Item", Level = level,
                            Message = message, Utc = DateTime.UtcNow
                        });
                        await db.SaveChangesAsync(CancellationToken.None);
                        return 0;
                    }, CancellationToken.None);
                }

                async Task RecordFailedSafelyAsync(ApplicationDbContext db, int id, string message)
                {
                    try
                    {
                        db.ChangeTracker.Clear();
                        await SetFinalStatusAsync(db, id, "Failed", "Error", message);
                    }
                    catch (Exception logEx)
                    {
                        _log.LogError(logEx,
                            "Could not persist failure state for item {ItemId}; inspect before retry.", id);
                    }
                }

                async Task WorkerAsync(int workerNumber)
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var index = Interlocked.Increment(ref nextIndex);
                        if (index >= candidateIds.Count) return;
                        await ProcessItemAsync(candidateIds[index], workerNumber);
                    }
                }

                _log.LogInformation("Run {RunId}: {Count} queued VMs, {Workers} parallel workers.",
                    runId, candidateIds.Count, maxParallel);
                await Task.WhenAll(Enumerable.Range(1, Math.Min(maxParallel, candidateIds.Count))
                    .Select(WorkerAsync));
                _log.LogInformation("Migration queue: frozen batch complete (run {RunId})", runId);
            }
            finally
            {
                _log.LogInformation("Migration queue: stop (run {RunId})", runId);
            }
        }

        // ---- Helpers ----

        /// <summary>
        /// Extract distinct Proxmox storage names from a queued item (based on DisksJson).
        /// Accepts storage keys: storage/Storage/datastore/targetStorage/dstStorage/storageName.
        /// </summary>
        // SQLite WAL permits one writer. Retry transient SQLite lock conflicts,
        // but never retry the import itself. EF's contexts remain worker-local.
        private static async Task<T> RetryDatabaseAsync<T>(Func<Task<T>> action, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try { return await action(); }
                catch (Microsoft.Data.Sqlite.SqliteException ex)
                    when ((ex.SqliteErrorCode is 5 or 6) && attempt < 5)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), ct);
                }
            }
        }

        private static IEnumerable<string> ExtractStoragesFromItem(MigrationQueueItem item)
        {
            if (string.IsNullOrWhiteSpace(item.DisksJson))
                return Array.Empty<string>();

            try
            {
                using var doc = JsonDocument.Parse(item.DisksJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return Array.Empty<string>();

                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string[] keys = { "storage", "Storage", "datastore", "Datastore", "targetStorage", "TargetStorage", "dstStorage", "storageName", "StorageName" };

                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;

                    foreach (var k in keys)
                    {
                        if (el.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                        {
                            var s = v.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) set.Add(s!);
                            break;
                        }
                    }
                }

                return set; // distinct storages
            }
            catch
            {
                // bad JSON? just treat as no storages
                return Array.Empty<string>();
            }
        }
        private static async Task LogSnapshotEventForItems(ApplicationDbContext db,
            IReadOnlyList<int> itemIds, string message, string runId,
            CancellationToken ct, string level = "Info")
        {
            if (itemIds.Count == 0) return;
            var now = DateTime.UtcNow;
            foreach (var id in itemIds)
                db.MigrationQueueLogs.Add(new MigrationQueueLog
                {
                    ItemId = id, Step = "Snapshot", Level = level,
                    Message = $"{message} (run {runId})", Utc = now
                });
            await db.SaveChangesAsync(ct);
        }
    }
}
