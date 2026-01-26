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
            if (cluster is null) throw new ArgumentNullException(nameof(cluster));
            if (string.IsNullOrWhiteSpace(node)) throw new ArgumentException("node is required", nameof(node));
            if (string.IsNullOrWhiteSpace(hostAddress)) throw new ArgumentException("hostAddress is required", nameof(hostAddress));
            if (string.IsNullOrWhiteSpace(upid)) throw new ArgumentException("upid is required", nameof(upid));

            // Build URL safely (escape node too)
            var escapedNode = Uri.EscapeDataString(node);
            var escapedUpid = Uri.EscapeDataString(upid);

            var url = $"https://{hostAddress}:8006/api2/json/nodes/{escapedNode}/tasks/{escapedUpid}/status";

            var start = DateTime.UtcNow;
            var deadline = start + timeout;

            var pollInterval = TimeSpan.FromSeconds(5);
            var perRequestTimeout = TimeSpan.FromSeconds(60); // 45s is OK, 60s is safer under load

            int consecutivePollTimeouts = 0;
            int consecutiveErrors = 0;
            const int MaxConsecutivePollTimeouts = 6; // ~6 * 60s = 6 minutes of dead air
            const int MaxConsecutiveErrors = 10;

            // Optional: log once at start (helps correlate)
            logger.LogDebug("Waiting for Proxmox task completion. upid={Upid} node={Node} host={Host} timeout={Timeout}",
                upid, node, hostAddress, timeout);

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                // Keep remaining time sane: don't request longer than we’re willing to wait overall
                var remaining = deadline - DateTime.UtcNow;
                var thisRequestTimeout = remaining < perRequestTimeout ? remaining : perRequestTimeout;
                if (thisRequestTimeout <= TimeSpan.Zero)
                    break;

                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(thisRequestTimeout);

                    using var resp = await SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, cts.Token);

                    // If proxy returns HTML or empty, ReadAsString can throw; keep it inside the try.
                    var json = await resp.Content.ReadAsStringAsync(cts.Token);

                    // Reset transient counters on a successful HTTP round-trip
                    consecutivePollTimeouts = 0;
                    consecutiveErrors = 0;

                    using var doc = JsonDocument.Parse(json);

                    if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
                    {
                        logger.LogDebug("Task status response had no data for {Upid} (will retry). BodyLen={Len}",
                            upid, json?.Length ?? 0);
                    }
                    else
                    {
                        var status = data.TryGetProperty("status", out var s) ? s.GetString() : null;

                        // Proxmox usually uses: running | stopped
                        if (string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase))
                        {
                            var exit = data.TryGetProperty("exitstatus", out var e) ? e.GetString() : null;

                            // Proxmox commonly returns "OK" or "OK: ..." for success
                            var ok = !string.IsNullOrWhiteSpace(exit) &&
                                     exit.StartsWith("OK", StringComparison.OrdinalIgnoreCase);

                            if (!ok)
                            {
                                // Also sometimes there’s an "errmsg"
                                var errmsg = data.TryGetProperty("errmsg", out var em) ? em.GetString() : null;
                                logger.LogWarning("Task {Upid} completed but not OK. exit={Exit} errmsg={Err}",
                                    upid, exit ?? "(null)", errmsg ?? "(null)");
                            }
                            else
                            {
                                logger.LogDebug("Task {Upid} completed OK.", upid);
                            }

                            return ok;
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Real cancellation: bubble up so the job can stop immediately
                    logger.LogInformation("Stopped waiting for task {Upid} due to cancellation.", upid);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // Per-request timeout
                    consecutivePollTimeouts++;

                    logger.LogDebug("Task status poll timed out for {Upid} ({Count}/{Max}). url={Url}",
                        upid, consecutivePollTimeouts, MaxConsecutivePollTimeouts, url);

                    if (consecutivePollTimeouts >= MaxConsecutivePollTimeouts)
                    {
                        logger.LogWarning("Too many consecutive poll timeouts waiting for {Upid}. Giving up early. url={Url}",
                            upid, url);
                        return false;
                    }
                }
                catch (HttpRequestException ex)
                {
                    consecutiveErrors++;

                    logger.LogWarning(ex, "HTTP error polling task status for {Upid} ({Count}/{Max}). url={Url}",
                        upid, consecutiveErrors, MaxConsecutiveErrors, url);

                    if (consecutiveErrors >= MaxConsecutiveErrors)
                    {
                        logger.LogWarning("Too many consecutive errors waiting for {Upid}. Giving up early.", upid);
                        return false;
                    }
                }
                catch (JsonException ex)
                {
                    consecutiveErrors++;

                    logger.LogWarning(ex, "JSON parse error polling task status for {Upid} ({Count}/{Max}). url={Url}",
                        upid, consecutiveErrors, MaxConsecutiveErrors, url);

                    if (consecutiveErrors >= MaxConsecutiveErrors)
                        return false;
                }
                catch (Exception ex)
                {
                    consecutiveErrors++;

                    logger.LogWarning(ex, "Failed to check task status for UPID: {Upid} ({Count}/{Max}). url={Url}",
                        upid, consecutiveErrors, MaxConsecutiveErrors, url);

                    if (consecutiveErrors >= MaxConsecutiveErrors)
                        return false;
                }

                // Delay between polls (don’t delay past deadline)
                var delay = pollInterval;
                var remainingDelayBudget = deadline - DateTime.UtcNow;
                if (remainingDelayBudget <= TimeSpan.Zero) break;
                if (delay > remainingDelayBudget) delay = remainingDelayBudget;

                await Task.Delay(delay, ct);
            }

            logger.LogWarning("Timeout waiting for task {Upid}. node={Node} host={Host} waited={Waited}",
                upid, node, hostAddress, DateTime.UtcNow - start);

            return false;
        }



        // The other IProxmoxOps methods (GetVmStatusAsync, GetVmConfigRawAsync, BuildUrl, ExtractUpidAsync)
        // stay as you already have them elsewhere in this file.
    }
}
