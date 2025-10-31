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

        public ProxmoxOpsService(
            ILogger<ProxmoxOpsService> logger,
            IProxmoxHelpersService proxmoxHelpers,
            IProxmoxAuthenticator auth)
        {
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
    // Resolve absolute URL and which host we’re talking to
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

    // First attempt — host-based CSRF client
    var client1 = await _auth.GetAuthenticatedClientForUrlAsync(cluster, absoluteUrl, ct);
    using (var req1 = new HttpRequestMessage(method, absoluteUrl)
    {
        Content = requestBody is null ? null : new StringContent(requestBody, Encoding.UTF8, mediaType)
    })
    {
        _logger.LogDebug("▶ Proxmox {Method} {Url}\nPayload:\n{Payload}", method, absoluteUrl, requestBody ?? "<no content>");
        var resp1 = await client1.SendAsync(req1, ct);

        // If success or non-auth error, return (throw on non-success as before)
        if (resp1.IsSuccessStatusCode)
        {
            _logger.LogDebug("◀ Proxmox {Code} {Reason}", (int)resp1.StatusCode, resp1.ReasonPhrase);
            return resp1;
        }

        if (resp1.StatusCode is not HttpStatusCode.Unauthorized and not HttpStatusCode.Forbidden)
        {
            var body = await resp1.Content.ReadAsStringAsync(ct);
            _logger.LogDebug("◀ Proxmox {Code} {Reason}\nBody:\n{Body}", (int)resp1.StatusCode, resp1.ReasonPhrase, body);
            resp1.EnsureSuccessStatusCode(); // preserve your current behavior
            return resp1; // unreachable
        }

        // 401/403 → retry once with a freshly ensured ticket for THIS host
        var body1 = await resp1.Content.ReadAsStringAsync(ct);
        _logger.LogInformation("Auth issue ({Code}) on {Url}. Will re-auth host-scoped and retry once.\nBody:\n{Body}",
            (int)resp1.StatusCode, absoluteUrl, body1);
        resp1.Dispose();
    }

    // Second attempt — the authenticator will probe & refresh per host
    var client2 = await _auth.GetAuthenticatedClientForUrlAsync(cluster, absoluteUrl, ct);
    using (var req2 = new HttpRequestMessage(method, absoluteUrl)
    {
        Content = requestBody is null ? null : new StringContent(requestBody, Encoding.UTF8, mediaType)
    })
    {
        var resp2 = await client2.SendAsync(req2, ct);
        var body2 = await resp2.Content.ReadAsStringAsync(ct);
        _logger.LogDebug("◀ (retry) Proxmox {Code} {Reason}\nBody:\n{Body}", (int)resp2.StatusCode, resp2.ReasonPhrase, body2);

        resp2.EnsureSuccessStatusCode();
        return resp2;
    }
}

        private Task<string> ResolveAbsoluteUrlAsync(ProxmoxCluster cluster, string url, CancellationToken ct)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var abs))
                return Task.FromResult(abs.ToString());

            // Require hosts to be present; prefer “queryable” (online) then fallback
            var host = _proxmoxHelpers.GetQueryableHosts(cluster).FirstOrDefault()
                       ?? cluster.Hosts.FirstOrDefault()
                       ?? throw new ServiceUnavailableException("No Proxmox hosts available for this cluster.");

            var absolute = $"https://{host.HostAddress}:8006/{url.TrimStart('/')}";
            return Task.FromResult(absolute);
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
