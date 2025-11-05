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
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BareProx.Models;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace BareProx.Services.Proxmox.Authentication
{
    public sealed class ProxmoxClusterDiscoveryService : IProxmoxClusterDiscoveryService
    {
        private readonly ILogger<ProxmoxClusterDiscoveryService> _log;

        public ProxmoxClusterDiscoveryService(ILogger<ProxmoxClusterDiscoveryService> log)
        {
            _log = log;
        }

        // ======================================================
        // DiscoverAsync
        // ======================================================
        public async Task<ProxmoxClusterDiscoveryResult> DiscoverAsync(
            string seedHost,
            string username,
            string password,
            CancellationToken ct = default)
        {
            var result = new ProxmoxClusterDiscoveryResult();
            var logs = result.Logs;

            if (string.IsNullOrWhiteSpace(seedHost))
            {
                result.Success = false;
                result.Error = "Seed host is required.";
                logs.Add("ERROR: Seed host is empty.");
                return result;
            }

            var sshUser = NormalizeSshUser(username);
            var sshPass = password ?? string.Empty;

            try
            {
                logs.Add($"Connecting to seed host {seedHost} via SSH as '{sshUser}'...");
                var membersJson = await ReadMembersFileAsync(seedHost, sshUser, sshPass, logs, ct);

                if (string.IsNullOrWhiteSpace(membersJson))
                {
                    result.Success = false;
                    result.Error = "/etc/pve/.members was empty.";
                    logs.Add("ERROR: /etc/pve/.members was empty.");
                    return result;
                }

                using var doc = JsonDocument.Parse(membersJson);
                var root = doc.RootElement;

                // Cluster name
                if (root.TryGetProperty("cluster", out var clusterEl) &&
                    clusterEl.ValueKind == JsonValueKind.Object &&
                    clusterEl.TryGetProperty("name", out var nameEl) &&
                    nameEl.ValueKind == JsonValueKind.String)
                {
                    result.ClusterName = nameEl.GetString();
                    logs.Add($"Detected cluster name: {result.ClusterName}");
                }
                else
                {
                    logs.Add("WARN: Could not find 'cluster.name' in /etc/pve/.members.");
                }

                // Node list
                if (!root.TryGetProperty("nodelist", out var nodelistEl) ||
                    nodelistEl.ValueKind != JsonValueKind.Object)
                {
                    result.Success = false;
                    result.Error = "Invalid /etc/pve/.members: missing 'nodelist'.";
                    logs.Add("ERROR: /etc/pve/.members missing 'nodelist' object.");
                    return result;
                }

                foreach (var nodeProp in nodelistEl.EnumerateObject())
                {
                    ct.ThrowIfCancellationRequested();

                    var nodeName = nodeProp.Name;
                    var nodeObj = nodeProp.Value;

                    if (!nodeObj.TryGetProperty("ip", out var ipEl) ||
                        ipEl.ValueKind != JsonValueKind.String)
                    {
                        logs.Add($"SKIP: Node '{nodeName}' missing 'ip'.");
                        continue;
                    }

                    var ip = ipEl.GetString()!.Trim();
                    if (string.IsNullOrWhiteSpace(ip))
                    {
                        logs.Add($"SKIP: Node '{nodeName}' has empty IP.");
                        continue;
                    }

                    // Reverse DNS
                    string? reverse = null;
                    try
                    {
                        var he = await Dns.GetHostEntryAsync(ip);
                        reverse = he.HostName;
                        logs.Add($"Node '{nodeName}': IP {ip}, reverse DNS '{reverse}'.");
                    }
                    catch
                    {
                        logs.Add($"Node '{nodeName}': IP {ip}, no reverse DNS.");
                    }

                    // SSH probe (short timeout)
                    var sshOk = await TrySshAsync(ip, sshUser, sshPass, 5000, ct);
                    logs.Add($"Node '{nodeName}': SSH {(sshOk ? "OK" : "FAILED")} on {ip}.");

                    result.Nodes.Add(new DiscoveredProxmoxNode
                    {
                        NodeName = nodeName,
                        IpAddress = ip,
                        ReverseName = reverse,
                        SshOk = sshOk
                    });
                }

                if (result.Nodes.Count == 0)
                {
                    result.Success = false;
                    result.Error = "No usable nodes discovered from /etc/pve/.members.";
                    logs.Add("ERROR: No nodes discovered.");
                    return result;
                }

                result.Success = true;
                return result;
            }
            catch (OperationCanceledException)
            {
                logs.Add("Discovery canceled.");
                // let cancellation propagate so caller can handle it
                throw;
            }
            catch (Exception ex)
            {
                logs.Add($"ERROR: Discovery failed: {ex.Message}");
                _log.LogWarning(ex, "Proxmox cluster discovery failed for seed host {SeedHost}", seedHost);
                result.Success = false;
                result.Error = "Discovery failed. See logs for details.";
                return result;
            }
        }

        // ======================================================
        // VerifyAsync
        // ======================================================
        public async Task<ProxmoxClusterDiscoveryResult> VerifyAsync(
            string seedHost,
            string username,
            string password,
            CancellationToken ct = default)
        {
            var result = new ProxmoxClusterDiscoveryResult();
            var logs = result.Logs;

            if (string.IsNullOrWhiteSpace(seedHost))
            {
                result.Success = false;
                result.Error = "Seed host is required.";
                logs.Add("ERROR: Seed host is empty.");
                return result;
            }

            var sshUser = NormalizeSshUser(username);
            var sshPass = password ?? string.Empty;

            try
            {
                logs.Add($"Verifying SSH connectivity to {seedHost} as '{sshUser}'...");
                var sshOk = await TrySshAsync(seedHost, sshUser, sshPass, 8000, ct);
                if (!sshOk)
                {
                    logs.Add("ERROR: SSH connection failed.");
                    result.Success = false;
                    result.Error = "SSH connection to seed host failed.";
                    return result;
                }

                logs.Add("SSH OK. Reading /etc/pve/.members...");
                var membersJson = await ReadMembersFileAsync(seedHost, sshUser, sshPass, logs, ct);
                if (string.IsNullOrWhiteSpace(membersJson))
                {
                    logs.Add("ERROR: /etc/pve/.members was empty.");
                    result.Success = false;
                    result.Error = "/etc/pve/.members is empty.";
                    return result;
                }

                using var doc = JsonDocument.Parse(membersJson);
                var root = doc.RootElement;

                if (!root.TryGetProperty("nodelist", out var nodelistEl) ||
                    nodelistEl.ValueKind != JsonValueKind.Object)
                {
                    logs.Add("ERROR: /etc/pve/.members missing 'nodelist'.");
                    result.Success = false;
                    result.Error = "Invalid /etc/pve/.members: missing 'nodelist'.";
                    return result;
                }

                if (root.TryGetProperty("cluster", out var clusterEl) &&
                    clusterEl.ValueKind == JsonValueKind.Object &&
                    clusterEl.TryGetProperty("name", out var nameEl) &&
                    nameEl.ValueKind == JsonValueKind.String)
                {
                    result.ClusterName = nameEl.GetString();
                    logs.Add($"Cluster name: {result.ClusterName}");
                }
                else
                {
                    logs.Add("WARN: .members has no 'cluster.name'.");
                }

                result.Success = true;
                return result;
            }
            catch (OperationCanceledException)
            {
                logs.Add("Verification canceled.");
                throw;
            }
            catch (Exception ex)
            {
                logs.Add($"ERROR: Verification failed: {ex.Message}");
                _log.LogWarning(ex, "Proxmox cluster verification failed for seed host {SeedHost}", seedHost);
                result.Success = false;
                result.Error = "Verification failed.";
                return result;
            }
        }

        // ======================================================
        // Helpers
        // ======================================================

        private static string NormalizeSshUser(string username)
        {
            var u = (username ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(u))
                return "root";

            var atIndex = u.IndexOf('@');
            return atIndex > 0 ? u[..atIndex] : u;
        }

        private async Task<string> ReadMembersFileAsync(
            string host,
            string user,
            string password,
            List<string> logs,
            CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                using var ssh = new SshClient(host, user, password)
                {
                    ConnectionInfo = { Timeout = TimeSpan.FromSeconds(10) }
                };

                try
                {
                    ssh.Connect();
                }
                catch (Exception ex)
                {
                    logs.Add($"ERROR: SSH connect to {host} failed: {ex.Message}");
                    throw;
                }

                using var cmd = ssh.CreateCommand("cat /etc/pve/.members");
                cmd.CommandTimeout = TimeSpan.FromSeconds(10);

                var output = cmd.Execute();
                var exit = cmd.ExitStatus;
                var err = cmd.Error;

                ssh.Disconnect();

                if (exit != 0)
                {
                    logs.Add($"ERROR: 'cat /etc/pve/.members' exited with {exit}: {err}");
                    throw new InvalidOperationException(
                        $"cat /etc/pve/.members failed with exit code {exit}");
                }

                if (string.IsNullOrWhiteSpace(output))
                {
                    logs.Add("WARN: /etc/pve/.members returned empty output.");
                }
                else
                {
                    logs.Add("OK: Read /etc/pve/.members.");
                }

                return output;
            }, ct);
        }

        private async Task<bool> TrySshAsync(
            string host,
            string user,
            string password,
            int timeoutMs,
            CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var ssh = new SshClient(host, user, password)
                    {
                        ConnectionInfo = { Timeout = TimeSpan.FromMilliseconds(timeoutMs) }
                    };
                    ssh.Connect();
                    ssh.Disconnect();
                    return true;
                }
                catch
                {
                    return false;
                }
            }, ct);
        }
    }
}
