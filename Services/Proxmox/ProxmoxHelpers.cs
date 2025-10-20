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
using BareProx.Services.Proxmox.Authentication;
using System.Text.Json;

namespace BareProx.Services.Proxmox.Helpers
{

    public class ProxmoxHelpers : IProxmoxHelpers
    {
        private readonly ApplicationDbContext _context;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IEncryptionService _encryptionService;
        private readonly ILogger<ProxmoxAuthenticator> _logger;

        public ProxmoxHelpers(
            ApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IEncryptionService encryptionService,
            ILogger<ProxmoxAuthenticator> logger)
        {
            _context = context;
            _httpClientFactory = httpClientFactory;
            _encryptionService = encryptionService;
            _logger = logger;
        }
        /// <summary>
        /// Returns only the hosts that were last marked online. If none have been checked yet,
        /// or all are offline, returns the full list so we can still attempt a first‐time call.
        /// </summary>
        public IEnumerable<ProxmoxHost> GetQueryableHosts(ProxmoxCluster cluster)
        {
            var up = cluster.Hosts.Where(h => h.IsOnline == true).ToList();
            return up.Any() ? up : cluster.Hosts;
        }

        public ProxmoxHost GetHostByNodeName(ProxmoxCluster cluster, string nodeName)
        {
            var host = cluster.Hosts.FirstOrDefault(h => h.Hostname == nodeName);
            if (host == null)
                throw new InvalidOperationException($"Node '{nodeName}' not found in cluster.");
            return host;
        }

        public Dictionary<string, string> FlattenConfig(JsonElement config)
        {
            var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in config.EnumerateObject())
            {
                switch (prop.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        payload[prop.Name] = prop.Value.GetString()!;
                        break;
                    case JsonValueKind.Number:
                        payload[prop.Name] = prop.Value.GetRawText();
                        break;
                }
            }
            return payload;
        }
        public static string ToPosix(string p) => (p ?? "").Replace('\\', '/');
        public static string GetDirPosix(string p)
        {
            p = ToPosix(p);
            var i = p.LastIndexOf('/');
            if (i < 0) return "/";
            return i == 0 ? "/" : p[..i];
        }

        // Small helper to quote bash paths
        public static string EscapeBash(string path) => "'" + path.Replace("'", "'\"'\"'") + "'";

    }
}