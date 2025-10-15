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

namespace BareProx.Models
{
    public class ProxmoxVM
    {
        public int Id { get; set; }  // Or string, depending on the API
        public string Name { get; set; }
        public string Storage { get; set; }
        public string Node { get; set; }
        // ... any other properties
        public string HostName { get; set; }
        public string HostAddress { get; set; }  // ← Add this
    }

    public class ProxmoxSnapshotInfo
    {
        public string Name { get; set; } = "";
        public int Snaptime { get; set; } = 0; // ← important
        public int Vmstate { get; set; } = 0;  // ← optional: 1 = includes memory
        public string? Description { get; set; }
    }

    public class ProxmoxSnapshot
    {
        public string Name { get; set; }
        public Dictionary<string, string> Disks { get; set; } = new();
        public Dictionary<string, string> Metadata { get; set; } = new();
    }

    public class ProxmoxVmWithSnapshots
    {
        public int VmId { get; set; }
        public string Name { get; set; }
        public List<ProxmoxSnapshot> Snapshots { get; set; } = new();
        // ...other properties (current disks, etc.)
    }

}
