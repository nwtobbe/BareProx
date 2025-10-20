/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 *
 * Copyright (C) 2025 Tobias Modig
 *
 * This file is part of BareProx.
 *
 * BareProx is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * BareProx is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with BareProx. If not, see <https://www.gnu.org/licenses/>.
 */

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

        // NEW: host-aware client based on the URL's host (IP/DNS)
        Task<HttpClient> GetAuthenticatedClientForUrlAsync(
            ProxmoxCluster cluster,
            string url,
            CancellationToken ct = default);
    }
}
