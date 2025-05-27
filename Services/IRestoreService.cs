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

using BareProx.Models;

namespace BareProx.Services
{
    /// <summary>
    /// Defines the restore operation contract. Implementation should enqueue or execute the restore and return success.
    /// </summary>
    public interface IRestoreService
    {
        /// <summary>
        /// Runs the restore operation asynchronously.
        /// Returns true if the restore job was successfully queued or started.
        /// </summary>
        /// <param name="model">The restore parameters from the form.</param>
        /// <returns>True if queued/started, false otherwise.</returns>
        Task<bool> RunRestoreAsync(RestoreFormViewModel model, CancellationToken ct);
    }
}