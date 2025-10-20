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

using BareProx.Data;
using BareProx.Models;
using BareProx.Services.Proxmox.Authentication; // assumes you have an auth service here
using BareProx.Services.Proxmox.Helpers;
using BareProx.Services.Proxmox.Ops;
using BareProx.Services.Restore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace BareProx.Services.Proxmox.Snapshots
{
    /// <summary>
    /// Thin transport + task-plumbing over the Proxmox API.
    /// - Handles auth/CSRF via IProxmoxAuthService
    /// - Polls UPIDs to completion
    /// - Provides a few generic lookups
    /// </summary>
    public sealed class ProxmoxSnapshotsService : IProxmoxSnapshotsService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<ProxmoxSnapshotsService> _logger;
        private readonly IProxmoxHelpersService _proxmoxHelpers;
        private readonly IProxmoxOpsService _proxmoxOps;
        private readonly IHttpClientFactory _httpFactory;
        private readonly IProxmoxAuthenticator _auth;

        // You can register a named HttpClient ("proxmox") or use default
        public ProxmoxSnapshotsService(
            ApplicationDbContext context,
            ILogger<ProxmoxSnapshotsService> logger,
            IProxmoxHelpersService proxmoxHelpers,
            IProxmoxOpsService proxmoxOps,
            IHttpClientFactory httpFactory,
            IProxmoxAuthenticator auth)
        {
            _context = context;
            _logger = logger;
            _proxmoxHelpers = proxmoxHelpers;
            _proxmoxOps = proxmoxOps;
            _httpFactory = httpFactory;
            _auth = auth;
        }

        public async Task<List<ProxmoxSnapshotInfo>> GetSnapshotListAsync(
            ProxmoxCluster cluster,
            string node,
            string hostAddress,
            int vmid,
            CancellationToken ct = default)
        {
            var client = await _auth.GetAuthenticatedClientAsync(cluster, ct);
            var url = $"https://{hostAddress}:8006/api2/json/nodes/{node}/qemu/{vmid}/snapshot";
            var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var dataProp) ||
                dataProp.ValueKind != JsonValueKind.Array)
            {
                return new List<ProxmoxSnapshotInfo>();
            }

            var list = new List<ProxmoxSnapshotInfo>();

            foreach (var snapshot in dataProp.EnumerateArray())
            {
                var name = snapshot.GetProperty("name").GetString() ?? "";

                int snaptime = 0;
                if (snapshot.TryGetProperty("snaptime", out var snaptimeProp))
                {
                    if (snaptimeProp.ValueKind == JsonValueKind.Number)
                        snaptime = snaptimeProp.GetInt32();
                    else if (snaptimeProp.ValueKind == JsonValueKind.String &&
                             int.TryParse(snaptimeProp.GetString(), out var parsedTime))
                        snaptime = parsedTime;
                }

                int vmstate = 0;
                if (snapshot.TryGetProperty("vmstate", out var vmstateProp) &&
                    vmstateProp.ValueKind == JsonValueKind.Number)
                {
                    vmstate = vmstateProp.GetInt32();
                }

                string? desc = null;
                if (snapshot.TryGetProperty("description", out var d) &&
                    (d.ValueKind == JsonValueKind.String))
                {
                    desc = d.GetString();
                }

                list.Add(new ProxmoxSnapshotInfo
                {
                    Name = name,
                    Snaptime = snaptime,
                    Vmstate = vmstate,
                    Description = desc
                });
            }

            return list;
        }

        public async Task<string?> CreateSnapshotAsync(
            ProxmoxCluster cluster,
            string node,
            string hostAddress,
            int vmid,
            string snapshotName,
            string description,
            bool withMemory,
            bool dontTrySuspend,
            CancellationToken ct = default)
        {
            var client = await _auth.GetAuthenticatedClientAsync(cluster, ct);

            var url = $"https://{hostAddress}:8006/api2/json/nodes/{node}/qemu/{vmid}/snapshot";

            var data = new Dictionary<string, string>
            {
                ["snapname"] = snapshotName,
                ["description"] = description,
                ["vmstate"] = withMemory ? "1" : "0"
            };

            var content = new FormUrlEncodedContent(data);
            var response = await client.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var upid = doc.RootElement
                          .GetProperty("data")
                          .GetString();

            return upid;
        }

        public async Task DeleteSnapshotAsync(
            ProxmoxCluster cluster,
            string host,
            string hostaddress,
            int vmId,
            string snapshotName,
            CancellationToken ct = default)
        {
            var url = $"https://{hostaddress}:8006/api2/json/nodes/{host}/qemu/{vmId}/snapshot/{snapshotName}";
            await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Delete, url, null, ct);
        }

        public async Task<bool> RollbackSnapshotAsync(
           ProxmoxCluster cluster,
           string node,
           string hostAddress,
           int vmid,
           string snapshotName,
           bool startAfterRollback,
           ILogger logger,
           CancellationToken ct = default)
        {
            var url = $"https://{hostAddress}:8006/api2/json/nodes/{node}/qemu/{vmid}/snapshot/{Uri.EscapeDataString(snapshotName)}/rollback";
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["start"] = startAfterRollback ? "1" : "0"
            });

            try
            {
                var resp = await _proxmoxOps.SendWithRefreshAsync(cluster, HttpMethod.Post, url, form, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                var upid = doc.RootElement.TryGetProperty("data", out var d) ? d.GetString() : null;
                if (string.IsNullOrWhiteSpace(upid))
                {
                    logger.LogWarning("RollbackSnapshotAsync: no UPID returned for VMID {Vmid} snapshot '{Snap}'.", vmid, snapshotName);
                    return false;
                }

                var ok = await _proxmoxOps.WaitForTaskCompletionAsync(
                    cluster, node, hostAddress, upid!, TimeSpan.FromMinutes(20), logger, ct);

                if (!ok)
                {
                    logger.LogWarning("RollbackSnapshotAsync: task did not complete OK for VMID {Vmid} snapshot '{Snap}'.", vmid, snapshotName);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "RollbackSnapshotAsync failed for VMID {Vmid}, snapshot '{Snap}'.", vmid, snapshotName);
                return false;
            }
        }

    }
}