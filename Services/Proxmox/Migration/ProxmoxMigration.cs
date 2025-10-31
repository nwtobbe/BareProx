using BareProx.Models;
using BareProx.Services.Helpers;                 // ProxmoxSshException
using BareProx.Services.Proxmox.Authentication;
using BareProx.Services.Proxmox.Helpers;
using BareProx.Services.Proxmox.Ops;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BareProx.Services.Proxmox.Migration
{
    public sealed class ProxmoxMigration : IProxmoxMigration
    {
        private readonly IProxmoxHelpersService _helpers;
        private readonly IProxmoxOpsService _ops;
        private readonly IEncryptionService _enc;
        private readonly ILogger<ProxmoxMigration> _log;

        public ProxmoxMigration(
            IProxmoxHelpersService helpers,
            IProxmoxOpsService ops,
            IEncryptionService enc,
            ILogger<ProxmoxMigration> log)
        {
            _helpers = helpers;
            _ops = ops;
            _enc = enc;
            _log = log;
        }

        // ───────────────────── VMID availability (API + conf file existence) ─────────────────────
        public async Task<bool> IsVmidAvailableAsync(int vmid, CancellationToken ct = default)
        {
            var (cluster, host) = await GetAnyClusterAndHostAsync(ct);
            var url = $"https://{host.HostAddress}:8006/api2/json/cluster/resources?type=vm";
            var resp = await _ops.SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.TryGetProperty("vmid", out var v) &&
                        v.ValueKind == JsonValueKind.Number &&
                        v.GetInt32() == vmid)
                        return false;
                }
            }

            // Also consider existing conf file:
            return !await FileExistsAsync($"/etc/pve/qemu-server/{vmid}.conf", ct);
        }

        // ───────────────────── File/dir helpers (SSH) ─────────────────────
        public async Task EnsureDirectoryAsync(string absPath, CancellationToken ct = default)
        {
            var (_, host, sshUser, sshPass) = await GetSshTargetAsync(ct);
            using var ssh = new SshClient(host.HostAddress, sshUser, sshPass);
            ssh.Connect();
            var cmd = ssh.CreateCommand($"mkdir -p -- {EscapeBash(absPath)}");
            var _ = cmd.Execute();
            if (cmd.ExitStatus != 0) throw new ProxmoxSshException(cmd.CommandText, cmd.ExitStatus, cmd.Error);
            ssh.Disconnect();
        }

        public async Task<string> ReadTextFileAsync(string absPath, CancellationToken ct = default)
        {
            var (_, host, sshUser, sshPass) = await GetSshTargetAsync(ct);
            using var ssh = new SshClient(host.HostAddress, sshUser, sshPass);
            ssh.Connect();
            using var cmd = ssh.CreateCommand($"cat -- {EscapeBash(absPath)}");
            var text = cmd.Execute();
            ssh.Disconnect();
            return text ?? string.Empty;
        }

        public async Task WriteTextFileAsync(string absPath, string content, CancellationToken ct = default)
        {
            var (_, host, sshUser, sshPass) = await GetSshTargetAsync(ct);
            absPath = ToPosix(absPath);
            var dir = GetDirPosix(absPath);

            var conn = new PasswordConnectionInfo(host.HostAddress, sshUser, sshPass) { Timeout = TimeSpan.FromSeconds(30) };
            using var ssh = new SshClient(conn) { KeepAliveInterval = TimeSpan.FromSeconds(15) };
            ssh.Connect();

            ExecOrThrow(ssh, $"mkdir -p -- {EscapeBash(dir)} && : > {EscapeBash(absPath)}");

            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content ?? string.Empty));
            const int chunkSize = 4096;
            for (int i = 0; i < b64.Length; i += chunkSize)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = b64.Substring(i, Math.Min(chunkSize, b64.Length - i));
                ExecOrThrow(ssh, $"printf %s {EscapeBash(chunk)} | base64 -d >> {EscapeBash(absPath)}");
            }
            ExecOrThrow(ssh, "sync || true");
            ssh.Disconnect();
        }

        private async Task<bool> FileExistsAsync(string absPath, CancellationToken ct = default)
        {
            var (_, host, sshUser, sshPass) = await GetSshTargetAsync(ct);
            using var ssh = new SshClient(host.HostAddress, sshUser, sshPass);
            ssh.Connect();
            using var cmd = ssh.CreateCommand($"test -e {EscapeBash(absPath)} && echo OK || echo NO");
            var result = cmd.Execute();
            ssh.Disconnect();
            return result.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase);
        }

        // ───────────────────── QEMU helpers (SSH/API mix) ─────────────────────
        public async Task<int?> FirstFreeVirtioSlotAsync(int vmid, CancellationToken ct = default)
        {
            var text = await ReadTextFileAsync($"/etc/pve/qemu-server/{vmid}.conf", ct);
            var used = new HashSet<int>();
            foreach (Match m in Regex.Matches(text ?? string.Empty, @"^\s*virtio(?<n>\d+):", RegexOptions.Multiline))
                if (int.TryParse(m.Groups["n"]?.Value, out var n)) used.Add(n);
            for (int i = 0; i <= 15; i++) if (!used.Contains(i)) return i;
            return null;
        }

        public async Task AddDummyDiskAsync(int vmid, string storage, int slot, int sizeGiB, CancellationToken ct = default)
        {
            var (_, host, sshUser, sshPass) = await GetSshTargetAsync(ct);
            var cmdText = $"qm set {vmid} --virtio{slot} {EscapeBash($"{storage}:{sizeGiB}")}";
            using var ssh = new SshClient(host.HostAddress, sshUser, sshPass);
            ssh.Connect();
            using var cmd = ssh.CreateCommand(cmdText);
            var _ = cmd.Execute();
            var rc = cmd.ExitStatus;
            var err = cmd.Error;
            ssh.Disconnect();
            if (rc != 0) throw new ProxmoxSshException(cmdText, rc, err);
        }

        public async Task AddEfiDiskAsync(int vmid, string storage, CancellationToken ct = default)
        {
            var (_, host, sshUser, sshPass) = await GetSshTargetAsync(ct);
            var target = $"{storage}:0";
            var cmdText = $"qm set {vmid} --efidisk0 {EscapeBash(target)}";
            using var ssh = new SshClient(host.HostAddress, sshUser, sshPass);
            ssh.Connect();
            using var cmd = ssh.CreateCommand(cmdText);
            var _ = cmd.Execute();
            var rc = cmd.ExitStatus;
            var err = cmd.Error;
            ssh.Disconnect();
            if (rc != 0) throw new ProxmoxSshException(cmdText, rc, err);
        }

        public async Task SetCdromAsync(int vmid, string volidOrName, CancellationToken ct = default)
        {
            var volid = volidOrName.Contains(':', StringComparison.Ordinal) ? volidOrName : $"local:iso/{volidOrName}";
            var (cluster, host) = await GetAnyClusterAndHostAsync(ct);
            var url = $"https://{host.HostAddress}:8006/api2/json/nodes/{host.Hostname}/qemu/{vmid}/config";
            var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["cdrom"] = volid });
            await _ops.SendWithRefreshAsync(cluster, HttpMethod.Post, url, form, ct);
        }

        // ───────────────────── Moved from ProxmoxService (migration-related lookups) ─────────────────────

        // GET /api2/json/nodes/{node}/network
        public async Task<IReadOnlyList<PveNetworkIf>> GetNodeNetworksAsync(string node, CancellationToken ct = default)
        {
            var resolved = await ResolveClusterAndHostAsync(node, ct);
            if (resolved == null) return Array.Empty<PveNetworkIf>();

            var (cluster, host) = resolved.Value;
            var url = $"https://{host.HostAddress}:8006/api2/json/nodes/{host.Hostname}/network";

            try
            {
                var resp = await _ops.SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    return Array.Empty<PveNetworkIf>();

                var list = new List<PveNetworkIf>();
                foreach (var n in data.EnumerateArray())
                {
                    var type = n.TryGetProperty("type", out var t) ? t.GetString() : null;
                    var iface = n.TryGetProperty("iface", out var i) ? i.GetString() : null;

                    list.Add(new PveNetworkIf { Type = type, Iface = iface });
                }
                return list;
            }
            catch
            {
                return Array.Empty<PveNetworkIf>();
            }
        }

        // GET /api2/json/cluster/sdn/vnets
        public async Task<IReadOnlyList<PveSdnVnet>> GetSdnVnetsAsync(CancellationToken ct = default)
        {
            // Use any available cluster/host to hit the cluster endpoint
            var clusters = await _helpers.LoadAllClustersAsync(ct);
            var cluster = clusters.FirstOrDefault();
            if (cluster == null || cluster.Hosts.Count == 0)
                return Array.Empty<PveSdnVnet>();

            var host = cluster.Hosts.First();
            var url = $"https://{host.HostAddress}:8006/api2/json/cluster/sdn/vnets";

            try
            {
                var resp = await _ops.SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    return Array.Empty<PveSdnVnet>();

                var list = new List<PveSdnVnet>();
                foreach (var v in data.EnumerateArray())
                {
                    var vnet = v.TryGetProperty("vnet", out var vn) ? vn.GetString() : null;

                    int? tag = null;
                    if (v.TryGetProperty("tag", out var tg))
                    {
                        if (tg.ValueKind == JsonValueKind.Number) tag = tg.GetInt32();
                        else if (tg.ValueKind == JsonValueKind.String && int.TryParse(tg.GetString(), out var ti)) tag = ti;
                    }

                    list.Add(new PveSdnVnet { Vnet = vnet, Tag = tag });
                }
                return list;
            }
            catch
            {
                return Array.Empty<PveSdnVnet>();
            }
        }

        // GET /api2/json/nodes/{node}/storage/{storage}/content?content=iso
        public async Task<IReadOnlyList<PveStorageContentItem>> GetStorageContentAsync(
            string node,
            string storage,
            string content,
            CancellationToken ct = default)
        {
            var resolved = await ResolveClusterAndHostAsync(node, ct);
            if (resolved == null) return Array.Empty<PveStorageContentItem>();

            var (cluster, host) = resolved.Value;
            var url =
                $"https://{host.HostAddress}:8006/api2/json/nodes/{host.Hostname}/storage/{Uri.EscapeDataString(storage)}/content?content={Uri.EscapeDataString(content)}";

            try
            {
                var resp = await _ops.SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    return Array.Empty<PveStorageContentItem>();

                var list = new List<PveStorageContentItem>();
                foreach (var i in data.EnumerateArray())
                {
                    long? ctime = null;
                    if (i.TryGetProperty("ctime", out var ctProp))
                    {
                        if (ctProp.ValueKind == JsonValueKind.Number) ctime = ctProp.GetInt64();
                        else if (ctProp.ValueKind == JsonValueKind.String && long.TryParse(ctProp.GetString(), out var l)) ctime = l;
                    }
                    // Proxmox can return volid/volId/volume; read all safely
                    string? name = i.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? volid = i.TryGetProperty("volid", out var vi) ? vi.GetString() : null;
                    string? volId = i.TryGetProperty("volId", out var vI) ? vI.GetString() : null;
                    string? volume = i.TryGetProperty("volume", out var vo) ? vo.GetString() : null;
                    string? cnt = i.TryGetProperty("content", out var c) ? c.GetString() : null;

                    list.Add(new PveStorageContentItem
                    {
                        Name = name,
                        Volid = volid,
                        VolId = volId,
                        Volume = volume,
                        Content = cnt,
                        Ctime = ctime
                    });
                }
                return list;
            }
            catch
            {
                return Array.Empty<PveStorageContentItem>();
            }
        }

        // GET /api2/json/nodes/{node}/storage
        public async Task<IReadOnlyList<PveStorageListItem>> GetNodeStoragesAsync(string node, CancellationToken ct = default)
        {
            var resolved = await ResolveClusterAndHostAsync(node, ct);
            if (resolved == null) return Array.Empty<PveStorageListItem>();

            var (cluster, host) = resolved.Value;
            var url = $"https://{host.HostAddress}:8006/api2/json/nodes/{host.Hostname}/storage";

            try
            {
                var resp = await _ops.SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    return Array.Empty<PveStorageListItem>();

                var list = new List<PveStorageListItem>();
                foreach (var s in data.EnumerateArray())
                {
                    var storage = s.TryGetProperty("storage", out var st) ? st.GetString() : null;
                    if (string.IsNullOrWhiteSpace(storage)) continue;

                    var content = s.TryGetProperty("content", out var c) ? c.GetString() : null;
                    var type = s.TryGetProperty("type", out var t) ? t.GetString() : null;

                    list.Add(new PveStorageListItem
                    {
                        Storage = storage,
                        Content = content,
                        Type = type
                    });
                }
                return list;
            }
            catch
            {
                return Array.Empty<PveStorageListItem>();
            }
        }

        // ───────────────────── internals ─────────────────────

        private async Task<(ProxmoxCluster Cluster, ProxmoxHost Host)> GetAnyClusterAndHostAsync(CancellationToken ct)
        {
            var clusters = await _helpers.LoadAllClustersAsync(ct);
            var first = clusters.FirstOrDefault();
            if (first is null || first.Hosts.Count == 0)
                throw new InvalidOperationException("No Proxmox cluster/host configured.");

            var host = first.Hosts.First();
            return (first, host);
        }

        /// <summary>
        /// Resolve cluster+host by node string (matches Hostname or HostAddress). Returns null if not found.
        /// </summary>
        private async Task<(ProxmoxCluster Cluster, ProxmoxHost Host)?> ResolveClusterAndHostAsync(string node, CancellationToken ct)
        {
            var clusters = await _helpers.LoadAllClustersAsync(ct);
            foreach (var c in clusters)
            {
                var h = c.Hosts.FirstOrDefault(x =>
                    x.Hostname.Equals(node, StringComparison.OrdinalIgnoreCase) ||
                    x.HostAddress.Equals(node, StringComparison.OrdinalIgnoreCase));
                if (h != null) return (c, h);
            }

            // Fallback: nothing matched → null (caller returns empty lists)
            return null;
        }

        private async Task<(ProxmoxCluster Cluster, ProxmoxHost Host, string SshUser, string SshPass)> GetSshTargetAsync(CancellationToken ct)
        {
            var (cluster, host) = await GetAnyClusterAndHostAsync(ct);
            var sshUser = cluster.Username.Split('@')[0];
            var sshPass = _enc.Decrypt(cluster.PasswordHash);
            return (cluster, host, sshUser, sshPass);
        }

        private static void ExecOrThrow(SshClient ssh, string command)
        {
            using var cmd = ssh.CreateCommand(command);
            cmd.CommandTimeout = TimeSpan.FromMinutes(2);
            var _ = cmd.Execute();
            if (cmd.ExitStatus != 0)
                throw new ProxmoxSshException(cmd.CommandText, cmd.ExitStatus, cmd.Error);
        }

        private static string ToPosix(string p) => (p ?? "").Replace('\\', '/');
        private static string GetDirPosix(string p)
        {
            p = ToPosix(p);
            var i = p.LastIndexOf('/');
            if (i < 0) return "/";
            return i == 0 ? "/" : p[..i];
        }
        private static string EscapeBash(string s) => "'" + (s ?? string.Empty).Replace("'", "'\"'\"'") + "'";
    }
}
