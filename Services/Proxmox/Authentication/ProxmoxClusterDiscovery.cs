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
using BareProx.Models;
using Microsoft.Extensions.Logging;

namespace BareProx.Services.Proxmox.Authentication
{
    /// <summary>
    /// Discovers Proxmox cluster information using the Proxmox HTTP API
    /// instead of SSH + /etc/pve/.members.
    /// </summary>
    public sealed class ProxmoxClusterDiscoveryService : IProxmoxClusterDiscoveryService
    {
        private readonly ILogger<ProxmoxClusterDiscoveryService> _log;

        public ProxmoxClusterDiscoveryService(ILogger<ProxmoxClusterDiscoveryService> log)
        {
            _log = log;
        }

        // ======================================================
        // DiscoverAsync (API-based)
        // ======================================================
        public async Task<ProxmoxClusterDiscoveryResult> DiscoverAsync(
            string seedHost,
            string username,
            string password,
            Action<string>? log = null,
            CancellationToken ct = default)
        {
            var result = new ProxmoxClusterDiscoveryResult();
            var logs = result.Logs;

            if (string.IsNullOrWhiteSpace(seedHost))
            {
                AddLog(logs, log, "ERROR: Seed host is empty.");
                result.Success = false;
                result.Error = "Seed host is required.";
                return result;
            }

            if (string.IsNullOrWhiteSpace(username))
            {
                AddLog(logs, log, "ERROR: Username is empty.");
                result.Success = false;
                result.Error = "Username is required.";
                return result;
            }

            password ??= string.Empty;

            try
            {
                AddLog(logs, log,
                    $"Using Proxmox API on seed host '{seedHost}' with user '{username}'...");

                using var handler = CreateHandlerAllowingSelfSigned();
                using var http = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(15)
                };

                // 1) Authenticate
                var (authOk, ticket, csrfToken, authError) =
                    await AuthenticateAsync(http, seedHost, username, password, logs, log, ct);

                if (!authOk || string.IsNullOrWhiteSpace(ticket))
                {
                    result.Success = false;
                    result.Error = authError ?? "Authentication failed.";
                    AddLog(logs, log, $"ERROR: {result.Error}");
                    return result;
                }

                AddLog(logs, log, "OK: Proxmox API authentication succeeded.");

                // Apply auth cookie for subsequent calls
                http.DefaultRequestHeaders.Remove("CSRFPreventionToken");
                if (!string.IsNullOrWhiteSpace(csrfToken))
                    http.DefaultRequestHeaders.Add("CSRFPreventionToken", csrfToken);

                // Cookie header: PVEAuthCookie=<ticket>
                http.DefaultRequestHeaders.Add("Cookie", $"PVEAuthCookie={ticket}");

                // 2) Query cluster status => same info as /etc/pve/.members
                var statusUrl = $"https://{seedHost}:8006/api2/json/cluster/status";
                AddLog(logs, log, $"GET {statusUrl}");
                using var statusResp = await http.GetAsync(statusUrl, ct);

                if (!statusResp.IsSuccessStatusCode)
                {
                    var body = await SafeReadAsync(statusResp, ct);
                    AddLog(logs, log,
                        $"ERROR: /cluster/status HTTP {(int)statusResp.StatusCode}: {body}");
                    result.Success = false;
                    result.Error = "Failed to query /cluster/status on seed host.";
                    return result;
                }

                var statusJson = await statusResp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(statusJson);

                if (!doc.RootElement.TryGetProperty("data", out var itemsEl) ||
                    itemsEl.ValueKind != JsonValueKind.Array)
                {
                    AddLog(logs, log,
                        "ERROR: Invalid /cluster/status response: missing 'data' array.");
                    result.Success = false;
                    result.Error = "Invalid /cluster/status response.";
                    return result;
                }

                string? clusterName = null;
                var discoveredNodes = new List<DiscoveredProxmoxNode>();

                // 3) Parse cluster + node entries
                foreach (var item in itemsEl.EnumerateArray())
                {
                    if (!item.TryGetProperty("type", out var typeEl) ||
                        typeEl.ValueKind != JsonValueKind.String)
                        continue;

                    var type = typeEl.GetString();

                    if (string.Equals(type, "cluster", StringComparison.OrdinalIgnoreCase))
                    {
                        if (item.TryGetProperty("name", out var nameEl) &&
                            nameEl.ValueKind == JsonValueKind.String)
                        {
                            clusterName = nameEl.GetString();
                        }
                    }
                    else if (string.Equals(type, "node", StringComparison.OrdinalIgnoreCase))
                    {
                        var nodeName = item.GetProperty("name").GetString() ?? "";
                        var ip = item.TryGetProperty("ip", out var ipEl) &&
                                 ipEl.ValueKind == JsonValueKind.String
                            ? ipEl.GetString() ?? ""
                            : "";

                        if (string.IsNullOrWhiteSpace(nodeName))
                        {
                            AddLog(logs, log, "SKIP: Node entry without name.");
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(ip))
                        {
                            AddLog(logs, log,
                                $"SKIP: Node '{nodeName}' missing IP in /cluster/status.");
                            continue;
                        }

                        // Reverse DNS (best-effort)
                        string? reverse = null;
                        try
                        {
                            var he = await Dns.GetHostEntryAsync(ip);
                            reverse = he.HostName;
                            AddLog(logs, log,
                                $"Node '{nodeName}': IP {ip}, reverse DNS '{reverse}'.");
                        }
                        catch
                        {
                            AddLog(logs, log,
                                $"Node '{nodeName}': IP {ip}, no reverse DNS.");
                        }

                        discoveredNodes.Add(new DiscoveredProxmoxNode
                        {
                            NodeName = nodeName,
                            IpAddress = ip,
                            ReverseName = reverse,
                            // We keep the property name for compatibility;
                            // here it means "API reachable", not "SSH ok".
                            SshOk = false
                        });
                    }
                }

                if (!string.IsNullOrWhiteSpace(clusterName))
                {
                    result.ClusterName = clusterName;
                    AddLog(logs, log, $"Detected cluster name: {clusterName}");
                }
                else
                {
                    AddLog(logs, log,
                        "WARN: Could not find cluster name in /cluster/status.");
                }

                if (discoveredNodes.Count == 0)
                {
                    result.Success = false;
                    result.Error = "No nodes discovered from /cluster/status.";
                    AddLog(logs, log, "ERROR: No nodes discovered.");
                    return result;
                }

                // 4) Probe each node via API to set SshOk flag (API reachability)
                await ProbeNodesApiAsync(http, discoveredNodes, logs, log, ct);

                result.Nodes.AddRange(discoveredNodes);
                result.Success = true;
                AddLog(logs, log, "Discovery completed successfully.");
                return result;
            }
            catch (OperationCanceledException)
            {
                AddLog(logs, log, "Discovery canceled.");
                throw;
            }
            catch (Exception ex)
            {
                AddLog(logs, log, $"ERROR: Discovery failed: {ex.Message}");
                _log.LogWarning(ex,
                    "Proxmox cluster discovery (API) failed for seed host {SeedHost}", seedHost);
                result.Success = false;
                result.Error = "Discovery failed. See logs for details.";
                return result;
            }
        }

        // ======================================================
        // VerifyAsync (API-based)
        // ======================================================
        public async Task<ProxmoxClusterDiscoveryResult> VerifyAsync(
     string seedHost,
     string username,
     string password,
     Action<string>? log = null,
     CancellationToken ct = default)
        {
            var result = new ProxmoxClusterDiscoveryResult();
            var logs = result.Logs;

            if (string.IsNullOrWhiteSpace(seedHost))
            {
                AddLog(logs, log, "ERROR: Seed host is empty.");
                result.Success = false;
                result.Error = "Seed host is required.";
                return result;
            }

            if (string.IsNullOrWhiteSpace(username))
            {
                AddLog(logs, log, "ERROR: Username is empty.");
                result.Success = false;
                result.Error = "Username is required.";
                return result;
            }

            password ??= string.Empty;

            try
            {
                using var handler = CreateHandlerAllowingSelfSigned();
                using var http = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(15)
                };

                AddLog(logs, log, $"Verifying Proxmox API on {seedHost} as '{username}'...");

                var (authOk, ticket, csrfToken, authError) =
                    await AuthenticateAsync(http, seedHost, username, password, logs, log, ct);

                if (!authOk || string.IsNullOrWhiteSpace(ticket))
                {
                    var err = authError ?? "Authentication failed.";
                    AddLog(logs, log, $"ERROR: {err}");
                    result.Success = false;
                    result.Error = err;
                    return result;
                }

                AddLog(logs, log, "OK: Authentication succeeded.");

                http.DefaultRequestHeaders.Remove("CSRFPreventionToken");
                if (!string.IsNullOrWhiteSpace(csrfToken))
                    http.DefaultRequestHeaders.Add("CSRFPreventionToken", csrfToken);

                http.DefaultRequestHeaders.Add("Cookie", $"PVEAuthCookie={ticket}");

                // Simple sanity check: /cluster/status reachable
                var statusUrl = $"https://{seedHost}:8006/api2/json/cluster/status";
                AddLog(logs, log, $"GET {statusUrl}");
                using var statusResp = await http.GetAsync(statusUrl, ct);

                if (!statusResp.IsSuccessStatusCode)
                {
                    var body = await SafeReadAsync(statusResp, ct);
                    AddLog(logs, log,
                        $"ERROR: /cluster/status HTTP {(int)statusResp.StatusCode}: {body}");
                    result.Success = false;
                    result.Error = "Failed to query /cluster/status on seed host.";
                    return result;
                }

                var json = await statusResp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("data", out var items) &&
                    items.ValueKind == JsonValueKind.Array)
                {
                    var clusterItem = items.EnumerateArray()
                        .FirstOrDefault(x =>
                            x.TryGetProperty("type", out var t) &&
                            t.ValueKind == JsonValueKind.String &&
                            t.GetString() == "cluster");

                    if (clusterItem.ValueKind == JsonValueKind.Object &&
                        clusterItem.TryGetProperty("name", out var nameEl) &&
                        nameEl.ValueKind == JsonValueKind.String)
                    {
                        result.ClusterName = nameEl.GetString();
                        AddLog(logs, log, $"Cluster name: {result.ClusterName}");
                    }
                    else
                    {
                        AddLog(logs, log,
                            "WARN: /cluster/status has no cluster.name.");
                    }
                }
                else
                {
                    AddLog(logs, log,
                        "WARN: /cluster/status response did not contain a valid 'data' array.");
                }

                result.Success = true;
                AddLog(logs, log, "Verification completed successfully.");
                return result;
            }
            catch (OperationCanceledException)
            {
                AddLog(logs, log, "Verification canceled.");
                throw;
            }
            catch (Exception ex)
            {
                AddLog(logs, log, $"ERROR: Verification failed: {ex.Message}");
                _log.LogWarning(ex,
                    "Proxmox cluster verification (API) failed for seed host {SeedHost}",
                    seedHost);
                result.Success = false;
                result.Error = "Verification failed.";
                return result;
            }
        }


        // ======================================================
        // Helpers
        // ======================================================

        private static HttpClientHandler CreateHandlerAllowingSelfSigned()
        {
            return new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
        }

        private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
        {
            try
            {
                return await resp.Content.ReadAsStringAsync(ct);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static (string user, string realm) SplitUserRealm(string username)
        {
            var u = (username ?? string.Empty).Trim();
            var idx = u.IndexOf('@');
            if (idx > 0 && idx < u.Length - 1)
                return (u[..idx], u[(idx + 1)..]);
            // default to pam if not specified
            return (u, "pam");
        }

        private static void AddLog(List<string> logs, Action<string>? log, string message)
        {
            logs.Add(message);
            log?.Invoke(message);
        }

        /// <summary>
        /// Authenticate against the Proxmox API using username/password.
        /// </summary>
        private static async Task<(bool ok, string? ticket, string? csrf, string? error)> AuthenticateAsync(
            HttpClient http,
            string host,
            string username,
            string password,
            List<string> logs,
            Action<string>? log,
            CancellationToken ct)
        {
            var (user, realm) = SplitUserRealm(username);

            var url = $"https://{host}:8006/api2/json/access/ticket";
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("username", $"{user}@{realm}"),
                new KeyValuePair<string, string>("password", password)
            });

            AddLog(logs, log, $"POST {url} (login)");

            using var resp = await http.PostAsync(url, form, ct);
            var body = await SafeReadAsync(resp, ct);

            if (!resp.IsSuccessStatusCode)
            {
                AddLog(logs, log,
                    $"ERROR: login failed: HTTP {(int)resp.StatusCode}: {body}");
                return (false, null, null, "Authentication against Proxmox API failed.");
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                var data = doc.RootElement.GetProperty("data");
                var ticket = data.GetProperty("ticket").GetString();
                var csrf = data.TryGetProperty("CSRFPreventionToken", out var csrfEl)
                    ? csrfEl.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(ticket))
                {
                    AddLog(logs, log,
                        "ERROR: login response missing ticket.");
                    return (false, null, null, "Authentication response missing ticket.");
                }

                return (true, ticket, csrf, null);
            }
            catch (Exception ex)
            {
                AddLog(logs, log,
                    $"ERROR: Failed to parse login response: {ex.Message}");
                return (false, null, null, "Failed to parse authentication response.");
            }
        }

        /// <summary>
        /// For each discovered node, probes its API status so we can flag it as reachable.
        /// Reuses the same cluster ticket (works across nodes in a Proxmox cluster).
        /// </summary>
        private static async Task ProbeNodesApiAsync(
            HttpClient http,
            List<DiscoveredProxmoxNode> nodes,
            List<string> logs,
            Action<string>? log,
            CancellationToken ct)
        {
            foreach (var node in nodes)
            {
                ct.ThrowIfCancellationRequested();

                var url =
                    $"https://{node.IpAddress}:8006/api2/json/nodes/{Uri.EscapeDataString(node.NodeName)}/status";

                try
                {
                    AddLog(logs, log, $"Probing node API: {url}");
                    using var resp = await http.GetAsync(url, ct);

                    if (resp.IsSuccessStatusCode)
                    {
                        node.SshOk = true; // "API reachable"
                        AddLog(logs, log,
                            $"OK: Node '{node.NodeName}' API reachable at {node.IpAddress}.");
                    }
                    else
                    {
                        AddLog(logs, log,
                            $"WARN: Node '{node.NodeName}' API probe failed: HTTP {(int)resp.StatusCode}.");
                    }
                }
                catch (Exception ex)
                {
                    AddLog(logs, log,
                        $"WARN: Node '{node.NodeName}' API probe error: {ex.Message}");
                }
            }
        }
    }
}
