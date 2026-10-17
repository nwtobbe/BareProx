/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 *
 * Copyright (C) 2025-2026 Tobias Modig
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

using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using BareProx.Data;
using BareProx.Models;
using Microsoft.EntityFrameworkCore;
using Renci.SshNet;

namespace BareProx.Services.Migration
{
    public interface IProxmoxFileScanner
    {
        Task<IReadOnlyList<VmxItemDto>> ScanForVmxAsync(int clusterId, int hostId, string storageIdentifier, CancellationToken ct = default);
    }

    public class ProxmoxFileScanner : IProxmoxFileScanner
    {
        private readonly ApplicationDbContext _db;
        private readonly IEncryptionService _enc;

        public ProxmoxFileScanner(ApplicationDbContext db, IEncryptionService enc)
        {
            _db = db;
            _enc = enc;
        }

        public async Task<IReadOnlyList<VmxItemDto>> ScanForVmxAsync(int clusterId, int hostId, string storageIdentifier, CancellationToken ct = default)
        {
            var host = await _db.ProxmoxHosts.AsNoTracking().FirstAsync(h => h.Id == hostId, ct);
            var cluster = await _db.ProxmoxClusters.AsNoTracking().FirstAsync(c => c.Id == clusterId, ct);

            // Always use HostAddress for SSH (never Hostname)
            var node = host.HostAddress ?? throw new InvalidOperationException("ProxmoxHost.HostAddress is null/empty.");

            // Normalize SSH username (strip realm like @pam/@pve if present)
            var usernameRaw = cluster.Username ?? throw new InvalidOperationException("ProxmoxCluster.Username is null/empty.");
            var username = usernameRaw.Split('@')[0];

            var password = _enc.Decrypt(cluster.PasswordHash) ?? string.Empty;
            var basePath = $"/mnt/pve/{storageIdentifier}";

            using var ssh = new SshClient(node, username, password);
            ssh.Connect();

            // Preflight: ensure base path exists
            var ok = RunText(ssh, $"test -d {Q(basePath)} && echo OK || echo NO").Trim();
            if (!string.Equals(ok, "OK", StringComparison.Ordinal))
            {
                ssh.Disconnect();
                return Array.Empty<VmxItemDto>();
            }

            // Follow symlinks and match case-insensitively
            var vmxPaths = RunLines(ssh, $"find -L {Q(basePath)} -type f -iname '*.vmx' -not -path '*/.snapshot/*'");
            var items = new List<VmxItemDto>();

            foreach (var vmxPath in vmxPaths)
            {
                if (string.IsNullOrWhiteSpace(vmxPath)) continue;
                ct.ThrowIfCancellationRequested();

                try
                {
                    var vmxText = RunText(ssh, $"cat {Q(vmxPath)}");
                    var dict = ParseVmx(vmxText);

                    var name = dict.TryGetValue("displayname", out var nm) ? nm : Path.GetFileNameWithoutExtension(vmxPath);

                    // guestOS (or guestos) → friendly
                    var guestRaw = dict.TryGetValue("guestOS", out var osCap) ? osCap :
                                   (dict.TryGetValue("guestos", out var osLow) ? osLow : "");
                    // VMware guestOS identifiers are not OS release years.
                    // e.g. windows2022srvNext-64 = Windows Server 2025.
                    var guestLabel = MapGuestOsLabel(guestRaw);
                    // Prefer a verified VMware Tools guest description over the configured VMX
                    // guestOS identifier. windows8srv-64 can describe either 2012 or 2012 R2.
                    // Never claim R2 solely from that identifier.
                    if (dict.TryGetValue("guestInfo.detailed.data", out var detailedData))
                    {
                        var detailOs = Regex.Match(detailedData,
                            @"Windows Server\s+(2012\s*R2|2012|2016|2019|2022|2025)\b",
                            RegexOptions.IgnoreCase);
                        if (detailOs.Success)
                        {
                            var release = Regex.Replace(detailOs.Groups[1].Value.Trim(), @"\s+", " ");
                            guestLabel = "Windows Server " + release.ToUpperInvariant();
                        }
                        else if (guestLabel == "Windows Server 2012 / 2012 R2" &&
                                 Regex.IsMatch(detailedData, @"(?:^|[;,\s])(?:version|build|osVersion)\s*=\s*[^;,\s]*6\.3\.9600\b", RegexOptions.IgnoreCase))
                        {
                            guestLabel = "Windows Server 2012 R2";
                        }
                    }

                    var cpu = TryInt(dict, "numvcpus");
                    var mem = TryInt(dict, "memsize");

                    // Disks
                    var disks = BuildDisks(dict, storageIdentifier, vmxPath);
                    long sumGiB = 0;
                    foreach (var d in disks.Where(d => !string.IsNullOrWhiteSpace(d.Source)))
                    {
                        var sizeGiB = GetVmdkSizeGiB(ssh, d.Source);
                        if (sizeGiB.HasValue && sizeGiB.Value > 0)
                        {
                            d.SizeGiB = sizeGiB.Value;
                            sumGiB += sizeGiB.Value;
                        }
                    }

                    // Independently inspect VMware snapshot artifacts over SSH (no vCenter).
                    var snapshot = InspectVmwareSnapshots(ssh, vmxPath, dict);

                    // NICs
                    var nics = BuildNics(dict);

                    // Extra metadata
                    var uuidBios = dict.TryGetValue("uuid.bios", out var ub) ? ub : null;
                    var vcUuid = dict.TryGetValue("vc.uuid", out var vu) ? vu : null;
                    var firmware = dict.TryGetValue("firmware", out var fw) ? fw : null; // "efi" or "bios"
                    var secureBoot = TryBool(dict, "uefi.secureBoot.enabled");
                    var tpm2 = TryBool(dict, "tpm2.present") ?? TryBool(dict, "tpm.present");
                    var nvram = dict.TryGetValue("nvram", out var nv) ? nv : null;
                    var diskUuid = TryBool(dict, "disk.EnableUUID");

                    var controllers = BuildControllers(dict);

                    items.Add(new VmxItemDto
                    {
                        Name = name,
                        VmxPath = vmxPath,
                        GuestOs = guestLabel,
                        SnapshotCheckPassed = snapshot.checkPassed,
                        HasVmwareSnapshots = snapshot.hasSnapshots,
                        SnapshotWarning = snapshot.warning,
                        GuestOsRaw = string.IsNullOrWhiteSpace(guestRaw) ? null : guestRaw,

                        CpuCores = cpu,
                        MemoryMiB = mem,
                        DiskSizeGiB = sumGiB > 0 ? (int)sumGiB : null,
                        Disks = disks,
                        Nics = nics,

                        UuidBios = uuidBios,
                        VcUuid = vcUuid,
                        Firmware = firmware,
                        SecureBoot = secureBoot,
                        Tpm2Present = tpm2,
                        NvramPath = nvram,
                        DiskEnableUuid = diskUuid,
                        Controllers = controllers,

                        Status = "Not queued"
                    });
                }
                catch
                {
                    // skip problematic entries; keep scanning
                }
            }

            ssh.Disconnect();
            return items;
        }

        // Checking file-system artifacts is deliberately conservative. A snapshot
        // can be represented by a delta VMDK, .vmsn, populated .vmsd or a VMX
        // disk reference to *-000001.vmdk. Never infer safety from a failed scan.
        private static (bool checkPassed, bool hasSnapshots, string? warning) InspectVmwareSnapshots(
            SshClient ssh, string vmxPath, Dictionary<string, string> vmx)
        {
            var vmDir = vmxPath[..vmxPath.LastIndexOf('/')];
            var reasons = new List<string>();
            var snapshotDisk = new Regex(@"-\d{6}(?:-delta|-sesparse)?\.vmdk$", RegexOptions.IgnoreCase);
            foreach (var item in vmx)
            {
                if (Regex.IsMatch(item.Key, @"^(scsi|sata|ide|nvme)\d+:\d+\.fileName$", RegexOptions.IgnoreCase) &&
                    snapshotDisk.IsMatch(item.Value ?? ""))
                    reasons.Add("A disk uses a VMware snapshot VMDK");
            }
            // One-level search prevents warnings from unrelated VMs/directories.
            // Explicit exit codes are critical: inaccessible directories are NOT 'snapshot free'.
            var command = "find " + Q(vmDir) + " -maxdepth 1 -type f " +
                "\\( -iname '*-00000*.vmdk' -o -iname '*-0000*.vmdk' " +
                "-o -iname '*-delta.vmdk' -o -iname '*-sesparse.vmdk' " +
                "-o -iname '*.vmsn' -o -iname '*.vmsd' \\) -print";
            using var cmd = ssh.CreateCommand(command);
            var output = cmd.Execute() ?? "";
            if (cmd.ExitStatus != 0)
                return (false, true, "Could not verify VMware snapshots on the datastore");
            var candidates = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var path in candidates)
            {
                if (!path.EndsWith(".vmsd", StringComparison.OrdinalIgnoreCase))
                {
                    reasons.Add("VMware snapshot/delta file found");
                    continue;
                }
                using var metadataCommand = ssh.CreateCommand("cat " + Q(path));
                var metadata = metadataCommand.Execute() ?? "";
                if (metadataCommand.ExitStatus != 0)
                    return (false, true, "Could not inspect VMware snapshot metadata (.vmsd)");
                // Empty VMSD files, or files recording zero snapshots, are not blockers.
                var countMatch = Regex.Match(metadata, @"(?im)^\s*snapshot\.numSnapshots\s*=\s*""?(\d+)");
                if (countMatch.Success && int.TryParse(countMatch.Groups[1].Value, out var count) && count > 0)
                    reasons.Add("VMware snapshot metadata reports " + count + " snapshots");
                else if (!countMatch.Success && Regex.IsMatch(metadata, @"(?im)^\s*snapshot\d+\."))
                    reasons.Add("VMware snapshot metadata contains snapshot entries");
            }
            return reasons.Count == 0
                ? (true, false, null)
                : (true, true, "VMware snapshot detected: " + string.Join("; ", reasons.Distinct()) + ". Consolidate/remove snapshots in VMware before queueing.");
        }

        // ---- helpers ----
        private static string Q(string p) => $"'{p.Replace("'", "'\"'\"'")}'";

        private static List<string> RunLines(SshClient ssh, string cmd)
        {
            using var c = ssh.CreateCommand(cmd);
            var s = c.Execute() ?? "";
            return s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        private static string RunText(SshClient ssh, string cmd)
        {
            using var c = ssh.CreateCommand(cmd);
            return c.Execute() ?? "";
        }

        private static Dictionary<string, string> ParseVmx(string text)
        {
            var rx = new Regex(@"^\s*([A-Za-z0-9\.\-:_]+)\s*=\s*""(.*)""\s*$");
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var sr = new StringReader(text);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                var m = rx.Match(line);
                if (m.Success) d[m.Groups[1].Value.Trim()] = m.Groups[2].Value.Trim();
            }
            return d;
        }

        private static int? TryInt(Dictionary<string, string> d, string key)
            => d.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : null;

        private static bool? TryBool(Dictionary<string, string> d, string key)
        {
            if (!d.TryGetValue(key, out var v)) return null;
            var x = v.Trim();
            if (x.Equals("TRUE", StringComparison.OrdinalIgnoreCase)) return true;
            if (x.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) return false;
            if (x == "1") return true;
            if (x == "0") return false;
            return null;
        }

        private static List<VmxDiskDto> BuildDisks(Dictionary<string, string> d, string storageIdentifier, string vmxPath)
        {
            var list = new List<VmxDiskDto>();
            var vmxDir = vmxPath[..vmxPath.LastIndexOf('/')];

            // scsi0:0.fileName, sata1:2.fileName, ide0:1.fileName, nvme0:0.fileName
            var rx = new Regex(@"^(?<prefix>(scsi|sata|ide|nvme)\d+:\d+)\.fileName$", RegexOptions.IgnoreCase);

            foreach (var kv in d)
            {
                var m = rx.Match(kv.Key);
                if (!m.Success) continue;

                var prefix = m.Groups["prefix"].Value; // e.g., scsi0:0

                // Require present when present flag exists
                if (d.TryGetValue($"{prefix}.present", out var presentStr) &&
                    !presentStr.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Skip non-hardDisk devices
                if (d.TryGetValue($"{prefix}.deviceType", out var devType) &&
                    !devType.Contains("hardDisk", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var file = (kv.Value ?? "").Trim();
                var lower = file.ToLowerInvariant();

                // Skip placeholders/empty backing
                if (string.IsNullOrEmpty(file) ||
                    lower == "emptybackingstring" ||
                    lower.EndsWith("/emptybackingstring"))
                {
                    continue;
                }

                // Only consider VMDK descriptors
                if (!lower.EndsWith(".vmdk"))
                    continue;

                var full = file.StartsWith("/", StringComparison.Ordinal) ? file : $"{vmxDir}/{file}";

                var bus = new string(prefix.TakeWhile(char.IsLetter).ToArray()); // scsi|sata|ide|nvme
                var index = prefix.Split(':').LastOrDefault() ?? "0";

                list.Add(new VmxDiskDto
                {
                    Source = full,
                    Storage = storageIdentifier,
                    Bus = bus,
                    Index = index
                });
            }
            return list;
        }

        private static List<VmxNicDto> BuildNics(Dictionary<string, string> d)
        {
            var idxs = new HashSet<int>();
            var rxIdx = new Regex(@"^ethernet(?<i>\d+)\.", RegexOptions.IgnoreCase);
            foreach (var k in d.Keys)
            {
                var m = rxIdx.Match(k);
                if (m.Success && int.TryParse(m.Groups["i"].Value, out var i)) idxs.Add(i);
            }

            var list = new List<VmxNicDto>();
            foreach (var i in idxs.OrderBy(x => x))
            {
                var p = $"ethernet{i}.";

                if (!d.TryGetValue(p + "present", out var present) ||
                    !present.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                d.TryGetValue(p + "virtualDev", out var model);
                if (string.IsNullOrWhiteSpace(model)) model = "vmxnet3";

                // Prefer address; fall back to generatedAddress (common with addressType=vpx)
                string? mac = null;
                if (!d.TryGetValue(p + "address", out mac) || string.IsNullOrWhiteSpace(mac))
                    d.TryGetValue(p + "generatedAddress", out mac);

                // VMX ethernetX.networkName is the VMware network label. It is not
                // a Proxmox bridge and must never be used as the target bridge.
                d.TryGetValue(p + "networkName", out var sourceNetwork);
                // Distributed portgroups carry an ID in the VMX, not necessarily
                // their display name. Do not invent a VMware network name.
                if (string.IsNullOrWhiteSpace(sourceNetwork) &&
                    d.TryGetValue(p + "dvs.portgroupId", out var dvsPortgroupId) &&
                    !string.IsNullOrWhiteSpace(dvsPortgroupId))
                {
                    sourceNetwork = "DVS / Not available";
                }
                list.Add(new VmxNicDto
                {
                    Model = model!,
                    Mac = mac,
                    SourceNetwork = string.IsNullOrWhiteSpace(sourceNetwork) ? null : sourceNetwork
                });
            }
            return list;
        }

        private static List<VmxControllerDto> BuildControllers(Dictionary<string, string> d)
        {
            var list = new List<VmxControllerDto>();

            // SCSI controllers with model
            var rxScsi = new Regex(@"^scsi(?<i>\d+)\.virtualDev$", RegexOptions.IgnoreCase);
            foreach (var kv in d)
            {
                var m = rxScsi.Match(kv.Key);
                if (!m.Success) continue;

                var i = int.Parse(m.Groups["i"].Value);
                var present = true;
                if (d.TryGetValue($"scsi{i}.present", out var pr))
                    present = pr.Equals("TRUE", StringComparison.OrdinalIgnoreCase);

                list.Add(new VmxControllerDto
                {
                    Type = "scsi",
                    Index = i,
                    Model = kv.Value,
                    Present = present
                });
            }

            // SATA / NVMe presence
            var rxBus = new Regex(@"^(?<type>sata|nvme)(?<i>\d+)\.present$", RegexOptions.IgnoreCase);
            foreach (var kv in d)
            {
                var m = rxBus.Match(kv.Key);
                if (!m.Success) continue;

                var type = m.Groups["type"].Value.ToLowerInvariant();
                var i = int.Parse(m.Groups["i"].Value);
                var present = kv.Value.Equals("TRUE", StringComparison.OrdinalIgnoreCase);

                list.Add(new VmxControllerDto
                {
                    Type = type,
                    Index = i,
                    Present = present
                });
            }

            return list.OrderBy(x => x.Type).ThenBy(x => x.Index).ToList();
        }

        /// <summary>
        /// Computes VMDK virtual capacity in GiB from descriptor "RW &lt;sectors&gt;" lines.
        /// Falls back to stat of "-flat.vmdk", then descriptor size.
        /// </summary>
        private static long? GetVmdkSizeGiB(SshClient ssh, string descriptorPath)
        {
            // 1) Sum virtual sectors from descriptor
            var awkCmd =
                $"awk 'BEGIN{{sum=0}} " +
                @"/^RW[[:space:]]+[0-9]+/ { sum += $2 } " +
                "END{ print sum }' " + Q(descriptorPath) + " 2>/dev/null";
            var sectorsStr = RunText(ssh, awkCmd).Trim();

            if (long.TryParse(sectorsStr, out var sectors) && sectors > 0)
            {
                var bytes = sectors * 512L;
                return (long)Math.Ceiling(bytes / 1024.0 / 1024.0 / 1024.0);
            }

            // 2) Fallback: stat the -flat.vmdk if present
            var flatPath = Regex.Replace(descriptorPath, @"\.vmdk$", "-flat.vmdk", RegexOptions.IgnoreCase);
            var flatBytesStr = RunText(ssh, $"stat -c %s {Q(flatPath)} 2>/dev/null").Trim();
            if (long.TryParse(flatBytesStr, out var flatBytes) && flatBytes > 0)
            {
                return (long)Math.Ceiling(flatBytes / 1024.0 / 1024.0 / 1024.0);
            }

            // 3) Last resort: descriptor size
            var descBytesStr = RunText(ssh, $"stat -c %s {Q(descriptorPath)} 2>/dev/null").Trim();
            if (long.TryParse(descBytesStr, out var descBytes) && descBytes > 0)
            {
                return (long)Math.Ceiling(descBytes / 1024.0 / 1024.0 / 1024.0);
            }

            return null;
        }

        /// <summary>
        /// Maps VMware VMX guestOS IDs to friendly labels (covers windows9srv-64 → Windows Server 2016).
        /// </summary>
        private static string MapGuestOsLabel(string guestRaw)
        {
            if (string.IsNullOrWhiteSpace(guestRaw)) return "Other";
            var s = guestRaw.Trim();
            var x = s.ToLowerInvariant();
            var vmwareId = Regex.Replace(x, @"[\s_.-]", "");

            // Explicit VMware guestOS mapping (also recognizes identifiers without Guest suffix).
            // These "srvNext" identifiers refer to the *next* Windows Server release.
            if (vmwareId.StartsWith("windows2022srvnext64", StringComparison.Ordinal))
                return "Windows Server 2025";
            if (vmwareId.StartsWith("windows2019srvnext64", StringComparison.Ordinal))
                return "Windows Server 2022";
            if (vmwareId.StartsWith("windows2019srv64", StringComparison.Ordinal))
                return "Windows Server 2019";
            if (vmwareId.StartsWith("windows9server64", StringComparison.Ordinal) ||
                vmwareId.StartsWith("windows9srv64", StringComparison.Ordinal))
                return "Windows Server 2016";

            // ---------- Windows Server ----------
            var isServer = x.Contains("server") || x.Contains("srv");
            if (isServer || x.Contains("windows9server") || x.Contains("windows9srv"))
            {
                if (x.Contains("2025")) return "Windows Server 2025";
                if (x.Contains("2022")) return "Windows Server 2022";
                if (x.Contains("2019")) return "Windows Server 2019";
                if (x.Contains("2016") || x.Contains("windows9server") || x.Contains("windows9srv"))
                    return "Windows Server 2016";
                if (Regex.IsMatch(x, @"2012\s*r2", RegexOptions.IgnoreCase))
                    return "Windows Server 2012 R2";
                if (vmwareId.StartsWith("windows8srv64", StringComparison.Ordinal))
                    return "Windows Server 2012 / 2012 R2";

                if (vmwareId.StartsWith("windows8server64", StringComparison.Ordinal))
                    return "Windows Server 2012 R2";
                if (x.Contains("2008") || x.Contains("longhorn")) return "Windows Server 2008 / 2008 R2";
                if (x.Contains("2003") || x.Contains("winnet")) return "Windows Server 2003";
                return $"Windows Server ({s})";
            }

            // ---------- Windows Desktop ----------
            if (x.Contains("windows11") || x.Contains("win11") || x.Contains("windows11_64")) return "Windows 11";
            if (x.Contains("windows9_64") || x.Contains("windows10") || x.Contains("win10")) return "Windows 10";
            if (x.Contains("win81") || x.Contains("8.1")) return "Windows 8.1";
            if (x.Contains("win8") || x.Contains("windows8")) return "Windows 8";
            if (x.Contains("win7") || x.Contains("windows7")) return "Windows 7";
            if (x.Contains("vista")) return "Windows Vista";
            if (x.Contains("xp")) return "Windows XP";

            // ---------- Linux ----------
            if (x.Contains("ubuntu")) return "Ubuntu";
            if (x.Contains("debian")) return "Debian";
            if (x.Contains("rhel") || x.Contains("redhat")) return "Red Hat Enterprise Linux";
            if (x.Contains("centos")) return "CentOS";
            if (x.Contains("rocky")) return "Rocky Linux";
            if (x.Contains("alma")) return "AlmaLinux";
            if (x.Contains("sles") || x.Contains("suse")) return "SUSE Linux Enterprise / openSUSE";
            if (x.Contains("oracle")) return "Oracle Linux";
            if (x.Contains("fedora")) return "Fedora";
            if (x.Contains("arch")) return "Arch Linux";
            if (x.Contains("otherlinux") || x.Contains("linux")) return "Linux";

            // ---------- BSD / Unix ----------
            if (x.Contains("freebsd")) return "FreeBSD";
            if (x.Contains("openbsd")) return "OpenBSD";
            if (x.Contains("netbsd")) return "NetBSD";
            if (x.Contains("solaris")) return "Solaris";

            // ---------- macOS ----------
            if (x.Contains("darwin") || x.Contains("macos")) return "macOS";

            return $"Other ({s})";
        }
    }
}
