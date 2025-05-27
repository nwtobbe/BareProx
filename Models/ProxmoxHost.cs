namespace BareProx.Models
{
    public class ProxmoxHost
    {
        public int Id { get; set; }
        public int ClusterId { get; set; }

        // Existing property
        public string HostAddress { get; set; }

        // New property for hostname
        public string? Hostname { get; set; }

        // Navigation property
        public ProxmoxCluster Cluster { get; set; }
    }
}