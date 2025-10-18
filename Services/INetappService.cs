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

namespace BareProx.Services
{
    using BareProx.Models;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    public interface INetappService
    {
        // --- Periodic volume and mount‐info sync ---
        Task UpdateAllSelectedVolumesAsync(CancellationToken ct = default);

        // --- Vserver and volume info ---
        
        Task<List<string>> ListFlexClonesAsync(int controllerId, CancellationToken ct = default);
        

        // --- Cloning and export policies ---
        Task<bool> EnsureExportPolicyExistsOnSecondaryAsync(
    string exportPolicyName,
    int primaryControllerId,
    int secondaryControllerId,
    string svmName,
    CancellationToken ct = default);

        Task<FlexCloneResult> CloneVolumeFromSnapshotAsync(string volumeName, string snapshotName, string cloneName, int controllerId, CancellationToken ct = default);
        Task<bool> SetExportPolicyAsync(string volumeName, string exportPolicyName, int controllerId, CancellationToken ct = default);
        Task<bool> CopyExportPolicyAsync(string sourceVolume, string targetVolume, int controllerId, CancellationToken ct = default);
        Task<bool> SetVolumeExportPathAsync(string volumeUuid, string exportPath, int controllerId, CancellationToken ct = default);

        // --- NFS and network ---
        Task<List<string>> GetNfsEnabledIpsAsync(int controllerId, string vserver, CancellationToken ct = default);

        // --- VM file operations ---
        //Task<bool> MoveAndRenameAllVmFilesAsync(
        //    string volumeName,
        //    int controllerId,
        //    string oldvmid,
        //    string newvmid,
        //    CancellationToken ct = default);

        // --- Volume deletion ---
        
    }
}
