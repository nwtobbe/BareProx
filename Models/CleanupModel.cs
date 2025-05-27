using Microsoft.AspNetCore.Mvc;

namespace BareProx.Models
{
    public class CleanupItem
    {
        public string VolumeName { get; set; } = "";
        public string MountIp { get; set; } = "";
        public bool IsInUse { get; set; }
        public List<ProxmoxVM> AttachedVms { get; set; } = new();
    }

    public class CleanupPageViewModel
    {
        public List<CleanupItem> InUse { get; set; }
        public List<CleanupItem> Orphaned { get; set; }
    }
}
