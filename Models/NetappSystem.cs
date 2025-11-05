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


using Newtonsoft.Json.Converters;
using Newtonsoft.Json;
using System.Text.Json.Serialization;

namespace BareProx.Models
{
    public class SnapshotResult
    {
        public bool Success { get; set; }
        public string SnapshotName { get; set; }

        // existing property, e.g.:
        public string ErrorMessage { get; set; }

        // alias:
        public string Message
        {
            get => ErrorMessage;
            set => ErrorMessage = value;
        }
    }
    public class DeleteSnapshotResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
    }
    public class SnapshotCreateBody
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("snapmirror_label")]
        public string SnapMirrorLabel { get; set; }

        // Only serialized when not null
        [JsonProperty("expiry_time", NullValueHandling = NullValueHandling.Ignore)]
        [Newtonsoft.Json.JsonConverter(typeof(CustomDateTimeConverter))]
        public DateTime? ExpiryTime { get; set; }

        [JsonProperty("snaplock", NullValueHandling = NullValueHandling.Ignore)]
        public SnapLockBlock SnapLock { get; set; }

        [JsonProperty("snaplock_expiry_time", NullValueHandling = NullValueHandling.Ignore)]
        [Newtonsoft.Json.JsonConverter(typeof(CustomDateTimeConverter))]
        public DateTime? SnapLockExpiryTime { get; set; }

        public class SnapLockBlock
        {
            [JsonProperty("expiry_time")]
            [Newtonsoft.Json.JsonConverter(typeof(CustomDateTimeConverter))]
            public DateTime ExpiryTime { get; set; }
        }
    }

    /// <summary>
    /// Custom converter that forces "yyyy-MM-dd HH:mm:ss" formatting on DateTime.
    /// </summary>
    public class CustomDateTimeConverter : IsoDateTimeConverter
    {
        public CustomDateTimeConverter()
        {
            DateTimeFormat = "yyyy-MM-dd HH:mm:ss";
        }
    }

    public class FlexCloneResult
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string? CloneVolumeName { get; set; }

        // ← add this:
        public string? JobUuid { get; set; }
    }

    public class VolumeInfo
    {
        public string Uuid { get; set; } = null!;
    }


    public class TransferInfo
    {
        [JsonPropertyName("state")]
        public string State { get; set; } = "";

        [JsonPropertyName("bytes_transferred")]
        public long BytesTransferred { get; set; }

        [JsonPropertyName("total_duration")]
        public string TotalDuration { get; set; } = "";

        [JsonPropertyName("end_time")]
        public DateTimeOffset EndTime { get; set; }
    }
    public class VolumeSnapshotViewModel
    {
        public string PrimaryVolumeName { get; set; }
        public string? SecondaryVolumeName { get; set; }
        public List<string> SnapshotNames { get; set; }
    }
    public class VolumeSnapshotTreeDto
    {
        public string Vserver { get; set; } = null!;
        public string VolumeName { get; set; } = null!;
        public List<string> Snapshots { get; set; } = new();
    }
    public class MountSnapshotViewModel
    {
        public string VolumeName { get; set; } = null!;
        public string Vserver { get; set; } = null!;
        public string SnapshotName { get; set; } = null!;
        public string ReadWrite { get; set; } = "ro";  // "ro" or "rw"
        public string MountIp { get; set; } = null!;
        public int ControllerId { get; set; }
        public int ClusterId { get; set; }
        public bool IsSecondary { get; set; }
        public int? PrimaryControllerId { get; set; }
        public string? PrimaryVolumeName { get; set; }

    }

    public class NetappControllerTreeDto
    {
        public string ControllerName { get; set; } = null!; // Name of the controller, e.g. "netapp1"
        public int ControllerId { get; set; } // Unique identifier for the controller
        public bool IsPrimary { get; set; } // "true" or "false"
        public List<NetappSvmDto> Svms { get; set; } = new();
    }

    public class NetappSvmDto
    {
        public string Name { get; set; }
        public List<NetappVolumeDto> Volumes { get; set; } = new();
    }

    public class NetappVolumeDto
    {
        public string VolumeName { get; set; }
        public string Uuid { get; set; }
        public string MountIp { get; set; }
        public int ClusterId { get; set; }
        public string Vserver { get; set; }
        public bool IsSelected { get; set; }
        public List<string> Snapshots { get; set; } = new();
    }

    public class NetappVolumeExportDto
    {
        public string Vserver { get; set; }
        public string VolumeName { get; set; }
        public string Uuid { get; set; }
        public string MountIp { get; set; }
        public int ClusterId { get; set; }
    }

    public sealed class NetappMountInfo
    {
        public string VolumeName { get; set; } = string.Empty;
        public string VserverName { get; set; } = string.Empty;

        // Full "IP:/junction" string you compose for mounts
        public string MountPath { get; set; } = string.Empty;

        // The IP address used for the mount (helpful for matching against Proxmox)
        public string? MountIp { get; set; }

        // NEW: which NetApp controller this entry belongs to
        public int NetappControllerId { get; set; }

        // Optional extras (keep if useful; safe to leave out if you don't use them)
        public string? Uuid { get; set; }
        public string? JunctionPath { get; set; }
    }

    public class VserverDto
    {
        public string Name { get; set; } = null!;
        public List<NetappVolumeDto> Volumes { get; set; } = new();
    }
}
