

using BareProx.Models;

namespace BareProx.Services
{
    public interface INetappSnapshotService
    {
        Task<SnapshotResult> CreateSnapshotAsync(int clusterId, string storageName, string snapmirrorLabel, bool snapLocking = false, int? lockRetentionCount = null, string? lockRetentionUnit = null, CancellationToken ct = default);
        Task<List<string>> GetSnapshotsAsync(int controllerId, string volumeName, CancellationToken ct = default);
        Task<DeleteSnapshotResult> DeleteSnapshotAsync(int controllerId, string volumeName, string snapshotName, CancellationToken ct = default);




    }
}
