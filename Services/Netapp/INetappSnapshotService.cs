﻿/*
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
    public interface INetappSnapshotService
    {
        Task<SnapshotResult> CreateSnapshotAsync(int clusterId, string storageName, string snapmirrorLabel, bool snapLocking = false, int? lockRetentionCount = null, string? lockRetentionUnit = null, CancellationToken ct = default);
        Task<List<string>> GetSnapshotsAsync(int controllerId, string volumeName, CancellationToken ct = default);
        Task<DeleteSnapshotResult> DeleteSnapshotAsync(int controllerId, string volumeName, string snapshotName, CancellationToken ct = default);
        Task<List<VolumeSnapshotTreeDto>> GetSnapshotsForVolumesAsync(HashSet<string> volumeNames, CancellationToken ct = default);

    }
}
