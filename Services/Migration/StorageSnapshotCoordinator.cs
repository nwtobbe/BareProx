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


// file: Services/Migration/StorageSnapshotCoordinator.cs
using BareProx.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BareProx.Services.Migration
{
    /// <summary>
    /// Resolves the active migration context and triggers one NetApp snapshot
    /// per distinct storage name (on the correct NetApp controller).
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

        public async Task EnsureSnapshotsByVolumeIdsAsync(IReadOnlyCollection<int> selectedNetappVolumeIds, CancellationToken ct)
        {
            if (selectedNetappVolumeIds == null || selectedNetappVolumeIds.Count == 0)
            {
                _log.LogInformation("SnapshotCoordinator: no SelectedNetappVolume ids provided; nothing to do.");
                return;
            }

            // Fetch the exact NetApp volumes to snapshot
            var vols = await _db.SelectedNetappVolumes
                .AsNoTracking()
                .Where(v => selectedNetappVolumeIds.Contains(v.Id))
                .Select(v => new
                {
                    v.Id,
                    v.NetappControllerId,
                    v.VolumeName,
                    v.Uuid,
                    v.Vserver,
                    v.Disabled
                })
                .ToListAsync(ct);

            if (vols.Count == 0)
            {
                _log.LogWarning("SnapshotCoordinator: none of the provided SelectedNetappVolume ids were found.");
                return;
            }

            const string snapmirrorLabel = "Migration";

            foreach (var v in vols)
            {
                ct.ThrowIfCancellationRequested();

                if (v.Disabled == true)
                    throw new InvalidOperationException(
                        $"SelectedNetappVolume (Id={v.Id}, {v.VolumeName}) is disabled; aborting.");

                _log.LogInformation("SnapshotCoordinator: creating snapshot for NetApp controller {ControllerId} volume '{Volume}' (SelVolId={Id}) with label '{Label}'",
                    v.NetappControllerId, v.VolumeName, v.Id, snapmirrorLabel);

                var result = await _snapshots.CreateSnapshotAsync(
                    controllerId: v.NetappControllerId,
                    storageName: v.VolumeName,
                    snapmirrorLabel: snapmirrorLabel,
                    snapLocking: false,
                    lockRetentionCount: null,
                    lockRetentionUnit: null,
                    volumeUuid: v.Uuid,          // ✅ prefer UUID
                    svmName: v.Vserver,          // ✅ SVM hint if UUID missing
                    ct: ct);

                if (result == null || !result.Success)
                {
                    var err = result?.ErrorMessage ?? "Unknown error";
                    _log.LogError("SnapshotCoordinator: snapshot failed for volume '{Volume}': {Error}", v.VolumeName, err);
                    throw new InvalidOperationException($"Snapshot failed for volume '{v.VolumeName}': {err}");
                }

                _log.LogInformation("SnapshotCoordinator: snapshot created for volume '{Volume}' -> {SnapshotName}",
                    v.VolumeName, result.SnapshotName);
            }

            _log.LogInformation("SnapshotCoordinator: ensured snapshots for {Count} NetApp volume(s).", vols.Count);
        }

        public async Task EnsureSnapshotsAsync(IReadOnlyCollection<string> storages, CancellationToken ct)
        {
            if (storages == null || storages.Count == 0)
            {
                _log.LogInformation("SnapshotCoordinator: no storages provided; nothing to do.");
                return;
            }

            // Require one active selection (for trace/log parity with existing behavior)
            var selection = await _db.MigrationSelections.AsNoTracking().FirstOrDefaultAsync(ct);
            if (selection == null)
                throw new InvalidOperationException("No MigrationSelection found — cannot resolve migration context.");

            var distinctStorages = storages
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (distinctStorages.Count == 0)
            {
                _log.LogInformation("SnapshotCoordinator: storages list collapsed to empty after filtering.");
                return;
            }

            // Load controllers and mappings once
            var controllers = await _db.NetappControllers
                .AsNoTracking()
                .ToListAsync(ct);

            var primaryControllerIds = controllers
                .Where(c => c.IsPrimary)
                .Select(c => c.Id)
                .ToHashSet();

            // Pull mappings (we filter in memory using CI matching to be safe across collations)
            var selectedMaps = await _db.SelectedNetappVolumes
                .AsNoTracking()
                .ToListAsync(ct);

            // Resolve a chosen mapping per storage (prefer PRIMARY controller)
            var chosenMapByStorage =
                new Dictionary<string, (int controllerId, string volumeName, string? uuid, string? svm, bool disabled)>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var storage in distinctStorages)
            {
                var mapsForStorage = selectedMaps
                    .Where(m => string.Equals(m.VolumeName, storage, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (mapsForStorage.Count == 0)
                {
                    // Safer default: fail fast to avoid acting on the wrong array
                    throw new InvalidOperationException(
                        $"No SelectedNetappVolume mapping found for storage '{storage}'. Cannot determine NetApp controller.");
                }

                var preferred = mapsForStorage.FirstOrDefault(m => primaryControllerIds.Contains(m.NetappControllerId))
                                ?? mapsForStorage.First();

                chosenMapByStorage[storage] = (
                    controllerId: preferred.NetappControllerId,
                    volumeName: preferred.VolumeName,
                    uuid: string.IsNullOrWhiteSpace(preferred.Uuid) ? null : preferred.Uuid,
                    svm: string.IsNullOrWhiteSpace(preferred.Vserver) ? null : preferred.Vserver,
                    disabled: preferred.Disabled == true
                );
            }

            const string snapmirrorLabel = "Migration";

            foreach (var storage in distinctStorages)
            {
                ct.ThrowIfCancellationRequested();

                var chosen = chosenMapByStorage[storage];

                if (chosen.disabled)
                    throw new InvalidOperationException(
                        $"Storage '{storage}' is mapped but disabled; aborting.");

                if (string.IsNullOrWhiteSpace(chosen.uuid))
                {
                    _log.LogWarning("[No UUID] Falling back to name-based NetApp ops for storage '{Storage}' on controller {ControllerId}{SvmHint}",
                        storage,
                        chosen.controllerId,
                        string.IsNullOrWhiteSpace(chosen.svm) ? "" : $" (svm={chosen.svm})");
                }

                _log.LogInformation(
                    "SnapshotCoordinator: creating snapshot on controller {ControllerId} for storage '{Storage}' (selection ClusterId={ClusterId}) with label '{Label}'",
                    chosen.controllerId, storage, selection.ClusterId, snapmirrorLabel);

                var result = await _snapshots.CreateSnapshotAsync(
                    controllerId: chosen.controllerId,
                    storageName: chosen.volumeName,        // actual NetApp volume name
                    snapmirrorLabel: snapmirrorLabel,
                    snapLocking: false,                    // pre-migration safety point; no lock
                    lockRetentionCount: null,
                    lockRetentionUnit: null,
                    volumeUuid: chosen.uuid,               // ✅ prefer UUID when available
                    svmName: chosen.svm,                   // ✅ SVM hint only used when UUID is missing
                    ct: ct);

                if (result == null || !result.Success)
                {
                    var err = result?.ErrorMessage ?? "Unknown error";
                    _log.LogError(
                        "SnapshotCoordinator: snapshot failed for storage '{Storage}' on controller {ControllerId}: {Error}",
                        storage, chosen.controllerId, err);
                    throw new InvalidOperationException(
                        $"Snapshot failed for storage '{storage}' on controller {chosen.controllerId}: {err}");
                }

                _log.LogInformation(
                    "SnapshotCoordinator: snapshot created for storage '{Storage}' on controller {ControllerId} -> {SnapshotName}",
                    storage, chosen.controllerId, result.SnapshotName);
            }

            _log.LogInformation("SnapshotCoordinator: all datastore snapshots ensured for {Count} storage(s).", distinctStorages.Count);
        }

    }
}
