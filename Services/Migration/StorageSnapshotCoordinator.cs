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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BareProx.Services.Migration
{
    /// <summary>
    /// Resolves the active migration context (cluster) from MigrationSelections
    /// and triggers one NetApp snapshot per distinct storage name.
    /// Throws on any failure to abort the run safely.
    /// </summary>
    public sealed class StorageSnapshotCoordinator : IStorageSnapshotCoordinator
    {
        private readonly ApplicationDbContext _db;
        private readonly INetappSnapshotService _snapshots;
        private readonly ILogger<StorageSnapshotCoordinator> _log;

        public StorageSnapshotCoordinator(
            ApplicationDbContext db,
            INetappSnapshotService snapshots,
            ILogger<StorageSnapshotCoordinator> log)
        {
            _db = db;
            _snapshots = snapshots;
            _log = log;
        }

        public async Task EnsureSnapshotsAsync(IReadOnlyCollection<string> storages, CancellationToken ct)
        {
            if (storages == null || storages.Count == 0)
            {
                _log.LogInformation("SnapshotCoordinator: no storages provided; nothing to do.");
                return;
            }

            // We keep exactly one active selection (as your UI uses FirstOrDefault()).
            var selection = await _db.MigrationSelections.AsNoTracking().FirstOrDefaultAsync(ct);
            if (selection == null)
                throw new InvalidOperationException("No MigrationSelection found — cannot resolve cluster for datastore snapshots.");

            var clusterId = selection.ClusterId;
            var distinctStorages = storages
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (distinctStorages.Count == 0)
            {
                _log.LogInformation("SnapshotCoordinator: storages list collapsed to empty after filtering.");
                return;
            }

            // Use a clear label so these are recognizable and policy-compatible.
            const string snapmirrorLabel = "Migration";

            foreach (var storage in distinctStorages)
            {
                ct.ThrowIfCancellationRequested();

                _log.LogInformation("SnapshotCoordinator: creating snapshot (cluster {ClusterId}) for storage '{Storage}' with label '{Label}'",
                    clusterId, storage, snapmirrorLabel);

                var result = await _snapshots.CreateSnapshotAsync(
                    clusterId: clusterId,
                    storageName: storage,
                    snapmirrorLabel: snapmirrorLabel,
                    snapLocking: false,                 // pre-migration safety point; no lock
                    lockRetentionCount: null,
                    lockRetentionUnit: null,
                    ct: ct);

                if (result == null || !result.Success)
                {
                    var err = result?.ErrorMessage ?? "Unknown error";
                    _log.LogError("SnapshotCoordinator: snapshot failed for storage '{Storage}': {Error}", storage, err);
                    throw new InvalidOperationException($"Snapshot failed for storage '{storage}': {err}");
                }

                _log.LogInformation("SnapshotCoordinator: snapshot created for storage '{Storage}' -> {SnapshotName}",
                    storage, result.SnapshotName);
            }

            _log.LogInformation("SnapshotCoordinator: all datastore snapshots ensured for {Count} storage(s).", distinctStorages.Count);
        }
    }
}
