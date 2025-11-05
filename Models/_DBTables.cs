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

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace BareProx.Models
{

    public class FeatureToggle // Enable Experimental Features
    {
        public int Id { get; set; }
        public string Key { get; set; } = string.Empty;
        public bool Enabled { get; set; }
    }

    public class EmailSettings
    {
        // Single-row table (Id = 1)
        [Key]
        public int Id { get; set; } = 1;

        public bool Enabled { get; set; }

        [MaxLength(255)]
        public string? SmtpHost { get; set; }

        [Range(1, 65535)]
        public int SmtpPort { get; set; } = 587;

        // "None" | "StartTls" | "SslTls"
        [MaxLength(16)]
        public string SecurityMode { get; set; } = "StartTls";

        [MaxLength(255)]
        public string? Username { get; set; }

        // Protected with IDataProtector
        public string? ProtectedPassword { get; set; }

        [EmailAddress, MaxLength(255)]
        public string? From { get; set; }

        [MaxLength(1024)]
        public string? DefaultRecipients { get; set; }  // comma-separated

        public bool OnBackupSuccess { get; set; }
        public bool OnBackupFailure { get; set; } = true;
        public bool OnRestoreSuccess { get; set; }
        public bool OnRestoreFailure { get; set; } = true;
        public bool OnWarnings { get; set; } = true;

        // "Info" | "Warning" | "Error" | "Critical"
        [MaxLength(16)]
        public string MinSeverity { get; set; } = "Info";

    }
    public class ProxmoxCluster
    {
        public int Id { get; set; }
        public string Name { get; set; } = default!;
        public string Username { get; set; } = default!;        // include realm, e.g. "root@pam"
        public string PasswordHash { get; set; } = default!;    // encrypted

        // Diagnostics
        public string? LastStatus { get; set; }
        public DateTime? LastChecked { get; set; }
        public bool? HasQuorum { get; set; }
        public int? OnlineHostCount { get; set; }
        public int? TotalHostCount { get; set; }
        public string? LastStatusMessage { get; set; }

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

        // New status/health columns:
        public bool? IsOnline { get; set; }
        public string? LastStatus { get; set; }      // e.g. "Online", "Offline", "Error"
        public string? LastStatusMessage { get; set; } // Any error or status description
        public DateTime? LastChecked { get; set; }
        public ProxmoxCluster Cluster { get; set; }
        // NEW: Host-scoped auth
        public string? TicketEnc { get; set; }     // encrypted PVEAuthCookie (ticket)
        public string? CsrfEnc { get; set; }       // encrypted CSRF
        public DateTime? TicketIssuedUtc { get; set; }
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
    public class SelectedNetappVolumes
    {
        public int Id { get; set; }
        public string Vserver { get; set; }
        public string VolumeName { get; set; }
        public string Uuid { get; set; }
        public string MountIp { get; set; }
        // public int ClusterId { get; set; }
        public int NetappControllerId { get; set; }

        public long? SpaceSize { get; set; }         // maps to "space.size"
        public long? SpaceAvailable { get; set; }    // maps to "space.available"
        public long? SpaceUsed { get; set; }         // maps to "space.used"
        public string? ExportPolicyName { get; set; } // maps to "nas.export_policy.name"
        public bool? SnapshotLockingEnabled { get; set; } // maps to "snapshot_locking_enabled"
        public bool? Disabled { get; set; }              // Is this volume disabled for selection?
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
        public int? SelectedNetappVolumeId { get; set; }
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
        // Notifications (new master switch)
        public bool NotificationsEnabled { get; set; } = true;
        public bool NotifyOnSuccess { get; set; }          // default false
        public bool NotifyOnError { get; set; }            // default false
        public string? NotificationEmails { get; set; }    // optional CSV override; null => use global/default
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
        public bool SnapshotAsvolumeChain { get; set; }
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

    public class JobVmResult
    {
        public int Id { get; set; }

        // FK to Jobs
        public int JobId { get; set; }
        public Job Job { get; set; } = null!;

        // VM identity
        public int VMID { get; set; }
        public string VmName { get; set; } = string.Empty;
        public string HostName { get; set; } = string.Empty;
        public string StorageName { get; set; } = string.Empty;

        // Outcome
        // Suggested values: Success | Skipped | Warning | Failed
        public string Status { get; set; } = "Pending";
        public string? Reason { get; set; }   // e.g. "Excluded", "Stopped", "Snapshot timeout", etc.
        public string? ErrorMessage { get; set; }

        // Snapshot info
        public bool WasRunning { get; set; }
        public bool IoFreezeAttempted { get; set; }
        public bool IoFreezeSucceeded { get; set; }
        public bool SnapshotRequested { get; set; }
        public bool SnapshotTaken { get; set; }
        public string? ProxmoxSnapshotName { get; set; }
        public string? SnapshotUpid { get; set; }

        // Timing
        public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAtUtc { get; set; }

        // Optional tie-back to your BackupRecord row (once created)
        public int? BackupRecordId { get; set; }
        public BackupRecord? BackupRecord { get; set; }

        public ICollection<JobVmLog> Logs { get; set; } = new List<JobVmLog>();
    }

    public class JobVmLog
    {
        public int Id { get; set; }
        public int JobVmResultId { get; set; }
        public JobVmResult JobVmResult { get; set; } = null!;
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

        // Info | Warning | Error
        public string Level { get; set; } = "Info";
        public string Message { get; set; } = string.Empty;
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
        public string? Period { get; set; }
    }
    public class MigrationSelection
    {
        public int Id { get; set; }

        // Which cluster the selection applies to
        public int ClusterId { get; set; }

        // The chosen host within that cluster
        public int ProxmoxHostId { get; set; }

        // The chosen datastore identifier (matches ProxSelectedStorage.StorageIdentifier)
        public int? SelectedNetappVolumeId { get; set; }
        public string StorageIdentifier { get; set; } = string.Empty;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public class MigrationQueueItem
    {
        [Key] public int Id { get; set; }

        public int? VmId { get; set; }
        [MaxLength(200)] public string? Name { get; set; }
        [MaxLength(100)] public string? CpuType { get; set; }
        // Prefer storing memory in MiB so we can write it straight to Proxmox.
        public int? MemoryMiB { get; set; }
        public int? Sockets { get; set; }
        public int? Cores { get; set; }

        [MaxLength(100)] public string? OsType { get; set; }

        public bool PrepareVirtio { get; set; }
        public bool MountVirtioIso { get; set; }
        [MaxLength(500)] public string? VirtioIsoName { get; set; }

        [MaxLength(100)] public string? ScsiController { get; set; }
        [MaxLength(1024)] public string? VmxPath { get; set; }

        [MaxLength(100)] public string? Uuid { get; set; }
        public bool Uefi { get; set; }

        // JSON blobs for arrays
        public string DisksJson { get; set; } = "[]";
        public string NicsJson { get; set; } = "[]";

        [MaxLength(40)] public string Status { get; set; } = "Queued"; // Queued / Processing / Done / Failed
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    }

    public class MigrationQueueLog
    {
        public int Id { get; set; }

        public int ItemId { get; set; }                 // FK to MigrationQueueItem
        public DateTime Utc { get; set; } = DateTime.UtcNow;

        [MaxLength(16)] public string Level { get; set; } = "Info";  // Info | Warning | Error
        [MaxLength(64)] public string Step { get; set; } = "";      // e.g. CheckVmid, Symlink[0]
        [MaxLength(2000)] public string Message { get; set; } = "";
        public string? DataJson { get; set; }                         // optional payload/context
    }

}
