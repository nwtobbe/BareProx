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

using System.Net.Http.Headers;
using System.Text;
using BareProx.Models;


namespace BareProx.Services.Netapp
{
    public class NetappAuthService : INetappAuthService
    {
        private readonly IHttpClientFactory _factory;
        private readonly IEncryptionService _enc;
        private readonly ILogger<NetappAuthService> _logger;

        public NetappAuthService(
            IHttpClientFactory factory,
            IEncryptionService enc,
            ILogger<NetappAuthService> logger)
        {
            _factory = factory;
            _enc = enc;
            _logger = logger;
        }

        public AuthenticationHeaderValue GetEncryptedAuthHeader(string username, string encryptedPassword)
        {
            var decrypted = _enc.Decrypt(encryptedPassword);
            var b64 = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{decrypted}"));
            return new AuthenticationHeaderValue("Basic", b64);
        }

        public HttpClient CreateAuthenticatedClient(NetappController controller, out string baseUrl)
        {
            var client = _factory.CreateClient("NetappClient");
            client.DefaultRequestHeaders.Authorization =
                GetEncryptedAuthHeader(controller.Username, controller.PasswordHash);

            baseUrl = $"https://{controller.IpAddress}/api/";
            return client;
        }

        public async Task<bool> TryAuthenticateAsync(
            string hostOrIp,
            string username,
            string plainPassword,
            CancellationToken ct = default)
        {
            // Reuse the named client so cert policy/proxies/etc. are consistent.
            var client = _factory.CreateClient("NetappClient");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{plainPassword}"))
            );

            var baseUrl = $"https://{hostOrIp}/api/";
            var url = $"{baseUrl}cluster?fields=version"; // cheap probe

            try
            {
                using var resp = await client.GetAsync(url, ct);
                if (resp.IsSuccessStatusCode)
                    return true;

                // 401/403 mean bad creds; others likely connectivity/cert/etc.
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("NetApp auth probe failed {Status}: {Body}", (int)resp.StatusCode, body);
                return false;
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                // cancelled by caller
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NetApp auth probe threw.");
                return false;
            }
        }
    }
}
