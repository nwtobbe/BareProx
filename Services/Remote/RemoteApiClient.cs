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

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BareProx.Services
{
    public sealed class RemoteApiClient : IRemoteApiClient
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IEncryptionService _encryptionService;
        private readonly ILogger<RemoteApiClient> _logger;

        public RemoteApiClient(IHttpClientFactory httpClientFactory,
                               IEncryptionService encryptionService,
                               ILogger<RemoteApiClient> logger)
        {
            _httpClientFactory = httpClientFactory;
            _encryptionService = encryptionService;
            _logger = logger;
        }

        public Task<HttpClient> CreateAuthenticatedClientAsync(
            string username,
            string encryptedPassword,
            string baseUrl,
            string clientName,
            bool isEncrypted = true,
            string? tokenHeaderName = null,
            string? tokenValue = null)
        {
            var password = isEncrypted ? _encryptionService.Decrypt(encryptedPassword) : encryptedPassword;
            var client = _httpClientFactory.CreateClient(clientName);
            client.BaseAddress = new Uri(baseUrl);

            if (string.IsNullOrEmpty(tokenHeaderName) || string.IsNullOrEmpty(tokenValue))
            {
                var creds = EncodeBasicAuth(username, password);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", creds);
            }
            else
            {
                // If tokenHeaderName is meant to be a scheme (e.g. "Bearer"), this keeps your behavior.
                // If it's a custom header name, consider: client.DefaultRequestHeaders.TryAddWithoutValidation(tokenHeaderName, tokenValue);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(tokenHeaderName, tokenValue);
            }

            return Task.FromResult(client);
        }

        public async Task<string> SendAsync(HttpClient client, HttpMethod method, string url, HttpContent? content = null)
        {
            var request = new HttpRequestMessage(method, url) { Content = content };
            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        public string EncodeBasicAuth(string username, string password)
            => Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
    }
}
