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
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BareProx.Data;
using BareProx.Models;
using BareProx.Services.Proxmox.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BareProx.Services.Proxmox.Authentication
{
    /// <summary>
    /// Prefers Proxmox API Tokens (Authorization header, no CSRF). If UseApiToken is enabled
    /// but the secret is missing/invalid/expiring, it can create/rotate the token via ticket login.
    /// Falls back to ticket+CSRF when token mode is off.
    /// </summary>
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
        // Public API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// If token mode is enabled and secret missing/expiring, create or rotate token.
        /// Else in ticket mode, obtain a host-scoped ticket.
        /// </summary>
        public async Task<bool> AuthenticateAndStoreTokenCidAsync(int clusterId, CancellationToken ct = default)
        {
            var cluster = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync(c => c.Id == clusterId, ct);

            if (cluster is null) return false;

            if (cluster.UseApiToken)
            {
                var onlineHosts = await _proxmoxHelpers.GetQueryableHostsAsync(cluster, ct);
                var host = onlineHosts.FirstOrDefault() ?? cluster.Hosts?.FirstOrDefault();

                if (host is null) return false;

                if (!HasUsableToken(cluster) || IsTokenExpiringSoon(cluster))
                {
                    var rotated = await EnsureApiTokenAsync(cluster, host, ct, forceRecreate: IsTokenExpiringSoon(cluster));
                    if (!rotated) return false;
                }
                return true;
            }

            // Ticket mode
            var hosts = await _proxmoxHelpers.GetQueryableHostsAsync(cluster, ct);
            var ticketHost = hosts.FirstOrDefault() ?? cluster.Hosts?.FirstOrDefault();
            if (ticketHost is null) return false;

            return await AuthenticateAndStoreTicketForHostAsync(cluster, ticketHost, ct);
        }

        /// <summary>
        /// Returns an HttpClient with auth attached for the given URL (absolute or relative).
        /// </summary>
        public async Task<HttpClient> GetAuthenticatedClientForUrlAsync(ProxmoxCluster cluster, string url, CancellationToken ct = default)
        {
            // TOKEN MODE
            if (cluster.UseApiToken)
            {
                // Resolve a good host for token work (prefer host that matches the URL; else first online; else any)
                async Task<ProxmoxHost?> PickHostForAsync(string? targetUrl, CancellationToken ct2)
                {
                    var resolved = ResolveHostForUrl(cluster, targetUrl);
                    if (resolved is not null) return resolved;

                    var online = await _proxmoxHelpers.GetQueryableHostsAsync(cluster, ct2);
                    var firstOnline = online.FirstOrDefault();
                    if (firstOnline is not null) return firstOnline;

                    return cluster.Hosts?.FirstOrDefault();
                }

                if (!HasUsableToken(cluster) || IsTokenExpiringSoon(cluster))
                {
                    var hostForToken = await PickHostForAsync(url, ct)
                                      ?? cluster.Hosts?.FirstOrDefault();
                    if (hostForToken is null)
                        throw new InvalidOperationException("No Proxmox hosts available to create/rotate token.");

                    var forceRotate = IsTokenExpiringSoon(cluster);
                    var okCreate = await EnsureApiTokenAsync(cluster, hostForToken, ct, forceRecreate: forceRotate);
                    if (!okCreate)
                        throw new InvalidOperationException("Unable to create/rotate Proxmox API token (check privileges).");
                }

                var tokenClient = MakeBareClient();
                ApplyApiToken(tokenClient, cluster);

                // Optional probe on a sensible host
                var probeHost = await PickHostForAsync(url, ct) ?? cluster.Hosts?.FirstOrDefault();
                if (probeHost is not null)
                {
                    var baseUrl = $"https://{probeHost.HostAddress}:8006";
                    if (!await ProbeAsync(tokenClient, baseUrl, ct))
                        _logger.LogWarning("Proxmox token probe failed against {BaseUrl}. Check token id/secret/privileges.", baseUrl);
                }

                return tokenClient;
            }

            // TICKET MODE
            var host =
                ResolveHostForUrl(cluster, url)
                ?? (await _proxmoxHelpers.GetQueryableHostsAsync(cluster, ct)).FirstOrDefault()
                ?? cluster.Hosts?.FirstOrDefault();

            if (host is null)
                throw new InvalidOperationException("No Proxmox hosts available for ticket mode.");

            return await GetAuthenticatedClientForHostAsync(cluster, host, ct);
        }

        /// <summary>
        /// Attempt to recover (recreate) a token, e.g., after 401/403.
        /// </summary>
        public async Task<bool> TryRecoverApiTokenAsync(ProxmoxCluster cluster, ProxmoxHost host, CancellationToken ct = default)
        {
            if (!cluster.UseApiToken) return false;

            var ok = await EnsureApiTokenAsync(cluster, host, ct, forceRecreate: true);
            if (!ok)
                _logger.LogWarning("Token recovery failed for {UserTokenId} on host {Host}.",
                    cluster.ApiTokenId ?? "(null)", host.HostAddress);
            return ok;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Internals
        // ─────────────────────────────────────────────────────────────────────

        private async Task<HttpClient> GetAuthenticatedClientForHostAsync(
            ProxmoxCluster cluster,
            ProxmoxHost host,
            CancellationToken ct)
        {
            // TOKEN MODE
            if (cluster.UseApiToken)
            {
                if (!HasUsableToken(cluster) || IsTokenExpiringSoon(cluster))
                {
                    var okCreate = await EnsureApiTokenAsync(cluster, host, ct, forceRecreate: IsTokenExpiringSoon(cluster));
                    if (!okCreate)
                        throw new InvalidOperationException("Unable to create/rotate Proxmox API token (check privileges).");
                }

                var tokenClient = MakeBareClient();
                ApplyApiToken(tokenClient, cluster);

                // Optional probe on that host
                var baseUrl = $"https://{host.HostAddress}:8006";
                if (!await ProbeAsync(tokenClient, baseUrl, ct))
                    _logger.LogWarning("Proxmox token probe failed against {BaseUrl}. Check token id/secret/privileges.", baseUrl);

                return tokenClient;
            }

            // TICKET MODE
            var needsRefresh =
                string.IsNullOrWhiteSpace(host.TicketEnc) ||
                string.IsNullOrWhiteSpace(host.CsrfEnc) ||
                host.TicketIssuedUtc == null ||
                (DateTime.UtcNow - host.TicketIssuedUtc.Value > MaxTicketAge);

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

            var authedClient = MakeBareClient();
            ApplyHostTicketHeaders(authedClient, host);
            return authedClient;
        }

        /// <summary>
        /// Ensure an API token exists (and optionally rotate). Creates with an expiry:
        /// 1) obtain ticket (CSRF required),
        /// 2) POST /access/users/{user}/token/{tokenid} with expire,
        /// 3) store secret + expiry in DB.
        /// Robust handling of "Token already exists." which Proxmox may return as HTTP 400.
        /// </summary>
        private async Task<bool> EnsureApiTokenAsync(
            ProxmoxCluster cluster,
            ProxmoxHost host,
            CancellationToken ct,
            bool forceRecreate = false)
        {
            // Normalize a stable, unique default token id if none was set:
            // default: user@realm!bareprox-{cluster.Id}-{<BareProx host>}
            var (userId, tokenId) = ParseUserAndTokenId(cluster);

            // Ticket required for token management
            var ticketOk = await AuthenticateAndStoreTicketForHostAsync(cluster, host, ct);
            if (!ticketOk)
            {
                _logger.LogWarning("Failed to obtain ticket for host {Host} to create/rotate token.", host.HostAddress);
                return false;
            }

            var ticketClient = MakeBareClient();
            ApplyHostTicketHeaders(ticketClient, host);

            // Lifetime: default 180 days if not configured
            var days = cluster.ApiTokenLifetimeDays.GetValueOrDefault(180);
            if (days <= 0) days = 180;

            // Clamp renew-before so it can never exceed total lifetime (prevents instant-rotate loops)
            var lifetimeMinutes = checked(days * 24 * 60);
            var renewBefore = cluster.ApiTokenRenewBeforeMinutes <= 0
                ? 1440
                : Math.Min(cluster.ApiTokenRenewBeforeMinutes, lifetimeMinutes - 1);
            if (renewBefore != cluster.ApiTokenRenewBeforeMinutes)
            {
                cluster.ApiTokenRenewBeforeMinutes = renewBefore;
                await _context.SaveChangesAsync(ct);
            }

            var desiredExpiryUtc = DateTime.UtcNow.AddDays(days);
            var expireUnix = ToUnixSeconds(desiredExpiryUtc);

            var tokenUrl =
                $"https://{host.HostAddress}:8006/api2/json/access/users/{Uri.EscapeDataString(userId)}/token/{Uri.EscapeDataString(tokenId)}";

            FormUrlEncodedContent BuildCreateContent() => new(new[]
            {
                new KeyValuePair<string,string>("privsep","0"),
                new KeyValuePair<string,string>("expire", expireUnix.ToString()),
                new KeyValuePair<string,string>("comment","BareProx managed token")
            });

            // If rotating or we don't have a secret yet, try a best-effort DELETE first (idempotent).
            if (forceRecreate || string.IsNullOrWhiteSpace(cluster.ApiTokenSecretEnc))
            {
                await TryDeleteTokenAsync(ticketClient, tokenUrl, userId, tokenId, ct);
            }

            // Try to create
            using (var res = await ticketClient.PostAsync(tokenUrl, BuildCreateContent(), ct))
            {
                if (res.IsSuccessStatusCode)
                    return await PersistTokenSecretAndExpiryAsync(cluster, res, desiredExpiryUtc, userId, tokenId, ct);

                var body = await res.Content.ReadAsStringAsync(ct);

                // Proxmox sometimes replies 400 "Token already exists." → treat like conflict
                if (res.StatusCode == HttpStatusCode.Conflict || IsTokenAlreadyExistsError(body))
                {
                    _logger.LogInformation("Token {User}!{Token} already exists. Deleting and recreating.", userId, tokenId);

                    await TryDeleteTokenAsync(ticketClient, tokenUrl, userId, tokenId, ct);

                    using var reRes = await ticketClient.PostAsync(tokenUrl, BuildCreateContent(), ct);
                    if (!reRes.IsSuccessStatusCode)
                    {
                        var bodyRe = await reRes.Content.ReadAsStringAsync(ct);
                        _logger.LogWarning("Failed to recreate token {User}!{Token}: {Code} {Body}",
                            userId, tokenId, (int)reRes.StatusCode, bodyRe);
                        return false;
                    }

                    return await PersistTokenSecretAndExpiryAsync(cluster, reRes, desiredExpiryUtc, userId, tokenId, ct);
                }

                _logger.LogWarning("Failed to create token {User}!{Token}: {Code} {Body}",
                    userId, tokenId, (int)res.StatusCode, body);
                return false;
            }
        }

        private static bool IsTokenAlreadyExistsError(string body)
        {
            // Example body:
            // {"errors":{"tokenid":"Token already exists."},"data":null,"message":"Parameter verification failed.\n"}
            if (string.IsNullOrWhiteSpace(body)) return false;
            return body.Contains("Token already exists", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task TryDeleteTokenAsync(
            HttpClient client,
            string tokenUrl,
            string userId,
            string tokenId,
            CancellationToken ct)
        {
            try
            {
                using var del = await client.DeleteAsync(tokenUrl, ct);
                // Accept 200/204/400/404 — deletion is best-effort (token may not exist or schema differs)
                if (!del.IsSuccessStatusCode &&
                    del.StatusCode != HttpStatusCode.NotFound &&
                    del.StatusCode != HttpStatusCode.BadRequest)
                {
                    var b = await del.Content.ReadAsStringAsync(ct);
                    // Log but don't fail flow
                    Console.WriteLine($"[Proxmox] Delete token {userId}!{tokenId} returned {(int)del.StatusCode}: {b}");
                }
            }
            catch
            {
                // ignore; POST will follow
            }
        }

        private async Task<bool> PersistTokenSecretAndExpiryAsync(
            ProxmoxCluster cluster,
            HttpResponseMessage res,
            DateTime desiredExpiryUtc,
            string userId,
            string tokenId,
            CancellationToken ct)
        {
            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");

            // Secret is returned only on creation (property "value")
            var secret = data.TryGetProperty("value", out var v) ? v.GetString() : null;
            if (string.IsNullOrWhiteSpace(secret))
            {
                _logger.LogWarning("Token created but secret (value) was not returned.");
                return false;
            }

            // Persist the exact computed id "user@realm!tokenid"
            var normalizedId = $"{userId}!{tokenId}";
            if (!string.Equals(cluster.ApiTokenId, normalizedId, StringComparison.Ordinal))
                cluster.ApiTokenId = normalizedId;

            cluster.ApiTokenSecretEnc = _encryptionService.Encrypt(secret);
            cluster.ApiTokenExpiresUtc = desiredExpiryUtc;

            // Ensure token mode is set
            cluster.UseApiToken = true;

            await _context.SaveChangesAsync(ct);
            _logger.LogInformation("Stored Proxmox API token secret for {User}!{Token} (expires {Expiry:u}).",
                userId, tokenId, desiredExpiryUtc);
            return true;
        }

        /// <summary>
        /// Build (userId, tokenId). If ApiTokenId already includes "user@realm!tokenid",
        /// it is normalized and reused; otherwise we produce "bareprox-{clusterId}-{localHost}".
        /// </summary>
        private static (string userId, string tokenId) ParseUserAndTokenId(ProxmoxCluster cluster)
        {
            var user = (cluster.Username ?? throw new InvalidOperationException("Cluster.Username is required.")).Trim();

            var configured = cluster.ApiTokenId?.Trim();
            if (!string.IsNullOrWhiteSpace(configured) && configured.Contains('!'))
            {
                // Already full "user@realm!tokenid" — normalize empties only
                var idx = configured.IndexOf('!');
                var userPart = configured[..idx].Trim();
                var tokenPart = configured[(idx + 1)..].Trim();
                if (string.IsNullOrWhiteSpace(userPart)) userPart = user;
                if (string.IsNullOrWhiteSpace(tokenPart)) tokenPart = $"bareprox-{cluster.Id}-{GetLocalInstanceTag()}";
                tokenPart = ClampTokenId(tokenPart);
                return (userPart, tokenPart);
            }

            // Default token id: bareprox-{clusterId}-{localHost}
            var localTag = GetLocalInstanceTag();
            var baseId = string.IsNullOrWhiteSpace(configured)
                ? $"bareprox-{cluster.Id}-{localTag}"
                : $"{configured}-{localTag}";
            baseId = ClampTokenId(baseId);

            return (user, baseId);
        }

        private static string GetLocalInstanceTag()
        {
            var name = Environment.MachineName; // Or: System.Net.Dns.GetHostName()
            return SanitizeTokenIdPart(name);
        }

        private static string ClampTokenId(string s)
        {
            // Proxmox tolerates reasonably long IDs; keep under ~96 chars to be safe in UI/API.
            return s.Length > 96 ? s[..96] : s;
        }

        private static string SanitizeTokenIdPart(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "host";
            // Proxmox token id safe charset: letters, digits, '-', '_', '.'
            Span<char> buf = stackalloc char[s.Length];
            var j = 0;
            foreach (var ch in s)
            {
                if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.')
                    buf[j++] = ch;
                else
                    buf[j++] = '-';
            }
            // Trim leading/trailing dots or dashes to avoid awkward tokens
            var cleaned = new string(buf[..j]).Trim('.', '-');
            return string.IsNullOrWhiteSpace(cleaned) ? "host" : cleaned;
        }

        private static bool HasUsableToken(ProxmoxCluster c)
            => c.UseApiToken
               && !string.IsNullOrWhiteSpace(c.ApiTokenId)
               && !string.IsNullOrWhiteSpace(c.ApiTokenSecretEnc);

        private static long ToUnixSeconds(DateTime utc)
            => (long)(utc - DateTime.UnixEpoch).TotalSeconds;

        private static bool IsTokenExpiringSoon(ProxmoxCluster c)
        {
            if (!c.UseApiToken || c.ApiTokenExpiresUtc is null) return false;
            var marginMinutes = Math.Max(1, c.ApiTokenRenewBeforeMinutes);
            return DateTime.UtcNow >= c.ApiTokenExpiresUtc.Value.AddMinutes(-marginMinutes);
        }

        /// <summary>
        /// Attaches Authorization header for API token. No CSRF required.
        /// </summary>
        private void ApplyApiToken(HttpClient client, ProxmoxCluster cluster)
        {
            client.DefaultRequestHeaders.Authorization = null;
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Remove("CSRFPreventionToken");

            var tokenId = cluster.ApiTokenId!;
            var tokenSecret = _encryptionService.Decrypt(cluster.ApiTokenSecretEnc!);

            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "Authorization",
                $"PVEAPIToken={tokenId}={tokenSecret}"
            );
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
                host.LastChecked = DateTime.UtcNow;

                await _context.SaveChangesAsync(ct);
                return true;
            }
            catch (Exception ex)
            {
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
            return null; // relative; caller picks default
        }
    }
}
