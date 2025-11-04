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

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BareProx.Models;

namespace BareProx.Services.Backup
{
    public interface IBackupService
    {
        /// <summary>
        /// Starts a storage-wide backup job and returns true if the job finished successfully.
        /// </summary>
        Task<bool> StartBackupAsync(
            string storageName,
            int? SelectedNetappVolumeId,
            bool isApplicationAware,
            string label,
            int clusterId,
            int netappControllerId,
            int retentionCount,
            string retentionUnit,
            bool enableIoFreeze,
            bool useProxmoxSnapshot,
            bool withMemory,
            bool dontTrySuspend,
            int scheduleId,
            bool replicateToSecondary,

            // Locking parameters
            bool enableLocking,
            int? lockRetentionCount,
            string? lockRetentionUnit,

            IEnumerable<string>? excludedVmIds = null,
            CancellationToken ct = default);
    }
}
