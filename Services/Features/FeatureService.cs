/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 * Copyright (C) 2025-2026 Tobias Modig
 * Licensed under the GNU Affero General Public License v3.
 */
using System;
using System.Threading;
using System.Threading.Tasks;
using BareProx.Controllers;
using BareProx.Data;
using BareProx.Models;
using BareProx.Services.Migration;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Services.Features
{
    public sealed class FeatureService : IFeatureService
    {
        private const string MigrationFeatureKey = "ExperimentalExtra";
        private readonly ApplicationDbContext _db;
        private readonly IMigrationQueueRunner _migrationRunner;

        public FeatureService(ApplicationDbContext db, IMigrationQueueRunner migrationRunner)
        {
            _db = db;
            _migrationRunner = migrationRunner;
        }

        public async Task<bool> IsEnabledAsync(string key, CancellationToken ct = default)
        {
            var row = await _db.FeatureToggles.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Key == key, ct);
            return row?.Enabled == true;
        }

        public async Task SetAsync(string key, bool enabled, CancellationToken ct = default)
        {
            if (key == MigrationFeatureKey && !enabled)
            {
                // Don't remove the data out from under a live manual or automated cutover.
                // The feature-flag update and deletions commit together or not at all.
                if (_migrationRunner.IsRunning || MigrationAutomationQueueStageController.IsRunning)
                    throw new InvalidOperationException(
                        "Cannot disable Migration while a migration runner is active. " +
                        "Wait for it to finish before disabling the feature.");

                await using var transaction = await _db.Database.BeginTransactionAsync(ct);
                // Recheck just before destructive actions (same process only).
                if (_migrationRunner.IsRunning || MigrationAutomationQueueStageController.IsRunning)
                    throw new InvalidOperationException("Migration became active. No settings were removed.");

                // Never delete MigrationQueueLogs: these are the historical audit trail.
                // There is deliberately no cascade delete of these logs.
                await _db.MigrationQueueItems.ExecuteDeleteAsync(ct);
                await _db.MigrationSelections.ExecuteDeleteAsync(ct);
                await _db.MigrationVCenterSettings.ExecuteDeleteAsync(ct);

                var toggle = await _db.FeatureToggles
                    .FirstOrDefaultAsync(x => x.Key == key, ct);
                if (toggle is null)
                    _db.FeatureToggles.Add(new FeatureToggle { Key = key, Enabled = false });
                else
                    toggle.Enabled = false;

                await _db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return;
            }

            var row = await _db.FeatureToggles
                .FirstOrDefaultAsync(x => x.Key == key, ct);
            if (row is null)
                _db.FeatureToggles.Add(new FeatureToggle { Key = key, Enabled = enabled });
            else
                row.Enabled = enabled;
            await _db.SaveChangesAsync(ct);
        }
    }
}
