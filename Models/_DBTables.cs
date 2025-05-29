using Microsoft.AspNetCore.Mvc;
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
        public string Uuid { get; set; }   // the NetApp relationship UUID
        public int SourceControllerId { get; set; }
        public string SourceVolume { get; set; } = null!;
        public int DestinationControllerId { get; set; }
        public string DestinationVolume { get; set; } = null!;
        public string RelationshipType { get; set; } = "vault";
        public string? SnapMirrorPolicy { get; set; }
        public string? state { get; set; }
        public string? lag_time { get; set; } // or TimeSpan?
        public bool healthy { get; set; }

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
}
