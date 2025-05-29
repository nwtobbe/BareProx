
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;
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
    }

    public class NetappControllerTreeDto
    {
        public string ControllerName { get; set; }
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

    //public class VolumeSelection
    //{
    //    public int Id { get; set; }
    //    public string Vserver { get; set; }
    //    public string VolumeName { get; set; }
    //    public string SnapshotPolicy { get; set; }
    //}

    public class NetappMountInfo
    {
        public string VolumeName { get; set; } = string.Empty;
        public string VserverName { get; set; }
        public string MountIp { get; set; } = string.Empty;
        public string MountPath { get; set; } = string.Empty;
    }

    public class VserverDto
    {
        public string Name { get; set; } = null!;
        public List<NetappVolumeDto> Volumes { get; set; } = new();
    }
}
