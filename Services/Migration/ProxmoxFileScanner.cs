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
                    var guestLabel = MapGuestOsLabel(guestRaw);

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

                list.Add(new VmxNicDto { Model = model!, Mac = mac });
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

            // Normalize to catch "windows2019srvNext_64Guest", "windows2019srvnext-64", etc.
            static string Normalize(string input)
            {
                var sb = new StringBuilder(input.Length);
                foreach (var ch in input)
                {
                    if (!char.IsWhiteSpace(ch) && ch != '_' && ch != '-' && ch != '.')
                        sb.Append(char.ToLowerInvariant(ch));
                }
                return sb.ToString();
            }

            var norm = Normalize(s);

            // ---------- Exact / normalized VMware guestOS IDs ----------

            // NOTE: keys are normalized (no spaces/_/-/. and all lower-case)
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // --- Windows Server (modern) ---
                ["windows2022srvnext64guest"] = "Windows Server 2025", // windows2022srvNext_64Guest
                ["windows2019srvnext64guest"] = "Windows Server 2022", // windows2019srvNext_64Guest
                ["windows2019srv64guest"] = "Windows Server 2019", // windows2019srv_64Guest

                // --- Windows Server (older) ---
                ["windows9server64guest"] = "Windows Server 2016",     // windows9Server64Guest
                ["windows9serverguest"] = "Windows Server 2016",
                ["windows9srv64guest"] = "Windows Server 2016",     // windows9Server, windows9Server-64
                ["windows8server64guest"] = "Windows Server 2012 / 2012 R2",
                ["windows8serverguest"] = "Windows Server 2012 / 2012 R2",
                ["longhorn64guest"] = "Windows Server 2008 / 2008 R2",
                ["longhornguest"] = "Windows Server 2008 / 2008 R2",
                ["winnetstandard64guest"] = "Windows Server 2003",
                ["winnetenterprise64guest"] = "Windows Server 2003",
                ["winnetstandardguest"] = "Windows Server 2003",
                ["winnetenterpriseguest"] = "Windows Server 2003",

                // --- Windows Desktop (modern) ---
                ["windows12_64guest"] = "Windows 12",
                ["windows11_64guest"] = "Windows 11",
                ["windows10_64guest"] = "Windows 10",
                ["windows9_64guest"] = "Windows 10",  // VMware's old ID for Win10

                // --- Windows Desktop (older) ---
                ["windows8_1_64guest"] = "Windows 8.1",
                ["windows81_64guest"] = "Windows 8.1",
                ["windows8_64guest"] = "Windows 8",
                ["windows7_64guest"] = "Windows 7",
                ["windows7guest"] = "Windows 7",
                ["winvista64guest"] = "Windows Vista",
                ["winvistaguest"] = "Windows Vista",
                ["winxppro64guest"] = "Windows XP",
                ["winxpproguest"] = "Windows XP",
                ["win2000proguest"] = "Windows 2000",
                ["win2000servguest"] = "Windows 2000",

                // --- Linux common IDs (all map to their distro name, not l26) ---
                ["ubuntu64guest"] = "Ubuntu",
                ["ubuntuguest"] = "Ubuntu",
                ["debian64guest"] = "Debian",
                ["debianguest"] = "Debian",
                ["rhel7_64guest"] = "Red Hat Enterprise Linux",
                ["rhel6_64guest"] = "Red Hat Enterprise Linux",
                ["rhel5_64guest"] = "Red Hat Enterprise Linux",
                ["centos64guest"] = "CentOS",
                ["centos7_64guest"] = "CentOS",
                ["centos8_64guest"] = "CentOS",
                ["rockylinux64guest"] = "Rocky Linux",
                ["almalinux64guest"] = "AlmaLinux",
                ["sles64guest"] = "SUSE Linux Enterprise / openSUSE",
                ["suse64guest"] = "SUSE Linux Enterprise / openSUSE",
                ["oraclelinux64guest"] = "Oracle Linux",
                ["fedora64guest"] = "Fedora",
                ["coreos64guest"] = "CoreOS",
                ["photon64guest"] = "VMware Photon OS",
                ["otherlinux64guest"] = "Linux",

                // --- BSD / Unix ---
                ["freebsd64guest"] = "FreeBSD",
                ["freebsdguest"] = "FreeBSD",
                ["openbsd64guest"] = "OpenBSD",
                ["openbsdguest"] = "OpenBSD",
                ["netbsd64guest"] = "NetBSD",
                ["netbsdguest"] = "NetBSD",
                ["solaris11_64guest"] = "Solaris 11",
                ["solaris10_64guest"] = "Solaris 10",
                ["solaris9guest"] = "Solaris",
                ["solaris8guest"] = "Solaris",

                // --- macOS ---
                ["darwin20_64guest"] = "macOS",
                ["darwin18_64guest"] = "macOS",
                ["darwin16_64guest"] = "macOS",

                // --- ESXi as guest ---
                ["vmkernel5guest"] = "VMware ESXi",
                ["vmkernel6guest"] = "VMware ESXi",
                ["vmkernel7guest"] = "VMware ESXi"
            };

            if (map.TryGetValue(norm, out var labelFromMap))
                return labelFromMap;

            // ---------- Fallback: pattern-based, but more careful ----------

            // Windows Server (VMware-style patterns first)
            if (norm.Contains("windows2022srvnext"))
                return "Windows Server 2025";

            if (norm.Contains("windows2019srvnext"))
                return "Windows Server 2022";

            if (norm.Contains("windows2019srv"))
                return "Windows Server 2019";

            if (norm.Contains("windows9server") || norm.Contains("windows9srv"))
                return "Windows Server 2016";

            if (norm.Contains("windows8server") || norm.Contains("windows2012"))
                return "Windows Server 2012 / 2012 R2";

            if (norm.Contains("longhorn"))
                return "Windows Server 2008 / 2008 R2";

            if (norm.Contains("winnet"))
                return "Windows Server 2003";

            // Generic text like "Microsoft Windows Server 2022 (64-bit)"
            if (x.Contains("server") && x.Contains("windows"))
            {
                if (x.Contains("2025")) return "Windows Server 2025";
                if (x.Contains("2022")) return "Windows Server 2022";
                if (x.Contains("2019")) return "Windows Server 2019";
                if (x.Contains("2016")) return "Windows Server 2016";
                if (x.Contains("2012")) return "Windows Server 2012 / 2012 R2";
                if (x.Contains("2008")) return "Windows Server 2008 / 2008 R2";
                if (x.Contains("2003")) return "Windows Server 2003";

                return $"Windows Server ({s})";
            }

            // ---------- Windows Desktop (fallback) ----------

            if (x.Contains("windows12") || x.Contains("win12"))
                return "Windows 12";

            if (x.Contains("windows11") || x.Contains("win11"))
                return "Windows 11";

            if (x.Contains("windows 10") || x.Contains("windows10") || x.Contains("win10") || x.Contains("windows9_64"))
                return "Windows 10";

            if (x.Contains("8.1") || x.Contains("windows 8.1") || x.Contains("win81"))
                return "Windows 8.1";

            if (x.Contains("windows 8") || x.Contains("windows8") || x.Contains("win8"))
                return "Windows 8";

            if (x.Contains("windows 7") || x.Contains("windows7") || x.Contains("win7"))
                return "Windows 7";

            if (x.Contains("vista"))
                return "Windows Vista";

            if (x.Contains("winxp") || x.Contains("windows xp") || x.Contains(" xp "))
                return "Windows XP";

            if (x.Contains("win2000") || x.Contains("windows 2000"))
                return "Windows 2000";

            // ---------- Linux (fallback by family) ----------

            if (x.Contains("ubuntu")) return "Ubuntu";
            if (x.Contains("debian")) return "Debian";
            if (x.Contains("rockylinux") ||
                x.Contains("rocky linux") ||
                x.Contains("rocky")) return "Rocky Linux";
            if (x.Contains("almalinux") ||
                x.Contains("alma linux") ||
                x.Contains("alma")) return "AlmaLinux";
            if (x.Contains("centos")) return "CentOS";
            if (x.Contains("rhel") ||
                x.Contains("red hat") ||
                x.Contains("redhat")) return "Red Hat Enterprise Linux";
            if (x.Contains("oraclelinux") ||
                x.Contains("oracle linux")) return "Oracle Linux";
            if (x.Contains("sles") ||
                x.Contains("suse")) return "SUSE Linux Enterprise / openSUSE";
            if (x.Contains("fedora")) return "Fedora";
            if (x.Contains("photon")) return "VMware Photon OS";
            if (x.Contains("coreos")) return "CoreOS";
            if (x.Contains("arch")) return "Arch Linux";
            if (x.Contains("otherlinux") ||
                x.Contains("linux")) return "Linux";

            // ---------- BSD / Unix ----------

            if (x.Contains("freebsd")) return "FreeBSD";
            if (x.Contains("openbsd")) return "OpenBSD";
            if (x.Contains("netbsd")) return "NetBSD";
            if (x.Contains("solaris11")) return "Solaris 11";
            if (x.Contains("solaris10")) return "Solaris 10";
            if (x.Contains("solaris")) return "Solaris";

            // ---------- macOS ----------

            if (x.Contains("darwin") || x.Contains("macos") || x.Contains("mac os"))
                return "macOS";

            // ---------- VMware ESXi as guest ----------

            if (x.Contains("vmkernel") || x.Contains("esxi"))
                return "VMware ESXi";

            // ---------- Fallback ----------

            return $"Other ({s})";
        }


    }
}
