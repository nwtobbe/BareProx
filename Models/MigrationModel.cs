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

using Microsoft.AspNetCore.Mvc.Rendering;
using System.ComponentModel.DataAnnotations;

namespace BareProx.Models
{
    public class MigrationSettingsViewModel
    {
        [Display(Name = "Proxmox Cluster")]
        public int SelectedClusterId { get; set; }

        [Display(Name = "Proxmox Host")]
        public int SelectedHostId { get; set; }

        [Display(Name = "Migration Datastore")]
        public string? SelectedStorageIdentifier { get; set; }

        public List<SelectListItem> ClusterOptions { get; set; } = new();
        public List<SelectListItem> HostOptions { get; set; } = new();
        public List<SelectListItem> StorageOptions { get; set; } = new();
    }

    public class MigratePageViewModel
    {
        public string? ClusterName { get; set; }
        public string? HostName { get; set; }
        public string? StorageName { get; set; }
    }
    public class MigrateViewModel
    {
        [Display(Name = "Dry run (no writes)")]
        public bool DryRun { get; set; } = true;

        [Display(Name = "Migrate VMs")]
        public bool IncludeVMs { get; set; } = true;

        [Display(Name = "Migrate Templates")]
        public bool IncludeTemplates { get; set; }

        [Display(Name = "Migrate ISO images")]
        public bool IncludeISOs { get; set; }

        [Display(Name = "Notes")]
        [MaxLength(2000)]
        public string? Notes { get; set; }
    }

    public class VmScanCandidate
    {
        public string VmxPath { get; set; } = "";
        public string Name { get; set; } = "";
        public string? GuestOs { get; set; } // e.g., "otherlinux-64"
        public int? CpuCores { get; set; }   // parsed from vmx (numvcpus)
        public int? MemoryMiB { get; set; }  // parsed from vmx (memsize)
        public long? DiskSizeGiB { get; set; } // first VMDK size (approx)
        public string? NicMac { get; set; }
        public string? NicModel { get; set; } // vmxnet3/e1000
        public string Status { get; set; } = "Not queued";
    }

    public class VmImportRequest
    {
        public string VmxPath { get; set; } = "";
        public int VmId { get; set; }
        public string Name { get; set; } = "";
        public string CpuType { get; set; } = "x86-64-v2-AES";
        public string OsType { get; set; } = "l26"; // Linux 2.6+ in Proxmox
        public string Version { get; set; } = "6.x - 2.6 Kernel";
        public string TargetStorage { get; set; } = "";   // from settings datastore
        public string TargetBridge { get; set; } = "vmbr0";
        public string Format { get; set; } = "qcow2";     // or "raw"
        public string ScsiController { get; set; } = "virtio-scsi-single"; // or pvscsi
        public string NicModel { get; set; } = "virtio";
        public string? MacAddress { get; set; }
        public bool LiveImport { get; set; } = false; // must be stopped on source
    }

    public class VmxControllerDto
    {
        public string Type { get; set; } = "";   // scsi | sata | nvme
        public int Index { get; set; }           // controller index (e.g., scsi0 → 0)
        public string? Model { get; set; }       // for SCSI: lsisas1068, pvscsi, etc.
        public bool Present { get; set; } = true;
    }

    public class VmxItemDto
    {
        public string Name { get; set; } = "";
        public string VmxPath { get; set; } = "";
        public string? GuestOs { get; set; }           // friendly label for UI
        public string? GuestOsRaw { get; set; }        // raw VMX value

        public int? CpuCores { get; set; }
        public int? MemoryMiB { get; set; }
        public int? DiskSizeGiB { get; set; }

        public List<VmxDiskDto> Disks { get; set; } = new();
        public List<VmxNicDto> Nics { get; set; } = new();

        // NEW: metadata you wanted to keep
        public string? UuidBios { get; set; }
        public string? VcUuid { get; set; }
        public string? Firmware { get; set; }          // "efi" or "bios" (null → default bios)
        public bool IsUefi => string.Equals(Firmware, "efi", StringComparison.OrdinalIgnoreCase);
        public bool? SecureBoot { get; set; }          // from uefi.secureBoot.enabled
        public bool? Tpm2Present { get; set; }         // from tpm2.present
        public bool? DiskEnableUuid { get; set; }      // from disk.EnableUUID
        public string? NvramPath { get; set; }

        public List<VmxControllerDto> Controllers { get; set; } = new();

        public string Status { get; set; } = "Not queued";
    }

    public class VmxDiskDto
    {
        public string Source { get; set; } = "";
        public long? SizeGiB { get; set; }
        public string Storage { get; set; } = "";
        public string Bus { get; set; } = "";          // scsi|sata|ide|nvme (target bus in wizard)
        public string Index { get; set; } = "0";       // slot index on that bus
    }

    public class VmxNicDto
    {
        public string Model { get; set; } = "vmxnet3"; // vmxnet3/e1000/virtio…
        public string? Mac { get; set; }               // address or generatedAddress
        public string? Bridge { get; set; }            // filled in wizard
        public string? Vlan { get; set; }              // filled in wizard
    }

    public class ImportItemDto
    {
        public int? VmId { get; set; }
        public string? Name { get; set; }
        public string? CpuType { get; set; }
        public string? OsType { get; set; }
        public bool PrepareVirtio { get; set; }
        public bool MountVirtioIso { get; set; }
        public string? VirtioIsoName { get; set; }
        public string? ScsiController { get; set; }
        public string? VmxPath { get; set; }
        public string? Uuid { get; set; }
        public bool Uefi { get; set; }
        public List<DiskDto>? Disks { get; set; }
        public List<NicDto>? Nics { get; set; }
    }

    public class DiskDto
    {
        public string? Source { get; set; }
        public double? SizeGiB { get; set; }
        public string? Storage { get; set; }
        public string? Bus { get; set; }
        public int? Index { get; set; }
    }

    public class NicDto
    {
        public string? Model { get; set; }
        public string? Mac { get; set; }
        public string? Bridge { get; set; }
        public int? Vlan { get; set; }
    }

    public sealed class QueueItemDto
    {
        public int? VmId { get; set; }
        public string? Name { get; set; }
        public string? CpuType { get; set; }
        public string? OsType { get; set; }

        public bool PrepareVirtio { get; set; }
        public bool MountVirtioIso { get; set; }
        public string? VirtioIsoName { get; set; }

        public string? ScsiController { get; set; }
        public string? VmxPath { get; set; }

        public string? Uuid { get; set; }
        public bool Uefi { get; set; }
        public int? MemoryMiB { get; set; }
        public int? Sockets { get; set; }
        public int? Cores { get; set; }

        // Accept either string or object/array; we'll serialize object if provided
        public string? DisksJson { get; set; }
        public string? NicsJson { get; set; }
        public object? Disks { get; set; }
        public object? Nics { get; set; }
    }
}