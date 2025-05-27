using Microsoft.AspNetCore.Mvc;

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
    }

}
