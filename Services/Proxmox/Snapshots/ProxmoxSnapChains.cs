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


using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BareProx.Models;
using BareProx.Services.Proxmox.Helpers;
using BareProx.Services.Proxmox.Ops;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace BareProx.Services.Proxmox.Snapshots
{
    public sealed class ProxmoxSnapChains : IProxmoxSnapChains
    {
        private readonly ILogger<ProxmoxSnapChains> _log;
        private readonly IProxmoxHelpersService _helpers;
        private readonly IProxmoxOpsService _ops;
        private readonly IEncryptionService _enc;

        public ProxmoxSnapChains(
            ILogger<ProxmoxSnapChains> log,
            IProxmoxHelpersService helpers,
            IProxmoxOpsService ops,
            IEncryptionService enc)
        {
            _log = log;
            _helpers = helpers;
            _ops = ops;
            _enc = enc;
        }

        public async Task<bool> IsSnapshotChainActiveFromDefAsync(
            ProxmoxCluster cluster,
            string storageName,
            CancellationToken ct = default)
        {
            if (cluster?.Hosts == null || cluster.Hosts.Count == 0) return false;

            var host = _helpers.GetQueryableHosts(cluster).FirstOrDefault()
                       ?? cluster.Hosts.First();

            var url = $"https://{host.HostAddress}:8006/api2/json/storage/{Uri.EscapeDataString(storageName)}";
            var resp = await _ops.SendWithRefreshAsync(cluster, HttpMethod.Get, url, null, ct);
            if (!resp.IsSuccessStatusCode) return false;

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return false;

            if (data.TryGetProperty("snapshot-as-volume-chain", out var prop))
            {
                return prop.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => prop.GetInt32() != 0,
                    JsonValueKind.String => (prop.GetString() ?? "") is "1" or "true" or "TRUE",
                    _ => false
                };
            }
            return false;
        }

        public async Task<bool> CreateOrUpdateNfsStorageWithChainAsync(
            ProxmoxCluster cluster,
            string node,
            string storageName,
            string serverIp,
            string exportPath,
            bool snapshotChainActive,
            string content = "images,backup,iso,vztmpl",
            string options = "vers=3",
            CancellationToken ct = default)
        {
            var nodeHost = _helpers
                .GetQueryableHosts(cluster)
                .FirstOrDefault(h => h.Hostname == node)?.HostAddress ?? string.Empty;

            if (string.IsNullOrWhiteSpace(nodeHost)) return false;

            var createUrl = $"https://{nodeHost}:8006/api2/json/storage";

            var payload = new Dictionary<string, string>
            {
                ["type"] = "nfs",
                ["storage"] = storageName,
                ["server"] = serverIp,
                ["export"] = exportPath,
                ["content"] = content,
                ["options"] = options,
                ["snapshot-as-volume-chain"] = snapshotChainActive ? "1" : "0"
            };

            try
            {
                using var body = new FormUrlEncodedContent(payload);
                // Cluster-wide create (or update). May return 409/500 if already defined—ignore, we verify below.
                _ = await _ops.SendWithRefreshAsync(cluster, HttpMethod.Post, createUrl, body, ct);
            }
            catch
            {
                // ignore, proceed to verification
            }

            return await VerifyStorageMountedAsync(cluster, nodeHost, node, storageName, ct);
        }

        public async Task<bool> RepairExternalSnapshotChainAsync(
            string nodeName,
            string storageName,
            int vmid,
            CancellationToken ct = default)
        {
            var clusters = await _helpers.LoadAllClustersAsync(ct);
            var resolved = _helpers.ResolveClusterAndHostFromLoaded(clusters, nodeName);
            if (resolved == null) throw new InvalidOperationException($"Node '{nodeName}' not found.");

            var (cluster, host) = resolved.Value;
            var sshUser = cluster.Username.Split('@')[0];
            var sshPass = _enc.Decrypt(cluster.PasswordHash);

            string BQ(string s) => "'" + (s ?? string.Empty).Replace("'", "'\"'\"'") + "'";

            var sb = new StringBuilder();
            sb.AppendLine("set -euo pipefail");
            sb.AppendLine($"storage={BQ(storageName)}");
            sb.AppendLine($"vmid={vmid}");
            sb.AppendLine();
            sb.AppendLine("""
                base=""
                if [ -d "/mnt/pve/$storage/images" ]; then
                  base="/mnt/pve/$storage"
                else
                  conf_path="$(pvesm config "$storage" 2>/dev/null | awk -F': ' '/^path: /{print $2}')" || true
                  if [ -n "$conf_path" ] && [ -d "$conf_path/images" ]; then
                    base="$conf_path"
                  fi
                fi
                [ -z "$base" ] && { echo "ERR: cannot resolve path for storage '$storage'" >&2; exit 2; }

                dir="$base/images/$vmid"
                [ -d "$dir" ] || { echo "ERR: dir not found: $dir" >&2; exit 3; }
                cd "$dir"

                command -v qemu-img >/dev/null 2>&1 || { echo "ERR: qemu-img missing" >&2; exit 4; }

                norm_fmt() {
                  local f="$1" fmt=""
                  fmt="$(qemu-img info --output=json -- "$f" 2>/dev/null | tr -d '\r\n' | sed -n 's/.*\"format\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p')" || true
                  if [ -z "$fmt" ]; then
                    fmt="$(qemu-img info -- "$f" 2>/dev/null | sed -n 's/^file format:[[:space:]]*//p' | head -n1 | tr -d '\r' | tr '\n' ' ' | awk '{print $NF}')"
                  fi
                  case "$fmt" in
                    qcow2|raw|vmdk|vdi|vpc) echo "$fmt" ;;
                    *) case "$f" in *.qcow2) echo qcow2 ;; *.raw) echo raw ;; *) echo qcow2 ;; esac ;;
                  esac
                }

                rebase_safe() {
                  local img="$1" base="$2"
                  local topfmt bfmt
                  topfmt="$(norm_fmt "$img")"
                  bfmt="$(norm_fmt "$base")"
                  qemu-img rebase -u -f "$topfmt" -F "$bfmt" -b "$base" -- "$img"
                  echo "REB: $img -> $base (f=$topfmt,F=$bfmt)"
                }

                ensure_overlay_is_file() {
                  local img="$1"
                  if [ -L "$img" ]; then
                    local base bfmt
                    base="$(qemu-img info --output=json -- "$img" 2>/dev/null | tr -d '\n' | sed -n 's/.*\"backing-filename\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p')"
                    [ -z "$base" ] && base="$(qemu-img info -- "$img" 2>/dev/null | sed -n 's/^backing file:[[:space:]]*//p' | head -n1)"
                    base="${base#./}"
                    base="$(readlink -f -- "$base" 2>/dev/null || echo "$base")"
                    bfmt="$(norm_fmt "$base")"
                    rm -f -- "$img"
                    qemu-img create -f qcow2 -o backing_file="$base",backing_fmt="$bfmt" -- "$img"
                    echo "CREATED overlay $img -> $base (F=$bfmt)"
                  fi
                }

                cleanup_bitmaps() {
                  for f in vm-$vmid-disk-*.qcow2; do
                    [ -e "$f" ] || continue
                    for b in $(qemu-img info "$f" 2>/dev/null | awk '/bitmaps:/{p=1;next} p && /name:/{print $2}' | tr -d ','); do
                      qemu-img bitmap --remove "$f" "$b" || true
                      echo "Removed bitmap $b from $f"
                    done
                  done
                }

                fix_one() {
                  local img="$1" backing base_b repl disknum cand
                  backing="$(qemu-img info --output=json -- "$img" | tr -d '\n' | sed -n 's/.*\"backing-filename\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p')" || true
                  [ -z "$backing" ] && return 0
                  backing="${backing#./}"
                  base_b="$(basename -- "$backing")"
                  [ -e "$base_b" ] && return 0

                  repl="$(echo "$base_b" | sed -E 's/vm-[0-9]+-/vm-'"$vmid"'-/g')" || true
                  if [ -n "$repl" ] && [ -e "$repl" ]; then
                    rebase_safe "$img" "$repl"
                    return 0
                  fi

                  disknum="$(echo "$base_b" | sed -n 's/.*-disk-\([0-9]\+\)\.qcow2/\1/p')" || true
                  cand=""
                  if [ -n "$disknum" ]; then
                    cand="$(ls -1 snap-*-vm-*-disk-"$disknum".qcow2 2>/dev/null | head -n1)" || true
                  fi
                  [ -z "$cand" ] && cand="$(ls -1 snap-*.qcow2 2>/dev/null | head -n1)" || true
                  if [ -n "$cand" ] && [ -e "$cand" ]; then
                    rebase_safe "$img" "$cand"
                    return 0
                  fi

                  echo "WARN: could not repair backing for $img (missing '$base_b')" >&2
                  return 0
                }

                shopt -s nullglob
                for t in vm-$vmid-disk-*.qcow2; do ensure_overlay_is_file "$t"; done
                cleanup_bitmaps || true
                for q in *.qcow2; do fix_one "$q"; done
                echo "OK: chain repair attempted in $dir"
                """);

            var script = sb.ToString();

            try
            {
                using var ssh = new SshClient(host.HostAddress, sshUser, sshPass);
                ssh.Connect();

                var eof = "EOF_" + Guid.NewGuid().ToString("N");
                var cmdText = "cat <<'" + eof + "' | tr -d '\\r' | bash\n" + script + "\n" + eof + "\n";

                using var cmd = ssh.CreateCommand(cmdText);
                cmd.CommandTimeout = TimeSpan.FromMinutes(5);
                var output = cmd.Execute();
                var rc = cmd.ExitStatus;
                var err = cmd.Error;

                ssh.Disconnect();

                _log.LogInformation("RepairExternalSnapshotChainAsync on {Host}: rc={RC}\n{Out}\n{Err}",
                    host.HostAddress, rc, (output ?? "").Trim(), (err ?? "").Trim());

                return rc == 0;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Snapshot chain repair failed on node {Node}", nodeName);
                return false;
            }
        }

        // ─────────────── internals ───────────────

        private async Task<bool> VerifyStorageMountedAsync(
            ProxmoxCluster cluster,
            string nodeHost,
            string node,
            string storageName,
            CancellationToken ct)
        {
            var statusUrl =
                $"https://{nodeHost}:8006/api2/json/nodes/{Uri.EscapeDataString(node)}/storage/{Uri.EscapeDataString(storageName)}/status";

            for (int i = 0; i < 30; i++)
            {
                try
                {
                    var resp = await _ops.SendWithRefreshAsync(cluster, HttpMethod.Get, statusUrl, null, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync(ct);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("data", out var data))
                        {
                            bool active = _helpers.TryGetTruthy(data, "active");
                            long total = _helpers.TryGetInt64(data, "total");
                            string state = data.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String
                                ? s.GetString() ?? ""
                                : "";

                            if (active && total > 0 && (state.Length == 0 || state.Equals("available", StringComparison.OrdinalIgnoreCase)))
                            {
                                var contentUrl =
                                    $"https://{nodeHost}:8006/api2/json/nodes/{Uri.EscapeDataString(node)}/storage/{Uri.EscapeDataString(storageName)}/content";
                                var listResp = await _ops.SendWithRefreshAsync(cluster, HttpMethod.Get, contentUrl, null, ct);
                                if (listResp.IsSuccessStatusCode) return true;
                            }
                        }
                    }
                }
                catch { /* transient */ }

                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }

            return false;
        }
    }
}
