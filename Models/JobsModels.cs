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


namespace BareProx.Models
{
    public class JobViewModel
    {
        public int Id { get; set; }
        public string? Type { get; set; }
        public string? RelatedVm { get; set; }
        public string? Status { get; set; }
        public DateTime StartedAtLocal { get; set; }
        public DateTime? CompletedAtLocal { get; set; }
        public string ErrorMessage { get; set; }
    }

    // ViewModels used by Details view
    public sealed class JobDetailsViewModel
    {
        public int JobId { get; set; }
        public string Type { get; set; } = string.Empty;
        public string RelatedVm { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? ErrorMessage { get; set; }
        public DateTime StartedAtLocal { get; set; }
        public DateTime? CompletedAtLocal { get; set; }
        public List<JobVmResultViewModel> VmResults { get; set; } = new();
    }

    public sealed class JobVmResultViewModel
    {
        public int Id { get; set; }
        public int JobId { get; set; }
        public int VMID { get; set; }
        public string VmName { get; set; } = string.Empty;
        public string HostName { get; set; } = string.Empty;
        public string StorageName { get; set; } = string.Empty;

        public string Status { get; set; } = "Pending";
        public string? Reason { get; set; }
        public string? ErrorMessage { get; set; }

        public bool WasRunning { get; set; }
        public bool IoFreezeAttempted { get; set; }
        public bool IoFreezeSucceeded { get; set; }
        public bool SnapshotRequested { get; set; }
        public bool SnapshotTaken { get; set; }
        public string? ProxmoxSnapshotName { get; set; }
        public string? SnapshotUpid { get; set; }

        public DateTime StartedAtLocal { get; set; }
        public DateTime? CompletedAtLocal { get; set; }

        public List<JobVmLogViewModel> Logs { get; set; } = new();
    }

    public sealed class JobVmLogViewModel
    {
        public string Level { get; set; } = "Info";
        public string Message { get; set; } = string.Empty;
        public DateTime TimestampLocal { get; set; }
    }
}
