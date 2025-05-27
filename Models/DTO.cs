namespace BareProx.Models
{
    public class ProxmoxStorage
    {
        public string Storage { get; set; }
        public string Node { get; set; }
    }


    // in BareProx.Models\StorageWithVMsDto.cs
    public class StorageWithVMsDto
    {
        public string StorageName { get; set; } = default!;
        public List<ProxmoxVM> VMs { get; set; } = new();
        public int ClusterId { get; set; }

        // renamed from StorageId:
        public int NetappControllerId { get; set; }
    }

}
