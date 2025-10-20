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
using BareProx.Services.Proxmox.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
        public async Task<bool> AuthenticateAndStoreTokenCidAsync(int clusterId, CancellationToken ct)
        {
            var cluster = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync(c => c.Id == clusterId, ct);

            if (cluster == null || !_proxmoxHelpers.GetQueryableHosts(cluster).Any())
                return false;

            return await AuthenticateAndStoreTokenCAsync(cluster, ct);
        }

        public async Task<bool> AuthenticateAndStoreTokenCAsync(ProxmoxCluster cluster, CancellationToken ct)
        {
            var host = _proxmoxHelpers.GetQueryableHosts(cluster).First();
            var url = $"https://{host.HostAddress}:8006/api2/json/access/ticket";

            var form = new Dictionary<string, string>
            {
                { "username", cluster.Username },
                { "password", _encryptionService.Decrypt(cluster.PasswordHash) }
            };

            var content = new FormUrlEncodedContent(form);
            var httpClient = _httpClientFactory.CreateClient("ProxmoxClient");

            try
            {
                var response = await httpClient.PostAsync(url, content, ct);
                if (!response.IsSuccessStatusCode) return false;

                var body = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                var data = doc.RootElement.GetProperty("data");

                cluster.ApiToken = _encryptionService.Encrypt(data.GetProperty("ticket").GetString());
                cluster.CsrfToken = _encryptionService.Encrypt(data.GetProperty("CSRFPreventionToken").GetString());
                cluster.LastChecked = DateTime.UtcNow;
                cluster.LastStatus = "Working";

                await _context.SaveChangesAsync(ct);
                return true;
            }
            catch (Exception ex)
            {
                cluster.LastStatus = $"Error: {ex.Message}";
                cluster.LastChecked = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);
                return false;
            }
        }

        public async Task<HttpClient> GetAuthenticatedClientAsync(ProxmoxCluster cluster, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(cluster.ApiToken) || string.IsNullOrEmpty(cluster.CsrfToken))
            {
                var ok = await AuthenticateAndStoreTokenCAsync(cluster, ct);
                if (!ok)
                    throw new Exception("Authentication failed: missing token or CSRF.");

                // reload tokens
                await _context.Entry(cluster).ReloadAsync(ct);
            }

            var client = _httpClientFactory.CreateClient("ProxmoxClient");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("PVEAuthCookie", _encryptionService.Decrypt(cluster.ApiToken));

            client.DefaultRequestHeaders.Remove("CSRFPreventionToken");
            client.DefaultRequestHeaders.Add("CSRFPreventionToken", _encryptionService.Decrypt(cluster.CsrfToken));

            return client;
        }

         }
}
