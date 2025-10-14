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
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BareProx.Data;
using BareProx.Models;
using Microsoft.Extensions.Logging;

namespace BareProx.Services.Migration
{
    /// <summary>
    /// Orchestrates preparing a Proxmox VM from VMware-sourced artifacts (VMDK descriptors).
    /// Pipeline:
    ///  1) Validate input (VMID/Name) + ensure VMID is free on PVE.
    ///  2) For each disk: copy descriptor into {storage}/images/{vmid}/, rewrite to <c>monolithicFlat</c>
    ///     and point the RW line at the absolute <c>-flat.vmdk</c> path (POSIX).
    ///  3) Generate a minimal but bootable QEMU config (ide2: cdrom; q35; agent; vga; smbios uuid if set).
    ///  4) Optional: add 1GiB dummy VirtIO disk for guest driver prep.
    ///  5) Optional: mount VirtIO drivers ISO.
    ///  6) If UEFI: ensure an EFI variables disk (<c>efidisk0</c>) exists (idempotent).
    /// 
    /// Notes:
    ///  - We only copy/modify the descriptor, not the <c>-flat.vmdk</c> payload.
    ///  - Paths are normalized to POSIX to match PVE host expectations.
    ///  - Steps are logged to DB and to ILogger; failures fail-fast with reason.
    /// </summary>
    public sealed class ProxmoxMigrationExecutor : IMigrationExecutor
    {
        /// <summary>Fallback storage used if none was provided on a disk.</summary>
        private const string DefaultStorage = "vm_migration";

        /// <summary>JSON options for forgiving, case-insensitive DTO deserialization.</summary>
        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        // ----------- Compiled regexes (hot paths) -----------
        /// <summary>
        /// Matches the RW line of a VMDK descriptor (e.g. "RW 123456 FLAT "path" 0") so we can rewrite it.
        /// </summary>
        private static readonly Regex ReRwFlatLine = new(
            @"^(?<p>\s*RW\s+)(?<sectors>\d+)\s+(?<kind>\S+)\s+""[^""]+""(?:\s+\d+)?\s*$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>
        /// Finds the <c>createType="..."</c> field so we can force <c>createType="monolithicFlat"</c>.
        /// </summary>
        private static readonly Regex ReCreateType = new(
            @"createType\s*=\s*""[^""]+""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Ensures a minimal cdrom stub (ide2: none,media=cdrom) exists in the base config.</summary>
        private static readonly Regex ReCdromNone = new(
            @"^\s*ide2:\s*none,media=cdrom\s*$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>Verifies cdrom is present after mounting an ISO (any path is ok, media=cdrom must exist).</summary>
        private static readonly Regex ReCdromPresent = new(
            @"^\s*ide2:\s*.+,media=cdrom",
            RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>Detects <c>efidisk0:</c> in config to keep the EFI vars disk add idempotent.</summary>
        private static readonly Regex ReEfidisk = new(
            @"^\s*efidisk0:\s*",
            RegexOptions.Multiline | RegexOptions.Compiled);

        private readonly ApplicationDbContext _db;
        private readonly ProxmoxService _pve;
        private readonly ILogger<ProxmoxMigrationExecutor> _logger;

        public ProxmoxMigrationExecutor(
            ApplicationDbContext db,
            ProxmoxService pve,
            ILogger<ProxmoxMigrationExecutor> logger)
        {
            _db = db;
            _pve = pve;
            _logger = logger;
        }

        /// <summary>
        /// Executes the full migration preparation pipeline for one queue item.
        /// </summary>
        public async Task ExecuteAsync(MigrationQueueItem item, CancellationToken ct)
        {
            // Parse once; avoid repeated deserialization.
            var disks = Deserialize<List<DiskSpec>>(item.DisksJson) ?? new();
            var nics = Deserialize<List<NicSpec>>(item.NicsJson) ?? new();

            // ---- 1) Validate input & VMID availability
            await Step(item, "Validate", async () =>
            {
                if (item.VmId is null or <= 0)
                    throw new InvalidOperationException("VMID missing.");
                if (string.IsNullOrWhiteSpace(item.Name))
                    throw new InvalidOperationException("Name missing.");
                if (disks.Count == 0)
                    _logger.LogWarning("No disks defined for item {Id}", item.Id);
                await Task.CompletedTask;
            });

            var vmid = item.VmId!.Value;

            await Step(item, "CheckVmid", async () =>
            {
                var free = await _pve.IsVmidAvailableAsync(vmid, ct);
                if (!free) throw new InvalidOperationException($"VMID {vmid} is already in use.");
            });

            // ---- 2) For each disk, copy & rewrite descriptor
            for (int i = 0; i < disks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var d = disks[i];

                await Step(item, $"PlaceDescriptor[{i}]", async () =>
                {
                    if (string.IsNullOrWhiteSpace(d.Source))
                        throw new InvalidOperationException($"Disk[{i}] Source missing.");
                    if (string.IsNullOrWhiteSpace(d.Storage))
                        throw new InvalidOperationException($"Disk[{i}] Storage missing.");

                    // Destination is the standard PVE layout: /mnt/pve/<storage>/images/<vmid>/
                    var baseDir = $"/mnt/pve/{d.Storage}/images/{vmid}";
                    await _pve.EnsureDirectoryAsync(baseDir, ct);

                    // Keep the original descriptor filename but drop it under the VM’s image folder.
                    var destDesc = $"{baseDir}/{FileNamePosix(d.Source!)}";

                    // VMware descriptors often reference relative "-flat.vmdk" files;
                    // on PVE we force an absolute POSIX path to the flat backing file.
                    var absFlat = AbsoluteFlatPath(d.Source!);

                    var original = await _pve.ReadTextFileAsync(d.Source!, ct);
                    var rewritten = RewriteVmdkDescriptor(original, absFlat);
                    await _pve.WriteTextFileAsync(destDesc, rewritten, ct);

                    // Quick integrity check: must be monolithicFlat and the RW line rewritten to FLAT "path" 0
                    var verify = await _pve.ReadTextFileAsync(destDesc, ct);
                    var ok = verify.Contains("createType=\"monolithicFlat\"", StringComparison.OrdinalIgnoreCase)
                             && ReRwFlatLine.IsMatch(verify);
                    if (!ok) throw new InvalidOperationException("Descriptor copy/rewrite verification failed.");
                });
            }

            // ---- 3) Write base QEMU config
            await Step(item, "WriteConf", async () =>
            {
                var confPath = $"/etc/pve/qemu-server/{vmid}.conf";
                var conf = BuildQemuConf(item, disks, nics);
                await _pve.WriteTextFileAsync(confPath, conf, ct);

                // Sanity checks: name persisted and cdrom stub present.
                var back = await _pve.ReadTextFileAsync(confPath, ct);
                if (!back.Contains($"name: {item.Name}", StringComparison.Ordinal))
                    _logger.LogWarning("Config name differs/missing for vmid {Vmid}", vmid);
                if (!ReCdromNone.IsMatch(back))
                    throw new InvalidOperationException("CD-ROM (ide2) not present in config.");
            });

            // ---- 4) Optional: add dummy VirtIO disk (1 GiB), helpful to stage drivers in guest prior to real migration
            if (item.PrepareVirtio)
            {
                await Step(item, "AddDummyDisk", async () =>
                {
                    // Prefer first disk's storage to keep artifacts local; else fallback.
                    var storage = disks.FirstOrDefault()?.Storage ?? DefaultStorage;
                    var slot = await _pve.FirstFreeVirtioSlotAsync(vmid, ct) ?? 5;
                    await _pve.AddDummyDiskAsync(vmid, storage, slot, 1, ct);
                });
            }

            // ---- 5) Optional: mount VirtIO ISO (kept idempotent by validating config after call)
            if (item.MountVirtioIso && !string.IsNullOrWhiteSpace(item.VirtioIsoName))
            {
                await Step(item, "MountISO", async () =>
                {
                    var confPath = $"/etc/pve/qemu-server/{vmid}.conf";
                    await _pve.SetCdromAsync(vmid, item.VirtioIsoName!, ct);
                    var conf = await _pve.ReadTextFileAsync(confPath, ct);
                    if (!ReCdromPresent.IsMatch(conf))
                        throw new InvalidOperationException("CD-ROM not present after ISO mount.");
                });
            }

            // ---- 6) UEFI: ensure efidisk0 exists (idempotent)
            if (item.Uefi)
            {
                await Step(item, "AddEfiDisk", async () =>
                {
                    var confPath = $"/etc/pve/qemu-server/{vmid}.conf";
                    var conf = await _pve.ReadTextFileAsync(confPath, ct);

                    if (!ReEfidisk.IsMatch(conf))
                    {
                        var storage = disks.FirstOrDefault()?.Storage ?? DefaultStorage;

                        // Equivalent to: qm set {vmid} --efidisk0 {storage}:0
                        // If your PVE supports secure-boot defaults, you can add ",efitype=4m,pre-enrolled-keys=1".
                        await _pve.AddEfiDiskAsync(vmid, storage, ct);

                        // Verify efidisk line materialized
                        conf = await _pve.ReadTextFileAsync(confPath, ct);
                        if (!ReEfidisk.IsMatch(conf))
                            throw new InvalidOperationException("efidisk0 missing after add.");
                    }
                    else
                    {
                        _logger.LogInformation("efidisk0 already present for VMID {Vmid}", vmid);
                    }
                });
            }

            await Log(item, "Finalize", $"VM {vmid} prepared.", "Info");
        }

        // ---------------- Step wrapper & logging ----------------

        /// <summary>
        /// Executes a named step with uniform logging and error mapping.
        /// Writes a START/OK (or ERROR/CANCELED) log to DB+ILogger around the action.
        /// </summary>
        private async Task Step(MigrationQueueItem item, string step, Func<Task> action)
        {
            await Log(item, step, "START", "Info");
            try
            {
                await action();
                await Log(item, step, "OK", "Info");
            }
            catch (OperationCanceledException)
            {
                await Log(item, step, "CANCELED", "Warning");
                throw;
            }
            catch (ProxmoxService.ProxmoxSshException ex)
            {
                // Normalize SSH failures separately for easier triage.
                await Log(item, step, $"ERROR SSH: {ex.Message}", "Error");
                throw;
            }
            catch (Exception ex)
            {
                await Log(item, step, $"ERROR: {ex.Message}", "Error");
                throw;
            }
        }

        /// <summary>
        /// Persists a migration log entry and mirrors it to ILogger with matching level.
        /// </summary>
        private async Task Log(MigrationQueueItem item, string step, string message, string level)
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
            await _db.SaveChangesAsync();
        }

        // ---------------- Utilities ----------------

        /// <summary>
        /// Safe JSON deserialize with a permissive, case-insensitive option.
        /// Returns default(T) on any parse error.
        /// </summary>
        private static T? Deserialize<T>(string? json)
        {
            try { return JsonSerializer.Deserialize<T>(json ?? "[]", JsonOpts); }
            catch { return default; }
        }

        // ---- POSIX path helpers (PVE host expects forward slashes) ----

        private static string ToPosix(string p) => (p ?? "").Replace('\\', '/');

        /// <summary>Returns the directory of a path in POSIX form (never empty; "/" if none).</summary>
        private static string DirPosix(string p)
        {
            p = ToPosix(p);
            var i = p.LastIndexOf('/');
            if (i < 0) return "/";
            return i == 0 ? "/" : p[..i];
        }

        /// <summary>Returns the filename (last segment) of a path in POSIX form.</summary>
        private static string FileNamePosix(string p)
        {
            p = ToPosix(p);
            var i = p.LastIndexOf('/');
            return i < 0 ? p : p[(i + 1)..];
        }

        /// <summary>
        /// Computes the absolute path to the companion <c>-flat.vmdk</c> for a given descriptor path.
        /// (Descriptor basename without ".vmdk", appended "-flat.vmdk" in the same directory.)
        /// </summary>
        private static string AbsoluteFlatPath(string descriptorPath)
        {
            var dir = DirPosix(descriptorPath);
            var name = FileNamePosix(descriptorPath);
            if (name.EndsWith(".vmdk", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
            return $"{dir}/{name}-flat.vmdk";
        }

        /// <summary>
        /// Rewrites a VMDK descriptor to:
        ///  - force <c>createType="monolithicFlat"</c>
        ///  - rewrite the RW line to <c>FLAT "absFlat" 0</c> with a POSIX absolute path
        /// </summary>
        private static string RewriteVmdkDescriptor(string content, string absFlat)
        {
            var s = content ?? string.Empty;

            // Force monolithicFlat to avoid split/2gbsparse/etc. surprises
            s = ReCreateType.Replace(s, "createType=\"monolithicFlat\"");

            // Normalize the RW line to FLAT with our explicit absolute -flat path
            s = ReRwFlatLine.Replace(s, m =>
                $"{m.Groups["p"].Value}{m.Groups["sectors"].Value} FLAT \"{ToPosix(absFlat)}\" 0");

            return s;
        }

        /// <summary>Normalizes a UUID to hyphenated lowercase form; passes through unknown formats.</summary>
        private static string NormalizeUuid(string? uuid)
        {
            if (string.IsNullOrWhiteSpace(uuid)) return string.Empty;
            if (Guid.TryParse(uuid, out var g)) return g.ToString("D"); // hyphenated, lowercase
            var hex = Regex.Replace(uuid, @"[^A-Fa-f0-9]", "");
            return hex.Length == 32 && Guid.TryParseExact(hex, "N", out var g2)
                 ? g2.ToString("D")
                 : uuid;
        }

        /// <summary>Uppercases and colon-separates a MAC address if 12 hex chars are present.</summary>
        private static string NormalizeMac(string? mac)
        {
            if (string.IsNullOrWhiteSpace(mac)) return "";
            var hex = Regex.Replace(mac, @"[^A-Fa-f0-9]", "");
            if (hex.Length != 12) return mac.ToUpperInvariant();
            return string.Join(":", Enumerable.Range(0, 6)
                                              .Select(i => hex.Substring(i * 2, 2).ToUpperInvariant()));
        }

        /// <summary>Strips trailing parenthetical suffixes such as " (SDN)" from bridge names.</summary>
        private static string CleanBridge(string? b)
        {
            var s = (b ?? "").Trim();
            s = Regex.Replace(s, @"\s*\(.*?\)\s*$", ""); // e.g. "vmbr0 (SDN)" -> "vmbr0"
            return s;
        }

        /// <summary>
        /// Builds a minimal PVE QEMU config:
        ///  - q35, seabios/ovmf, agent, std vga, ide2 cdrom stub
        ///  - smbios uuid if provided
        ///  - sockets/cores from explicit pair or fallback to vCPU count
        ///  - optional SCSI controller
        ///  - disks mapped with iothread for scsi/virtio, discard/ssd hints
        ///  - NICs with normalized MAC/bridge and firewall=1
        /// </summary>
        private static string BuildQemuConf(MigrationQueueItem item, List<DiskSpec> disks, List<NicSpec> nics)
        {
            var sb = new StringBuilder();
            var vmid = item.VmId!.Value;

            sb.AppendLine($"name: {item.Name}");
            sb.AppendLine("machine: q35");
            sb.AppendLine($"bios: {(item.Uefi ? "ovmf" : "seabios")}");
            sb.AppendLine("agent: 1");
            sb.AppendLine("vga: std");
            sb.AppendLine("ide2: none,media=cdrom");

            // UUID (smbios1)
            if (!string.IsNullOrWhiteSpace(item.Uuid))
            {
                var uuid = NormalizeUuid(item.Uuid);
                if (!string.IsNullOrEmpty(uuid)) sb.AppendLine($"smbios1: uuid={uuid}");
            }

            // CPU model (optional)
            if (!string.IsNullOrWhiteSpace(item.CpuType))
                sb.AppendLine($"cpu: {item.CpuType}");

            // Memory / CPU topology
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

            // SCSI controller (prefer virtio-scsi-single if any SCSI or VirtIO prep is requested)
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

            // Disks: keep bus/index; map to <storage>:<vmid>/<descriptor>; add iothread for scsi/virtio
            string? firstBoot = null;
            foreach (var d in disks.OrderBy(d => d.Index ?? 0))
            {
                var bus = (d.Bus ?? "sata").ToLowerInvariant();
                var idx = d.Index ?? 0;
                var storage = string.IsNullOrWhiteSpace(d.Storage) ? DefaultStorage : d.Storage!;
                var desc = FileNamePosix(d.Source ?? "");

                var useIoThread = bus is "scsi" or "virtio";
                var opts = (useIoThread ? "iothread=1," : "") + "discard=on,ssd=1";

                sb.AppendLine($"{bus}{idx}: {storage}:{vmid}/{desc},{opts}");
                firstBoot ??= $"{bus}{idx}";
            }
            if (firstBoot != null)
                sb.AppendLine($"boot: order={firstBoot}");

            // NICs: normalize MAC, strip SDN suffixes, keep VLAN tag, always enable firewall
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

        /// <summary>
        /// Attempts to read an integer-like property by any of the given names on the supplied object.
        /// Useful for coping with multiple DTO flavors.
        /// </summary>
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
                catch { /* ignore non-numeric values */ }
            }
            return null;
        }

        // ---------------- Internal DTOs ----------------

        /// <summary>
        /// Minimal disk specification for migration:
        ///  - <see cref="Source"/>: path to the VMDK descriptor to copy/transform
        ///  - <see cref="Storage"/>: PVE storage ID where the descriptor should live
        ///  - <see cref="Bus"/>: qemu bus (sata/scsi/virtio), defaults to sata if empty
        ///  - <see cref="Index"/>: device index on the chosen bus
        /// </summary>
        private sealed class DiskSpec
        {
            public string? Source { get; set; }
            public string? Storage { get; set; }
            public string? Bus { get; set; }
            public int? Index { get; set; }
        }

        /// <summary>
        /// Minimal NIC specification for migration:
        ///  - <see cref="Model"/>: qemu model (virtio/e1000e/virtio-net-pci/etc.), defaults to virtio
        ///  - <see cref="Mac"/>: MAC address, normalized to uppercase colon-separated if valid
        ///  - <see cref="Bridge"/>: PVE bridge name; trailing " (SDN)" is stripped for safety
        ///  - <see cref="Vlan"/>: optional VLAN tag (PVE "tag=")
        /// </summary>
        private sealed class NicSpec
        {
            public string? Model { get; set; }
            public string? Mac { get; set; }
            public string? Bridge { get; set; }
            public int? Vlan { get; set; }
        }
    }
}
