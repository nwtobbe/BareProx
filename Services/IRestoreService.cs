using System.Threading.Tasks;
using BareProx.Models;

namespace BareProx.Services
{
    /// <summary>
    /// Defines the restore operation contract. Implementation should enqueue or execute the restore and return success.
    /// </summary>
    public interface IRestoreService
    {
        /// <summary>
        /// Runs the restore operation asynchronously.
        /// Returns true if the restore job was successfully queued or started.
        /// </summary>
        /// <param name="model">The restore parameters from the form.</param>
        /// <returns>True if queued/started, false otherwise.</returns>
        Task<bool> RunRestoreAsync(RestoreFormViewModel model);
    }
}