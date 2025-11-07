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
        // identity / scope
        string storageName,
        int? selectedNetappVolumeId,
        string? volumeUuid,

        // context
        bool isApplicationAware,
        string label,
        int clusterId,
        int netappControllerId,

        // policy
        int retentionCount,
        string retentionUnit,

        // behavior
        bool enableIoFreeze,
        bool useProxmoxSnapshot,
        bool withMemory,
        bool dontTrySuspend,

        // scheduling / replication / locking
        int scheduleId,
        bool replicateToSecondary,
        bool enableLocking,
        int? lockRetentionCount,
        string? lockRetentionUnit,

        // extras
        IEnumerable<string>? excludedVmIds = null,
        CancellationToken ct = default
        );
    }

}
