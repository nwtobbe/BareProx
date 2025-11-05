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
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BareProx.Data;
using BareProx.Models;
using BareProx.Services.Proxmox.Migration; // IProxmoxMigration (node-aware)
using BareProx.Services.Helpers;           // ProxmoxSshException
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BareProx.Services.Migration
{
    /// <summary>
    /// Orchestrates preparing a Proxmox VM from VMware-sourced artifacts (VMDK descriptors).
    /// Always executes on the node selected in MigrationSelections and uses its default storage.
    /// </summary>
    public sealed class ProxmoxMigrationExecutor : IMigrationExecutor
    {
        private const string HardcodedFallbackStorage = "vm_migration";
        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        // ----------- Compiled regexes (hot paths) -----------
        private static readonly Regex ReRwFlatLine = new(
            @"^(?<p>\s*RW\s+)(?<sectors>\d+)\s+(?<kind>\S+)\s+""[^""]+""(?:\s+\d+)?\s*$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex ReCreateType = new(
            @"createType\s*=\s*""[^""]+""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReCdromNone = new(
            @"^\s*ide2:\s*none,media=cdrom\s*$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex ReCdromPresent = new(
            @"^\s*ide2:\s*.+,media=cdrom",
            RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex ReEfidisk = new(
            @"^\s*efidisk0:\s*",
            RegexOptions.Multiline | RegexOptions.Compiled);

        private readonly ApplicationDbContext _db;
        private readonly IProxmoxMigration _pve;
        private readonly ILogger<ProxmoxMigrationExecutor> _logger;

        public ProxmoxMigrationExecutor(
            ApplicationDbContext db,
            IProxmoxMigration pve,
            ILogger<ProxmoxMigrationExecutor> logger)
        {
            _db = db;
            _pve = pve;
            _logger = logger;
        }

        /// <summary>
        /// Executes the full migration preparation pipeline for one queue item.
        /// Node and default storage come from MigrationSelections:
        ///   - Node: ProxmoxHosts[ProxmoxHostId].Hostname ?? HostAddress
        ///   - Default storage: MigrationSelections.StorageIdentifier (fallback to "vm_migration")
        /// </summary>
        public async Task ExecuteAsync(MigrationQueueItem item, CancellationToken ct)
        {
            // Resolve the concrete node and default storage we will act on
            var (node, defaultStorage) = await ResolveNodeAndDefaultAsync(ct);

            var disks = Deserialize<List<DiskSpec>>(item.DisksJson) ?? new();
            var nics = Deserialize<List<NicSpec>>(item.NicsJson) ?? new();

            // ---- 1) Validate input & VMID availability (on target node)
            await Step(item, "Validate", async () =>
            {
                if (item.VmId is null or <= 0) throw new InvalidOperationException("VMID missing.");
                if (string.IsNullOrWhiteSpace(item.Name)) throw new InvalidOperationException("Name missing.");
                if (disks.Count == 0) _logger.LogWarning("No disks defined for item {Id}", item.Id);
                await Task.CompletedTask;
            });

            var vmid = item.VmId!.Value;

            await Step(item, "CheckVmid", async () =>
            {
                var free = await _pve.IsVmidAvailableAsync(node, vmid, ct);
                if (!free) throw new InvalidOperationException($"VMID {vmid} is already in use on node {node}.");
            });

            // ---- 2) Copy & rewrite descriptors (on target node)
            for (int i = 0; i < disks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var d = disks[i];

                await Step(item, $"PlaceDescriptor[{i}]", async () =>
                {
                    if (string.IsNullOrWhiteSpace(d.Source))
                        throw new InvalidOperationException($"Disk[{i}] Source missing.");
                    if (string.IsNullOrWhiteSpace(d.Storage))
                        d.Storage = defaultStorage; // respect MigrationSelections storage

                    var baseDir = $"/mnt/pve/{d.Storage}/images/{vmid}";
                    _logger.LogInformation("Using node '{Node}' for storage '{Storage}' base '{BaseDir}'", node, d.Storage, baseDir);

                    await _pve.EnsureDirectoryAsync(node, baseDir, ct);

                    var destDesc = $"{baseDir}/{FileNamePosix(d.Source!)}";
                    var absFlat = AbsoluteFlatPath(d.Source!);

                    var original = await _pve.ReadTextFileAsync(node, d.Source!, ct);
                    var rewritten = RewriteVmdkDescriptor(original, absFlat);
                    await _pve.WriteTextFileAsync(node, destDesc, rewritten, ct);

                    var verify = await _pve.ReadTextFileAsync(node, destDesc, ct);
                    var ok = verify.Contains("createType=\"monolithicFlat\"", StringComparison.OrdinalIgnoreCase)
                             && ReRwFlatLine.IsMatch(verify);
                    if (!ok) throw new InvalidOperationException("Descriptor copy/rewrite verification failed.");
                });
            }

            // ---- 3) Write base QEMU config (on target node)
            await Step(item, "WriteConf", async () =>
            {
                var confPath = $"/etc/pve/qemu-server/{vmid}.conf";
                var conf = BuildQemuConf(item, disks, nics, defaultStorage);
                _logger.LogInformation("Writing config for VMID {Vmid} on node '{Node}' → {Path}", vmid, node, confPath);

                await _pve.WriteTextFileAsync(node, confPath, conf, ct);

                var back = await _pve.ReadTextFileAsync(node, confPath, ct);
                if (!back.Contains($"name: {item.Name}", StringComparison.Ordinal))
                    _logger.LogWarning("Config name differs/missing for vmid {Vmid}", vmid);
                if (!ReCdromNone.IsMatch(back))
                    throw new InvalidOperationException("CD-ROM (ide2) not present in config.");
            });

            // ---- 4) Optional: add dummy VirtIO disk (on target node)
            if (item.PrepareVirtio)
            {
                await Step(item, "AddDummyDisk", async () =>
                {
                    var storage = disks.FirstOrDefault()?.Storage ?? defaultStorage;
                    var slot = await _pve.FirstFreeVirtioSlotAsync(node, vmid, ct) ?? 5;
                    await _pve.AddDummyDiskAsync(node, vmid, storage, slot, 1, ct);
                });
            }

            // ---- 5) Optional: mount VirtIO ISO (on target node)
            if (item.MountVirtioIso && !string.IsNullOrWhiteSpace(item.VirtioIsoName))
            {
                await Step(item, "MountISO", async () =>
                {
                    var confPath = $"/etc/pve/qemu-server/{vmid}.conf";
                    await _pve.SetCdromAsync(node, vmid, item.VirtioIsoName!, ct);
                    var conf = await _pve.ReadTextFileAsync(node, confPath, ct);
                    if (!ReCdromPresent.IsMatch(conf))
                        throw new InvalidOperationException("CD-ROM not present after ISO mount.");
                });
            }

            // ---- 6) UEFI: ensure efidisk0 exists (idempotent, on target node)
            if (item.Uefi)
            {
                await Step(item, "AddEfiDisk", async () =>
                {
                    var confPath = $"/etc/pve/qemu-server/{vmid}.conf";
                    var conf = await _pve.ReadTextFileAsync(node, confPath, ct);

                    if (!ReEfidisk.IsMatch(conf))
                    {
                        var storage = disks.FirstOrDefault()?.Storage ?? defaultStorage;
                        await _pve.AddEfiDiskAsync(node, vmid, storage, ct);

                        conf = await _pve.ReadTextFileAsync(node, confPath, ct);
                        if (!ReEfidisk.IsMatch(conf))
                            throw new InvalidOperationException("efidisk0 missing after add.");
                    }
                    else
                    {
                        _logger.LogInformation("efidisk0 already present for VMID {Vmid}", vmid);
                    }
                });
            }

            await Log(item, "Finalize",
                $"VM {vmid} prepared on node {node} (default storage: {defaultStorage}).",
                "Info", ct);
        }

        // --------------------------------------------------------------------
        // Resolve node + default storage from MigrationSelections
        // --------------------------------------------------------------------
        private async Task<(string node, string defaultStorage)> ResolveNodeAndDefaultAsync(CancellationToken ct)
        {
            var sel = await _db.MigrationSelections.AsNoTracking().FirstOrDefaultAsync(ct)
                      ?? throw new InvalidOperationException("No MigrationSelection found; configure Migration → Settings first.");

            var host = await _db.ProxmoxHosts.AsNoTracking()
                          .FirstOrDefaultAsync(h => h.Id == sel.ProxmoxHostId, ct)
                       ?? throw new InvalidOperationException($"Selected ProxmoxHostId {sel.ProxmoxHostId} not found.");

            var node = !string.IsNullOrWhiteSpace(host.Hostname) ? host.Hostname! :
                       !string.IsNullOrWhiteSpace(host.HostAddress) ? host.HostAddress! :
                       throw new InvalidOperationException("Selected host has neither Hostname nor HostAddress.");

            var storage = string.IsNullOrWhiteSpace(sel.StorageIdentifier)
                ? HardcodedFallbackStorage
                : sel.StorageIdentifier!;

            _logger.LogInformation("Resolved MigrationSelections → node '{Node}', default storage '{Storage}'", node, storage);
            return (node, storage);
        }

        // ---------------- Step wrapper & logging ----------------

        private async Task Step(MigrationQueueItem item, string step, Func<Task> action)
        {
            await Log(item, step, "START", "Info", CancellationToken.None);
            try
            {
                await action();
                await Log(item, step, "OK", "Info", CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                await Log(item, step, "CANCELED", "Warning", CancellationToken.None);
                throw;
            }
            catch (ProxmoxSshException ex)
            {
                await Log(item, step, $"ERROR SSH: {ex.Message}", "Error", CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                await Log(item, step, $"ERROR: {ex.Message}", "Error", CancellationToken.None);
                throw;
            }
        }

        private async Task Log(MigrationQueueItem item, string step, string message, string level, CancellationToken ct)
        {
            var lvl = level.Equals("Error", StringComparison.OrdinalIgnoreCase) ? LogLevel.Error
                    : level.Equals("Warning", StringComparison.OrdinalIgnoreCase) ? LogLevel.Warning
                    : LogLevel.Information;

            _logger.Log(lvl, "[{Step}] {Msg} (item:{Id})", step, message, item.Id);

            _db.MigrationQueueLogs.Add(new MigrationQueueLog
            {
                ItemId = item.Id,
                Step = step,
                Level = level,
                Message = message,
                Utc = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }

        // ---------------- Utilities ----------------

        private static T? Deserialize<T>(string? json)
        {
            try { return JsonSerializer.Deserialize<T>(json ?? "[]", JsonOpts); }
            catch { return default; }
        }

        private static string ToPosix(string p) => (p ?? "").Replace('\\', '/');

        private static string DirPosix(string p)
        {
            p = ToPosix(p);
            var i = p.LastIndexOf('/');
            if (i < 0) return "/";
            return i == 0 ? "/" : p[..i];
        }

        private static string FileNamePosix(string p)
        {
            p = ToPosix(p);
            var i = p.LastIndexOf('/');
            return i < 0 ? p : p[(i + 1)..];
        }

        private static string AbsoluteFlatPath(string descriptorPath)
        {
            var dir = DirPosix(descriptorPath);
            var name = FileNamePosix(descriptorPath);
            if (name.EndsWith(".vmdk", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
            return $"{dir}/{name}-flat.vmdk";
        }

        private static string RewriteVmdkDescriptor(string content, string absFlat)
        {
            var s = content ?? string.Empty;
            s = ReCreateType.Replace(s, "createType=\"monolithicFlat\"");
            s = ReRwFlatLine.Replace(s, m =>
                $"{m.Groups["p"].Value}{m.Groups["sectors"].Value} FLAT \"{ToPosix(absFlat)}\" 0");
            return s;
        }

        private static string NormalizeUuid(string? uuid)
        {
            if (string.IsNullOrWhiteSpace(uuid)) return string.Empty;
            if (Guid.TryParse(uuid, out var g)) return g.ToString("D");
            var hex = Regex.Replace(uuid, @"[^A-Fa-f0-9]", "");
            return hex.Length == 32 && Guid.TryParseExact(hex, "N", out var g2)
                 ? g2.ToString("D")
                 : uuid;
        }

        private static string NormalizeMac(string? mac)
        {
            if (string.IsNullOrWhiteSpace(mac)) return "";
            var hex = Regex.Replace(mac, @"[^A-Fa-f0-9]", "");
            if (hex.Length != 12) return mac.ToUpperInvariant();
            return string.Join(":", Enumerable.Range(0, 6)
                                              .Select(i => hex.Substring(i * 2, 2).ToUpperInvariant()));
        }

        private static string CleanBridge(string? b)
        {
            var s = (b ?? "").Trim();
            s = Regex.Replace(s, @"\s*\(.*?\)\s*$", "");
            return s;
        }

        private static string BuildQemuConf(MigrationQueueItem item, List<DiskSpec> disks, List<NicSpec> nics, string defaultStorage)
        {
            var sb = new StringBuilder();
            var vmid = item.VmId!.Value;

            sb.AppendLine($"name: {item.Name}");
            sb.AppendLine("machine: q35");
            sb.AppendLine($"bios: {(item.Uefi ? "ovmf" : "seabios")}");
            sb.AppendLine("agent: 1");
            sb.AppendLine("vga: std");
            sb.AppendLine("ide2: none,media=cdrom");

            if (!string.IsNullOrWhiteSpace(item.Uuid))
            {
                var uuid = NormalizeUuid(item.Uuid);
                if (!string.IsNullOrEmpty(uuid)) sb.AppendLine($"smbios1: uuid={uuid}");
            }

            if (!string.IsNullOrWhiteSpace(item.CpuType))
                sb.AppendLine($"cpu: {item.CpuType}");

            if (item.MemoryMiB is > 0)
                sb.AppendLine($"memory: {item.MemoryMiB}");

            var sockets = GetIntOpt(item, "Sockets", "CpuSockets");
            var cores = GetIntOpt(item, "Cores", "CpuCores");
            var vcpu = GetIntOpt(item, "Vcpu", "vCpu", "VCPU", "TotalCpu", "TotalVcpu");

            if (sockets is > 0 && cores is > 0)
            {
                sb.AppendLine($"sockets: {sockets}");
                sb.AppendLine($"cores: {cores}");
            }
            else if (vcpu is > 0)
            {
                sb.AppendLine("sockets: 1");
                sb.AppendLine($"cores: {vcpu}");
            }

            var scsihw = item.ScsiController;
            if (string.IsNullOrWhiteSpace(scsihw))
            {
                if (item.PrepareVirtio ||
                    disks.Any(d => string.Equals(d.Bus, "scsi", StringComparison.OrdinalIgnoreCase)))
                {
                    scsihw = "virtio-scsi-single";
                }
            }
            if (!string.IsNullOrWhiteSpace(scsihw))
                sb.AppendLine($"scsihw: {scsihw}");

            string? firstBoot = null;
            foreach (var d in disks.OrderBy(d => d.Index ?? 0))
            {
                var bus = (d.Bus ?? "sata").ToLowerInvariant();
                var idx = d.Index ?? 0;
                var storage = string.IsNullOrWhiteSpace(d.Storage) ? defaultStorage : d.Storage!;
                var desc = FileNamePosix(d.Source ?? "");

                var useIoThread = bus is "scsi" or "virtio";
                var opts = (useIoThread ? "iothread=1," : "") + "discard=on,ssd=1";

                sb.AppendLine($"{bus}{idx}: {storage}:{vmid}/{desc},{opts}");
                firstBoot ??= $"{bus}{idx}";
            }
            if (firstBoot != null)
                sb.AppendLine($"boot: order={firstBoot}");

            for (int i = 0; i < nics.Count; i++)
            {
                var n = nics[i];
                var parts = new List<string>();
                var model = string.IsNullOrWhiteSpace(n.Model) ? "virtio" : n.Model!;
                var mac = NormalizeMac(n.Mac);
                var bridge = CleanBridge(n.Bridge);

                parts.Add(string.IsNullOrWhiteSpace(mac) ? model : $"{model}={mac}");
                if (!string.IsNullOrWhiteSpace(bridge)) parts.Add($"bridge={bridge}");
                if (n.Vlan is > 0) parts.Add($"tag={n.Vlan}");
                parts.Add("firewall=1");

                sb.AppendLine($"net{i}: {string.Join(",", parts)}");
            }

            return sb.ToString();
        }

        private static int? GetIntOpt(object obj, params string[] propertyNames)
        {
            var t = obj.GetType();
            foreach (var name in propertyNames)
            {
                var p = t.GetProperty(name);
                if (p == null) continue;

                var v = p.GetValue(obj);
                if (v == null) continue;

                try { return Convert.ToInt32(v); }
                catch { }
            }
            return null;
        }

        // ---------------- Internal DTOs ----------------

        private sealed class DiskSpec
        {
            public string? Source { get; set; }
            public string? Storage { get; set; }
            public string? Bus { get; set; }
            public int? Index { get; set; }
        }

        private sealed class NicSpec
        {
            public string? Model { get; set; }
            public string? Mac { get; set; }
            public string? Bridge { get; set; }
            public int? Vlan { get; set; }
        }
    }
}
