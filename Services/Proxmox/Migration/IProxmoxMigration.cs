using BareProx.Models;
using System.Threading;
using System.Threading.Tasks;

namespace BareProx.Services.Proxmox.Migration
{
    public interface IProxmoxMigration
    {
        // VMID
        Task<bool> IsVmidAvailableAsync(int vmid, CancellationToken ct = default);

        // Files / directories on PVE (via SSH)
        Task EnsureDirectoryAsync(string absPath, CancellationToken ct = default);
        Task<string> ReadTextFileAsync(string absPath, CancellationToken ct = default);
        Task WriteTextFileAsync(string absPath, string content, CancellationToken ct = default);

        // QEMU/VM config helpers used by migration
        Task<int?> FirstFreeVirtioSlotAsync(int vmid, CancellationToken ct = default);
        Task AddDummyDiskAsync(int vmid, string storage, int slot, int sizeGiB, CancellationToken ct = default);
        Task AddEfiDiskAsync(int vmid, string storage, CancellationToken ct = default);
        Task SetCdromAsync(int vmid, string volidOrName, CancellationToken ct = default);

        Task<IReadOnlyList<PveNetworkIf>> GetNodeNetworksAsync(string node, CancellationToken ct = default);
        Task<IReadOnlyList<PveSdnVnet>> GetSdnVnetsAsync(CancellationToken ct = default);
        Task<IReadOnlyList<PveStorageContentItem>> GetStorageContentAsync(string node, string storage, string content, CancellationToken ct = default);
        Task<IReadOnlyList<PveStorageListItem>> GetNodeStoragesAsync(string node, CancellationToken ct = default);
    }
}
