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
 * along with BareProx. If not, see <https://www.gnu.org/licenses/>..
 */

using BareProx.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace BareProx.Services.Backup
{
    /// <summary>
    /// Per-node semaphores for snapshot create/delete. Reads limits from IOptionsMonitor so
    /// config changes apply to NEW acquires without restarting the app.
    ///
    /// NOTE:
    /// - We do NOT resize existing semaphores.
    /// - When limits change, we clear the caches so new semaphores are created with the new limits.
    /// - In-flight operations keep using the old semaphores until they finish.
    /// </summary>
    public sealed class NodeSnapshotGateManager : INodeSnapshotGateManager, IDisposable
    {
        private readonly IOptionsMonitor<BackupThrottlesOptions> _opts;
        private readonly ILogger<NodeSnapshotGateManager> _logger;

        // Per-node semaphores (create/delete)
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _create = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _delete = new(StringComparer.OrdinalIgnoreCase);

        private IDisposable? _onChangeSub;

        // Volatile limits (used for new semaphore creation)
        private volatile int _createLimit = 1;
        private volatile int _deleteLimit = 1;

        public NodeSnapshotGateManager(
            IOptionsMonitor<BackupThrottlesOptions> opts,
            ILogger<NodeSnapshotGateManager> logger)
        {
            _opts = opts;
            _logger = logger;

            Apply(_opts.CurrentValue);
            _onChangeSub = _opts.OnChange(Apply);
        }

        private void Apply(BackupThrottlesOptions o)
        {
            // Clamp to sane minimums
            var newCreate = Math.Max(1, o.MaxParallelPerNodeSnapshotCreate);
            var newDelete = Math.Max(1, o.MaxParallelPerNodeSnapshotDelete);

            var oldCreate = _createLimit;
            var oldDelete = _deleteLimit;

            _createLimit = newCreate;
            _deleteLimit = newDelete;

            // Only log/clear when it actually changes
            if (newCreate != oldCreate || newDelete != oldDelete)
            {
                _logger.LogInformation(
                    "Snapshot per-node gates updated: Create={Create} Delete={Delete}",
                    newCreate, newDelete);

                // IMPORTANT:
                // We do NOT try to resize existing semaphores.
                // Instead, we clear caches so future acquires create new semaphores with the new limits.
                // Existing semaphores may still be held by in-flight operations and will be GC'd later.
                _create.Clear();
                _delete.Clear();
            }
        }

        private static string Key(string? node) =>
            string.IsNullOrWhiteSpace(node) ? "unknown" : node.Trim();

        public async ValueTask<IDisposable> AcquireCreateAsync(string? node, CancellationToken ct)
        {
            // Read current limit at creation time (avoids “old limit” semaphores after Apply/clear)
            var limit = Math.Max(1, Volatile.Read(ref _createLimit));

            var sem = _create.GetOrAdd(Key(node), _ => new SemaphoreSlim(limit, limit));
            await sem.WaitAsync(ct).ConfigureAwait(false);
            return new Releaser(sem);
        }

        public async ValueTask<IDisposable> AcquireDeleteAsync(string? node, CancellationToken ct)
        {
            var limit = Math.Max(1, Volatile.Read(ref _deleteLimit));

            var sem = _delete.GetOrAdd(Key(node), _ => new SemaphoreSlim(limit, limit));
            await sem.WaitAsync(ct).ConfigureAwait(false);
            return new Releaser(sem);
        }

        private sealed class Releaser : IDisposable
        {
            private SemaphoreSlim? _sem;
            public Releaser(SemaphoreSlim sem) => _sem = sem;

            public void Dispose()
            {
                var s = Interlocked.Exchange(ref _sem, null);
                if (s != null) s.Release();
            }
        }

        public void Dispose()
        {
            _onChangeSub?.Dispose();
            _onChangeSub = null;

            foreach (var s in _create.Values) s.Dispose();
            foreach (var s in _delete.Values) s.Dispose();

            _create.Clear();
            _delete.Clear();
        }
    }
}
