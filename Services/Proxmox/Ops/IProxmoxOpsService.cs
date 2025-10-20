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
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace BareProx.Services.Proxmox.Ops
{
    /// <summary>
    /// Thin transport + task-plumbing facade over the Proxmox API.
    /// No snapshot/restore semantics here—pure HTTP + UPID wait + a few generic lookups.
    /// </summary>
    public interface IProxmoxOpsService
    {
        /// <summary>
        /// Sends an HTTP request to Proxmox, automatically handling auth/csrf refresh.
        /// Accepts absolute (https://host:8006/...) or relative (/api2/json/...) URLs.
        /// </summary>
        Task<HttpResponseMessage> SendWithRefreshAsync(ProxmoxCluster cluster, HttpMethod method, string url, HttpContent content = null, CancellationToken ct = default);

        /// <summary>
        /// Waits for a Proxmox task (UPID) to complete successfully.
        /// Returns true on success, false on error/timeout.
        /// </summary>
        Task<bool> WaitForTaskCompletionAsync(
            ProxmoxCluster cluster,
            string node,
            string hostAddress,
            string upid,
            TimeSpan timeout,
            ILogger logger,
            CancellationToken ct = default);

     
    }
}
