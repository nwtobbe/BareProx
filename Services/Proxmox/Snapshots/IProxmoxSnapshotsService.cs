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


using BareProx.Models;
using Microsoft.AspNetCore.Mvc;

namespace BareProx.Services.Proxmox.Snapshots
{
    public interface IProxmoxSnapshotsService
    {

        Task<List<ProxmoxSnapshotInfo>> GetSnapshotListAsync(ProxmoxCluster cluster, string node, string hostAddress, int vmid, CancellationToken ct = default);
        Task<string?> CreateSnapshotAsync(ProxmoxCluster cluster, string node, string hostAddress, int vmid, string snapshotName, string description, bool withMemory, bool dontTrySuspend, CancellationToken ct = default);
        Task DeleteSnapshotAsync(ProxmoxCluster cluster, string host, string hostaddress, int vmId, string snapshotName, CancellationToken ct = default);
        Task<bool> RollbackSnapshotAsync(ProxmoxCluster cluster, string node, string hostAddress, int vmid, string snapshotName, bool startAfterRollback, ILogger logger, CancellationToken ct = default);
    }
}
