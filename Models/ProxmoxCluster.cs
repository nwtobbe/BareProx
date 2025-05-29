using BareProx.Models;
using System;
using System.Collections.Generic;

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
    }

}