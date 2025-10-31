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

namespace BareProx.Services.Proxmox.Migration
{
    /// <summary>
    /// Proxmox migration helpers that operate explicitly on a given node.
    /// Every SSH/API operation is pinned to the provided <paramref name="node"/>.
    /// </summary>
    public interface IProxmoxMigration
    {
        // VMID (node-scoped)
        Task<bool> IsVmidAvailableAsync(string node, int vmid, CancellationToken ct = default);

        // Files / directories on PVE (via SSH; node-scoped)
        Task EnsureDirectoryAsync(string node, string absPath, CancellationToken ct = default);
        Task<string> ReadTextFileAsync(string node, string absPath, CancellationToken ct = default);
        Task WriteTextFileAsync(string node, string absPath, string content, CancellationToken ct = default);
        Task<bool> FileExistsAsync(string node, string absPath, CancellationToken ct = default);

        // QEMU/VM config helpers used by migration (node-scoped)
        Task<int?> FirstFreeVirtioSlotAsync(string node, int vmid, CancellationToken ct = default);
        Task AddDummyDiskAsync(string node, int vmid, string storage, int slot, int sizeGiB, CancellationToken ct = default);
        Task AddEfiDiskAsync(string node, int vmid, string storage, CancellationToken ct = default);
        Task SetCdromAsync(string node, int vmid, string volidOrName, CancellationToken ct = default);

        // Capabilities / inventory
        Task<IReadOnlyList<PveNetworkIf>> GetNodeNetworksAsync(string node, CancellationToken ct = default);
        Task<IReadOnlyList<PveSdnVnet>> GetSdnVnetsAsync(CancellationToken ct = default);
        Task<IReadOnlyList<PveStorageContentItem>> GetStorageContentAsync(string node, string storage, string content, CancellationToken ct = default);
        Task<IReadOnlyList<PveStorageListItem>> GetNodeStoragesAsync(string node, CancellationToken ct = default);
    }
}
