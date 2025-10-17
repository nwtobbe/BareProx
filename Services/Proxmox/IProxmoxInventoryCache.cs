// Services/Proxmox/Interfaces/IProxmoxInventoryCache.cs
using BareProx.Models;

namespace BareProx.Services
{
    public interface IProxmoxInventoryCache
    {
        Task<Dictionary<string, List<ProxmoxVM>>> GetVmsByStorageListAsync(
            ProxmoxCluster cluster,
            IEnumerable<string> storageNames,
            CancellationToken ct,
            TimeSpan? maxAge = null,
            bool forceRefresh = false);

        // NOTE: storageFilterNames is optional. If null/empty, we cache the heavy discovery path.
        Task<Dictionary<string, List<ProxmoxVM>>> GetEligibleBackupStorageWithVMsAsync(
            ProxmoxCluster cluster,
            int netappControllerId,
            IEnumerable<string>? storageFilterNames,
            CancellationToken ct,
            TimeSpan? maxAge = null,
            bool forceRefresh = false);

        void InvalidateCluster(int clusterId);
        void InvalidateAll();
    }
}
