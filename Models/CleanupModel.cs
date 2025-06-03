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

        // We no longer store snapshots here; snapshots live under PrimaryVolumeSnapshots.
    }

    // ** NEW **: one object per “primary volume,” with its own orphaned‐snapshot list.
    public class PrimaryVolumeSnapshots
    {
        public string VolumeName { get; set; } = "";

        /// <summary>
        /// All snapshot names on disk for this volume that are *not* in BackupRecords.
        /// </summary>
        public List<SnapshotInfo> OrphanedSnapshots { get; set; } = new();
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
