using BareProx.Data;
using BareProx.Models;
using BareProx.Services.Proxmox.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace BareProx.Services.Proxmox.Authentication
{
    public class ProxmoxAuthenticator : IProxmoxAuthenticator
    {
        private readonly ApplicationDbContext _context;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IEncryptionService _encryptionService;
        private readonly ILogger<ProxmoxAuthenticator> _logger;
        private readonly IProxmoxHelpersService _proxmoxHelpers;

        public ProxmoxAuthenticator(
            ApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IEncryptionService encryptionService,
            ILogger<ProxmoxAuthenticator> logger,
            IProxmoxHelpersService proxmoxHelpers)
        {
            _context = context;
            _httpClientFactory = httpClientFactory;
            _encryptionService = encryptionService;
            _logger = logger;
            _proxmoxHelpers = proxmoxHelpers;
        }

        private static readonly TimeSpan MaxTicketAge = TimeSpan.FromMinutes(90);

        // ─────────────────────────────────────────────────────────────────────
        // Public: keep existing controller call-sites working
        // ─────────────────────────────────────────────────────────────────────

        public async Task<bool> AuthenticateAndStoreTokenCidAsync(int clusterId, CancellationToken ct = default)
        {
            var cluster = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync(c => c.Id == clusterId, ct);

            if (cluster == null) return false;

            // Choose a host (prefer online)
            var host = _proxmoxHelpers.GetQueryableHosts(cluster).FirstOrDefault();
            if (host == null) return false;

            return await AuthenticateAndStoreTicketForHostAsync(cluster, host, ct);
        }

        /// <summary>
        /// When callers have an absolute or relative URL, this resolves the host
        /// and returns a client with that host’s ticket+CSRF attached.
        /// </summary>
        public async Task<HttpClient> GetAuthenticatedClientForUrlAsync(ProxmoxCluster cluster, string url, CancellationToken ct = default)
        {
            var host = ResolveHostForUrl(cluster, url)
                       ?? _proxmoxHelpers.GetQueryableHosts(cluster).First()
                       ?? cluster.Hosts.First();

            return await GetAuthenticatedClientForHostAsync(cluster, host, ct);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Internals: host-scoped ticket+CSRF
        // ─────────────────────────────────────────────────────────────────────

        private async Task<HttpClient> GetAuthenticatedClientForHostAsync(
            ProxmoxCluster cluster,
            ProxmoxHost host,
            CancellationToken ct)
        {
            var needsRefresh =
                string.IsNullOrWhiteSpace(host.TicketEnc) ||
                string.IsNullOrWhiteSpace(host.CsrfEnc) ||
                host.TicketIssuedUtc == null ||                                    // NEW: null => refresh
                (host.TicketIssuedUtc != null &&                                   // NEW: age-based proactive refresh
                 DateTime.UtcNow - host.TicketIssuedUtc.Value > MaxTicketAge);

            if (needsRefresh)
            {
                var ok = await AuthenticateAndStoreTicketForHostAsync(cluster, host, ct);
                if (!ok) throw new Exception($"Auth failed for host {host.HostAddress}");
            }
            else
            {
                // probe; if 401/403 -> refresh once
                var probeClient = MakeBareClient();
                ApplyHostTicketHeaders(probeClient, host);
                var baseUrl = $"https://{host.HostAddress}:8006";
                if (!await ProbeAsync(probeClient, baseUrl, ct))
                {
                    var ok = await AuthenticateAndStoreTicketForHostAsync(cluster, host, ct);
                    if (!ok) throw new Exception($"Auth refresh failed for host {host.HostAddress}");
                }
            }

            var client = MakeBareClient();
            ApplyHostTicketHeaders(client, host);
            return client;
        }


        /// <summary>
        /// Login to a specific host (by IP/hostname) and persist that host's ticket+CSRF.
        /// </summary>
        private async Task<bool> AuthenticateAndStoreTicketForHostAsync(ProxmoxCluster cluster, ProxmoxHost host, CancellationToken ct)
        {
            var http = MakeBareClient();
            var url = $"https://{host.HostAddress}:8006/api2/json/access/ticket";
            var form = new Dictionary<string, string>
            {
                ["username"] = cluster.Username, // include realm (e.g., root@pam)
                ["password"] = _encryptionService.Decrypt(cluster.PasswordHash)
            };

            try
            {
                using var content = new FormUrlEncodedContent(form);
                using var res = await http.PostAsync(url, content, ct);
                if (!res.IsSuccessStatusCode)
                {
                    host.LastStatus = $"Error: ticket {res.StatusCode}";
                    host.LastChecked = DateTime.UtcNow;
                    await _context.SaveChangesAsync(ct);
                    return false;
                }

                var json = await res.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data");

                var ticket = data.GetProperty("ticket").GetString()!;
                var csrf = data.GetProperty("CSRFPreventionToken").GetString()!;

                host.TicketEnc = _encryptionService.Encrypt(ticket);
                host.CsrfEnc = _encryptionService.Encrypt(csrf);
                host.TicketIssuedUtc = DateTime.UtcNow;
                host.LastStatus = "Working (ticket)";
                host.LastChecked = DateTime.UtcNow;

                await _context.SaveChangesAsync(ct);
                return true;
            }
            catch (Exception ex)
            {
                host.LastStatus = $"Error: {ex.Message}";
                host.LastChecked = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);
                _logger.LogError(ex, "Ticket auth failed for host {Host} (cluster {ClusterId})", host.HostAddress, cluster.Id);
                return false;
            }
        }

        private void ApplyHostTicketHeaders(HttpClient client, ProxmoxHost host)
        {
            if (string.IsNullOrWhiteSpace(host.TicketEnc) || string.IsNullOrWhiteSpace(host.CsrfEnc))
                throw new InvalidOperationException("Missing ticket/CSRF for host.");

            var ticket = _encryptionService.Decrypt(host.TicketEnc);
            var csrf = _encryptionService.Decrypt(host.CsrfEnc);

            client.DefaultRequestHeaders.Authorization = null;
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Remove("CSRFPreventionToken");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", $"PVEAuthCookie={ticket}");
            client.DefaultRequestHeaders.TryAddWithoutValidation("CSRFPreventionToken", csrf);
        }

        private static async Task<bool> ProbeAsync(HttpClient client, string baseUrl, CancellationToken ct)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api2/json/version");
                using var resp = await client.SendAsync(req, ct);
                if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return false;
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        private HttpClient MakeBareClient() => _httpClientFactory.CreateClient("ProxmoxClient");

        private static ProxmoxHost? ResolveHostForUrl(ProxmoxCluster cluster, string urlOrPath)
        {
            if (Uri.TryCreate(urlOrPath, UriKind.Absolute, out var abs))
            {
                var hostName = abs.Host; // IP or DNS
                return cluster.Hosts.FirstOrDefault(h =>
                    string.Equals(h.HostAddress, hostName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(h.Hostname, hostName, StringComparison.OrdinalIgnoreCase));
            }
            return null; // relative; let caller pick default
        }


    }
}
