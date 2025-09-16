namespace BareProx.Models
{
    public record CpuOption(string Value, string Label);
    public record NicModelOption(string Value, string Label);
    public record ScsiControllerOption(string Value, string Label);
    public record IsoOption(string Value, string Label);
    public record BridgeOption(string Name);
    public record VlanOption(string Tag);
    public record OsOption(string Value, string Label);

    public class MigrationCapabilities
    {
        public IReadOnlyList<CpuOption> Cpus { get; set; } = Array.Empty<CpuOption>();
        public IReadOnlyList<NicModelOption> Nics { get; set; } = Array.Empty<NicModelOption>();
        public IReadOnlyList<ScsiControllerOption> ScsiControllers { get; set; } = Array.Empty<ScsiControllerOption>();
        public IReadOnlyList<OsOption> OsTypes { get; set; } = Array.Empty<OsOption>();

        // Populated live from Proxmox (leave empty here)
        public IReadOnlyList<BridgeOption> Bridges { get; set; } = Array.Empty<BridgeOption>();
        public IReadOnlyList<VlanOption> Vlans { get; set; } = Array.Empty<VlanOption>();
        public IReadOnlyList<IsoOption> VirtioIsos { get; set; } = Array.Empty<IsoOption>();
    }

    public static class CapabilityCatalog
    {
        // CPU types commonly exposed in the PVE UI (generic “x86-64-v*”, host passthrough, plus legacy kvm64)
        // UI default for new VMs is typically x86-64-v2-AES, with v3/v4 also available on newer hosts.
        public static readonly IReadOnlyList<CpuOption> DefaultCpus = new[]
        {
            new CpuOption("x86-64-v2-AES", "x86-64-v2-AES (Default)"),
            new CpuOption("x86-64-v3",     "x86-64-v3 (generic)"),
            new CpuOption("x86-64-v4",     "x86-64-v4 (generic)"),
            new CpuOption("host",          "Host CPU (passthrough)"),
            new CpuOption("kvm64",         "kvm64 (legacy, migratable)")
            // Note: Proxmox also allows many specific “reported-model” values (Skylake, EPYC, etc.) via custom CPU models.
            // Those are host/QEMU dependent; fetch dynamically if you want to expose them.
        };

        // Complete NIC model list from qm.conf (“net[n] model=...”) 
        // https://pve.proxmox.com/wiki/Manual:qm.conf
        public static readonly IReadOnlyList<NicModelOption> DefaultNics = new[]
        {
            new NicModelOption("virtio",        "VirtIO (paravirtualized)"),
            new NicModelOption("e1000",         "Intel E1000"),
            new NicModelOption("e1000-82540em", "Intel E1000 82540EM"),
            new NicModelOption("e1000-82544gc", "Intel E1000 82544GC"),
            new NicModelOption("e1000-82545em", "Intel E1000 82545EM"),
            new NicModelOption("e1000e",        "Intel E1000e"),
            new NicModelOption("i82551",        "Intel i82551"),
            new NicModelOption("i82557b",       "Intel i82557B"),
            new NicModelOption("i82559er",      "Intel i82559ER"),
            new NicModelOption("ne2k_isa",      "NE2000 ISA"),
            new NicModelOption("ne2k_pci",      "NE2000 PCI"),
            new NicModelOption("pcnet",         "AMD PCnet"),
            new NicModelOption("rtl8139",       "Realtek RTL8139"),
            new NicModelOption("vmxnet3",       "VMware VMXNET3")
        };

        // Complete SCSI controller list (“scsihw=...”) 
        // https://pve.proxmox.com/wiki/Manual:qm.conf
        public static readonly IReadOnlyList<ScsiControllerOption> DefaultScsi = new[]
        {
            new ScsiControllerOption("lsi",               "LSI Logic"),
            new ScsiControllerOption("lsi53c810",         "LSI 53C810"),
            new ScsiControllerOption("megasas",           "LSI MegaRAID SAS"),
            new ScsiControllerOption("pvscsi",            "VMware PVSCSI"),
            new ScsiControllerOption("virtio-scsi-pci",   "VirtIO SCSI"),
            new ScsiControllerOption("virtio-scsi-single","VirtIO SCSI (single)")
        };

        // OS types (“ostype=...”) from qm.conf
        // https://pve.proxmox.com/wiki/Manual:qm.conf
        public static readonly IReadOnlyList<OsOption> DefaultOs = new[]
        {
            new OsOption("other", "Other / unspecified"),
            new OsOption("wxp",   "Windows XP"),
            new OsOption("w2k",   "Windows 2000"),
            new OsOption("w2k3",  "Windows 2003"),
            new OsOption("w2k8",  "Windows 2008"),
            new OsOption("wvista","Windows Vista"),
            new OsOption("win7",  "Windows 7 / 2008 R2"),
            new OsOption("win8",  "Windows 8 / 2012 / 2012 R2"),
            new OsOption("win10", "Windows 10 / 2016 / 2019"),
            new OsOption("win11", "Windows 11 / 2022 / 2025"),
            new OsOption("l24",   "Linux 2.4 kernel"),
            new OsOption("l26",   "Linux 2.6 – 6.x kernel"),
            new OsOption("solaris","Solaris / OpenSolaris / OpenIndiana")
        };
    }
}
