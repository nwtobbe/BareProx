using Microsoft.AspNetCore.Mvc;

namespace BareProx.Models
{
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
        public bool IsSelected { get; set; } // ✅ Add this
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
    public class NetappVolumeExportDto
    {
        public string Vserver { get; set; }
        public string VolumeName { get; set; }
        public string Uuid { get; set; }
        public string MountIp { get; set; }
        public int ClusterId { get; set; }
    }

    public class VolumeSelection
    {
        public int Id { get; set; }
        public string Vserver { get; set; }
        public string VolumeName { get; set; }
        public string SnapshotPolicy { get; set; }
    }

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
