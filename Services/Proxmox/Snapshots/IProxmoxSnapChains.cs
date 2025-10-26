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

namespace BareProx.Services.Proxmox.Snapshots
{
    public interface IProxmoxSnapChains
    {
        /// <summary>
        /// Reads storage definition and returns whether "snapshot-as-volume-chain" is active.
        /// GET /api2/json/storage/{storage}
        /// </summary>
        Task<bool> IsSnapshotChainActiveFromDefAsync(
            ProxmoxCluster cluster,
            string storageName,
            CancellationToken ct = default);

        /// <summary>
        /// Creates (or ensures) an NFS storage with "snapshot-as-volume-chain" set/unset,
        /// and verifies it is mounted on a specific node.
        /// </summary>
        Task<bool> CreateOrUpdateNfsStorageWithChainAsync(
            ProxmoxCluster cluster,
            string node,
            string storageName,
            string serverIp,
            string exportPath,
            bool snapshotChainActive,
            string content = "images,backup,iso,vztmpl",
            string options = "vers=3",
            CancellationToken ct = default);

        /// <summary>
        /// Attempts to repair qcow2 external snapshot chains on a node/storage/vmid directory.
        /// Uses qemu-img rebase -u with normalized formats.
        /// </summary>
        Task<bool> RepairExternalSnapshotChainAsync(
            string nodeName,
            string storageName,
            int vmid,
            CancellationToken ct = default);
    }
}
