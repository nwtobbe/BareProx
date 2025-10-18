using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BareProx.Models;

namespace BareProx.Services.Backup
{
    public interface IBackupService
    {
        /// <summary>
        /// Starts a storage-wide backup job and returns true if the job finished successfully.
        /// </summary>
        Task<bool> StartBackupAsync(
            string storageName,
            bool isApplicationAware,
            string label,
            int clusterId,
            int netappControllerId,
            int retentionCount,
            string retentionUnit,
            bool enableIoFreeze,
            bool useProxmoxSnapshot,
            bool withMemory,
            bool dontTrySuspend,
            int scheduleId,
            bool replicateToSecondary,

            // Locking parameters
            bool enableLocking,
            int? lockRetentionCount,
            string? lockRetentionUnit,

            IEnumerable<string>? excludedVmIds = null,
            CancellationToken ct = default);
    }
}
