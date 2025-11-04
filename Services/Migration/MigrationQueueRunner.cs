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
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using BareProx.Data;
    using BareProx.Models;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;

    public interface IMigrationQueueRunner
    {
        bool IsRunning { get; }
        Task<bool> StartAsync(CancellationToken ct = default);
    }

    public interface IStorageSnapshotCoordinator
    {
        Task EnsureSnapshotsAsync(IReadOnlyCollection<string> storages, CancellationToken ct);
        Task EnsureSnapshotsByVolumeIdsAsync(IReadOnlyCollection<int> selectedNetappVolumeIds, CancellationToken ct);
    }

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
                List<int> candidateIds;
                List<MigrationQueueItem> itemsAtStart;

                // ===== 1) PLAN & TAKE SNAPSHOTS (once per run) =====
                using (var planScope = _scopeFactory.CreateScope())
                {
                    var db = planScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    // Read current selection (used for fallback mapping & logging)
                    var selection = await db.MigrationSelections
                        .AsNoTracking()
                        .FirstOrDefaultAsync(ct);

                    if (selection == null)
                        throw new InvalidOperationException("No MigrationSelection found. Configure cluster/host/storage first.");

                    // Prefer authoritative: SelectedNetappVolumeId(s) across selections
                    var selectedVolIds = await db.MigrationSelections
                        .AsNoTracking()
                        .Where(ms => ms.SelectedNetappVolumeId != null)
                        .Select(ms => ms.SelectedNetappVolumeId!.Value)
                        .Distinct()
                        .ToListAsync(ct);

                    // Gather queued items (may wait a bit if we need to derive storages)
                    itemsAtStart = await db.Set<MigrationQueueItem>()
                        .Where(x => x.Status == "Queued")
                        .OrderBy(x => x.CreatedAtUtc)
                        .ThenBy(x => x.Id)
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
                                .OrderBy(x => x.CreatedAtUtc)
                                .ThenBy(x => x.Id)
                                .ToListAsync(ct);
                        }
                    }

                    candidateIds = itemsAtStart.Select(x => x.Id).ToList();

                    try
                    {
                        var snapper = planScope.ServiceProvider.GetRequiredService<IStorageSnapshotCoordinator>();

                        // CASE A: Snapshot by SelectedNetappVolumeId
                        if (selectedVolIds.Count > 0)
                        {
                            _log.LogInformation(
                                "Run {RunId}: creating NetApp snapshots via SelectedNetappVolume ids: {Ids}",
                                runId, string.Join(", ", selectedVolIds));

                            await snapper.EnsureSnapshotsByVolumeIdsAsync(selectedVolIds, ct);
                            await LogSnapshotEventForItems(db, candidateIds,
                                "Snapshots ensured by SelectedNetappVolumeId.", runId, ct);

                            _log.LogInformation("Run {RunId}: NetApp snapshots ensured via SelectedNetappVolume ids.", runId);
                        }
                        else
                        {
                            // CASE B: Derive storages from queued items' DisksJson
                            var storagesFromItems = itemsAtStart
                                .SelectMany(ExtractStoragesFromItem)
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                                .ToList();

                            // CASE C: Fallback to selection.StorageIdentifier mapping if nothing derived
                            if (storagesFromItems.Count == 0 && !string.IsNullOrWhiteSpace(selection.StorageIdentifier))
                            {
                                var mapped = await db.SelectedNetappVolumes
                                    .AsNoTracking()
                                    .Where(v => v.VolumeName == selection.StorageIdentifier)
                                    .Select(v => v.VolumeName)
                                    .Distinct()
                                    .ToListAsync(ct);

                                storagesFromItems = mapped
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList();

                                if (storagesFromItems.Count > 0)
                                {
                                    _log.LogInformation("Run {RunId}: falling back to StorageIdentifier mapping: {Storages}",
                                        runId, string.Join(", ", storagesFromItems));
                                }
                            }

                            if (storagesFromItems.Count == 0)
                            {
                                _log.LogWarning("Run {RunId}: snapshot step skipped — no SelectedNetappVolumeId and no storages could be derived.", runId);
                                await LogSnapshotEventForItems(db, candidateIds,
                                    "Snapshot step skipped (no storages could be derived).", runId, ct);
                            }
                            else
                            {
                                _log.LogInformation(
                                    "Run {RunId}: creating datastore snapshots for Proxmox storages: {Storages}",
                                    runId, string.Join(", ", storagesFromItems));

                                await snapper.EnsureSnapshotsAsync(storagesFromItems, ct);
                                await LogSnapshotEventForItems(db, candidateIds,
                                    $"Snapshots ensured for storages: {string.Join(", ", storagesFromItems)}", runId, ct);

                                _log.LogInformation("Run {RunId}: datastore snapshots ensured from derived storages.", runId);
                            }
                        }
                    }
                    catch (InvalidOperationException ex) // likely missing DI registration
                    {
                        _log.LogWarning(ex,
                            "Run {RunId}: IStorageSnapshotCoordinator not registered; skipping snapshot step.",
                            runId);
                        await LogSnapshotEventForItems(db, candidateIds,
                            "Snapshot step skipped (IStorageSnapshotCoordinator not registered).", runId, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        _log.LogWarning("Run {RunId}: snapshot planning canceled; aborting before VM work.", runId);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex,
                            "Run {RunId}: snapshot step failed; aborting run to keep state consistent.",
                            runId);
                        await LogSnapshotEventForItems(db, candidateIds,
                            $"Snapshot step failed: {ex.Message}", runId, ct, level: "Error");
                        return;
                    }
                }

                // ===== 2) PROCESS FROZEN SET =====
                while (!ct.IsCancellationRequested)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var exec = scope.ServiceProvider.GetRequiredService<IMigrationExecutor>();

                    var item = await db.Set<MigrationQueueItem>()
                        .Where(x => candidateIds.Contains(x.Id) && x.Status == "Queued")
                        .OrderBy(x => x.CreatedAtUtc)
                        .ThenBy(x => x.Id)
                        .FirstOrDefaultAsync(ct);

                    if (item == null)
                    {
                        _log.LogInformation("Migration queue: frozen batch complete (run {RunId})", runId);
                        break;
                    }

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
                        // Single-signature executor: resolves node & default storage internally from MigrationSelections
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
                        dbItem.Status = "Queued";
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
                    }
                }
            }
            finally
            {
                _log.LogInformation("Migration queue: stop (run {RunId})", runId);
            }
        }


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
                string[] keys = {
                    "storage","Storage",
                    "datastore","Datastore",
                    "targetStorage","TargetStorage",
                    "dstStorage",
                    "storageName","StorageName"
                };

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

                return set;
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static async Task LogSnapshotEventForItems(
            ApplicationDbContext db,
            IReadOnlyList<int> itemIds,
            string message,
            string runId,
            CancellationToken ct,
            string level = "Info")
        {
            if (itemIds == null || itemIds.Count == 0) return;

            var now = DateTime.UtcNow;

            foreach (var id in itemIds)
            {
                db.MigrationQueueLogs.Add(new MigrationQueueLog
                {
                    ItemId = id,
                    Step = "Snapshot",
                    Level = level,                  // "Info" | "Warning" | "Error"
                    Message = $"{message} (run {runId})",
                    Utc = now
                });
            }

            await db.SaveChangesAsync(ct);
        }
    }
}
