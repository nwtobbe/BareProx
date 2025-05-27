using BareProx.Models;
using System.Text.Json;

namespace BareProx.Services.Proxmox.Helpers
{
    public interface IProxmoxHelpers
    {
        IEnumerable<ProxmoxHost> GetQueryableHosts(ProxmoxCluster cluster);
        ProxmoxHost GetHostByNodeName(ProxmoxCluster cluster, string nodeName);
        public Dictionary<string, string> FlattenConfig(JsonElement config);
    }
}
