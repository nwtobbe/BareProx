// Services/Proxmox/IProxmoxRestore.cs
using BareProx.Models;

namespace BareProx.Services.Proxmox
{
    public interface IProxmoxRestore
    {
        Task<bool> RestoreVmFromConfigAsync(
            RestoreFormViewModel model,
            string hostAddress,
            string cloneStorageName,
            bool snapshotChainActive = false,
            CancellationToken ct = default);

        Task<bool> RestoreVmFromConfigWithOriginalIdAsync(
            RestoreFormViewModel model,
            string hostAddress,
            string cloneStorageName,
            bool snapshotChainActive = false,
            CancellationToken ct = default);
    }
}
