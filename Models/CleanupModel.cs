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

namespace BareProx.Models
{
    public class CleanupPageViewModel
    {
        public CleanupPageViewModel()
        {
            Clusters = new List<CleanupClusterViewModel>();
        }

        public string? WarningMessage { get; set; }
        public List<CleanupClusterViewModel> Clusters { get; set; }
    }

    public class CleanupClusterViewModel
    {
        public string ClusterName { get; set; } = "";

        // Existing “In‐Use” clones and “Orphaned” clones:
        public List<CleanupItem> InUse { get; set; } = new();
        public List<CleanupItem> Orphaned { get; set; } = new();

        // ** NEW **: For each primary volume, a list of orphaned snapshots
        public List<PrimaryVolumeSnapshots> Volumes { get; set; } = new();
    }

    // Details about a single clone (unchanged):
    public class CleanupItem
    {
        public string VolumeName { get; set; } = "";
        public string? MountIp { get; set; }
        public bool IsInUse { get; set; }
        public List<ProxmoxVM> AttachedVms { get; set; } = new();
        public string ControllerName { get; set; } = "";
        public int ControllerId { get; set; }
        public int ClusterId { get; set; }
        public bool IsSelectedVolume { get; set; }
    }

    // ** NEW **: one object per “primary volume,” with its own orphaned‐snapshot list.
    public class PrimaryVolumeSnapshots
    {
        public string VolumeName { get; set; } = "";
        public List<SnapshotInfo> OrphanedSnapshots { get; set; } = new();
        public string ControllerName { get; set; } = "";
        public int ControllerId { get; set; }
    }

    // ** NEW **: For each orphaned snapshot, does there exist a clone built from it?
    // If there is, is that clone in‐use by a VM or mounted?
    public class SnapshotInfo
    {
        public string SnapshotName { get; set; } = "";

        /// <summary>
        /// If there is a FlexClone built from this snapshot, its name; or null if no clone exists.
        /// </summary>
        public string? CloneName { get; set; }

        /// <summary>
        /// If CloneName != null, these are the attached VMs running on that clone.
        /// </summary>
        public List<ProxmoxVM> CloneAttachedVms { get; set; } = new();

        /// <summary>
        /// If CloneName != null, what MountIp (if any) is the clone currently mounted on?
        /// </summary>
        public string? CloneMountIp { get; set; }
    }


}
