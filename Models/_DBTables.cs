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

using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace BareProx.Models
{



    public class ProxmoxCluster
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Username { get; set; }
        public string PasswordHash { get; set; } // or encrypted string
        public string? ApiToken { get; set; }
        public string? CsrfToken { get; set; }
        public DateTime? TokenExpiry { get; set; }
        public string? LastStatus { get; set; }
        public DateTime? LastChecked { get; set; }

        public ICollection<ProxmoxHost> Hosts { get; set; } = new List<ProxmoxHost>();
    }

    public class ProxmoxHost
    {
        public int Id { get; set; }
        public int ClusterId { get; set; }

        // Existing property
        public string HostAddress { get; set; }

        // New property for hostname
        public string? Hostname { get; set; }

        // Navigation property
        public ProxmoxCluster Cluster { get; set; }
    }
    public class ProxSelectedStorage
    {
        public int Id { get; set; }
        public int ClusterId { get; set; }
        public string StorageIdentifier { get; set; } = "";
    }

    public class NetappController
    {
        public int Id { get; set; }

        public string Hostname { get; set; } = null!;

        public string IpAddress { get; set; } = null!;

        public bool IsPrimary { get; set; }

        // New properties:
        public string Username { get; set; } = null!;

        public string PasswordHash { get; set; } = null!;  // Store hashed password, NOT plain text!
    }
    public class SelectedNetappVolume
    {
        public int Id { get; set; }
        public string Vserver { get; set; }
        public string VolumeName { get; set; }
        public string Uuid { get; set; }
        public string MountIp { get; set; }
        public int ClusterId { get; set; }
        public int NetappControllerId { get; set; }

        public long? SpaceSize { get; set; }         // maps to "space.size"
        public long? SpaceAvailable { get; set; }    // maps to "space.available"
        public long? SpaceUsed { get; set; }         // maps to "space.used"
        public string? ExportPolicyName { get; set; } // maps to "nas.export_policy.name"
        public bool? SnapshotLockingEnabled { get; set; } // maps to "snapshot_locking_enabled"
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
    public class SnapMirrorRelation
    {
        public int Id { get; set; }
        public string Uuid { get; set; } = null!;                  // Relationship UUID (PK on NetApp)
        public int SourceControllerId { get; set; }
        public string SourceVolume { get; set; } = null!;
        public int DestinationControllerId { get; set; }
        public string DestinationVolume { get; set; } = null!;

        public string RelationshipType { get; set; } = "vault";
        public string? SnapMirrorPolicy { get; set; }
        public string? state { get; set; }
        public string? lag_time { get; set; }
        public bool healthy { get; set; }

        // --- NEW FIELDS ---
        public string? SourceClusterName { get; set; }
        public string? DestinationClusterName { get; set; }
        public string? SourceSvmName { get; set; }
        public string? DestinationSvmName { get; set; }
        public string? LastTransferState { get; set; }
        public DateTime? LastTransferEndTime { get; set; }
        public string? LastTransferDuration { get; set; }
        public string? PolicyUuid { get; set; }
        public string? PolicyType { get; set; }
        public string? ExportedSnapshot { get; set; }
        public string? TotalTransferDuration { get; set; }
        public long? TotalTransferBytes { get; set; }
        public string? LastTransferType { get; set; }
        public string? LastTransferCompressionRatio { get; set; }
        public string? BackoffLevel { get; set; }

        // --- Unmapped transfer details for one-off API responses ---
        [JsonPropertyName("transfer")]
        [NotMapped]
        public TransferInfo? transfer { get; set; }
    }

    public class BackupSchedule
    {
        public int Id { get; set; }
        public bool IsEnabled { get; set; } = true;
        public int ClusterId { get; set; }
        public int ControllerId { get; set; }
        public string Name { get; set; }
        public string StorageName { get; set; }
        public string Schedule { get; set; }
        public string Frequency { get; set; }
        public TimeSpan? TimeOfDay { get; set; }
        public bool IsApplicationAware { get; set; }
        public bool EnableIoFreeze { get; set; }
        public bool UseProxmoxSnapshot { get; set; }
        public bool WithMemory { get; set; }
        public string? ExcludedVmIds { get; set; }
        public int RetentionCount { get; set; }
        public string RetentionUnit { get; set; }
        public bool ReplicateToSecondary { get; set; }
        public DateTime? LastRun { get; set; }
        public bool EnableLocking { get; set; }
        public int? LockRetentionCount { get; set; }
        public string? LockRetentionUnit { get; set; }
    }

    public class BackupRecord
    {
        public int Id { get; set; }
        public int VMID { get; set; }
        public int JobId { get; set; }
        public Job Job { get; set; } = default!;
        public string? VmName { get; set; }
        public string HostName { get; set; }
        public string StorageName { get; set; }
        public string SnapshotName { get; set; }
        public int ControllerId { get; set; }
        public string Label { get; set; }
        public string ConfigurationJson { get; set; }
        public int RetentionCount { get; set; }
        public string RetentionUnit { get; set; } = default!;
        public DateTime TimeStamp { get; set; }
        // 🔗 Schedule that triggered this backup
        public int? ScheduleId { get; set; }
        // 🔐 App-aware context
        public bool IsApplicationAware { get; set; }
        public bool EnableIoFreeze { get; set; }
        public bool UseProxmoxSnapshot { get; set; }
        public bool WithMemory { get; set; }
        public bool ReplicateToSecondary { get; set; }
    }

    public class Job
    {
        public int Id { get; set; }
        public string Type { get; set; } // "Restore", "Backup", etc.
        public string Status { get; set; } // "Pending", "Running", "Completed", "Failed", "Cancelled"
        public string? ErrorMessage { get; set; }
        public string? RelatedVm { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        // Optional: serialized payload (config, labels, paths)
        public string? PayloadJson { get; set; }
    }

    public class SnapMirrorPolicy
    {
        public int Id { get; set; }
        public string Uuid { get; set; } = null!;
        public string Name { get; set; } = null!;
        public string Scope { get; set; } = null!; // "cluster", "svm", etc
        public string Type { get; set; } = null!;  // "async", etc
        public bool NetworkCompressionEnabled { get; set; }
        public int Throttle { get; set; }

        // JSON serialized retention, or normalized as a related table (see below)
        public List<SnapMirrorPolicyRetention> Retentions { get; set; } = new();

        // Optionally, add created/updated timestamps, etc.
    }

    public class SnapMirrorPolicyRetention
    {
        public int Id { get; set; }
        public int SnapMirrorPolicyId { get; set; }
        public SnapMirrorPolicy Policy { get; set; } = null!;

        public string Label { get; set; } = null!;     // "daily", "hourly", "weekly"
        public int Count { get; set; }
        public bool Preserve { get; set; }
        public int Warn { get; set; }
        public string? Period { get; set; }            // e.g. "P10M" for retention locking
    }

}
