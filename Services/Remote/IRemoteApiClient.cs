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

using System.Net.Http;
using System.Threading.Tasks;

namespace BareProx.Services
{
    public interface IRemoteApiClient
    {
        Task<HttpClient> CreateAuthenticatedClientAsync(
            string username,
            string encryptedPassword,
            string baseUrl,
            string clientName,
            bool isEncrypted = true,
            string? tokenHeaderName = null,
            string? tokenValue = null);

        Task<string> SendAsync(HttpClient client, HttpMethod method, string url, HttpContent? content = null);

        string EncodeBasicAuth(string username, string password);
    }
}
