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


using BareProx.Data;
using BareProx.Models;
using BareProx.Services.Proxmox.Authentication;
using BareProx.Services.Proxmox.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace BareProx.Services.Proxmox.Ops
{
    /// <summary>
    /// Thin transport + task-plumbing over the Proxmox API.
    /// - Handles auth/CSRF via IProxmoxAuthenticator
    /// - Polls UPIDs to completion
    /// - Provides a few generic lookups
    /// </summary>
    public sealed class ProxmoxOpsService : IProxmoxOpsService
    {
        private readonly ILogger<ProxmoxOpsService> _logger;
        private readonly IProxmoxHelpersService _proxmoxHelpers;
        private readonly IProxmoxAuthenticator _auth;
        private readonly IQueryDbFactory _qdbf;

        public ProxmoxOpsService(
            ILogger<ProxmoxOpsService> logger,
            IProxmoxHelpersService proxmoxHelpers,
            IProxmoxAuthenticator auth,
            IQueryDbFactory qdbf)
        {
            _logger = logger;
            _proxmoxHelpers = proxmoxHelpers;
            _auth = auth;
            _qdbf = qdbf;
        }

        /// <summary>
        /// Sends a request and retries once if a 401 is returned, refreshing auth.
        /// Accepts absolute or relative URLs (your callers currently pass absolute).
        /// </summary>
        public async Task<HttpResponseMessage> SendWithRefreshAsync(
            ProxmoxCluster cluster,
            HttpMethod method,
            string url,
            HttpContent? content = null,
            CancellationToken ct = default)
        {
            var absoluteUrl = await ResolveAbsoluteUrlAsync(cluster, url, ct);

            // Buffer content so we can resend safely
            string? requestBody = null;
            string? mediaType = null;
            if (content != null)
            {
                requestBody = await content.ReadAsStringAsync(ct);
                mediaType = content.Headers?.ContentType?.MediaType
                            ?? (content is FormUrlEncodedContent ? "application/x-www-form-urlencoded" : "application/json");
            }

            // Helper: pick the host that matches the absolute URL (if any), else online first
            async Task<ProxmoxHost?> PickHostForRecoveryAsync()
            {
                if (Uri.TryCreate(absoluteUrl, UriKind.Absolute, out var abs))
                {
                    var h = cluster.Hosts?.FirstOrDefault(x =>
                        string.Equals(x.HostAddress, abs.Host, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.Hostname, abs.Host, StringComparison.OrdinalIgnoreCase));
                    if (h != null) return h;
                }

                var online = await _proxmoxHelpers.GetQueryableHostsAsync(cluster, ct);
                return online.FirstOrDefault()
                       ?? cluster.Hosts?.FirstOrDefault();
            }

            // --- Attempt 1
            var client1 = await _auth.GetAuthenticatedClientForUrlAsync(cluster, absoluteUrl, ct);
            using (var req1 = new HttpRequestMessage(method, absoluteUrl)
            {
                Content = requestBody is null ? null : new StringContent(requestBody, Encoding.UTF8, mediaType)
            })
            {
                _logger.LogDebug("▶ Proxmox {Method} {Url}\nPayload:\n{Payload}", method, absoluteUrl, requestBody ?? "<no content>");
                var resp1 = await client1.SendAsync(req1, ct);

                if (resp1.IsSuccessStatusCode)
                    return resp1;

                if (resp1.StatusCode is not HttpStatusCode.Unauthorized and not HttpStatusCode.Forbidden)
                {
                    // Keep existing behavior: throw on non-auth errors
                    var body = await resp1.Content.ReadAsStringAsync(ct);
                    _logger.LogDebug("◀ Proxmox {Code} {Reason}\nBody:\n{Body}", (int)resp1.StatusCode, resp1.ReasonPhrase, body);
                    resp1.EnsureSuccessStatusCode();
                    return resp1; // unreachable
                }

                // Auth error: dispose and recover once
                var body1 = await resp1.Content.ReadAsStringAsync(ct);
                _logger.LogInformation("Auth issue ({Code}) on {Url}. Will attempt recovery and retry once. Body: {Body}",
                    (int)resp1.StatusCode, absoluteUrl, body1);

                resp1.Dispose();
            }

            // --- Recovery
            var hostForRecovery = await PickHostForRecoveryAsync();
            if (hostForRecovery != null)
            {
                if (cluster.UseApiToken)
                    await _auth.TryRecoverApiTokenAsync(cluster, hostForRecovery, ct);
                else
                    await _auth.TryRecoverTicketAsync(cluster, hostForRecovery, ct);
            }

            // --- Attempt 2
            var client2 = await _auth.GetAuthenticatedClientForUrlAsync(cluster, absoluteUrl, ct);
            using (var req2 = new HttpRequestMessage(method, absoluteUrl)
            {
                Content = requestBody is null ? null : new StringContent(requestBody, Encoding.UTF8, mediaType)
            })
            {
                var resp2 = await client2.SendAsync(req2, ct);

                if (!resp2.IsSuccessStatusCode)
                {
                    var body2 = await resp2.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("Proxmox retry failed: {Code} {Reason} for {Url}. Body: {Body}",
                        (int)resp2.StatusCode, resp2.ReasonPhrase, absoluteUrl, body2);
                    resp2.EnsureSuccessStatusCode();
                }

                return resp2;
            }
        }



        private async Task<string> ResolveAbsoluteUrlAsync(ProxmoxCluster cluster, string url, CancellationToken ct)
        {
            // Already absolute? return as-is
            if (Uri.TryCreate(url, UriKind.Absolute, out var abs))
                return abs.ToString();

            await using var qdb = await _qdbf.CreateAsync(ct);

            // Prefer an online host from inventory
            var invHost = await qdb.InventoryHostStatuses
                .AsNoTracking()
                .Where(h => h.ClusterId == cluster.Id && h.IsOnline)
                .OrderBy(h => h.Hostname)
                .Select(h => new { h.HostAddress, h.Hostname })
                .FirstOrDefaultAsync(ct);

            string? addr = null;

            if (invHost is not null)
            {
                addr = !string.IsNullOrWhiteSpace(invHost.HostAddress)
                    ? invHost.HostAddress
                    : invHost.Hostname;
            }
            else
            {
                // Fallback: first configured host from main DB entity
                var cfgHost = cluster.Hosts?.FirstOrDefault()
                    ?? throw new ServiceUnavailableException("No Proxmox hosts available for this cluster.");

                addr = !string.IsNullOrWhiteSpace(cfgHost.HostAddress)
                    ? cfgHost.HostAddress
                    : cfgHost.Hostname;
            }

            if (string.IsNullOrWhiteSpace(addr))
                throw new ServiceUnavailableException("Selected Proxmox host has no usable address/hostname.");

            var path = url.StartsWith("/") ? url[1..] : url;
            return $"https://{addr}:8006/{path}";
        }



        public async Task<bool> WaitForTaskCompletionAsync(
            ProxmoxCluster cluster,
            string node,
            string hostAddress,
            string upid,
            TimeSpan timeout,
            ILogger logger,
            CancellationToken ct = default)
        {
            var url = $"https://{hostAddress}:8006/api2/json/nodes/{node}/tasks/{Uri.EscapeDataString(upid)}/status";
            var start = DateTime.UtcNow;

            var pollInterval = TimeSpan.FromSeconds(5);
            var perRequestTimeout = TimeSpan.FromSeconds(15);

            while (DateTime.UtcNow - start < timeout)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(perRequestTimeout);

                    var resp = await SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, cts.Token);
                    var json = await resp.Content.ReadAsStringAsync(ct);

                    using var doc = JsonDocument.Parse(json);
                    var data = doc.RootElement.GetProperty("data");

                    var status = data.TryGetProperty("status", out var s) ? s.GetString() : null;
                    if (string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase))
                    {
                        var exit = data.TryGetProperty("exitstatus", out var e) ? e.GetString() : null;
                        var ok = !string.IsNullOrWhiteSpace(exit) &&
                                 exit.StartsWith("OK", StringComparison.OrdinalIgnoreCase);

                        if (!ok)
                            logger.LogWarning("Task {Upid} exited with status: {Exit}", upid, exit ?? "(null)");

                        return ok;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Only this poll timed out; keep polling
                    logger.LogDebug("Task status poll timed out for {Upid} (will retry)", upid);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to check task status for UPID: {Upid}", upid);
                }

                await Task.Delay(pollInterval, ct);
            }

            logger.LogWarning("Timeout waiting for task {Upid}", upid);
            return false;
        }


        // The other IProxmoxOps methods (GetVmStatusAsync, GetVmConfigRawAsync, BuildUrl, ExtractUpidAsync)
        // stay as you already have them elsewhere in this file.
    }
}
