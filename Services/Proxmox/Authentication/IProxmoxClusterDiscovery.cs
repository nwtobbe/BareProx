/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 *
 * Copyright (C) 2025 Tobias Modig
 *
 * This file is part of BareProx.
 *
 * BareProx is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * BareProx is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with BareProx. If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using BareProx.Models;

namespace BareProx.Services.Proxmox.Authentication
{
    public interface IProxmoxClusterDiscoveryService
    {
        /// <summary>
        /// Full discovery using the Proxmox HTTP API:
        /// - Authenticates against the seed host
        /// - Reads /cluster/status to discover cluster name and nodes
        /// - Optionally probes node APIs
        /// All progress is written to result.Logs and streamed via the optional log callback.
        /// </summary>
        Task<ProxmoxClusterDiscoveryResult> DiscoverAsync(
            string seedHost,
            string username,
            string password,
            Action<string>? log = null,
            CancellationToken ct = default);

        /// <summary>
        /// Lightweight verification using the Proxmox HTTP API:
        /// - Authenticates against the seed host
        /// - Confirms /cluster/status is reachable and structurally valid
        /// All progress is written to result.Logs and streamed via the optional log callback.
        /// </summary>
        Task<ProxmoxClusterDiscoveryResult> VerifyAsync(
            string seedHost,
            string username,
            string password,
            Action<string>? log = null,
            CancellationToken ct = default);
    }
}
