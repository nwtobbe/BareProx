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

namespace BareProx.Services.Migration
{
    using BareProx.Data;
    using BareProx.Models;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using System.Text.Json;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    public interface IMigrationQueueRunner
    {
        bool IsRunning { get; }
        Task<bool> StartAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Optional service you can implement to perform datastore snapshots once per run.
    /// If not registered, snapshotting is skipped with a warning.
    /// </summary>
    public interface IStorageSnapshotCoordinator
    {
        /// <summary>
        /// Ensure a snapshot exists for each storage before we mutate anything.
        /// SHOULD throw on failure to abort the run safely.
        /// </summary>
        Task EnsureSnapshotsAsync(IReadOnlyCollection<string> storages, CancellationToken ct);
    }

    /// <summary>
    /// Single-concurrency queue runner: plans datastore snapshots, then
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

                // ===== 1) PLAN & TAKE DATASTORE SNAPSHOTS (once per storage) =====
                using (var planScope = _scopeFactory.CreateScope())
                {
                    var db = planScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    // Wait briefly for something to be queued (user may hit "Run" first, then queue)
                    var deadline = DateTime.UtcNow.AddSeconds(12);
                    List<MigrationQueueItem> itemsAtStart;
                    do
                    {
                        ct.ThrowIfCancellationRequested();

                        itemsAtStart = await db.Set<MigrationQueueItem>()
                            .Where(x => x.Status == "Queued")
                            .OrderBy(x => x.CreatedAtUtc)
                            .ThenBy(x => x.Id)
                            .ToListAsync(ct);

                        if (itemsAtStart.Count > 0) break;
                        await Task.Delay(400, ct);
                    } while (DateTime.UtcNow < deadline);

                    candidateIds = itemsAtStart.Select(x => x.Id).ToList();

                    var storages = itemsAtStart
                        .SelectMany(ExtractStoragesFromItem)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (storages.Count == 0)
                    {
                        _log.LogInformation("Run {RunId}: no storages detected from queued items; skipping snapshot step.", runId);
                    }
                    else
                    {
                        // Make snapshotting required so we don't silently skip it
                        try
                        {
                            var snapper = planScope.ServiceProvider.GetRequiredService<IStorageSnapshotCoordinator>();
                            _log.LogInformation("Run {RunId}: creating datastore snapshots for: {Storages}", runId, string.Join(", ", storages));

                            await snapper.EnsureSnapshotsAsync(storages, ct);

                            _log.LogInformation("Run {RunId}: datastore snapshots ensured.", runId);
                        }
                        catch (InvalidOperationException ex) // thrown if not registered
                        {
                            _log.LogWarning(ex, "Run {RunId}: IStorageSnapshotCoordinator not registered; skipping datastore snapshots.", runId);
                        }
                        catch (OperationCanceledException)
                        {
                            _log.LogWarning("Run {RunId}: snapshot planning canceled; aborting run before any VM work.", runId);
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(ex, "Run {RunId}: datastore snapshot step failed; aborting run to keep state consistent.", runId);
                            return; // safer not to proceed
                        }
                    }
                }

                // ===== 2) PROCESS ONLY THE FROZEN SET (still requiring Status == "Queued") =====
                while (!ct.IsCancellationRequested)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var exec = scope.ServiceProvider.GetRequiredService<IMigrationExecutor>();

                    // Pick next item: must be in frozen set AND still Queued
                    var item = await db.Set<MigrationQueueItem>()
                        .Where(x => candidateIds.Contains(x.Id) && x.Status == "Queued")
                        .OrderBy(x => x.CreatedAtUtc)
                        .ThenBy(x => x.Id)
                        .FirstOrDefaultAsync(ct);

                    if (item == null)
                    {
                        _log.LogInformation("Migration queue: frozen batch complete (run {RunId})", runId);
                        break; // no more from the frozen set
                    }

                    // Mark processing
                    item.Status = "Processing";
                    db.MigrationQueueLogs.Add(new MigrationQueueLog
                    {
                        ItemId = item.Id,
                        Step = "Item",
                        Level = "Info",
                        Message = $"Processing start (run {runId})",
                        Utc = DateTime.UtcNow
                    });
                    await db.SaveChangesAsync(ct);

                    try
                    {
                        await exec.ExecuteAsync(item, ct);

                        var dbItem = await db.Set<MigrationQueueItem>().FirstAsync(x => x.Id == item.Id, ct);
                        dbItem.Status = "Done";
                        db.MigrationQueueLogs.Add(new MigrationQueueLog
                        {
                            ItemId = item.Id,
                            Step = "Item",
                            Level = "Info",
                            Message = $"Processing done (run {runId})",
                            Utc = DateTime.UtcNow
                        });
                        await db.SaveChangesAsync(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        var dbItem = await db.Set<MigrationQueueItem>().FirstAsync(x => x.Id == item.Id, ct);
                        dbItem.Status = "Queued"; // return to queue on cancel
                        db.MigrationQueueLogs.Add(new MigrationQueueLog
                        {
                            ItemId = item.Id,
                            Step = "Item",
                            Level = "Warning",
                            Message = $"Processing canceled (run {runId})",
                            Utc = DateTime.UtcNow
                        });
                        await db.SaveChangesAsync(ct);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        var dbItem = await db.Set<MigrationQueueItem>().FirstAsync(x => x.Id == item.Id, ct);
                        dbItem.Status = "Failed";
                        db.MigrationQueueLogs.Add(new MigrationQueueLog
                        {
                            ItemId = item.Id,
                            Step = "Item",
                            Level = "Error",
                            Message = $"Processing failed (run {runId}): {ex.Message}",
                            Utc = DateTime.UtcNow
                        });
                        await db.SaveChangesAsync(ct);

                        _log.LogError(ex, "Run {RunId} item {ItemId} failed.", runId, item.Id);
                        // continue to next from the frozen set
                    }
                }
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
    }
}
