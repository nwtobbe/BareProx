using BareProx.Models;

namespace BareProx.Services.Proxmox.Authentication
{
    /// <summary>
    /// Defines methods for Proxmox API authentication and token management.
    /// </summary>
    public interface IProxmoxAuthenticator
    {
        /// <summary>
        /// Ensure the API ticket and CSRF tokens are valid for the specified cluster.
        /// If expired or missing, performs authentication against Proxmox and updates the stored tokens.
        /// </summary>
        /// <param name="clusterId">Database ID of the Proxmox cluster record.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if authentication succeeded or tokens were already valid; false otherwise.</returns>
        Task<bool> AuthenticateAndStoreTokenCidAsync(int clusterId, CancellationToken ct = default);
        Task<bool> AuthenticateAndStoreTokenCAsync(ProxmoxCluster cluster, CancellationToken ct = default);
        /// <summary>
        /// Builds an HttpClient pre-configured with the PVEAuthCookie and CSRFPreventionToken headers.
        /// Ensures tokens are refreshed before returning.
        /// </summary>
        /// <param name="clusterId">Database ID of the Proxmox cluster record.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Authenticated HttpClient.</returns>
        Task<HttpClient> GetAuthenticatedClientAsync(ProxmoxCluster cluster, CancellationToken ct = default);
    }
}
