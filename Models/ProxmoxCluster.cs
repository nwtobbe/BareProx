using BareProx.Models;
using System;
using System.Collections.Generic;

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
    public class ProxSelectedStorage
    {
        public int Id { get; set; }
        public int ClusterId { get; set; }
        public string StorageIdentifier { get; set; } = "";
    }

}