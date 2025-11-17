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
using BareProx.Data;
using BareProx.Models;
using BareProx.Services.Proxmox.Helpers;
using BareProx.Services.Proxmox.Ops;
using BareProx.Services.Proxmox.Snapshots;
using BareProx.Services.Proxmox; // for ProxmoxService (temp rename helper)
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BareProx.Services.Proxmox.Restore
{
    public sealed class ProxmoxRestore : IProxmoxRestore
    {
        private readonly IDbContextFactory<ApplicationDbContext> _dbf;
        private readonly IEncryptionService _enc;
        private readonly IProxmoxHelpersService _helpers;
        private readonly IProxmoxOpsService _ops;
        private readonly IProxmoxSnapshotsService _snaps;
        private readonly IProxmoxSnapChains _snapChains;     // NEW: snapshot-chain ops
        private readonly ProxmoxService _proxmox;             // TEMP: for RenameVmDirectoryAndFilesAsync
        private readonly ILogger<ProxmoxRestore> _log;

        public ProxmoxRestore(
            IDbContextFactory<ApplicationDbContext> dbf,
            IEncryptionService enc,
            IProxmoxHelpersService helpers,
            IProxmoxOpsService ops,
            IProxmoxSnapshotsService snaps,
            IProxmoxSnapChains snapChains,
            ProxmoxService proxmox, // TEMP until rename helper is moved out
            ILogger<ProxmoxRestore> log)
        {
            _dbf = dbf;
            _enc = enc;
            _helpers = helpers;
            _ops = ops;
            _snaps = snaps;
            _snapChains = snapChains;
            _proxmox = proxmox;
            _log = log;
        }

        public async Task<bool> RestoreVmFromConfigAsync(
            RestoreFormViewModel model,
            string hostAddress,
            string cloneStorageName,
            bool snapshotChainActive = false,
            CancellationToken ct = default)
        {
            using var db = await _dbf.CreateDbContextAsync(ct);

            using var rootDoc = JsonDocument.Parse(model.OriginalConfig);
            if (!rootDoc.RootElement.TryGetProperty("config", out var config))
                return false;

            var host = await db.ProxmoxHosts.FirstOrDefaultAsync(h => h.HostAddress == hostAddress, ct);
            if (host?.Hostname is null) return false;
            var nodeName = host.Hostname;

            var cluster = await db.ProxmoxClusters.Include(c => c.Hosts).FirstOrDefaultAsync(ct);
            if (cluster == null) return false;

            var anyQueryable = (await _helpers.GetQueryableHostsAsync(cluster, ct)).Any();
            var anyConfigured = cluster.Hosts?.Any() ?? false;

            if (!anyQueryable && !anyConfigured) return false;


            var nextIdUrl = $"https://{hostAddress}:8006/api2/json/cluster/nextid";
            var idResp = await _ops.SendWithRefreshAsync(cluster, HttpMethod.Get, nextIdUrl, null, ct);
            using var idDoc = JsonDocument.Parse(await idResp.Content.ReadAsStringAsync(ct));
            var vmid = idDoc.RootElement.GetProperty("data").GetString();
            if (string.IsNullOrEmpty(vmid)) return false;

            var payload = _helpers.FlattenConfig(config);

            // Detect old storage name from any disk line
            string? oldStorageName = null;
            var diskKeys = payload.Keys
                .Where(k => Regex.IsMatch(k, @"^(scsi|virtio|sata|efidisk|tpmstate)\d+$", RegexOptions.IgnoreCase))
                .ToList();

            foreach (var key in diskKeys)
            {
                var val = payload[key];
                if (!string.IsNullOrEmpty(val) && val.Contains(":"))
                {
                    oldStorageName = val.Split(':', 2)[0].Trim();
                    break;
                }
            }
            if (string.IsNullOrEmpty(oldStorageName))
                throw new InvalidOperationException("Failed to determine oldStorageName from config.");

            payload["name"] = model.NewVmName;
            payload.Remove("meta");
            payload.Remove("digest");
            payload["protection"] = "0";

            // Disconnect NICs if requested
            if (model.StartDisconnected)
            {
                foreach (var netKey in payload.Keys.Where(k => Regex.IsMatch(k, @"^net\d+$", RegexOptions.IgnoreCase)).ToList())
                {
                    var def = payload[netKey];
                    payload[netKey] = Regex.IsMatch(def, @"\blink_down=\d")
                        ? Regex.Replace(def, @"\blink_down=\d", "link_down=1")
                        : def + ",link_down=1";
                }
            }

            var oldVmid = _helpers.ExtractOldVmidFromConfig(payload)
                         ?? throw new InvalidOperationException("Failed to determine oldVmid from config.");

            // TEMP call (still lives in ProxmoxService)
            await _proxmox.RenameVmDirectoryAndFilesAsync(nodeName, cloneStorageName, oldVmid, vmid, ct);

            _helpers.UpdateDiskPathsInConfig(payload, oldVmid, vmid, cloneStorageName);

            // Global remap old → new storage
            foreach (var key in payload.Keys.ToList())
            {
                var val = payload[key];
                if (!string.IsNullOrEmpty(val))
                    payload[key] = val.Replace(oldStorageName, cloneStorageName, StringComparison.OrdinalIgnoreCase);
            }

            // Explicit vmstate
            if (payload.ContainsKey("vmstate"))
            {
                payload["vmstate"] = RemapStorageAndVmid(payload["vmstate"], oldStorageName!, cloneStorageName, oldVmid, vmid);
            }

            // New IDs
            string? newUuid = null, newVmgen = null;
            if (model.GenerateNewUuid)
            {
                newUuid = Guid.NewGuid().ToString("D");
                newVmgen = Guid.NewGuid().ToString("D");

                if (payload.TryGetValue("smbios1", out var smbios))
                {
                    var parts = smbios.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                      .Where(p => !p.StartsWith("uuid=", StringComparison.OrdinalIgnoreCase));
                    payload["smbios1"] = string.Join(",", parts.Append($"uuid={newUuid}"));
                }
                else
                {
                    payload["smbios1"] = $"uuid={newUuid}";
                }
                payload["vmgenid"] = newVmgen!;
            }

            // Build .conf

            if (!string.IsNullOrWhiteSpace(model.NewVmName))
                payload["name"] = model.NewVmName.Trim();

            var sb = new StringBuilder();
            foreach (var kv in payload) sb.AppendLine($"{kv.Key}: {kv.Value}");

            // Snapshots
            if (rootDoc.RootElement.TryGetProperty("snapshots", out var snapElem) &&
                snapElem.ValueKind == JsonValueKind.Object)
            {
                foreach (var snapProp in snapElem.EnumerateObject())
                {
                    sb.AppendLine($"[{snapProp.Name}]");

                    var snapDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var snapLine in snapProp.Value.EnumerateObject())
                        snapDict[snapLine.Name] = snapLine.Value.GetString() ?? "";

                    if (model.StartDisconnected)
                    {
                        foreach (var netKey in snapDict.Keys.Where(k => Regex.IsMatch(k, @"^net\d+$", RegexOptions.IgnoreCase)).ToList())
                        {
                            var def = snapDict[netKey];
                            snapDict[netKey] = Regex.IsMatch(def, @"\blink_down=\d")
                                ? Regex.Replace(def, @"\blink_down=\d", "link_down=1")
                                : def + ",link_down=1";
                        }
                    }

                    if (newUuid != null && newVmgen != null)
                    {
                        if (snapDict.TryGetValue("smbios1", out var smbiosSnap))
                        {
                            var parts = smbiosSnap.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                                  .Where(p => !p.StartsWith("uuid=", StringComparison.OrdinalIgnoreCase));
                            snapDict["smbios1"] = string.Join(",", parts.Append($"uuid={newUuid}"));
                        }
                        else
                        {
                            snapDict["smbios1"] = $"uuid={newUuid}";
                        }
                        snapDict["vmgenid"] = newVmgen;
                    }

                    _helpers.UpdateDiskPathsInConfig(snapDict, oldVmid, vmid, cloneStorageName);

                    foreach (var k in snapDict.Keys.ToList())
                    {
                        var v = snapDict[k];
                        if (!string.IsNullOrEmpty(v))
                            snapDict[k] = v.Replace(oldStorageName, cloneStorageName, StringComparison.OrdinalIgnoreCase);
                    }

                    if (snapDict.TryGetValue("vmstate", out var vmstateValue))
                    {
                        snapDict["vmstate"] = RemapStorageAndVmid(vmstateValue, oldStorageName!, cloneStorageName, oldVmid, vmid);
                    }

                    foreach (var snapKvp in snapDict)
                        sb.AppendLine($"{snapKvp.Key}: {snapKvp.Value}");
                }
            }

            if (!payload.ContainsKey("storage"))
                payload["storage"] = cloneStorageName;

            // Upload config
            var sshUser = cluster.Username.Split('@')[0];
            var sshPass = _enc.Decrypt(cluster.PasswordHash);
            var configPath = $"/etc/pve/qemu-server/{vmid}.conf";
            var configContent = sb.ToString();

            var eofMarker = "EOF_" + Guid.NewGuid().ToString("N");
            var sshCmd = $"cat > {configPath} <<'{eofMarker}'\n{configContent}\n{eofMarker}\n";

            using (var ssh = new Renci.SshNet.SshClient(hostAddress, sshUser, sshPass))
            {
                ssh.Connect();
                using (var cmd = ssh.CreateCommand(sshCmd))
                {
                    var _ = cmd.Execute();
                    if (cmd.ExitStatus != 0) { ssh.Disconnect(); return false; }
                }
                ssh.Disconnect();
            }

            // Post-restore repair/rollback/delete (best-effort)
            try
            {
                var vmidInt = int.Parse(vmid);
                var snaps = await _snaps.GetSnapshotListAsync(cluster, nodeName, hostAddress, vmidInt, ct);
                if (snaps != null && snaps.Count > 0)
                {
                    var bareproxSnap = snaps
                        .Where(s => !string.IsNullOrWhiteSpace(s.Name) && s.Name.StartsWith("BareProx-", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(s => s.Snaptime)
                        .FirstOrDefault();

                    var newestNonCurrent = snaps
                        .Where(s => !string.Equals(s.Name, "current", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(s => s.Snaptime)
                        .FirstOrDefault();

                    if (newestNonCurrent != null)
                    {
                        try
                        {
                            // UPDATED: use the new snap-chains service
                            await _snapChains.RepairExternalSnapshotChainAsync(nodeName, cloneStorageName, vmidInt, ct);
                            _log.LogInformation("Repaired external snapshot chain for VMID {Vmid}.", vmidInt);
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "Snapshot chain repair failed for VMID {Vmid}; continuing.", vmidInt);
                        }
                    }

                    if (model.RollbackSnapshot)
                    {
                        var targetSnap = bareproxSnap ?? newestNonCurrent;
                        if (targetSnap != null)
                        {
                            try
                            {
                                var ok = await _snaps.RollbackSnapshotAsync(
                                    cluster, nodeName, hostAddress, vmidInt, targetSnap.Name, false, _log, ct);

                                if (!ok)
                                    _log.LogWarning("Rollback task for '{Snap}' on VMID {Vmid} did not complete OK.", targetSnap.Name, vmidInt);
                            }
                            catch (Exception ex)
                            {
                                _log.LogWarning(ex, "Rollback call failed for '{Snap}' on VMID {Vmid}. Will still attempt delete.", targetSnap.Name, vmidInt);
                            }

                            try
                            {
                                await _snaps.DeleteSnapshotAsync(cluster, nodeName, hostAddress, vmidInt, targetSnap.Name, ct);
                                _log.LogInformation("Deleted snapshot '{Snap}' after rollback on VMID {Vmid}.", targetSnap.Name, vmidInt);
                            }
                            catch (Exception ex)
                            {
                                _log.LogWarning(ex, "Failed to delete snapshot '{Snap}' after rollback on VMID {Vmid}.", targetSnap.Name, vmidInt);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Post-restore snapshot handling (repair/rollback/delete) skipped due to error.");
            }

            // Optional: regenerate MACs
            if (model.GenerateNewMacAddresses)
            {
                var netKeys = payload.Keys.Where(k => Regex.IsMatch(k, @"^net\d+$", RegexOptions.IgnoreCase))
                                          .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                                          .ToList();

                if (netKeys.Count > 0)
                {
                    var sshUser2 = cluster.Username.Split('@')[0];
                    var sshPass2 = _enc.Decrypt(cluster.PasswordHash);

                    using var ssh2 = new Renci.SshNet.SshClient(hostAddress, sshUser2, sshPass2);
                    ssh2.Connect();

                    foreach (var netKey in netKeys)
                    {
                        var def = payload[netKey];
                        var parts = def.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                        if (parts.Count == 0) continue;

                        var modelToken = parts[0];
                        var modelOnly = modelToken.Split('=', 2)[0];

                        var tail = parts.Skip(1);
                        var newNetValue = string.Join(",", new[] { modelOnly }.Concat(tail));

                        var netIdx = Regex.Match(netKey, @"^net(\d+)$", RegexOptions.IgnoreCase).Groups[1].Value;
                        var qmCmd = $"qm set {vmid} -net{netIdx} \"{newNetValue}\"";

                        using var cmd2 = ssh2.CreateCommand(qmCmd);
                        var _ = cmd2.Execute();
                        if (cmd2.ExitStatus != 0)
                        {
                            _log.LogWarning("Failed to regenerate MAC for {NetKey} on VMID {Vmid}. Exit={Exit} Error={Err}",
                                netKey, vmid, cmd2.ExitStatus, cmd2.Error);
                        }
                        else
                        {
                            _log.LogInformation("Regenerated MAC for {NetKey} on VMID {Vmid} using '{NewVal}'.",
                                netKey, vmid, newNetValue);
                        }
                    }

                    ssh2.Disconnect();
                }
            }

            return true;
        }

        public async Task<bool> RestoreVmFromConfigWithOriginalIdAsync(
            RestoreFormViewModel model,
            string hostAddress,
            string cloneStorageName,
            bool snapshotChainActive = false,
            CancellationToken ct = default)
        {
            using var db = await _dbf.CreateDbContextAsync(ct);

            using var rootDoc = JsonDocument.Parse(model.OriginalConfig);
            if (!rootDoc.RootElement.TryGetProperty("config", out var config) &&
                !rootDoc.RootElement.TryGetProperty("data", out config))
                return false;

            var host = await db.ProxmoxHosts.FirstOrDefaultAsync(h => h.HostAddress == hostAddress, ct);
            if (host?.Hostname is null) return false;
            var nodeName = host.Hostname;

            var cluster = await db.ProxmoxClusters.Include(c => c.Hosts).FirstOrDefaultAsync(ct);
            if (cluster == null) return false;

            var anyQueryable = (await _helpers.GetQueryableHostsAsync(cluster, ct)).Any();
            var anyConfigured = cluster.Hosts?.Any() ?? false;

            if (!anyQueryable && !anyConfigured) return false;


            var vmid = int.Parse(model.VmId).ToString();
            var payload = _helpers.FlattenConfig(config);

            if (model.StartDisconnected)
            {
                foreach (var netKey in payload.Keys.Where(k => Regex.IsMatch(k, @"^net\d+$", RegexOptions.IgnoreCase)).ToList())
                {
                    var def = payload[netKey];
                    payload[netKey] = Regex.IsMatch(def, @"\blink_down=\d")
                        ? Regex.Replace(def, @"\blink_down=\d", "link_down=1")
                        : def + ",link_down=1";
                }
            }

            // Detect old storage
            string? oldStorageName = null;
            var diskKeys = payload.Keys
                .Where(k => Regex.IsMatch(k, @"^(scsi|virtio|ide|sata|efidisk|tpmstate)\d+$", RegexOptions.IgnoreCase))
                .ToList();

            foreach (var key in diskKeys)
            {
                var val = payload[key];
                if (!string.IsNullOrEmpty(val) && val.Contains(":"))
                {
                    oldStorageName = val.Split(':', 2)[0].Trim();
                    break;
                }
            }
            if (string.IsNullOrEmpty(oldStorageName))
                throw new InvalidOperationException("Failed to determine oldStorageName from config.");

            payload.Remove("meta");
            payload.Remove("digest");
            payload["protection"] = "0";
            payload["storage"] = cloneStorageName;

            // Remap disk paths in main config
            foreach (var diskKey in diskKeys)
            {
                var diskVal = payload[diskKey];
                if (!diskVal.Contains(":")) continue;

                // keep filename/options
                var parts = diskVal.Split(new[] { ':' }, 2);
                var diskDef = parts[1];
                var sub = diskDef.Split(new[] { ',' }, 2);
                var filenameWithExt = sub[0];
                var options = sub.Length > 1 ? "," + sub[1] : string.Empty;

                payload[diskKey] = $"{cloneStorageName}:{filenameWithExt}{options}";
            }

            // Global replace
            foreach (var key in payload.Keys.ToList())
            {
                var val = payload[key];
                if (!string.IsNullOrEmpty(val))
                    payload[key] = val.Replace(oldStorageName, cloneStorageName, StringComparison.OrdinalIgnoreCase);
            }

            if (payload.ContainsKey("vmstate"))
            {
                payload["vmstate"] = RemapStorageAndVmid(payload["vmstate"], oldStorageName!, cloneStorageName, vmid, vmid);
            }

            // Build .conf
            var sb = new StringBuilder();
            foreach (var kv in payload) sb.AppendLine($"{kv.Key}: {kv.Value}");

            // Snapshots
            if (rootDoc.RootElement.TryGetProperty("snapshots", out var snapElem) &&
                snapElem.ValueKind == JsonValueKind.Object)
            {
                foreach (var snapProp in snapElem.EnumerateObject())
                {
                    sb.AppendLine($"[{snapProp.Name}]");

                    var snapDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var snapLine in snapProp.Value.EnumerateObject())
                        snapDict[snapLine.Name] = snapLine.Value.GetString() ?? "";

                    if (model.StartDisconnected)
                    {
                        foreach (var netKey in snapDict.Keys.Where(k => Regex.IsMatch(k, @"^net\d+$", RegexOptions.IgnoreCase)).ToList())
                        {
                            var def = snapDict[netKey];
                            snapDict[netKey] = Regex.IsMatch(def, @"\blink_down=\d")
                                ? Regex.Replace(def, @"\blink_down=\d", "link_down=1")
                                : def + ",link_down=1";
                        }
                    }

                    var snapDiskKeys = snapDict.Keys
                        .Where(k => Regex.IsMatch(k, @"^(scsi|virtio|ide|sata|efidisk|tpmstate)\d+$", RegexOptions.IgnoreCase))
                        .ToList();

                    foreach (var diskKey in snapDiskKeys)
                    {
                        var diskVal = snapDict[diskKey];
                        if (!diskVal.Contains(":")) continue;

                        // skip plain CD-ROMs unless cloud-init
                        if (diskVal.Contains("media=cdrom", StringComparison.OrdinalIgnoreCase) &&
                            !diskVal.Contains("cloudinit", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var parts = diskVal.Split(new[] { ':' }, 2);
                        var diskDef = parts[1];
                        var sub = diskDef.Split(new[] { ',' }, 2);
                        var filenameWithExt = sub[0];
                        var options = sub.Length > 1 ? "," + sub[1] : string.Empty;

                        snapDict[diskKey] = $"{cloneStorageName}:{filenameWithExt}{options}";
                    }

                    foreach (var key in snapDict.Keys.ToList())
                    {
                        var val = snapDict[key];
                        if (!string.IsNullOrEmpty(val))
                            snapDict[key] = val.Replace(oldStorageName, cloneStorageName, StringComparison.OrdinalIgnoreCase);
                    }

                    if (snapDict.TryGetValue("vmstate", out var vmstateValue))
                    {
                        snapDict["vmstate"] = RemapStorageAndVmid(vmstateValue, oldStorageName!, cloneStorageName, vmid, vmid);
                    }

                    foreach (var snapKvp in snapDict)
                        sb.AppendLine($"{snapKvp.Key}: {snapKvp.Value}");
                }
            }

            // Upload config
            var sshUser = cluster.Username.Split('@')[0];
            var sshPass = _enc.Decrypt(cluster.PasswordHash);
            var configPath = $"/etc/pve/qemu-server/{vmid}.conf";
            var configContent = sb.ToString();

            var eofMarker = "EOF_" + Guid.NewGuid().ToString("N");
            var sshCmd = $"cat > {configPath} <<'{eofMarker}'\n{configContent}\n{eofMarker}\n";

            using (var ssh = new Renci.SshNet.SshClient(hostAddress, sshUser, sshPass))
            {
                ssh.Connect();
                using (var cmd = ssh.CreateCommand(sshCmd))
                {
                    var _ = cmd.Execute();
                    if (cmd.ExitStatus != 0) { ssh.Disconnect(); return false; }
                }
                ssh.Disconnect();
            }

            // Post-restore repair/rollback/delete (best-effort)
            try
            {
                var vmidInt = int.Parse(vmid);
                var snaps = await _snaps.GetSnapshotListAsync(cluster, nodeName, hostAddress, vmidInt, ct);
                if (snaps != null && snaps.Count > 0)
                {
                    var bareproxSnap = snaps
                        .Where(s => !string.IsNullOrWhiteSpace(s.Name) && s.Name.StartsWith("BareProx-", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(s => s.Snaptime)
                        .FirstOrDefault();

                    var newestNonCurrent = snaps
                        .Where(s => !string.Equals(s.Name, "current", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(s => s.Snaptime)
                        .FirstOrDefault();

                    if (newestNonCurrent != null)
                    {
                        try
                        {
                            // UPDATED: use the new snap-chains service
                            await _snapChains.RepairExternalSnapshotChainAsync(nodeName, cloneStorageName, vmidInt, ct);
                            _log.LogInformation("Repaired external snapshot chain for VMID {Vmid}.", vmidInt);
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "Snapshot chain repair failed for VMID {Vmid}; continuing.", vmidInt);
                        }
                    }

                    if (model.RollbackSnapshot)
                    {
                        var targetSnap = bareproxSnap ?? newestNonCurrent;
                        if (targetSnap != null)
                        {
                            try
                            {
                                var ok = await _snaps.RollbackSnapshotAsync(
                                    cluster, nodeName, hostAddress, vmidInt, targetSnap.Name, false, _log, ct);

                                if (!ok)
                                    _log.LogWarning("Rollback task for '{Snap}' on VMID {Vmid} did not complete OK.", targetSnap.Name, vmidInt);
                            }
                            catch (Exception ex)
                            {
                                _log.LogWarning(ex, "Rollback call failed for '{Snap}' on VMID {Vmid}. Will still attempt delete.", targetSnap.Name, vmidInt);
                            }

                            try
                            {
                                await _snaps.DeleteSnapshotAsync(cluster, nodeName, hostAddress, vmidInt, targetSnap.Name, ct);
                                _log.LogInformation("Deleted snapshot '{Snap}' after rollback on VMID {Vmid}.", targetSnap.Name, vmidInt);
                            }
                            catch (Exception ex)
                            {
                                _log.LogWarning(ex, "Failed to delete snapshot '{Snap}' after rollback on VMID {Vmid}.", targetSnap.Name, vmidInt);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Post-restore snapshot handling (repair/rollback/delete) skipped due to error.");
            }

            return true;
        }

        // ----- helpers -----

        private static string RemapStorageAndVmid(
            string input,
            string oldStorage,
            string newStorage,
            string oldVmid,
            string newVmid)
        {
            if (string.IsNullOrEmpty(input)) return input;
            var updated = input;

            if (!string.IsNullOrEmpty(oldStorage) && !string.IsNullOrEmpty(newStorage))
            {
                var storagePrefix = new Regex(@"(?i)(?<![A-Za-z0-9_])" + Regex.Escape(oldStorage) + @"(?=:)",
                    RegexOptions.CultureInvariant);
                updated = storagePrefix.Replace(updated, newStorage);
            }

            if (!string.IsNullOrEmpty(oldVmid) && !string.IsNullOrEmpty(newVmid))
            {
                var vmidSegment = new Regex(@"(?:(?<=^)|(?<=:)|(?<=/)|(?<=\\))" + Regex.Escape(oldVmid) + @"(?=(/|\\))",
                    RegexOptions.CultureInvariant);
                updated = vmidSegment.Replace(updated, newVmid);

                var vmToken = new Regex(@"(?i)\bvm-" + Regex.Escape(oldVmid) + "-",
                    RegexOptions.CultureInvariant);
                updated = vmToken.Replace(updated, $"vm-{newVmid}-");
            }

            return updated;
        }
    }
}
