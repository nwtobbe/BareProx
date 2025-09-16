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
        private readonly ApplicationDbContext _context;
        private readonly ILogger<ProxmoxOpsService> _logger;
        private readonly IProxmoxHelpersService _proxmoxHelpers;
        private readonly IProxmoxAuthenticator _auth;

        public ProxmoxOpsService(
            ApplicationDbContext context,
            ILogger<ProxmoxOpsService> logger,
            IProxmoxHelpersService proxmoxHelpers,
            IProxmoxAuthenticator auth)
        {
            _context = context;
            _logger = logger;
            _proxmoxHelpers = proxmoxHelpers;
            _auth = auth;
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
            try
            {
                var client = await _auth.GetAuthenticatedClientAsync(cluster, ct);

                // Buffer for logging and possible retry
                string? requestBody = null;
                string? mediaType = null;
                if (content != null)
                {
                    requestBody = await content.ReadAsStringAsync(ct);
                    mediaType = content.Headers?.ContentType?.MediaType
                                ?? (content is FormUrlEncodedContent ? "application/x-www-form-urlencoded" : "application/json");
                }

                // First attempt
                using (var request = new HttpRequestMessage(method, url) { Content = content })
                {
                    _logger.LogDebug("▶ Proxmox {Method} {Url}\nPayload:\n{Payload}", method, url, requestBody ?? "<no content>");
                    var response = await client.SendAsync(request, ct);

                    var responseBody = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogDebug("◀ Proxmox {Code} {Reason}\nBody:\n{Body}", (int)response.StatusCode, response.ReasonPhrase, responseBody);

                    if (response.StatusCode != HttpStatusCode.Unauthorized)
                    {
                        response.EnsureSuccessStatusCode();
                        return response;
                    }

                    // Retry once on 401
                    response.Dispose();
                }

                _logger.LogInformation("Proxmox auth expired, re-authenticating…");
                var reauth = await _auth.AuthenticateAndStoreTokenCAsync(cluster, ct);
                if (!reauth)
                    throw new ServiceUnavailableException("Authentication failed: missing token or CSRF.");

                await _context.Entry(cluster).ReloadAsync(ct);
                var retryClient = await _auth.GetAuthenticatedClientAsync(cluster, ct);

                using (var retry = new HttpRequestMessage(method, url))
                {
                    if (requestBody != null)
                        retry.Content = new StringContent(requestBody, Encoding.UTF8, mediaType ?? "application/octet-stream");

                    var retryResp = await retryClient.SendAsync(retry, ct);
                    var retryBody = await retryResp.Content.ReadAsStringAsync(ct);
                    _logger.LogDebug("◀ (retry) Proxmox {Code} {Reason}\nBody:\n{Body}",
                        (int)retryResp.StatusCode, retryResp.ReasonPhrase, retryBody);

                    retryResp.EnsureSuccessStatusCode();
                    return retryResp;
                }
            }
            catch (HttpRequestException ex)
            {
                var host = _proxmoxHelpers.GetQueryableHosts(cluster).FirstOrDefault()?.HostAddress ?? "unknown";

                await _context.ProxmoxClusters
                    .Where(c => c.Id == cluster.Id)
                    .ExecuteUpdateAsync(b => b
                        .SetProperty(c => c.LastStatus, _ => $"Unreachable: {ex.Message}")
                        .SetProperty(c => c.LastChecked, _ => DateTime.UtcNow), ct);

                throw new ServiceUnavailableException($"Cannot reach Proxmox host at {host}:8006. {ex.Message}", ex);
            }
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

            while (DateTime.UtcNow - start < timeout)
            {
                try
                {
                    var resp = await SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    var data = doc.RootElement.GetProperty("data");

                    var status = data.TryGetProperty("status", out var s) ? s.GetString() : null;
                    if (string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase))
                    {
                        var exit = data.TryGetProperty("exitstatus", out var e) ? e.GetString() : null;
                        var ok = !string.IsNullOrWhiteSpace(exit) && exit.StartsWith("OK", StringComparison.OrdinalIgnoreCase);
                        if (!ok)
                            logger.LogWarning("Task {Upid} exited with status: {Exit}", upid, exit ?? "(null)");
                        return ok;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to check task status for UPID: {Upid}", upid);
                }

                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }

            logger.LogWarning("Timeout waiting for task {Upid}", upid);
            return false;
        }

        // The other IProxmoxOps methods (GetVmStatusAsync, GetVmConfigRawAsync, BuildUrl, ExtractUpidAsync)
        // stay as you already have them elsewhere in this file.
    }
}
