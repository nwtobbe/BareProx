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

namespace BareProx.Services
{
    public interface INetappVolumeService
    {
        Task<List<VserverDto>> GetVserversAndVolumesAsync(int controllerId, CancellationToken ct = default);
        Task<List<NetappMountInfo>> GetVolumesWithMountInfoAsync(int controllerId, CancellationToken ct = default);
        Task<List<NetappVolumeDto>> ListVolumesAsync(int controllerId, CancellationToken ct = default);
        Task<List<string>> ListVolumesByPrefixAsync(string prefix, int controllerId, CancellationToken ct = default);
        Task<VolumeInfo?> LookupVolumeAsync(string volumeName, int controllerId, CancellationToken ct = default);
        Task<bool> DeleteVolumeAsync(string volumeName, int controllerId, CancellationToken ct = default);
        Task UpdateAllSelectedVolumesAsync(CancellationToken ct = default);
    }
}
