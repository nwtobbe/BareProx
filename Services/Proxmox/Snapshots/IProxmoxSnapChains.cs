using BareProx.Models;

namespace BareProx.Services.Proxmox.Snapshots
{
    public interface IProxmoxSnapChains
    {
        /// <summary>
        /// Reads storage definition and returns whether "snapshot-as-volume-chain" is active.
        /// GET /api2/json/storage/{storage}
        /// </summary>
        Task<bool> IsSnapshotChainActiveFromDefAsync(
            ProxmoxCluster cluster,
            string storageName,
            CancellationToken ct = default);

        /// <summary>
        /// Creates (or ensures) an NFS storage with "snapshot-as-volume-chain" set/unset,
        /// and verifies it is mounted on a specific node.
        /// </summary>
        Task<bool> CreateOrUpdateNfsStorageWithChainAsync(
            ProxmoxCluster cluster,
            string node,
            string storageName,
            string serverIp,
            string exportPath,
            bool snapshotChainActive,
            string content = "images,backup,iso,vztmpl",
            string options = "vers=3",
            CancellationToken ct = default);

        /// <summary>
        /// Attempts to repair qcow2 external snapshot chains on a node/storage/vmid directory.
        /// Uses qemu-img rebase -u with normalized formats.
        /// </summary>
        Task<bool> RepairExternalSnapshotChainAsync(
            string nodeName,
            string storageName,
            int vmid,
            CancellationToken ct = default);
    }
}
