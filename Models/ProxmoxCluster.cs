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

using System.Text.Json.Serialization;

namespace BareProx.Models
{
    public class SelectStorageViewModel
    {
        public int ClusterId { get; set; }
        public List<ProxmoxStorageDto> StorageList { get; set; } = new();
        public List<string> SelectedStorageIds { get; set; } = new();
    }

    public class ProxmoxStorageDto
    {
        public string Id { get; set; } = "";
        public string Storage { get; set; } = "";
        public string Type { get; set; } = "";
        public string Path { get; set; } = "";
        public string Node { get; set; } = "";
        public bool IsSelected { get; set; } = false;
    }

    public class StorageWithVMsDto
    {
        public string StorageName { get; set; } = default!;
        public List<ProxmoxVM> VMs { get; set; } = new();
        public int ClusterId { get; set; }

        // renamed from StorageId:
        public int NetappControllerId { get; set; }
        public bool IsReplicable { get; set; }
        public bool SnapshotLockingEnabled { get; set; }  // New property for snapshot locking
    }

    public class StorageConfig
    {
        [JsonPropertyName("storage")]
        public string Storage { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("content")]
        public string Content { get; set; } = "";

        [JsonPropertyName("online")]
        public int Online { get; set; }  // 1 = mounted/online, 0 = not
    }

}