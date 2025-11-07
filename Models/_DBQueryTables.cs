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

using System.ComponentModel.DataAnnotations;

namespace BareProx.Models
{


    public sealed class InventoryMetadata
    {
        public string Key { get; set; } = default!;
        public string? Value { get; set; }
    }

    public sealed class InventoryStorage
    {
        public int ClusterId { get; set; }
        public string StorageId { get; set; } = default!;

        public string Type { get; set; } = default!;
        public string ContentFlags { get; set; } = default!;
        public bool IsImageCapable { get; set; }

        public string? Server { get; set; }
        public string? Export { get; set; }
        public string? Path { get; set; }
        public string? Options { get; set; }
        public bool Shared { get; set; }

        public string? NetappVolumeUuid { get; set; }
        public string? MatchQuality { get; set; }

        public DateTime LastSeenUtc { get; set; }
        public string LastScanStatus { get; set; } = default!;
    }

    public sealed class InventoryVm
    {
        public int ClusterId { get; set; }
        public int VmId { get; set; }
        public string Name { get; set; } = default!;
        public string NodeName { get; set; } = default!;
        public string Type { get; set; } = default!;    // 'qemu' or 'lxc'
        public string Status { get; set; } = default!;
        public bool IsTemplate { get; set; }

        public DateTime LastSeenUtc { get; set; }
    }

    public sealed class InventoryVmDisk
    {
        public int ClusterId { get; set; }
        public int VmId { get; set; }
        public string StorageId { get; set; } = default!;
        public string VolId { get; set; } = default!;

        public string NodeName { get; set; } = default!;
        public bool IsBootDisk { get; set; }
        public long? SizeBytes { get; set; }

        public DateTime LastSeenUtc { get; set; }
    }

    public sealed class InventoryNetappVolume
    {
        public string VolumeUuid { get; set; } = default!;
        public int NetappControllerId { get; set; }

        public string SvmName { get; set; } = default!;
        public string VolumeName { get; set; } = default!;
        public string? JunctionPath { get; set; }
        public string? NfsIps { get; set; }   // JSON/CSV of NFS LIF IPs
        public bool IsPrimary { get; set; }
        public bool? SnapshotLockingEnabled { get; set; }

        public DateTime LastSeenUtc { get; set; }
    }

    public sealed class InventoryVolumeReplication
    {
        public string PrimaryVolumeUuid { get; set; } = default!;
        public string SecondaryVolumeUuid { get; set; } = default!;

        public string? RelationshipType { get; set; }
        public bool IsHealthy { get; set; }

        public DateTime LastSeenUtc { get; set; }
    }

    public class InventoryClusterStatus
    {
        [Key]                                  // PK: one row per cluster
        public int ClusterId { get; set; }
        public string? ClusterName { get; set; }
        public bool HasQuorum { get; set; }
        public int OnlineHostCount { get; set; }
        public int TotalHostCount { get; set; }
        public string? LastStatus { get; set; }
        public string? LastStatusMessage { get; set; }
        public DateTime LastCheckedUtc { get; set; }
    }

    public class InventoryHostStatus
    {
        // COMPOSITE KEY: (ClusterId, HostId) – define via Fluent API below
        public int ClusterId { get; set; }
        public int HostId { get; set; }

        public string Hostname { get; set; } = "";
        public string HostAddress { get; set; } = "";
        public bool IsOnline { get; set; }
        public string? LastStatus { get; set; }
        public string? LastStatusMessage { get; set; }
        public DateTime LastCheckedUtc { get; set; }
    }

    public class NetappSnapshot
    {
        public int Id { get; set; }
        public int JobId { get; set; }
        public string SnapshotName { get; set; }
        public string PrimaryVolume { get; set; } = null!;
        public string? SecondaryVolume { get; set; }
        public int PrimaryControllerId { get; set; }
        public int? SecondaryControllerId { get; set; }
        public bool ExistsOnPrimary { get; set; }
        public bool ExistsOnSecondary { get; set; }
        public DateTime CreatedAt { get; set; }         // Local time (converted via AppTimeZoneService)
        public string SnapmirrorLabel { get; set; }     // Optional
        public bool IsReplicated { get; set; }          // Did replication succeed
        public DateTime LastChecked { get; set; }
    }

}
