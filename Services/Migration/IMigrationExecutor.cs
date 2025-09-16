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
    using System.Threading;
    using System.Threading.Tasks;
    using BareProx.Models; // contains MigrationQueueItem

    /// <summary>Does the actual import for a single queued item.</summary>
    public interface IMigrationExecutor
    {
        /// <remarks>Throw to mark the item as Failed. You may set item.VmId inside this method.</remarks>
        Task ExecuteAsync(MigrationQueueItem item, CancellationToken ct);
    }
}
