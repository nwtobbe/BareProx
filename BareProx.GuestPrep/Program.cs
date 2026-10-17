
/*
 * BareProx.GuestPrep
 * Windows VirtIO SCSI guest preparation utility
 *
 * Copyright (C) 2025-2026 Tobias Modig
 *
 * Licensed under the GNU Affero General Public License
 * version 3 or (at your option) any later version.
 *
 * Portions of the VirtIO SCSI phantom-device preparation
 * implementation are based on:
 *
 *   load-virtio-scsi-on-boot
 *   Copyright (c) 2025 derhanns
 *   https://github.com/croit/load-virtio-scsi-on-boot
 *
 * The original implementation is licensed under the MIT License.
 * See THIRD-PARTY-NOTICES.txt for the complete license text.
 *
 * This program is distributed WITHOUT ANY WARRANTY.
 * See the GNU Affero General Public License for details.
 */

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using System.Xml.Linq;

namespace BareProx.GuestPrep;

internal static class Program
{
    private const string HardwareId = @"PCI\VEN_1AF4&DEV_1004&SUBSYS_00081AF4&REV_00";
    private const string ShortHardwareId = @"PCI\VEN_1AF4&DEV_1004";
    private const string ClassId = "{4d36e97b-e325-11ce-bfc1-08002be10318}";
    private static readonly List<string> Actions = [];
    private const string PhantomEnumPath = @"SYSTEM\CurrentControlSet\Enum\ROOT\VIOSCSI";

    private static int Main(string[] args)
    {
        string? output = GetArg(args, "--output");
        try
        {
            if (args.Length == 0 || args.Contains("--help"))
            {
                Console.WriteLine("BareProx.GuestPrep prepare-scsi --inf C:\\path\\vioscsi.inf [--output C:\\path\\result.json]");
                Console.WriteLine("Use 'verify-scsi' to inspect existing registration without modifying it.");
                Console.WriteLine("Use 'capture-network --output C:\\path\\network.json' to save NIC configuration by MAC address.");
                Console.WriteLine("Use 'prepare-all --inf C:\\path\\vioscsi.inf --drivers-msi C:\\path\\virtio-win-gt-x64.msi --agent-msi C:\\path\\qemu-ga-x86_64.msi --output C:\\path\\result.json'.");
                GuestMaintenance.PrintHelp();
                PostMigrationGenerator.PrintHelp();
                AutoPrep.PrintHelp();
                return 0;
            }
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
                throw new InvalidOperationException("Windows x64 is required.");
            var command = args[0];
            if (command.Equals("autoprep", StringComparison.OrdinalIgnoreCase)) return AutoPrep.Run(args);
            if (command.Equals("generate-postmigration", StringComparison.OrdinalIgnoreCase)) return PostMigrationGenerator.Run(args);
            if (GuestMaintenance.IsCommand(command)) return GuestMaintenance.Run(args);
            if (command is not ("prepare-scsi" or "verify-scsi" or "capture-network" or "prepare-all")) throw new ArgumentException("Unknown command");
            if (command == "capture-network")
            {
                if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("--output is required for capture-network");
                NetworkCapture.Capture(output);
                return 0;
            }
            if (command == "prepare-scsi" || command == "prepare-all")
            {
                if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new UnauthorizedAccessException("Run as Administrator.");
                var inf = GetArg(args, "--inf") ?? throw new ArgumentException("--inf is required");
                inf = Path.GetFullPath(inf);
                if (!File.Exists(inf)) throw new FileNotFoundException("VirtIO SCSI INF not found", inf);
                if (!File.Exists(Path.Combine(Path.GetDirectoryName(inf)!, "vioscsi.sys")))
                    throw new FileNotFoundException("vioscsi.sys must accompany the INF");
                if (!File.Exists(Path.Combine(Path.GetDirectoryName(inf)!, "vioscsi.cat")))
                    throw new FileNotFoundException("vioscsi.cat must accompany the INF");
                bool fullInstall = command == "prepare-all";
                if (fullInstall)
                {
                    foreach (string option in new[] { "--drivers-msi", "--agent-msi" })
                    {
                        string path = GetArg(args, option)
                            ?? throw new ArgumentException($"{option} is required for prepare-all");
                        if (!File.Exists(path)) throw new FileNotFoundException($"Installer for {option} not found", path);
                    }
                }
                var before = Verify();
                if (before.Prepared)
                {
                    Actions.Add("Already prepared; no modifications made");
                }
                else
                {
                    // Windows Server 2012 R2 (NT 6.3) requires legacy pnputil syntax.
                    // Use import-only (-a), NOT -i -a: while still running on VMware,
                    // the VirtIO SCSI controller is absent, so -i can return 259
                    // (ERROR_NO_MORE_ITEMS) even for a usable signed driver package.
                    // The SetupAPI phantom-device installation below performs binding;
                    // Verify() must confirm boot-critical preparation before cutover.
                    if (IsWindowsServer2012R2())
                    {
                        Actions.Add("Windows Server 2012 R2: importing VirtIO SCSI INF with legacy pnputil -a (no matching controller required)");
                        RunPnP("-a", inf);
                    }
                    else
                    {
                        Actions.Add("Invoking modern pnputil /add-driver /install");
                        RunPnP("/add-driver", inf, "/install");
                    }
                    Actions.Add("Driver package staged");
                    var installedInf = FindPublishedInf()
                        ?? throw new InvalidOperationException("vioscsi OEM INF not found in DriverDatabase");

                    var existing = FindPhantomInstance();
                    if (existing is null)
                    {
                        using var device = NativeDevice.Create("vioscsi", HardwareId);
                        existing = device.InstanceId;
                        Actions.Add("Phantom controller created: " + existing);
                    }
                    else Actions.Add("Existing phantom controller reused: " + existing);

                    if (IsWindowsServer2012R2() || IsWindowsServer2016())
                    {
                        // Windows Server 2012 R2 and 2016 protect PnP-managed Enum keys
                        // against direct registry writes, including elevated administrators.
                        // Bind through SetupAPI instead; do not weaken the registry ACLs.
                        NativeDevice.InstallExisting(existing);
                        Actions.Add("Windows Server 2012 R2/2016: SetupAPI bound phantom controller " + existing);
                    }
                    else
                    {
                        // Preserve the established path on newer Windows Server releases.
                        BindDevice(existing, installedInf);
                    }
                    CreateCriticalDeviceEntry(ShortHardwareId);
                    CreateCriticalDeviceEntry(HardwareId);
                    Actions.Add("CriticalDeviceDatabase entries checked");
                    // Do not run a second pnputil /install. It returned 259 on Server 2019
                    // even when pnputil said that the driver was already up to date.
                }
                if (fullInstall)
                {
                    string driversMsi = Path.GetFullPath(GetArg(args, "--drivers-msi")
                        ?? throw new ArgumentException("--drivers-msi is required for prepare-all"));
                    string agentMsi = Path.GetFullPath(GetArg(args, "--agent-msi")
                        ?? throw new ArgumentException("--agent-msi is required for prepare-all"));
                    string logDirectory = GetArg(args, "--log-dir") is string logDir
                        ? Path.GetFullPath(logDir)
                        : Path.Combine(Path.GetTempPath(), "BareProx.GuestPrep");
                    GuestSoftware.Install(driversMsi, agentMsi, logDirectory, Actions);
                }
            }
            var result = Verify();
            if (command == "prepare-all")
            {
                var guest = GuestSoftware.Check();
                Actions.Add($"VirtIO driver package installation: {guest.DriverPackageInstalled}");
                Actions.Add($"QEMU Guest Agent service installed: {guest.AgentServiceInstalled}");
                Actions.Add($"QEMU Guest Agent service status: {guest.AgentServiceStatus}");
                bool ok = result.Prepared && guest.DriverPackageInstalled && guest.AgentServiceInstalled;
                result = new Result(result.DriverServicePresent, result.CriticalDatabaseConfigured,
                    ok, Actions.ToArray(), ok
                        ? "VirtIO SCSI preparation, driver package and QEMU Guest Agent installed. Agent cannot connect until Proxmox provides a virtio-serial channel; actual boot remains untested."
                        : "Incomplete: check boot-critical SCSI binding, VirtIO driver MSI installation, and QEMU Guest Agent service.");
            }
            Emit(output, result);
            return result.Prepared ? 0 : 3;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            Emit(output, new Result(false, false, false, Actions, e.Message));
            return 1;
        }
    }

    private static string? GetArg(string[] a, string name)
    {
        var i = Array.FindIndex(a, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        return i < 0 || i + 1 >= a.Length ? null : a[i + 1];
    }

    private static void Emit(string? path, Result result)
    {
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        if (path is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, json);
        }
    }

    private static Result Verify()
    {
        using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\vioscsi");
        var image = service?.GetValue("ImagePath")?.ToString();
        var start = service?.GetValue("Start");
        bool driver = !string.IsNullOrWhiteSpace(image) && start is int n && n >= 0 && n <= 3;
        bool critical = new[] { ShortHardwareId, HardwareId }.All(id =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\CriticalDeviceDatabase\" + id.Replace('\\', '#'));
            return string.Equals(key?.GetValue("Service") as string, "vioscsi", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(key?.GetValue("ClassGUID") as string, ClassId, StringComparison.OrdinalIgnoreCase);
        });
        var phantom = FindPhantomInstance();
        bool binding = phantom is not null && HasBinding(phantom);
        return new Result(driver, critical, driver && critical && binding, Actions.ToArray(),
            driver && critical && binding
                ? "Driver and phantom registration verified. Actual Proxmox boot is not yet proven."
                : $"Incomplete preparation: service={driver}; criticalDatabase={critical}; phantomBinding={binding}");
    }

    private static string? FindPhantomInstance()
    {
        using var root = Registry.LocalMachine.OpenSubKey(PhantomEnumPath);
        if (root is null) return null;
        foreach (var name in root.GetSubKeyNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var id = @"ROOT\VIOSCSI\" + name;
            using var instance = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + id);
            var ids = instance?.GetValue("HardwareID") as string[];
            if (ids?.Any(x => string.Equals(x, HardwareId, StringComparison.OrdinalIgnoreCase)) == true)
                return id;
        }
        return null;
    }

    private static bool HasBinding(string instanceId)
    {
        using var dev = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + instanceId);
        if (!string.Equals(dev?.GetValue("Service") as string, "vioscsi", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(dev?.GetValue("ClassGUID") as string, ClassId, StringComparison.OrdinalIgnoreCase))
            return false;
        var driverKey = dev?.GetValue("Driver") as string;
        if (string.IsNullOrWhiteSpace(driverKey) || !driverKey.StartsWith(ClassId + @"\", StringComparison.OrdinalIgnoreCase)) return false;
        using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driverKey);
        return cls is not null && string.Equals(cls.GetValue("MatchingDeviceId") as string,
            HardwareId, StringComparison.OrdinalIgnoreCase) &&
            (cls.GetValue("InfPath") as string)?.StartsWith("oem", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? FindPublishedInf()
    {
        using var db = Registry.LocalMachine.OpenSubKey(@"SYSTEM\DriverDatabase\DriverPackages");
        if (db is null) return null;
        foreach (string name in db.GetSubKeyNames().Where(n => n.StartsWith("vioscsi.inf", StringComparison.OrdinalIgnoreCase)))
        {
            using var key = db.OpenSubKey(name);
            var inf = key?.GetValue(null) as string;
            if (inf is not null && inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase)) return inf;
        }
        return null;
    }

    private static bool IsWindowsServer2012R2()
    {
        // Use the Windows registry build instead of only runtime version helpers.
        // Native Windows Server 2012 R2 reports NT 6.3 / build 9600.
        // ProductType excludes Windows 8.1 client installations.
        using var product = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\ProductOptions");
        var productType = product?.GetValue("ProductType") as string;
        if (!string.Equals(productType, "ServerNT", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(productType, "LanmanNT", StringComparison.OrdinalIgnoreCase))
            return false;

        using var version = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var currentVersion = version?.GetValue("CurrentVersion")?.ToString();
        var build = version?.GetValue("CurrentBuildNumber")?.ToString()
            ?? version?.GetValue("CurrentBuild")?.ToString();
        return string.Equals(currentVersion, "6.3", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(build, "9600", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWindowsServer2016()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393) ||
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            return false;

        // Distinguish Windows Server 2016 from Windows 10 builds sharing 14393.
        using var product = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\ProductOptions");
        var productType = product?.GetValue("ProductType") as string;
        return string.Equals(productType, "ServerNT", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(productType, "LanmanNT", StringComparison.OrdinalIgnoreCase);
    }

    private static void BindDevice(string instanceId, string publishedInf)
    {
        // Match croit phantom binding: Class/Service plus a generated SCSIAdapter class key.
        string classPath = @"SYSTEM\CurrentControlSet\Control\Class\" + ClassId;
        using var cls = Registry.LocalMachine.OpenSubKey(classPath, writable: true)
            ?? throw new InvalidOperationException("SCSIAdapter class not present");
        using var existingDev = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + instanceId);
        var oldDriver = existingDev?.GetValue("Driver") as string;
        var oldSlot = oldDriver is not null && oldDriver.StartsWith(ClassId + @"\", StringComparison.OrdinalIgnoreCase)
            ? oldDriver[(ClassId.Length + 1)..] : null;
        string slot = oldSlot is not null && oldSlot.Length == 4 && oldSlot.All(char.IsDigit)
            ? oldSlot
            : Enumerable.Range(0, 10000).Select(i => i.ToString("D4"))
                .First(i => !cls.GetSubKeyNames().Contains(i, StringComparer.OrdinalIgnoreCase));
        using (var entry = cls.CreateSubKey(slot, writable: true)!)
        {
            entry.SetValue("DriverDesc", "VirtIO SCSI Controller");
            entry.SetValue("InfPath", publishedInf);
            entry.SetValue("MatchingDeviceId", HardwareId.ToLowerInvariant());
            entry.SetValue("ProviderName", "Red Hat, Inc.");
        }
        string enumPath = @"SYSTEM\CurrentControlSet\Enum\" + instanceId;
        using var dev = Registry.LocalMachine.OpenSubKey(enumPath, writable: true)
            ?? throw new InvalidOperationException("Phantom device registry key not accessible: " + instanceId);
        dev.SetValue("Service", "vioscsi");
        dev.SetValue("Class", "SCSIAdapter");
        dev.SetValue("ClassGUID", ClassId);
        dev.SetValue("Driver", ClassId + "\\" + slot);
        dev.SetValue("Mfg", "Red Hat, Inc.");
        dev.SetValue("DeviceDesc", "VirtIO SCSI Controller");
        dev.SetValue("CompatibleIDs", new[] { ShortHardwareId }, RegistryValueKind.MultiString);
        Actions.Add("Phantom driver binding created: " + slot);
    }

    private static void CreateCriticalDeviceEntry(string hardwareId)
    {
        string path = @"SYSTEM\CurrentControlSet\Control\CriticalDeviceDatabase\" + hardwareId.Replace('\\', '#');
        using var key = Registry.LocalMachine.CreateSubKey(path, writable: true)!;
        key.SetValue("Service", "vioscsi");
        key.SetValue("ClassGUID", ClassId);
    }

    private static void RunPnP(params string[] arguments)
    {
        var p = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"))
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string a in arguments) p.ArgumentList.Add(a);
        var commandDescription = string.Join(" ", arguments.Select((argument, index) => index == 2 && arguments.Length > 2 ? "<INF path>" : argument));
        using var process = Process.Start(p) ?? throw new InvalidOperationException("Cannot start pnputil");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120000)) { process.Kill(); throw new TimeoutException("pnputil timed out"); }
        Task.WaitAll(stdout, stderr);
        Console.WriteLine(stdout.Result);
        // Windows Server 2016 may return ERROR_NO_MORE_ITEMS (259) when the
        // signed driver package is already published and no device needs an update.
        // This does NOT prove boot readiness: SetupAPI binding and Verify() follow.
        // Keep newer Windows versions and unrelated pnputil errors unchanged.
        if (process.ExitCode == 259 && IsWindowsServer2016() &&
            arguments.Length >= 3 &&
            string.Equals(arguments[0], "/add-driver", StringComparison.OrdinalIgnoreCase) &&
            arguments.Contains("/install", StringComparer.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(FindPublishedInf()))
        {
            Actions.Add("Windows Server 2016: pnputil 259 with existing published vioscsi package; SetupAPI binding and verification still required.");
            return;
        }
        if (process.ExitCode != 0)
        {
            throw new Win32Exception(
                process.ExitCode,
                $"pnputil {commandDescription} failed with exit code {process.ExitCode}. " +
                $"stdout: {stdout.Result}; stderr: {stderr.Result}");
        }
    }
    private sealed record Result(bool DriverServicePresent, bool CriticalDatabaseConfigured, bool Prepared,
        IReadOnlyList<string> Actions, string Message);
}

internal sealed class NativeDevice : IDisposable
{
    private const uint SPDRP_HARDWAREID = 1;
    private const uint DICD_GENERATE_ID = 1;
    private const uint DIF_REGISTERDEVICE = 0x19;
    private const uint DIF_SELECTBESTCOMPATDRV = 0x17;
    private const uint SPDIT_COMPATDRIVER = 2;
    private const uint DIF_INSTALLDEVICE = 2;
    private readonly IntPtr _handle;
    public string InstanceId { get; private set; } = "";
    private NativeDevice(IntPtr handle) => _handle = handle;
    public static NativeDevice Create(string deviceName, string hardwareId)
    {
        Guid classId = new("4d36e97b-e325-11ce-bfc1-08002be10318");
        var h = SetupDiCreateDeviceInfoList(ref classId, IntPtr.Zero);
        if (h == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var result = new NativeDevice(h);
        try
        {
            var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
            Check(SetupDiCreateDeviceInfoW(h, deviceName, ref classId, "VirtIO SCSI Controller", IntPtr.Zero, DICD_GENERATE_ID, ref info), "CreateDeviceInfo");
            byte[] id = Encoding.Unicode.GetBytes(hardwareId + "\0\0");
            Check(SetupDiSetDeviceRegistryPropertyW(h, ref info, SPDRP_HARDWAREID, id, (uint)id.Length), "SetHardwareId");
            Check(SetupDiRegisterDeviceInfo(h, ref info, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero), "RegisterDeviceInfo");
            // Windows may return ERROR_NO_COMPAT_DRIVERS here; registry binding is applied by caller.
            if (SetupDiBuildDriverInfoList(h, ref info, SPDIT_COMPATDRIVER) &&
                SetupDiCallClassInstaller(DIF_SELECTBESTCOMPATDRV, h, ref info))
                SetupDiCallClassInstaller(DIF_INSTALLDEVICE, h, ref info);
            var buffer = new StringBuilder(512);
            Check(SetupDiGetDeviceInstanceIdW(h, ref info, buffer, buffer.Capacity, out _), "GetDeviceInstanceId");
            result.InstanceId = buffer.ToString();
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    /// <summary>
    /// Bind a previously registered ROOT\VIOSCSI phantom device using Windows PnP.
    /// Unlike direct Enum/Class registry editing, SetupAPI owns the device installation.
    /// Fail closed if no compatible staged driver is found or installation is rejected.
    /// </summary>
    public static void InstallExisting(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId) ||
            !System.Text.RegularExpressions.Regex.IsMatch(instanceId,
                @"^ROOT\\VIOSCSI\\[0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new ArgumentException("Unexpected VirtIO phantom device ID", nameof(instanceId));

        Guid classId = new("4d36e97b-e325-11ce-bfc1-08002be10318");
        var h = SetupDiCreateDeviceInfoList(ref classId, IntPtr.Zero);
        if (h == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDeviceInfoList");
        try
        {
            var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
            Check(SetupDiOpenDeviceInfoW(h, instanceId, IntPtr.Zero, 0, ref info), "OpenDeviceInfo");
            Check(SetupDiBuildDriverInfoList(h, ref info, SPDIT_COMPATDRIVER), "BuildDriverInfoList");
            Check(SetupDiCallClassInstaller(DIF_SELECTBESTCOMPATDRV, h, ref info), "SelectBestCompatibleDriver");
            Check(SetupDiCallClassInstaller(DIF_INSTALLDEVICE, h, ref info), "InstallDevice");
        }
        finally { SetupDiDestroyDeviceInfoList(h); }
    }

    private static void Check(bool ok, string operation)
    {
        if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
    }
    public void Dispose() => SetupDiDestroyDeviceInfoList(_handle);
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classId, IntPtr parent);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiCreateDeviceInfoW(IntPtr set, string name, ref Guid classId, string description, IntPtr parent, uint flags, ref DeviceInfo info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref DeviceInfo info, uint property, byte[] value, uint length);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiRegisterDeviceInfo(IntPtr set, ref DeviceInfo info, uint flags, IntPtr compare, IntPtr context, IntPtr dup);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiOpenDeviceInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string instanceId, IntPtr hwndParent, uint openFlags, ref DeviceInfo info);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiBuildDriverInfoList(IntPtr set, ref DeviceInfo info, uint type);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref DeviceInfo info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref DeviceInfo info, StringBuilder output, int length, out int needed);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}


/// <summary>
/// Captures network configuration *inside* the Windows guest. Only Windows built-in
/// PowerShell networking cmdlets are used; neither WinRM nor a Proxmox-side agent is needed.
/// No credentials or Wi-Fi profiles are captured. Restore must be a separate, explicit step.
/// </summary>

/// <summary>
/// Installs the Fedora VirtIO Windows driver bundle and QEMU guest agent from
/// files supplied by BareProx. Never downloads packages or removes VMware Tools.
/// The QEMU agent is not expected to be Running while still on VMware.
/// </summary>
internal static class GuestSoftware
{
    internal sealed record CheckResult(bool DriverPackageInstalled, bool AgentServiceInstalled,
        string AgentServiceStatus);

    public static CheckResult Check()
    {
        // Driver package is an MSI installed product; use Windows Installer's
        // uninstall registry entries rather than Win32_Product (which can repair MSIs).
        bool drivers = false;
        foreach (var uninstallRoot in new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        })
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(uninstallRoot);
            if (root is null) continue;
            foreach (string subName in root.GetSubKeyNames())
            {
                using var entry = root.OpenSubKey(subName);
                string? name = entry?.GetValue("DisplayName") as string;
                if (name is not null &&
                    (name.Contains("VirtIO", StringComparison.OrdinalIgnoreCase) &&
                     (name.Contains("Driver", StringComparison.OrdinalIgnoreCase) ||
                      name.Contains("Guest Tools", StringComparison.OrdinalIgnoreCase))))
                    drivers = true;
            }
        }
        using var agent = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\QEMU-GA");
        bool installed = agent?.GetValue("ImagePath") is not null;
        string status = installed ? "Installed (runtime status not checked)" : "NotInstalled";
        return new CheckResult(drivers, installed, status);
    }

    public static void Install(string driversMsi, string agentMsi, string logDirectory,
        List<string> actions)
    {
        if (!File.Exists(driversMsi)) throw new FileNotFoundException("VirtIO driver MSI not found", driversMsi);
        if (!File.Exists(agentMsi)) throw new FileNotFoundException("QEMU guest agent MSI not found", agentMsi);
        Directory.CreateDirectory(logDirectory);

        // Install drivers first, including VirtIO serial. This does not replace
        // the active VMware network adapter; its physical hardware ID differs.
        int driverExit = InstallMsi(driversMsi, Path.Combine(logDirectory, "virtio-drivers-msi.log"));
        actions.Add($"VirtIO driver MSI completed: exit code {driverExit}");

        int agentExit = InstallMsi(agentMsi, Path.Combine(logDirectory, "qemu-guest-agent-msi.log"));
        actions.Add($"QEMU Guest Agent MSI completed: exit code {agentExit}");
        if (driverExit == 3010 || agentExit == 3010)
            actions.Add("Windows Installer requested reboot (3010). Defer reboot to the migration orchestrator.");
    }

    private static int InstallMsi(string installer, string logFile)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in new[] { "/i", installer, "/qn", "/norestart", "/L*v", logFile })
            psi.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start msiexec");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(600000))
        {
            process.Kill();
            throw new TimeoutException($"MSI installation timed out: {installer}; log: {logFile}");
        }
        Task.WaitAll(output, error);
        int exit = process.ExitCode;
        if (exit is not (0 or 3010))
            throw new InvalidOperationException($"MSI install failed (exit {exit}): {installer}; log: {logFile}; stderr: {error.Result}");
        return exit;
    }
}

internal static class NetworkCapture
{
    private const string Script = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $warnings = New-Object 'System.Collections.Generic.List[string]'
        function Optional([scriptblock] $block, [string] $name) {
            try { @(& $block) }
            catch {
                [void]$warnings.Add("$name : $($_.Exception.Message)")
                @()
            }
        }
        function MacKey($mac) {
            if ([string]::IsNullOrWhiteSpace($mac)) { return $null }
            $hex = ([string]$mac).ToUpperInvariant() -replace '[^0-9A-F]', ''
            if ($hex.Length -ne 12) { return $null }
            return ($hex -replace '(..)(?!$)', '$1:')
        }
        function ArrayOf($data) { return @($data | Where-Object { $null -ne $_ }) }

        $byMac = @{}
        $withoutMac = @()
        $adapters = Optional { Get-NetAdapter -IncludeHidden } 'Get-NetAdapter'
        if (-not $adapters.Count) { throw 'No network adapters returned by Get-NetAdapter.' }
        $wmiConfigs = Optional { Get-CimInstance Win32_NetworkAdapterConfiguration } 'Win32_NetworkAdapterConfiguration'
        foreach ($nic in $adapters) {
            $idx = [int]$nic.ifIndex
            $key = MacKey $nic.MacAddress
            $ips = Optional { Get-NetIPAddress -InterfaceIndex $idx } "Get-NetIPAddress $idx"
            $ipIf = Optional { Get-NetIPInterface -InterfaceIndex $idx } "Get-NetIPInterface $idx"
            $routes = Optional { Get-NetRoute -InterfaceIndex $idx } "Get-NetRoute $idx"
            $dnsServers = Optional { Get-DnsClientServerAddress -InterfaceIndex $idx } "Get-DnsClientServerAddress $idx"
            $dnsClient = Optional { Get-DnsClient -InterfaceIndex $idx } "Get-DnsClient $idx"
            $bindings = Optional { Get-NetAdapterBinding -Name $nic.Name -ErrorAction Stop } "Get-NetAdapterBinding $($nic.Name)"
            $advanced = Optional { Get-NetAdapterAdvancedProperty -Name $nic.Name -ErrorAction Stop } "Get-NetAdapterAdvancedProperty $($nic.Name)"
            $wmi = $wmiConfigs | Where-Object { $_.InterfaceIndex -eq $idx } | Select-Object -First 1
            $entry = [ordered]@{
                InterfaceIndex = $idx
                InterfaceGuid = [string]$nic.InterfaceGuid
                Name = [string]$nic.Name
                InterfaceDescription = [string]$nic.InterfaceDescription
                MacAddress = $key
                RawMacAddress = [string]$nic.MacAddress
                Status = [string]$nic.Status
                LinkSpeed = [string]$nic.LinkSpeed
                HardwareInterface = $nic.HardwareInterface
                PhysicalMediaType = [string]$nic.PhysicalMediaType
                VlanId = $nic.VlanID
                IPInterfaces = @(foreach ($ip in $ipIf) { [ordered]@{
                    AddressFamily = [string]$ip.AddressFamily
                    Dhcp = [string]$ip.Dhcp
                    ConnectionState = [string]$ip.ConnectionState
                    InterfaceMetric = $ip.InterfaceMetric
                    NlMtu = $ip.NlMtu
                    RouterDiscovery = [string]$ip.RouterDiscovery
                    Forwarding = [string]$ip.Forwarding
                    Advertising = [string]$ip.Advertising
                    WeakHostSend = [string]$ip.WeakHostSend
                    WeakHostReceive = [string]$ip.WeakHostReceive
                }})
                IPAddresses = @(foreach ($ip in $ips) { [ordered]@{
                    AddressFamily = [string]$ip.AddressFamily
                    IPAddress = [string]$ip.IPAddress
                    PrefixLength = $ip.PrefixLength
                    PrefixOrigin = [string]$ip.PrefixOrigin
                    SuffixOrigin = [string]$ip.SuffixOrigin
                    AddressState = [string]$ip.AddressState
                    Type = [string]$ip.Type
                    SkipAsSource = $ip.SkipAsSource
                    ValidLifetime = [string]$ip.ValidLifetime
                    PreferredLifetime = [string]$ip.PreferredLifetime
                    PolicyStore = [string]$ip.PolicyStore
                }})
                Routes = @(foreach ($r in $routes) { [ordered]@{
                    AddressFamily = [string]$r.AddressFamily
                    DestinationPrefix = [string]$r.DestinationPrefix
                    NextHop = [string]$r.NextHop
                    RouteMetric = $r.RouteMetric
                    Protocol = [string]$r.Protocol
                    PolicyStore = [string]$r.PolicyStore
                    Publish = [string]$r.Publish
                    ValidLifetime = [string]$r.ValidLifetime
                }})
                DnsServers = @(foreach ($d in $dnsServers) { [ordered]@{
                    AddressFamily = [string]$d.AddressFamily
                    ServerAddresses = @($d.ServerAddresses)
                }})
                DnsClient = @(foreach ($d in $dnsClient) { [ordered]@{
                    ConnectionSpecificSuffix = [string]$d.ConnectionSpecificSuffix
                    RegisterThisConnectionsAddress = $d.RegisterThisConnectionsAddress
                    UseSuffixWhenRegistering = $d.UseSuffixWhenRegistering
                }})
                AdvancedProperties = @(foreach ($a in $advanced) { [ordered]@{
                    DisplayName = [string]$a.DisplayName
                    DisplayValue = [string]$a.DisplayValue
                    RegistryKeyword = [string]$a.RegistryKeyword
                    RegistryValue = @($a.RegistryValue)
                }})
                AdapterBindings = @(foreach ($b in $bindings) { [ordered]@{
                    ComponentId = [string]$b.ComponentID
                    DisplayName = [string]$b.DisplayName
                    Enabled = [bool]$b.Enabled
                }})
                Wins = if ($wmi) { [ordered]@{
                    WINSEnableLMHostsLookup = $wmi.WINSEnableLMHostsLookup
                    WINSPrimaryServer = $wmi.WINSPrimaryServer
                    WINSSecondaryServer = $wmi.WINSSecondaryServer
                    TcpipNetbiosOptions = $wmi.TcpipNetbiosOptions
                    DHCPEnabled = $wmi.DHCPEnabled
                    DHCPServer = $wmi.DHCPServer
                    DHCPLeaseObtained = [string]$wmi.DHCPLeaseObtained
                    DHCPLeaseExpires = [string]$wmi.DHCPLeaseExpires
                }} else { $null }
            }
            if ($key) {
                if (-not $byMac.ContainsKey($key)) { $byMac[$key] = @() }
                $byMac[$key] = @($byMac[$key]) + @($entry)
            } else { $withoutMac += ,$entry }
        }
        $globalDns = Optional { Get-DnsClientGlobalSetting } 'Get-DnsClientGlobalSetting'
        $globalRoutes = Optional { Get-NetRoute -PolicyStore PersistentStore } 'Persistent routes'
        $result = [ordered]@{
            SchemaVersion = 1
            CapturedAtUtc = [DateTime]::UtcNow.ToString('o')
            ComputerName = $env:COMPUTERNAME
            OsVersion = [Environment]::OSVersion.Version.ToString()
            NetworkAdaptersByMac = $byMac
            NetworkAdaptersWithoutMac = @($withoutMac)
            GlobalDns = @(foreach ($g in $globalDns) { [ordered]@{
                SuffixSearchList = @($g.SuffixSearchList)
                UseDevolution = $g.UseDevolution
                DevolutionLevel = $g.DevolutionLevel
            }})
            PersistentRoutes = @(foreach ($r in $globalRoutes) { [ordered]@{
                InterfaceIndex = $r.InterfaceIndex
                DestinationPrefix = [string]$r.DestinationPrefix
                NextHop = [string]$r.NextHop
                RouteMetric = $r.RouteMetric
                PolicyStore = [string]$r.PolicyStore
            }})
            Warnings = @($warnings.ToArray())
        }
        $result | ConvertTo-Json -Depth 14 -Compress
        """;

    public static void Capture(string outputPath)
    {
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(powershell))
            throw new FileNotFoundException("Windows PowerShell 5.1 is required", powershell);
        var psi = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)));
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Cannot start Windows PowerShell");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Network capture timed out");
        }
        Task.WaitAll(stdout, stderr);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Network capture failed (exit {process.ExitCode}): {stderr.Result.Trim()} {stdout.Result.Trim()}");
        // Never write a partial or malformed capture over a previous known-good result.
        using var document = JsonDocument.Parse(stdout.Result);
        var data = document.RootElement;
        if (!data.TryGetProperty("NetworkAdaptersByMac", out _))
            throw new InvalidOperationException("Network capture returned an unexpected JSON structure");
        string fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temp = fullPath + ".tmp";
        try
        {
            File.WriteAllText(temp,
                JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        Console.WriteLine($"Network configuration captured: {fullPath}");
        Console.WriteLine($"MAC groups: {data.GetProperty("NetworkAdaptersByMac").EnumerateObject().Count()}, " +
                          $"adapters without valid MAC: {data.GetProperty("NetworkAdaptersWithoutMac").GetArrayLength()}");
    }
}

// Generates an offline, reboot-resumable, SYSTEM-at-startup post-migration runner.
// Register from the VMware source AFTER preparing drivers, but before final shutdown.
// VMware-side orchestrator. Uses the established command implementations as child processes.
// The source VM is shut down only after every selected stage passes independent verification.
internal static class AutoPrep
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private sealed record StageResult(string Name, bool Success, int ExitCode, string ResultFile, string Message);

    internal static void PrintHelp()
    {
        Console.WriteLine("  autoprep [--virtio-scsi --inf PATH] [--virtio-drivers --drivers-msi PATH] [--qemu-agent --agent-msi PATH]");
        Console.WriteLine("           [--network] [--firstboot] [--uninstall-vmware-tools] [--disable-vmci] [--shutdown]");
        Console.WriteLine("           [--workdir PATH] [--mapping-json PATH] [--cleanup-workdir PATH] [--apply --yes]");
        Console.WriteLine("  Default is a non-destructive plan. --apply --yes authorizes selected operations.");
        Console.WriteLine("  --shutdown requires VirtIO SCSI, firstboot, VMware Tools uninstall and VMCI disable.");
    }

    private static bool Has(string[] args, string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    private static string? Val(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return null;
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Missing value for {name}");
        return args[i + 1];
    }
    private static bool JsonBool(JsonElement obj, string key) => obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
    private static string JsonString(JsonElement obj, string key) => obj.TryGetProperty(key, out var v) ? v.ToString() : "";
    private static string MustExist(string? path, string flag)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException($"{flag} is required");
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException($"{flag} file not found", full);
        return full;
    }

    internal static int Run(string[] args)
    {
        string? manifest = null;
        var completed = new List<StageResult>();
        bool shutdownInitiated = false;
        try
        {
            string[] flags = ["--virtio-scsi", "--virtio-drivers", "--qemu-agent", "--network", "--firstboot",
                "--uninstall-vmware-tools", "--disable-vmci", "--shutdown"];
            bool apply = Has(args, "--apply"), yes = Has(args, "--yes");
            if (apply != yes) throw new ArgumentException("Use --apply and --yes together, or neither for plan-only mode.");
            if (!flags.Any(f => Has(args, f))) throw new ArgumentException("Select at least one autoprep stage.");
            if (Has(args, "--disable-vmci") && !Has(args, "--uninstall-vmware-tools"))
                throw new ArgumentException("--disable-vmci requires --uninstall-vmware-tools.");
            if (Has(args, "--shutdown") && (!Has(args, "--virtio-scsi") || !Has(args, "--firstboot") ||
                !Has(args, "--uninstall-vmware-tools") || !Has(args, "--disable-vmci")))
                throw new ArgumentException("--shutdown requires --virtio-scsi, --firstboot, --uninstall-vmware-tools, and --disable-vmci.");
            if (Has(args, "--firstboot") && !Has(args, "--network"))
                throw new ArgumentException("--firstboot requires --network; network capture must be current.");
            if (Has(args, "--virtio-drivers") != Has(args, "--qemu-agent") ||
                (Has(args, "--virtio-drivers") && !Has(args, "--virtio-scsi")))
                throw new ArgumentException("MSI installation requires --virtio-scsi --virtio-drivers --qemu-agent together.");
            if (Has(args, "--cleanup-workdir") && !Has(args, "--firstboot"))
                throw new ArgumentException("--cleanup-workdir requires --firstboot.");
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows required");
            var vm = GetPlatform();
            if (!vm) throw new InvalidOperationException("autoprep must run inside a VMware guest; QEMU/unknown platform rejected.");
            string workdir = Path.GetFullPath(Val(args, "--workdir") ?? Path.GetDirectoryName(Environment.ProcessPath!)!);
            var inf = Has(args, "--virtio-scsi") ? MustExist(Val(args, "--inf"), "--inf") : null;
            var drivers = Has(args, "--virtio-drivers") ? MustExist(Val(args, "--drivers-msi"), "--drivers-msi") : null;
            var agent = Has(args, "--qemu-agent") ? MustExist(Val(args, "--agent-msi"), "--agent-msi") : null;
            var mapping = Val(args, "--mapping-json");
            if (mapping != null) mapping = MustExist(mapping, "--mapping-json");
            if (mapping != null && !Has(args, "--network")) throw new ArgumentException("MAC mapping requires --network.");
            string networkJson = Path.Combine(workdir, "network.json");
            string script = Path.Combine(workdir, "BareProx-FirstBoot.ps1");
            manifest = Path.Combine(workdir, "autoprep-result.json");
            if (!apply)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    Success = true,
                    Mode = "Plan",
                    Stages = flags.Where(f => Has(args, f)),
                    Workdir = workdir,
                    NetworkJson = networkJson,
                    FirstBootScript = script,
                    ShutdownRequested = Has(args, "--shutdown"),
                    Message = "No changes made. Repeat with --apply --yes after reviewing the plan."
                }, JsonOptions));
                return 0;
            }
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("Administrator rights required for autoprep.");
            Directory.CreateDirectory(workdir);
            void WriteManifest(bool ok, string message)
            {
                string data = JsonSerializer.Serialize(new
                {
                    Success = ok,
                    Message = message,
                    Stages = completed,
                    ShutdownRequested = Has(args, "--shutdown"),
                    ShutdownInitiated = shutdownInitiated,
                    UpdatedUtc = DateTime.UtcNow
                }, JsonOptions);
                string tmp = manifest + ".tmp";
                File.WriteAllText(tmp, data, new UTF8Encoding(false));
                File.Move(tmp, manifest, true);
                Console.WriteLine(data);
            }
            void Stage(string name, List<string> command, Func<JsonElement, bool> verify)
            {
                string output = name == "network-capture" ? networkJson : Path.Combine(workdir, "autoprep-" + name + ".json");
                if (File.Exists(output)) File.Delete(output); // Never accept an old successful result.
                command.Add("--output"); command.Add(output);
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                foreach (var a in command) start.ArgumentList.Add(a);
                using var p = Process.Start(start) ?? throw new InvalidOperationException("Could not start GuestPrep for " + name);
                Task<string> stdout = p.StandardOutput.ReadToEndAsync(), stderr = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(600000)) { p.Kill(entireProcessTree: true); throw new TimeoutException(name + " timed out"); }
                Task.WaitAll(stdout, stderr);
                string info = stderr.Result.Trim();
                bool passed = false;
                if (p.ExitCode == 0 && (File.Exists(output) || name == "firstboot"))
                {
                    if (name == "firstboot") File.WriteAllText(output, stdout.Result);
                    using var doc = JsonDocument.Parse(File.ReadAllText(output));
                    passed = verify(doc.RootElement);
                    if (!passed) info = JsonString(doc.RootElement, "Message") + " | failed stage verification";
                }
                else if (File.Exists(output))
                {
                    try { using var doc = JsonDocument.Parse(File.ReadAllText(output)); info += " " + JsonString(doc.RootElement, "Message"); }
                    catch (JsonException) { info += " (result JSON malformed)"; }
                }
                completed.Add(new StageResult(name, passed, p.ExitCode, output, info));
                WriteManifest(passed, passed ? name + " verified" : name + " failed: " + info);
                if (!passed) throw new InvalidOperationException($"{name} failed (exit {p.ExitCode}): {info}");
            }
            if (inf != null) Stage("scsi", ["prepare-scsi", "--inf", inf], r => JsonBool(r, "Prepared"));
            if (inf != null) Stage("verify-scsi", ["verify-scsi"], r => JsonBool(r, "Prepared"));
            if (drivers != null || agent != null)
            {
                // Existing prepare-all installs both MSIs. Do not imply partial installation support.
                if (drivers == null || agent == null || inf == null)
                    throw new ArgumentException("Current MSI installer requires --virtio-scsi, --virtio-drivers and --qemu-agent together.");
                Stage("guest-software", ["prepare-all", "--inf", inf, "--drivers-msi", drivers, "--agent-msi", agent], r => JsonBool(r, "Prepared"));
            }
            if (Has(args, "--network"))
            {
                Stage("network-capture", ["capture-network"], r => r.TryGetProperty("NetworkAdaptersByMac", out var n) && n.ValueKind == JsonValueKind.Object);
            }
            if (Has(args, "--firstboot"))
            {
                var cmd = new List<string> { "generate-postmigration", "--script", script, "--network-json", networkJson };
                if (mapping != null) { cmd.Add("--mapping-json"); cmd.Add(mapping); }
                if (Val(args, "--cleanup-workdir") is string cleanup) { cmd.Add("--cleanup-workdir"); cmd.Add(Path.GetFullPath(cleanup)); }
                cmd.AddRange(["--register", "--apply", "--yes"]);
                Stage("firstboot", cmd, r => JsonBool(r, "Success") && JsonBool(r, "Registered") && File.Exists(script));
            }
            if (Has(args, "--uninstall-vmware-tools"))
            {
                var cmd = new List<string> { "uninstall-vmware-tools", "--apply", "--yes", "--allow-reboot-required" };
                if (Has(args, "--disable-vmci")) cmd.Add("--disable-vmci");
                Stage("vmware-tools", cmd, r => JsonBool(r, "Success") && JsonBool(r, "VerificationPassed") &&
                    !JsonBool(r, "StillInstalled") && (!Has(args, "--disable-vmci") || JsonBool(r, "VmciStartupDisabled")));
            }
            WriteManifest(true, "All selected preparation stages passed. Shutdown not yet requested.");
            if (Has(args, "--shutdown"))
            {
                // Command is scheduled after the manifest is safely persisted. A failed launch keeps VM running.
                string shutdown = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
                var psi = new ProcessStartInfo(shutdown) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in new[] { "/s", "/t", "15", "/c", "BareProx GuestPrep completed and verified" }) psi.ArgumentList.Add(a);
                using var sp = Process.Start(psi) ?? throw new InvalidOperationException("Unable to launch shutdown.exe");
                if (!sp.WaitForExit(15000) || sp.ExitCode != 0) throw new InvalidOperationException("shutdown.exe failed; VM left running.");
                shutdownInitiated = true;
                WriteManifest(true, "All selected preparation stages passed; shutdown scheduled in 15 seconds.");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("autoprep: " + ex);
            if (manifest != null)
            {
                try
                {
                    File.WriteAllText(manifest, JsonSerializer.Serialize(new
                    {
                        Success = false,
                        Error = ex.Message,
                        Stages = completed,
                        ShutdownInitiated = shutdownInitiated,
                        UpdatedUtc = DateTime.UtcNow
                    }, JsonOptions));
                }
                catch (IOException) { }
            }
            return 1;
        }
    }
    private static bool GetPlatform()
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add("$c=Get-CimInstance Win32_ComputerSystem; if(($c.Manufacturer+' '+$c.Model) -match 'VMware' -and ($c.Manufacturer+' '+$c.Model) -notmatch 'QEMU|KVM'){exit 0}else{exit 10}");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Cannot check VMware platform");
        if (!p.WaitForExit(30000)) { p.Kill(entireProcessTree: true); throw new TimeoutException("Platform check timed out"); }
        return p.ExitCode == 0;
    }
}

internal static class PostMigrationGenerator
{
    private const string TaskName = "BareProx-PostMigration";
    internal static void PrintHelp()
    {
        Console.WriteLine("  generate-postmigration --script PATH [--network-json PATH] [--mapping-json PATH] [--cleanup-workdir PATH] [--register --apply --yes]");
        Console.WriteLine("  Generated script checks QEMU/KVM, runs each cleanup step, verifies it, and retries on the next boot if needed.");
        Console.WriteLine("  --cleanup-workdir deletes the entire explicitly named dedicated directory only after successful verification.");
        Console.WriteLine("  --register installs a SYSTEM startup scheduled task; generating without it makes no system changes.");
    }
    private static bool Has(string[] args, string option) => args.Any(x => x.Equals(option, StringComparison.OrdinalIgnoreCase));
    private static string? Get(string[] args, string option)
    {
        int i = Array.FindIndex(args, x => x.Equals(option, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return null;
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Missing value after {option}");
        return args[i + 1];
    }
    private static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

    internal static int Run(string[] args)
    {
        try
        {
            bool register = Has(args, "--register"), apply = Has(args, "--apply"), yes = Has(args, "--yes");
            if (register && (!apply || !yes))
                throw new ArgumentException("Registering startup automation requires --register --apply --yes.");
            if ((apply || yes) && !register)
                throw new ArgumentException("--apply/--yes are valid only with --register.");
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator) && register)
                throw new UnauthorizedAccessException("Administrator required for scheduled-task registration.");
            string script = Path.GetFullPath(Get(args, "--script") ?? throw new ArgumentException("--script is required"));
            if (!script.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("--script must be a .ps1 file");
            string? network = Get(args, "--network-json") is string v ? Path.GetFullPath(v) : null;
            string? mapping = Get(args, "--mapping-json") is string m ? Path.GetFullPath(m) : null;
            if (network is not null && !File.Exists(network)) throw new FileNotFoundException("Network capture missing", network);
            if (mapping is not null && network is null) throw new ArgumentException("--mapping-json needs --network-json");
            if (mapping is not null && !File.Exists(mapping)) throw new FileNotFoundException("MAC mapping file missing", mapping);
            string? cleanupDir = Get(args, "--cleanup-workdir") is string w ? Path.GetFullPath(w).TrimEnd('\\', '/') : null;
            if (cleanupDir is not null)
            {
                if (!register || !Directory.Exists(cleanupDir)) throw new ArgumentException("--cleanup-workdir requires --register --apply --yes and an existing directory.");
                if (cleanupDir.Length <= Path.GetPathRoot(cleanupDir)!.Length ||
                    (File.GetAttributes(cleanupDir) & FileAttributes.ReparsePoint) != 0 ||
                    Directory.Exists(Path.Combine(cleanupDir, ".git")))
                    throw new ArgumentException("Unsafe cleanup directory: drive root, junction, or source-control tree.");
                foreach (string protectedPath in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) })
                    if (cleanupDir.Equals(protectedPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("Refusing protected directory cleanup.");
            }
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine GuestPrep executable path");
            if (!File.Exists(exe)) throw new FileNotFoundException("GuestPrep executable not found", exe);
            if (string.Equals(Path.GetFullPath(script), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Script cannot overwrite executable");
            if (cleanupDir is not null &&
                (!string.Equals(Path.GetDirectoryName(script), cleanupDir, StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(Path.GetDirectoryName(Path.GetFullPath(exe)), cleanupDir, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("--cleanup-workdir must be the directory containing both GuestPrep.exe and the generated script.");
            string contents = ScriptTemplate.Replace("__EXE__", PsQuote(exe))
                .Replace("__NETWORK__", network is null ? "$null" : PsQuote(network))
                .Replace("__MAPPING__", mapping is null ? "$null" : PsQuote(mapping))
                .Replace("__WORKDIR__", cleanupDir is null ? "$null" : PsQuote(cleanupDir));
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            // Preserve any previously generated runner until replacement is fully written.
            string temp = script + ".new";
            File.WriteAllText(temp, contents, new UTF8Encoding(false));
            File.Move(temp, script, overwrite: true);
            if (register) RegisterTask(script);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Success = true,
                Script = script,
                GuestPrepExecutable = exe,
                NetworkJson = network,
                ExplicitMappingJson = mapping,
                AutoDetectMapping = mapping is null && network is not null,
                Registered = register,
                CleanupWorkDirectory = cleanupDir,
                TaskName = register ? TaskName : null,
                Note = "Runner executes only on QEMU/KVM. On failure the startup task remains for next boot; it never reboots Windows."
            },
                new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void RegisterTask(string script)
    {
        // schtasks /SC ONSTART /RU SYSTEM avoids Windows Server 2019 rejecting
        // the custom XML principal LogonType. ProcessStartInfo.ArgumentList
        // protects the arguments from CMD shell interpolation.
        string powershell = Path.Combine(Environment.SystemDirectory,
            @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(powershell))
            throw new FileNotFoundException("Windows PowerShell 5.1 not found", powershell);
        string action = "\"" + powershell + "\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"";
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string arg in new[] { "/Create", "/TN", TaskName, "/SC", "ONSTART",
                                     "/RU", "SYSTEM", "/RL", "HIGHEST", "/TR", action, "/F" })
            psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start schtasks.exe");
        Task<string> stdout = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderr = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(30000))
        {
            proc.Kill(entireProcessTree: true);
            throw new TimeoutException("Scheduled task creation timed out");
        }
        Task.WaitAll(stdout, stderr);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"schtasks create failed ({proc.ExitCode}): {stdout.Result} {stderr.Result}");
    }

    private const string ScriptTemplate = """
# BareProx post-migration first-boot runner. Auto-generated by BareProx.GuestPrep.
# Requires Windows PowerShell 5.1. No internet access or external .ps1 helpers required.
# Copyright (C) 2025-2026 Tobias Modig; AGPL-3.0-or-later.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$guestPrep = __EXE__
$networkJson = __NETWORK__
$explicitMapping = __MAPPING__
$cleanupWorkDir = __WORKDIR__
$taskName = 'BareProx-PostMigration'
$stateDir = Join-Path $env:ProgramData 'BareProx\PostMigration'
New-Item -Path $stateDir -ItemType Directory -Force | Out-Null
$log = Join-Path $stateDir 'first-boot.log'
$statusFile = Join-Path $stateDir 'status.json'
function Log([string]$msg) { Add-Content -LiteralPath $log -Value ('{0} {1}' -f (Get-Date -Format o),$msg) -Encoding UTF8 }
function SaveStatus([string]$stage,[bool]$success,[string]$message) {
    $json = @{ UpdatedUtc=[datetime]::UtcNow.ToString('o'); Stage=$stage; Success=$success; Message=$message } | ConvertTo-Json
    [IO.File]::WriteAllText($statusFile,$json,[Text.UTF8Encoding]::new($false))
}
function Archive-And-CleanupWorkingDirectory {
    if (-not $cleanupWorkDir) { Log 'No working-directory deletion requested.'; return }
    $folder = [IO.Path]::GetFullPath($cleanupWorkDir).TrimEnd([char]92, '/')
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) { Log "Working directory already absent: $folder"; return }
    $root = [IO.Path]::GetPathRoot($folder)
    if ($folder.Length -le $root.Length) { throw 'Refusing deletion of volume root.' }
    if ($folder -ine ([IO.Path]::GetFullPath((Split-Path -Parent $PSCommandPath)).TrimEnd([char]92, '/')) -or
        $folder -ine ([IO.Path]::GetFullPath((Split-Path -Parent $guestPrep)).TrimEnd([char]92, '/'))) {
        throw 'Workdir differs from the script or executable directory.'
    }
    foreach ($protected in @($env:WINDIR,$env:ProgramData,$env:ProgramFiles,$env:USERPROFILE)) {
        if ($protected -and $folder -ieq $protected.TrimEnd([char]92, '/')) { throw "Protected working directory: $folder" }
    }
    if (Test-Path -LiteralPath (Join-Path $folder '.git')) { throw 'Git checkout detected; refusing deletion.' }
    $files = @(Get-ChildItem -LiteralPath $folder -Recurse -Force -ErrorAction Stop)
    if (@($files | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -gt 0) {
        throw 'Working directory contains junctions or symlinks; refusing deletion.'
    }
    $archive = Join-Path $stateDir ('Archive\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff'))
    [IO.Directory]::CreateDirectory($archive) | Out-Null
    $relevant = @($files | Where-Object { -not $_.PSIsContainer -and $_.Extension -in @('.json','.log','.ps1') })
    foreach ($file in $relevant) {
        $relative = $file.FullName.Substring($folder.Length).TrimStart([char]92, '/')
        $target = Join-Path $archive $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -ErrorAction Stop
        if ((Get-Item -LiteralPath $target -ErrorAction Stop).Length -ne $file.Length) {
            throw "Archive verification failed: $relative"
        }
    }
    $manifest = [ordered]@{ ArchivedUtc = [DateTime]::UtcNow.ToString('o'); Source = $folder;
        Destination = $archive; Files = @($relevant | ForEach-Object { $_.FullName.Substring($folder.Length).TrimStart([char]92, '/') }) }
    [IO.File]::WriteAllText((Join-Path $archive 'manifest.json'), ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    Log "Archived $($relevant.Count) JSON/log/script files to $archive"
    # User explicitly authorized deletion of the ENTIRE directory, including installers and other files.
    Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction Stop
    if (Test-Path -LiteralPath $folder) { throw "Working directory still exists: $folder" }
    Log "Deleted working directory $folder"
}
function Assert-Qemu {
    $vm = Get-CimInstance Win32_ComputerSystem
    $mfg=[string]$vm.Manufacturer; $model=[string]$vm.Model
    if ($mfg -match 'VMware' -or $model -match 'VMware') { SaveStatus 'waiting-for-proxmox' $false 'Source still running under VMware.'; Log 'Source still on VMware; waiting for Proxmox first boot.'; exit 0 }
    if (-not ($mfg -match 'QEMU|Red Hat|KVM' -or $model -match 'QEMU|KVM|Standard PC.*Q35')) { SaveStatus 'waiting-for-proxmox' $false "Unknown platform $mfg / $model"; Log "Unexpected platform $mfg / $model; waiting."; exit 0 }
}
function Invoke-Stage([string]$name,[string[]]$argv,[scriptblock]$verify) {
    $file = Join-Path $stateDir ($name + '.json')
    SaveStatus $name $false "Running $name"
    Log "START $name"
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
    # Avoid treating redirected native stderr as a terminating PowerShell error.
    $previousEap = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & $guestPrep @argv --output $file 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previousEap }
    Log "$name exit=$exitCode output=$($output.Trim())"
    if (-not (Test-Path -LiteralPath $file)) { throw "$name did not produce JSON (exit $exitCode). Output: $($output.Trim())" }
    $result = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    if ($exitCode -ne 0 -or -not $result.Success) { throw "$name failed (exit $exitCode): $($result.Message); details: $file" }
    if (-not (& $verify $result)) { throw "$name verification failed: $($result.Message); details: $file" }
    Log "VERIFIED $name"
    SaveStatus $name $true "Verified $name"
}
try {
    Assert-Qemu
    if (-not (Test-Path -LiteralPath $guestPrep -PathType Leaf)) { throw "GuestPrep EXE not found: $guestPrep" }
    # Allow Plug-and-Play and storage services to settle after boot.
    $ready=$false
    for($i=0; $i -lt 16; $i++) {
        $real=@(Get-PnpDevice -PresentOnly -Class SCSIAdapter -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match '^PCI\\VEN_1AF4&DEV_(1004|1048)&' -and $_.Status -eq 'OK' })
        if($real.Count -gt 0) { $ready=$true; break }
        Start-Sleep -Seconds 15
    }
    if (-not $ready) { throw 'No healthy real VirtIO SCSI PCI controller became available within four minutes.' }
    Log 'Real VirtIO SCSI controller verified.'

    if ($networkJson) {
        if (-not (Test-Path -LiteralPath $networkJson -PathType Leaf)) { throw "Network capture missing: $networkJson" }
        $mapping = $explicitMapping
        if (-not $mapping) {
            $dir = Split-Path -Parent $networkJson
            foreach($candidate in @((Join-Path $dir 'mac-table.json'),(Join-Path $dir 'mac-mapping.json'),(Join-Path (Split-Path -Parent $PSCommandPath) 'mac-table.json'),(Join-Path (Split-Path -Parent $PSCommandPath) 'mac-mapping.json'))) {
                if (Test-Path -LiteralPath $candidate -PathType Leaf) { $mapping=$candidate; break }
            }
        }
        $netArgs = @('restore-network','--network-json',$networkJson,'--apply','--yes')
        if ($mapping) {
            if (-not (Test-Path -LiteralPath $mapping -PathType Leaf)) { throw "Explicit MAC mapping missing: $mapping" }
            $netArgs += @('--mapping-json',$mapping)
            $rawMap = Get-Content -LiteralPath $mapping -Raw -Encoding UTF8 | ConvertFrom-Json
            $mapList = if ($rawMap -is [array]) { @($rawMap) } else { @($rawMap.NetworkMappings) }
            if ($mapList.Count -eq 0 -or @($mapList | Where-Object { -not $_.SourceMac -or -not $_.DestinationMac }).Count -gt 0) { throw "Invalid MAC mapping file (expected SourceMac/DestinationMac entries): $mapping" }
            Log "Using MAC mapping: $mapping"
        } else { Log 'No MAC mapping file; restore will match source and destination MAC exactly.' }
        Invoke-Stage 'network' $netArgs {
            param($r)
            if (-not $r.Success -or @($r.Errors).Count -ne 0) { return $false }
            foreach ($adapter in @($r.Mappings)) {
                $idx = [int]$adapter.InterfaceIndex
                $actual = @(Get-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction Stop |
                    ForEach-Object { '{0}/{1}' -f $_.IPAddress,$_.PrefixLength })
                foreach ($ip in @($adapter.IPv4)) { if ($ip -notin $actual) { return $false } }
                $routes = @(Get-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction Stop |
                    Where-Object { $_.DestinationPrefix -eq '0.0.0.0/0' } |
                    ForEach-Object { $_.NextHop })
                foreach ($gw in @($adapter.DefaultGateways)) { if ($gw -notin $routes) { return $false } }
                if (@($adapter.DnsServers).Count -gt 0) {
                    $dns = @(Get-DnsClientServerAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction Stop |
                        ForEach-Object { $_.ServerAddresses })
                    foreach ($server in @($adapter.DnsServers)) { if ($server -notin $dns) { return $false } }
                }
            }
            return $true
        }
    } else { Log 'No --network-json supplied; network restoration skipped.' }

    Invoke-Stage 'cleanup-vmware' @('cleanup-vmware','--apply','--yes','--cleanup-vmci-driver') {
        param($r) $r.VerificationPassed -eq $true -and @($r.RemainingDevices).Count -eq 0 -and
            (-not $r.VmciPublishedInf -or $r.VmciDriverRemoved -eq $true) -and
            $r.VmciServiceRemoved -eq $true -and
            $null -eq (Get-Service vmci -ErrorAction SilentlyContinue)
    }
    Invoke-Stage 'cleanup-guestprep' @('cleanup-guestprep','--apply','--yes') {
        param($r) $r.VerificationPassed -eq $true -and @($r.RemainingGuestPrepDevices).Count -eq 0 -and $r.HealthyVirtioScsiControllers -ge 1
    }
    # Independent final verification, before removing the scheduled task.
    $controller = @(Get-PnpDevice -PresentOnly -Class SCSIAdapter | Where-Object { $_.InstanceId -match '^PCI\\VEN_1AF4&DEV_(1004|1048)&' -and $_.Status -eq 'OK' })
    if ($controller.Count -lt 1) { throw 'Final verification: no healthy VirtIO SCSI controllers.' }
    if (@(Get-PnpDevice | Where-Object { $_.InstanceId -match '^ROOT\\VIOSCSI\\' }).Count -gt 0) { throw 'Final verification: GuestPrep phantom controller remains.' }
    $tools = Get-Service VMTools -ErrorAction SilentlyContinue
    if ($tools) { throw 'Final verification: VMTools service remains.' }
    $vmci = Get-Service vmci -ErrorAction SilentlyContinue
    if ($vmci) { throw 'Final verification: VMCI service still registered.' }
    $agent = Get-Service QEMU-GA -ErrorAction SilentlyContinue
    if (-not $agent -or $agent.Status -ne 'Running') { throw 'Final verification: QEMU Guest Agent is not running.' }
    Log 'All stages verified; removing startup task.'
    & (Join-Path $env:WINDIR 'System32\schtasks.exe') /Delete /TN $taskName /F | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Scheduled task deletion failed with exit code $LASTEXITCODE" }
    Archive-And-CleanupWorkingDirectory
    SaveStatus 'complete' $true 'Post-migration stages verified; task removed; working directory cleanup completed.'
    Log 'SUCCESS: all stages verified and task removed.' 
} catch {
    $message = $_.Exception.Message
    Log "FAILED: $message"
    SaveStatus 'failed' $false $message
    # Keep startup task registered for a possible next-boot retry. Never reboot automatically.
    exit 1
}
""";
}

// Integrated maintenance commands for BareProx.GuestPrep (Windows PowerShell 5.1 backend).
// Copyright (C) 2025-2026 Tobias Modig. SPDX-License-Identifier: AGPL-3.0-or-later.
// This integration retains the established PowerShell implementations for now;
// it does NOT constitute a native C# rewrite of SetupAPI/network cmdlets.
internal static class GuestMaintenance
{
    private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        "restore-network", "uninstall-vmware-tools", "detect-vmware-tools", "disable-vmci",
        "cleanup-vmware", "cleanup-guestprep", "remove-vmci-package"
    };
    public static bool IsCommand(string name) => Commands.Contains(name);
    public static void PrintHelp()
    {
        Console.WriteLine("Maintenance (all modes emit JSON; --output PATH saves it):");
        Console.WriteLine("  restore-network --network-json PATH [--mapping-json PATH] [--apply] [--replace-existing-ipv4] [--yes]");
        Console.WriteLine("  detect-vmware-tools [--output PATH]");
        Console.WriteLine("  uninstall-vmware-tools [--apply] [--yes] [--disable-vmci] [--allow-reboot-required] [--log-dir PATH]");
        Console.WriteLine("  disable-vmci [--apply] [--yes] [--allow-reboot-required]");
        Console.WriteLine("  cleanup-vmware [--apply] [--yes] [--cleanup-vmci-driver]  (also deletes orphaned vmci service)");
        Console.WriteLine("  cleanup-guestprep [--apply] [--yes]");
        Console.WriteLine("  remove-vmci-package --vmci-inf oemN.inf [--apply] [--yes]");
        Console.WriteLine("Network restore defaults to preview; other destructive modes use -WhatIf without --apply.");
        Console.WriteLine("--yes bypasses interactive confirmations only when --apply is also supplied.");
        Console.WriteLine("--disable-vmci disables VMCI startup after VMware Tools uninstall; final VMware-side action before shutdown and Proxmox boot.");
    }
    private static bool Flag(string[] a, string name) => a.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
    private static string? Value(string[] a, string name)
    {
        int i = Array.FindIndex(a, x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return null;
        if (i + 1 >= a.Length || a[i + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Missing value for {name}");
        return a[i + 1];
    }
    private static void AddValue(List<string> list, string switchName, string? value)
    {
        if (value is not null) { list.Add(switchName); list.Add(value); }
    }
    public static int Run(string[] args)
    {
        string mode = args[0].ToLowerInvariant();
        bool apply = Flag(args, "--apply"), yes = Flag(args, "--yes");
        if (yes && !apply) throw new ArgumentException("--yes requires --apply");
        if (apply && !yes && mode != "detect-vmware-tools") throw new ArgumentException("--apply requires --yes for non-interactive execution");
        string? output = Value(args, "--output");
        var switches = new List<string>();
        bool network = mode == "restore-network";
        if (network)
        {
            string capture = Value(args, "--network-json")
                ?? throw new ArgumentException("--network-json is required");
            AddValue(switches, "-NetworkJson", capture);
            AddValue(switches, "-MappingJson", Value(args, "--mapping-json"));
            if (apply) switches.Add("-Apply");
            if (Flag(args, "--replace-existing-ipv4"))
            {
                if (!apply) throw new ArgumentException("--replace-existing-ipv4 requires --apply");
                switches.Add("-ReplaceExistingIPv4");
            }
        }
        else
        {
            switches.Add(mode switch
            {
                "detect-vmware-tools" => "",
                "uninstall-vmware-tools" => "-Uninstall",
                "disable-vmci" => "-DisableVmci",
                "cleanup-vmware" => "-RemoveObsoleteDevices",
                "cleanup-guestprep" => "-RemoveGuestPrepDevices",
                "remove-vmci-package" => "-RemoveVmciPackage",
                _ => throw new ArgumentException("Unsupported maintenance command")
            });
            switches.RemoveAll(string.IsNullOrEmpty);
            if (mode == "remove-vmci-package")
                AddValue(switches, "-VmciPublishedInf", Value(args, "--vmci-inf")
                    ?? throw new ArgumentException("--vmci-inf is required"));
            if (Flag(args, "--cleanup-vmci-driver"))
            {
                if (mode != "cleanup-vmware") throw new ArgumentException("--cleanup-vmci-driver requires cleanup-vmware");
                switches.Add("-CleanupVmciDriver");
            }
            if (Flag(args, "--disable-vmci"))
            {
                if (mode != "uninstall-vmware-tools") throw new ArgumentException("--disable-vmci requires uninstall-vmware-tools");
                switches.Add("-DisableVmciAfterUninstall");
            }
            if (Flag(args, "--allow-reboot-required")) switches.Add("-AllowRebootRequired");
            AddValue(switches, "-LogDirectory", Value(args, "--log-dir"));
            if (!apply && mode != "detect-vmware-tools") switches.Add("-WhatIf");
        }
        if (yes && mode != "detect-vmware-tools") switches.Add("-Confirm:$false");
        // Every invocation needs a clean machine-readable result. Native utilities
        // (pnputil/sc.exe) may write informational lines to stdout before the JSON.
        bool temporaryResult = string.IsNullOrWhiteSpace(output);
        string resultPath = temporaryResult
            ? Path.Combine(Path.GetTempPath(), "BareProx-result-" + Guid.NewGuid().ToString("N") + ".json")
            : Path.GetFullPath(output!);
        AddValue(switches, "-OutputJson", resultPath);
        string script = network ? RestoreNetworkPowerShell : VmwareCleanupPowerShell;
        try { return InvokeScript(script, switches, resultPath); }
        finally
        {
            if (temporaryResult)
            {
                try { File.Delete(resultPath); } catch (IOException) { }
            }
        }
    }
    private static int InvokeScript(string contents, List<string> args, string resultPath)
    {
        // PowerShell 5.1 is built into supported Server versions; no external .ps1 deployment.
        // A randomized temp script avoids command-line length limits and shell interpolation.
        string temp = Path.Combine(Path.GetTempPath(), "BareProx-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(temp, contents, new UTF8Encoding(false));
        // Prevent a stale JSON file from satisfying verification if PowerShell dies early.
        if (File.Exists(resultPath)) File.Delete(resultPath);
        try
        {
            string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            var psi = new ProcessStartInfo(powershell)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(temp);
            // -File cannot reliably convert -Confirm:$false to a PowerShell boolean;
            // use -EncodedCommand when --yes was selected.
            if (args.Contains("-Confirm:$false", StringComparer.Ordinal))
            {
                psi.ArgumentList.Clear();
                string literal = "'" + temp.Replace("'", "''") + "'";
                var expression = new StringBuilder("& " + literal);
                for (int i = 0; i < args.Count; i++)
                {
                    string item = args[i];
                    if (item.StartsWith("-", StringComparison.Ordinal)) expression.Append(' ').Append(item);
                    else expression.Append(" '").Append(item.Replace("'", "''")).Append('\'');
                }
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-EncodedCommand");
                psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(expression.ToString())));
            }
            else
                foreach (string arg in args) psi.ArgumentList.Add(arg);
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Cannot start Windows PowerShell");
            Task<string> stdout = proc.StandardOutput.ReadToEndAsync();
            Task<string> stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(600000))
            {
                proc.Kill(entireProcessTree: true);
                throw new TimeoutException("Maintenance command timed out after 10 minutes");
            }
            Task.WaitAll(stdout, stderr);
            if (!string.IsNullOrWhiteSpace(stdout.Result)) Console.Write(stdout.Result);
            if (!string.IsNullOrWhiteSpace(stderr.Result) && !stderr.Result.TrimStart().StartsWith("#< CLIXML", StringComparison.Ordinal))
                Console.Error.Write(stderr.Result);
            if (proc.ExitCode != 0) return proc.ExitCode;
            // The result file contains only JSON; stdout can legitimately contain
            // pnputil, msiexec or sc.exe diagnostics before the final JSON object.
            if (!File.Exists(resultPath))
            {
                Console.Error.WriteLine($"Maintenance returned zero but did not write its result JSON: {resultPath}");
                return 3;
            }
            try
            {
                using JsonDocument response = JsonDocument.Parse(File.ReadAllText(resultPath));
                if (response.RootElement.ValueKind != JsonValueKind.Object ||
                    !response.RootElement.TryGetProperty("Success", out JsonElement success) ||
                    success.ValueKind != JsonValueKind.True)
                {
                    Console.Error.WriteLine("Maintenance result JSON does not report Success=true.");
                    return 3;
                }
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"Maintenance result JSON is invalid: {ex.Message}");
                return 3;
            }
            return 0;
        }
        finally
        {
            try { File.Delete(temp); }
            catch (IOException) { /* OS will clear temp file */ }
        }
    }
    private const string RestoreNetworkPowerShell = """
#requires -version 5.1
<#
.SYNOPSIS
  Restore portable Windows network settings captured by BareProx.GuestPrep.
.DESCRIPTION
  Reads schema v1 network.json. Matches source adapters to destination adapters
  by MAC address, or using explicit SourceMac -> DestinationMac mapping JSON.
  Default mode is read-only. -Apply makes changes; -ReplaceExistingIPv4 is
  needed if a destination adapter already has a different non-APIPA IPv4 address.
  Only static IPv4 addresses, default gateway, IPv4 DNS, and IPv4 MTU are applied.
  Adapter GUIDs/indices, hardware-specific driver settings and auto-generated
  routes, IPv6 link-local addresses and legacy IPv6 DNS placeholders are ignored.
  Designed for Windows Server 2016/2019/2022/2025 with NetTCPIP/NetAdapter modules.
  WARNING: Applying settings can disconnect RDP/guest-agent network access.
.EXAMPLE
  .\Restore-BareProxNetwork.ps1 -NetworkJson C:\test\network.json
.EXAMPLE
  .\Restore-BareProxNetwork.ps1 -NetworkJson C:\test\network.json -MappingJson C:\test\mac-mapping.json
.EXAMPLE
  .\Restore-BareProxNetwork.ps1 -NetworkJson C:\test\network.json -MappingJson C:\test\mac-mapping.json -Apply -Confirm:$false -OutputJson C:\test\network-restore-result.json
.NOTES
  Copyright (C) 2025-2026 Tobias Modig.
  SPDX-License-Identifier: AGPL-3.0-or-later
  Licensed under GNU Affero General Public License v3 or later.
  Distributed without warranty. See https://www.gnu.org/licenses/agpl-3.0.html
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)][string]$NetworkJson,
    [string]$MappingJson,
    [switch]$Apply,
    [switch]$ReplaceExistingIPv4,
    [string]$OutputJson
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Get-Prop($Object, [string]$Name, $Default = $null) {
    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    return $property.Value
}
function Normalize-Mac([string]$Mac) {
    if ([string]::IsNullOrWhiteSpace($Mac)) { return $null }
    $hex = $Mac -replace '[^0-9A-Fa-f]', ''
    if ($hex.Length -ne 12 -or $hex -notmatch '^[0-9a-fA-F]{12}$' -or $hex -eq '000000000000') { return $null }
    return ((0,2,4,6,8,10 | ForEach-Object { $hex.Substring($_, 2).ToUpperInvariant() }) -join ':')
}
function Write-JsonResult($Data) {
    $json = ConvertTo-Json -InputObject $Data -Depth 20
    Write-Output $json
    if ($OutputJson) {
        $path = [IO.Path]::GetFullPath($OutputJson)
        $dir = [IO.Path]::GetDirectoryName($path)
        if (-not [IO.Directory]::Exists($dir)) { [IO.Directory]::CreateDirectory($dir) | Out-Null }
        [IO.File]::WriteAllText($path, $json, [Text.UTF8Encoding]::new($false))
    }
}
function Get-IPv4Addresses($InterfaceIndex) {
    @(Get-NetIPAddress -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
      Where-Object { $_.IPAddress -and $_.IPAddress -notlike '169.254.*' } |
      ForEach-Object { '{0}/{1}' -f $_.IPAddress,$_.PrefixLength })
}
function Get-TargetRoutes($InterfaceIndex) {
    @(Get-NetRoute -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
      Where-Object { $_.DestinationPrefix -eq '0.0.0.0/0' -and $_.NextHop -and $_.NextHop -ne '0.0.0.0' })
}
function Get-TargetDns($InterfaceIndex) {
    $r = Get-DnsClientServerAddress -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
    return @($r | ForEach-Object { @($_.ServerAddresses) } | Where-Object { $_ })
}
function Test-IPv4Address([string]$Value) {
    $address = $null
    return ([Net.IPAddress]::TryParse($Value, [ref]$address) -and $address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork)
}

$result = [ordered]@{
    SchemaVersion = 1
    ComputerName = $env:COMPUTERNAME
    CapturedAtUtc = [DateTime]::UtcNow.ToString('o')
    Mode = $(if ($Apply) { 'Apply' } else { 'Preview' })
    NetworkJson = $NetworkJson
    MappingJson = $MappingJson
    Mappings = @()
    Actions = @()
    Warnings = @()
    Errors = @()
    Success = $false
    ChangesMade = $false
    Message = ''
}
try {
    if (-not (Test-Path -LiteralPath $NetworkJson -PathType Leaf)) { throw "Network JSON not found: $NetworkJson" }
    $capture = Get-Content -LiteralPath $NetworkJson -Raw -Encoding UTF8 | ConvertFrom-Json
    if ((Get-Prop $capture 'SchemaVersion') -ne 1) { throw 'Unsupported network.json schema version (expected 1).' }
    $sourceByMac = Get-Prop $capture 'NetworkAdaptersByMac'
    if ($null -eq $sourceByMac) { throw 'NetworkAdaptersByMac was not found in network.json.' }
    $sourceKeys = @($sourceByMac.PSObject.Properties.Name)
    if ($sourceKeys.Count -eq 0) { throw 'No source adapters with MAC addresses were captured.' }

    $mappings = @()
    if ($MappingJson) {
        if (-not (Test-Path -LiteralPath $MappingJson -PathType Leaf)) { throw "Mapping JSON not found: $MappingJson" }
        $rawMapping = Get-Content -LiteralPath $MappingJson -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($rawMapping -is [array]) { $mappings = @($rawMapping) }
        else {
            $property = Get-Prop $rawMapping 'NetworkMappings'
            if ($null -eq $property) { throw 'Mapping file must be an array or contain NetworkMappings array.' }
            $mappings = @($property)
        }
    } else {
        foreach ($mac in $sourceKeys) { $mappings += [pscustomobject]@{ SourceMac = $mac; DestinationMac = $mac } }
    }
    if ($mappings.Count -eq 0) { throw 'No MAC mappings found.' }
    $destinationAdapters = @(Get-NetAdapter -IncludeHidden -ErrorAction Stop)
    $resolved = @()
    $sourceSeen = @{}
    $destSeen = @{}

    foreach ($map in $mappings) {
        $sourceMac = Normalize-Mac ([string](Get-Prop $map 'SourceMac'))
        $destMac = Normalize-Mac ([string](Get-Prop $map 'DestinationMac'))
        if (-not $sourceMac -or -not $destMac) { throw 'Mapping has invalid SourceMac or DestinationMac.' }
        if ($sourceSeen.ContainsKey($sourceMac) -or $destSeen.ContainsKey($destMac)) { throw "Duplicate source/destination mapping involving $sourceMac -> $destMac." }
        $sourceSeen[$sourceMac] = $true; $destSeen[$destMac] = $true
        $sourceEntry = @($sourceKeys | Where-Object { (Normalize-Mac $_) -eq $sourceMac })
        if ($sourceEntry.Count -ne 1) { throw "Source MAC $sourceMac does not uniquely match the capture." }
        $source = @(Get-Prop $sourceByMac $sourceEntry[0])
        if ($source.Count -ne 1) { throw "Source MAC $sourceMac has $($source.Count) adapters; cannot select safely." }
        $target = @($destinationAdapters | Where-Object { (Normalize-Mac ([string]$_.MacAddress)) -eq $destMac })
        if ($target.Count -ne 1) { throw "Destination MAC $destMac matches $($target.Count) adapters; refusing to guess." }
        $nic = $target[0]
        $index = [int]$nic.ifIndex
        $sourceIPv4 = @((Get-Prop $source[0] 'IPAddresses' @()) | Where-Object { (Get-Prop $_ 'AddressFamily') -eq 'IPv4' -and (Get-Prop $_ 'PrefixOrigin') -eq 'Manual' -and (Test-IPv4Address ([string](Get-Prop $_ 'IPAddress'))) } | ForEach-Object { '{0}/{1}' -f $_.IPAddress,$_.PrefixLength })
        if ($sourceIPv4.Count -ne (@($sourceIPv4 | Select-Object -Unique)).Count) { throw "Repeated IPv4 address for $sourceMac." }
        $ipInterface = @((Get-Prop $source[0] 'IPInterfaces' @()) | Where-Object { (Get-Prop $_ 'AddressFamily') -eq 'IPv4' })
        if ($ipInterface.Count -gt 1) { throw "Multiple source IPv4 interface records for $sourceMac." }
        $dhcp = if ($ipInterface.Count -eq 1) { [string](Get-Prop $ipInterface[0] 'Dhcp') } else { $null }
        if ($dhcp -notin @('Enabled','Disabled')) { throw "Unable to determine IPv4 DHCP state for $sourceMac." }
        $gatewayRecords = @((Get-Prop $source[0] 'Routes' @()) | Where-Object { (Get-Prop $_ 'AddressFamily') -eq 'IPv4' -and (Get-Prop $_ 'DestinationPrefix') -eq '0.0.0.0/0' -and (Test-IPv4Address ([string](Get-Prop $_ 'NextHop'))) })
        $gateways = @($gatewayRecords | ForEach-Object { [string]$_.NextHop } | Select-Object -Unique)
        if ($gateways.Count -gt 1) { throw "Multiple distinct default gateways for $sourceMac; manual handling required." }
        $dns = @((Get-Prop $source[0] 'DnsServers' @()) | Where-Object { [string](Get-Prop $_ 'AddressFamily') -in @('2','IPv4') } | ForEach-Object { @(Get-Prop $_ 'ServerAddresses' @()) } | Where-Object { Test-IPv4Address ([string]$_) } | Select-Object -Unique)
        $mtu = $null
        if ($ipInterface.Count -eq 1) { $mtu = Get-Prop $ipInterface[0] 'NlMtu' }
        if ($null -ne $mtu -and ([int]$mtu -lt 576 -or [int]$mtu -gt 9000)) { throw "Unreasonable IPv4 MTU for ${sourceMac}: $mtu" }
        $currentIPs = @(Get-IPv4Addresses $index)
        $targetDhcp = (Get-NetIPInterface -InterfaceIndex $index -AddressFamily IPv4 -ErrorAction Stop).Dhcp.ToString()
        $unwanted = @($currentIPs | Where-Object { $_ -notin $sourceIPv4 })
        if ($Apply -and -not $ReplaceExistingIPv4 -and $unwanted.Count -gt 0) { throw "Destination $($nic.Name) has unrelated IPv4 addresses: $($unwanted -join ', '); use -ReplaceExistingIPv4 only with console access." }
        $resolved += [pscustomobject]@{ SourceMac=$sourceMac; DestinationMac=$destMac; SourceName=[string](Get-Prop $source[0] 'Name'); DestinationName=$nic.Name; InterfaceIndex=$index; DHCP=$dhcp; CurrentDhcp=$targetDhcp; IPv4=$sourceIPv4; CurrentIPv4=$currentIPs; UnwantedIPv4=$unwanted; DefaultGateways=$gateways; DnsServers=$dns; Mtu=$mtu }
    }
    $result.Mappings = @($resolved)
    if (-not $Apply) {
        $result.Success = $true
        $result.Message = 'Read-only preview completed. Use -Apply to make changes.'
        Write-JsonResult $result
        return
    }
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run PowerShell as Administrator to apply settings.' }
    foreach ($r in $resolved) {
        if (-not $PSCmdlet.ShouldProcess("$($r.DestinationName) [$($r.DestinationMac)]", 'Apply captured network configuration')) {
            $result.Warnings += "Skipped $($r.DestinationName) (confirmation declined/WhatIf)."
            continue
        }
        $idx = $r.InterfaceIndex
        $result.ChangesMade = $true
        if ($r.DHCP -eq 'Enabled') {
            if ($r.CurrentDhcp -ne 'Enabled') { Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -Dhcp Enabled -ErrorAction Stop; $result.Actions += "Enabled IPv4 DHCP on $($r.DestinationName)" }
            Set-DnsClientServerAddress -InterfaceIndex $idx -ResetServerAddresses -ErrorAction Stop
            $result.Actions += "Restored automatic DNS on $($r.DestinationName)"
        } else {
            if ($r.CurrentDhcp -ne 'Disabled') { Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -Dhcp Disabled -ErrorAction Stop; $result.Actions += "Disabled IPv4 DHCP on $($r.DestinationName)" }
            if ($ReplaceExistingIPv4) {
                foreach ($old in $r.UnwantedIPv4) {
                    $addr = $old.Split('/')[0]
                    Remove-NetIPAddress -InterfaceIndex $idx -IPAddress $addr -Confirm:$false -ErrorAction Stop
                    $result.Actions += "Removed previous IPv4 $old from $($r.DestinationName)"
                }
            }
            foreach ($addr in $r.IPv4) {
                if ($addr -notin @(Get-IPv4Addresses $idx)) {
                    $parts = $addr.Split('/')
                    New-NetIPAddress -InterfaceIndex $idx -IPAddress $parts[0] -PrefixLength ([int]$parts[1]) -ErrorAction Stop | Out-Null
                    $result.Actions += "Added IPv4 $addr on $($r.DestinationName)"
                }
            }
            $routes = @(Get-TargetRoutes $idx)
            foreach ($route in $routes) {
                if ($route.NextHop -notin $r.DefaultGateways) { Remove-NetRoute -InterfaceIndex $idx -DestinationPrefix '0.0.0.0/0' -NextHop $route.NextHop -Confirm:$false -ErrorAction Stop; $result.Actions += "Removed old default gateway $($route.NextHop)" }
            }
            foreach ($gateway in $r.DefaultGateways) {
                if (@(Get-TargetRoutes $idx | Where-Object { $_.NextHop -eq $gateway }).Count -eq 0) { New-NetRoute -InterfaceIndex $idx -DestinationPrefix '0.0.0.0/0' -NextHop $gateway -ErrorAction Stop | Out-Null; $result.Actions += "Added default gateway $gateway on $($r.DestinationName)" }
            }
            if ($r.DnsServers.Count -gt 0) { Set-DnsClientServerAddress -InterfaceIndex $idx -ServerAddresses $r.DnsServers -ErrorAction Stop; $result.Actions += "Set IPv4 DNS on $($r.DestinationName): $($r.DnsServers -join ', ')" }
            else { $result.Warnings += "No explicit IPv4 DNS captured for $($r.DestinationName); existing DNS settings left unchanged." }
        }
        if ($null -ne $r.Mtu) {
            $actualMtu = [int](Get-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction Stop).NlMtu
            if ($actualMtu -ne [int]$r.Mtu) { Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -NlMtuBytes ([int]$r.Mtu) -ErrorAction Stop; $result.Actions += "Set IPv4 MTU $($r.Mtu) on $($r.DestinationName)" }
        }
    }
    $result.Success = $true
    $result.Message = 'Network apply operations completed. Validate connectivity, DNS and routes before cutover.'
    Write-JsonResult $result
} catch {
    $result.Errors += $_.Exception.Message
    $result.Message = 'Network restore failed. Some changes may already have been applied; inspect Actions and Errors.'
    Write-JsonResult $result
    exit 1
}
""";
    private const string VmwareCleanupPowerShell = """
#requires -version 5.1
# BareProx - VMware Tools uninstall and post-migration VMware device cleanup helper
# Copyright (C) 2025-2026 Tobias Modig
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# This program is free software: you can redistribute it and/or modify it
# under the terms of the GNU Affero General Public License as published by
# the Free Software Foundation, either version 3 of the License, or (at your
# option) any later version. This program comes WITHOUT ANY WARRANTY.
# See https://www.gnu.org/licenses/ for the complete license.
<#
.SYNOPSIS
  Detect/uninstall VMware Tools; separately clean up remaining VMware VMCI driver on Proxmox.
.DESCRIPTION
  Default mode is read-only. -Uninstall removes MSI-registered VMware Tools, with reboot
  suppressed. -DisableVmci is a separate post-migration action: it requires Windows to
  report QEMU/KVM as the current platform, disables the VMCI driver and requests an
  operator-managed reboot. -RemoveVmciPackage is a second, post-reboot operation and
  requires an explicitly specified verified oemN.inf; it never uses /force.
  -RemoveObsoleteDevices removes verified non-present VMware hardware instances through
  Windows SetupAPI (no DevCon required). Add -CleanupVmciDriver to also discover and
  remove the unused VMware VMCI driver package using pnputil, without /force.
  -RemoveGuestPrepDevices removes only ROOT\VIOSCSI phantom controllers once a working real
  VirtIO SCSI PCI controller is confirmed on QEMU/KVM. The boot driver is retained.
  Do not perform driver cleanup while still on VMware. No mode reboots the machine.
.EXAMPLE
  .\Uninstall-VMwareTools.ps1 -OutputJson C:\test\detect.json
.EXAMPLE
  .\Uninstall-VMwareTools.ps1 -Uninstall -Confirm:$false -OutputJson C:\test\uninstall.json
.EXAMPLE
  .\Uninstall-VMwareTools.ps1 -DisableVmci -Confirm:$false -OutputJson C:\test\disable.json
.EXAMPLE
  .\Uninstall-VMwareTools.ps1 -RemoveVmciPackage -VmciPublishedInf oem2.inf -Confirm:$false
.EXAMPLE
  .\Uninstall-VMwareTools.ps1 -RemoveObsoleteDevices -WhatIf
.EXAMPLE
  .\Uninstall-VMwareTools.ps1 -RemoveObsoleteDevices -CleanupVmciDriver -Confirm:$false -OutputJson C:\test\device-cleanup.json
.EXAMPLE
  .\Uninstall-VMwareTools.ps1 -RemoveGuestPrepDevices -WhatIf -OutputJson C:\test\phantom-preview.json
.EXAMPLE
  .\Uninstall-VMwareTools.ps1 -RemoveGuestPrepDevices -Confirm:$false -OutputJson C:\test\phantom-cleanup.json
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High', DefaultParameterSetName = 'Detect')]
param(
    [Parameter(ParameterSetName = 'Uninstall')][switch]$Uninstall,
    [Parameter(ParameterSetName = 'Uninstall')][switch]$DisableVmciAfterUninstall,
    [Parameter(ParameterSetName = 'DisableVmci')][switch]$DisableVmci,
    [Parameter(ParameterSetName = 'RemoveVmciPackage')][switch]$RemoveVmciPackage,
    [Parameter(ParameterSetName = 'RemoveObsoleteDevices')][switch]$RemoveObsoleteDevices,
    [Parameter(ParameterSetName = 'RemoveGuestPrepDevices')][switch]$RemoveGuestPrepDevices,
    [Parameter(ParameterSetName = 'RemoveObsoleteDevices')][switch]$CleanupVmciDriver,
    [Parameter(ParameterSetName = 'RemoveVmciPackage', Mandatory = $true)][ValidatePattern('^oem[0-9]+\.inf$')][string]$VmciPublishedInf,
    [switch]$AllowRebootRequired,
    [string]$LogDirectory = "$env:ProgramData\BareProx\Logs",
    [string]$OutputJson
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Get-ValueSafe([object]$Item, [string]$Name) {
    if ($null -eq $Item) { return $null }
    $p = $Item.PSObject.Properties[$Name]
    if ($null -eq $p) { return $null }
    return $p.Value
}
function Get-ToolsEntries {
    foreach ($path in @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')) {
        foreach ($item in @(Get-ItemProperty -Path $path -ErrorAction SilentlyContinue)) {
            $name = [string](Get-ValueSafe $item 'DisplayName')
            if ($name -notmatch '^VMware Tools(?:\s|$)') { continue }
            $pub = [string](Get-ValueSafe $item 'Publisher')
            if ($pub -and $pub -notmatch 'VMware|Broadcom') { continue }
            $keyName = [string](Get-ValueSafe $item 'PSChildName')
            $cmd = [string](Get-ValueSafe $item 'UninstallString')
            $guid = $null
            if ($keyName -match '^\{[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\}$') { $guid = $keyName.ToUpperInvariant() }
            elseif ($cmd -match '\{[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\}') { $guid = $Matches[0].ToUpperInvariant() }
            [pscustomobject]@{ DisplayName=$name; DisplayVersion=[string](Get-ValueSafe $item 'DisplayVersion'); Publisher=$pub; ProductCode=$guid; WindowsInstaller=(Get-ValueSafe $item 'WindowsInstaller'); RegistryPath=[string](Get-ValueSafe $item 'PSPath') }
        }
    }
}
function Get-VMCIService {
    $key = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\vmci' -ErrorAction SilentlyContinue
    if ($null -eq $key) { return $null }
    # Win32_SystemDriver can temporarily report no instance / Unknown after a driver
    # package has been removed, even if SCM confirms the driver is STOPPED.
    # Use the Windows Service Control Manager directly. Fail closed on errors.
    $sc = Join-Path $env:WINDIR 'System32\sc.exe'
    $lines = @(& $sc query vmci 2>&1)
    $code = $LASTEXITCODE
    $state = 'Unknown'
    if ($code -eq 0) {
        foreach ($line in $lines) {
            if ([string]$line -match '^\s*STATE\s*:\s*(\d+)\s+([A-Za-z_]+)\b') {
                $number = [int]$Matches[1]
                $label = $Matches[2].ToUpperInvariant()
                if ($number -eq 1 -and $label -eq 'STOPPED') { $state = 'Stopped' }
                elseif ($number -eq 4 -and $label -eq 'RUNNING') { $state = 'Running' }
                else { $state = $label }
                break
            }
        }
    }
    [pscustomobject]@{ Name='vmci'; Start=[int](Get-ValueSafe $key 'Start'); State=$state; PathName=[string](Get-ValueSafe $key 'ImagePath') }
}
function Get-Platform {
    $sys = Get-CimInstance Win32_ComputerSystem
    $mfg = [string]$sys.Manufacturer; $model = [string]$sys.Model
    $isQemu = ($mfg -match 'QEMU|Red Hat|KVM' -or $model -match 'QEMU|KVM|Standard PC.*Q35')
    $isVmware = ($mfg -match 'VMware' -or $model -match 'VMware')
    [pscustomobject]@{ Manufacturer=$mfg; Model=$model; IsQemu=$isQemu; IsVmware=$isVmware }
}
function Assert-SafeProxmox {
    param($Platform)
    if ($Platform.IsVmware -or -not $Platform.IsQemu) { throw "VMCI cleanup refused: cannot positively identify QEMU/KVM guest (Manufacturer='$($Platform.Manufacturer)', Model='$($Platform.Model)')." }
    $present = @(Get-CimInstance Win32_PnPEntity | Where-Object { $_.DeviceID -match '^PCI\\VEN_15AD' -and $_.Present -eq $true })
    # Win32_PnPEntity.Present is not populated consistently on older Windows builds.
    $present2 = @(Get-PnpDevice -PresentOnly -ErrorAction Stop | Where-Object { $_.InstanceId -match '^PCI\\VEN_15AD' })
    if ($present.Count -gt 0 -or $present2.Count -gt 0) { throw 'VMware PCI hardware is currently present; refusing to change VMCI.' }
}
function Assert-Admin {
    $p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator rights are required.' }
}
function Emit-Result($value) {
    $json = ConvertTo-Json -InputObject ([pscustomobject]$value) -Depth 10
    if (-not [string]::IsNullOrWhiteSpace($OutputJson)) {
        $path = [IO.Path]::GetFullPath($OutputJson); $dir = [IO.Path]::GetDirectoryName($path)
        if (-not [IO.Directory]::Exists($dir)) { [IO.Directory]::CreateDirectory($dir) | Out-Null }
        [IO.File]::WriteAllText($path,$json,(New-Object System.Text.UTF8Encoding($false)))
    }
    Write-Output $json
}
function Get-ObsoleteVmwareDevices {
    # The allow-list intentionally avoids display-name-based matching, which can
    # accidentally include active Microsoft, Red Hat, or unrelated OEM devices.
    $presentIds = @{}
    foreach ($active in @(Get-PnpDevice -PresentOnly -ErrorAction Stop)) {
        $presentIds[[string]$active.InstanceId] = $true
    }
    foreach ($dev in @(Get-PnpDevice -PresentOnly:$false -ErrorAction Stop)) {
        $id = [string]$dev.InstanceId
        $kind = $null
        if ($id -match '^PCI\\VEN_15AD&') { $kind = 'VMware PCI device' }
        elseif ($id -match '^PCI\\VEN_1000&DEV_0054&SUBSYS_[0-9A-Fa-f]{4}15AD') { $kind = 'VMware LSI SAS controller' }
        elseif ($id -match '^PCI\\VEN_8086&DEV_10D3&SUBSYS_07D015AD(?:&|\\)') { $kind = 'VMware Intel E1000E network adapter' }
        elseif ($id -match '^SCSI\\DISK&VEN_VMWARE&') { $kind = 'VMware virtual disk' }
        elseif ($id -match '^SCSI\\CDROM&VEN_NECVMWAR&') { $kind = 'VMware virtual CD-ROM' }
        if (-not $kind) { continue }

        # Safety: require the explicit PnP presence property to be false.
        # Unknown means NOT approved for removal.
        $presence = 'Unknown'
        $prop = Get-PnpDeviceProperty -InstanceId $id -KeyName 'DEVPKEY_Device_IsPresent' -ErrorAction SilentlyContinue
        if ($null -ne $prop -and $null -ne $prop.Data) {
            if ($prop.Data -eq $true) { $presence='Present' }
            elseif ($prop.Data -eq $false) { $presence='Absent' }
        }
        if ($presentIds.ContainsKey($id)) { $presence = 'Present' }
        [pscustomobject]@{
            Name=[string]$dev.FriendlyName; InstanceId=$id; Class=[string]$dev.Class
            Status=[string]$dev.Status; Presence=$presence; Kind=$kind
            Eligible=($presence -eq 'Absent')
        }
    }
}
function Get-GuestPrepPhantomDevices {
    # Exact ROOT-enumerated family only. NEVER match all VirtIO SCSI devices by FriendlyName.
    foreach ($dev in @(Get-PnpDevice -ErrorAction Stop)) {
        $id = [string]$dev.InstanceId
        if ($id -notmatch '^ROOT\\VIOSCSI\\[0-9]+$') { continue }
        [pscustomobject]@{ Name=[string]$dev.FriendlyName; InstanceId=$id; Class=[string]$dev.Class; Status=[string]$dev.Status }
    }
}
function Assert-HealthyRealVirtioScsi {
    # virtio-scsi-single presents real PCI controllers. We require a present and healthy
    # PCI controller before deleting the artificial ROOT device created by GuestPrep.
    $active = @(Get-PnpDevice -PresentOnly -ErrorAction Stop | Where-Object {
        ($_.InstanceId.StartsWith('PCI\VEN_1AF4&DEV_1004&', [StringComparison]::OrdinalIgnoreCase) -or
         $_.InstanceId.StartsWith('PCI\VEN_1AF4&DEV_1048&', [StringComparison]::OrdinalIgnoreCase)) -and
        $_.Class -eq 'SCSIAdapter' -and $_.Status -eq 'OK'
    })
    if ($active.Count -lt 1) {
        throw 'No healthy real VirtIO SCSI PCI controller found. Refusing phantom-device cleanup.'
    }
    return $active.Count
}
function Initialize-SetupApi {
    if ('BareProx.NativeDeviceRemoval' -as [type]) { return }
    $native = @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace BareProx {
    public static class NativeDeviceRemoval {
        private const uint DIGCF_ALLCLASSES = 0x00000004;
        private const uint DIF_REMOVE = 0x00000005;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
        private static extern IntPtr GetClassDevs(IntPtr classGuid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiOpenDeviceInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenDeviceInfo(IntPtr set, string instanceId, IntPtr hwnd, uint flags, ref DeviceInfoData data);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiCallClassInstaller", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CallClassInstaller(uint operation, IntPtr set, ref DeviceInfoData data);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiDestroyDeviceInfoList", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyDeviceInfoList(IntPtr set);

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceInfoData {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }
        public static void Remove(string instanceId) {
            if (String.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("Empty device ID");
            IntPtr set = GetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_ALLCLASSES);
            if (set == InvalidHandle) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs failed");
            try {
                DeviceInfoData data = new DeviceInfoData();
                data.cbSize = (uint)Marshal.SizeOf(typeof(DeviceInfoData));
                if (!OpenDeviceInfo(set, instanceId, IntPtr.Zero, 0, ref data))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiOpenDeviceInfo failed for " + instanceId);
                if (!CallClassInstaller(DIF_REMOVE, set, ref data))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "DIF_REMOVE failed for " + instanceId);
            } finally { DestroyDeviceInfoList(set); }
        }
    }
}
'@
    Add-Type -TypeDefinition $native -ErrorAction Stop
}

function Disable-VmciForMigration {
    param($Platform)
    # Only as part of final source-side VMware Tools uninstall, never generic cleanup.
    if (-not $Platform.IsVmware -or $Platform.IsQemu) {
        throw 'Source-side VMCI disable requires positively identified VMware hardware.'
    }
    $current = Get-VMCIService
    if ($null -eq $current) { return 'Absent' }
    if ($current.Start -eq 4) { return 'Disabled' }
    $sc = Join-Path $env:WINDIR 'System32\sc.exe'
    $proc = Start-Process -FilePath $sc -ArgumentList @('config','vmci','start=','disabled') -Wait -PassThru -NoNewWindow
    if ($proc.ExitCode -ne 0) { throw "sc.exe config vmci failed (exit $($proc.ExitCode))." }
    $check = Get-VMCIService
    if ($null -eq $check -or $check.Start -ne 4) { throw 'VMCI startup could not be verified as Disabled.' }
    return 'Disabled'
}

function Get-VmciPackage([string]$inf) {
    # Query the servicing inventory and match the published INF exactly.
    $drivers = @(Get-WindowsDriver -Online -All -ErrorAction Stop)
    $driverMatches = @($drivers | Where-Object { [string](Get-ValueSafe $_ 'Driver') -ieq $inf })
    if ($driverMatches.Count -ne 1) { return $null }
    return $driverMatches[0]
}
function Find-VmciPublishedInf {
    # Prefer servicing metadata, which is structured and not localized console text.
    # Never infer a package from provider alone: Broadcom signs unrelated drivers.
    $foundPackages = @()
    foreach ($driver in @(Get-WindowsDriver -Online -All -ErrorAction Stop)) {
        $name = [string](Get-ValueSafe $driver 'Driver')
        $original = [string](Get-ValueSafe $driver 'OriginalFileName')
        $provider = [string](Get-ValueSafe $driver 'ProviderName')
        if ($name -match '^oem[0-9]+\.inf$' -and [IO.Path]::GetFileName($original) -ieq 'vmci.inf' -and $provider -match 'VMware|Broadcom') {
            $foundPackages += $name
        }
    }
    $foundPackages = @($foundPackages | Sort-Object -Unique)
    if ($foundPackages.Count -gt 1) { throw "Multiple VMCI driver packages found ($($foundPackages -join ', ')); manual selection required." }
    if ($foundPackages.Count -eq 1) { return $foundPackages[0] }
    return $null
}
function Get-VmciPnPDeleteArguments([string]$Inf) {
    # Windows Server 2016 (build 14393) and older PnPUtil releases do not
    # support /uninstall for /delete-driver. Query the OS build from the
    # registry rather than parsing localized pnputil help output.
    $key = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop
    $buildText = [string](Get-ValueSafe $key 'CurrentBuildNumber')
    if (-not $buildText) { $buildText = [string](Get-ValueSafe $key 'CurrentBuild') }
    $build = 0
    if (-not [int]::TryParse($buildText, [ref]$build) -or $build -le 0) {
        throw "Cannot determine Windows build ('$buildText'); refusing VMCI package deletion."
    }
    $args = @('/delete-driver', $Inf)
    if ($build -ge 17763) { $args += '/uninstall' }
    return $args
}
function Remove-VmciDriverVerified {
    param([string]$Inf)
    if ([string]::IsNullOrWhiteSpace($Inf) -or $Inf -notmatch '^oem[0-9]+\.inf$') { throw 'Refusing invalid published INF name.' }
    Assert-VmciInf $Inf
    # Do not remove a package while its VMware device is present.
    if (@(Get-PnpDevice -PresentOnly -ErrorAction Stop | Where-Object {
        $_.InstanceId -match '^PCI\\VEN_15AD&DEV_0740&'
    }).Count -gt 0) { throw 'VMCI device is still present; refusing driver package deletion.' }
    $arguments = @(Get-VmciPnPDeleteArguments $Inf)
    $pnp = Join-Path $env:WINDIR 'System32\pnputil.exe'
    # Capture native diagnostics even when pnputil rejects the command.
    $outFile = Join-Path $env:TEMP ('BareProx-pnputil-' + [guid]::NewGuid().ToString('N') + '.out')
    $errFile = $outFile + '.err'
    try {
        $proc = Start-Process -FilePath $pnp -ArgumentList $arguments -Wait -PassThru -NoNewWindow -RedirectStandardOutput $outFile -RedirectStandardError $errFile
        $exit = [int]$proc.ExitCode
        $stdout = if (Test-Path -LiteralPath $outFile) { [IO.File]::ReadAllText($outFile).Trim() } else { '' }
        $stderr = if (Test-Path -LiteralPath $errFile) { [IO.File]::ReadAllText($errFile).Trim() } else { '' }
        if ($exit -ne 0) {
            throw "pnputil $($arguments -join ' ') failed with exit code $exit; no /force was used. stdout: $stdout stderr: $stderr"
        }
    } finally {
        Remove-Item -LiteralPath $outFile,$errFile -Force -ErrorAction SilentlyContinue
    }
    if ($null -ne (Get-VmciPackage $Inf)) { throw "pnputil returned 0, but $Inf is still present in servicing inventory." }
    return 0
}
function Remove-OrphanedVmciService {
    param($Platform)
    Assert-SafeProxmox $Platform
    if (@(Get-ToolsEntries).Count -gt 0) { throw 'VMware Tools remains installed.' }
    $svc = Get-VMCIService
    if ($null -eq $svc) { return 'Absent' }
    if ($svc.Start -ne 4 -or $svc.State -ne 'Stopped') {
        throw "Refusing to delete VMCI service: Start=$($svc.Start), State=$($svc.State); must be disabled and stopped."
    }
    if (@(Get-ObsoleteVmwareDevices).Count -ne 0) {
        throw 'Obsolete VMware devices remain; refusing VMCI service deletion.'
    }
    if (@(Get-PnpDevice -PresentOnly -ErrorAction Stop | Where-Object { $_.InstanceId -match '^PCI\\VEN_15AD&DEV_0740&' }).Count -ne 0) {
        throw 'A VMCI device is still present.'
    }
    if ($null -ne (Find-VmciPublishedInf)) {
        throw 'VMCI INF package is still installed; refusing service deletion.'
    }
    if (-not $PSCmdlet.ShouldProcess('vmci (disabled, stopped, orphaned)', 'Delete Windows driver service registration via sc.exe delete')) {
        return 'Skipped (WhatIf or confirmation declined)'
    }
    $result.VmciServiceRemovalAttempted = $true
    $result.Attempted = $true
    $sc = Join-Path $env:WINDIR 'System32\sc.exe'
    $proc = Start-Process -FilePath $sc -ArgumentList @('delete','vmci') -Wait -PassThru -NoNewWindow
    if ($proc.ExitCode -ne 0 -and $proc.ExitCode -ne 1060) {
        throw "sc.exe delete vmci failed with exit code $($proc.ExitCode)."
    }
    # Registry/service deletion can be pending until all SCM handles close / next boot.
    $remaining = Get-VMCIService
    if ($null -eq $remaining) { return 'Removed' }
    if ($remaining.Start -eq 4 -and $remaining.State -eq 'Stopped') { return 'PendingReboot' }
    throw 'VMCI service still registered in an unexpected state after deletion.'
}

function Assert-VmciInf([string]$inf) {
    $driver = Get-VmciPackage $inf
    if ($null -eq $driver) { throw "Driver package $inf is not present in Windows servicing inventory." }
    $original = [string](Get-ValueSafe $driver 'OriginalFileName')
    if ([IO.Path]::GetFileName($original) -ine 'vmci.inf') { throw "Refusing to remove ${inf}: servicing API reports original INF '$original', not vmci.inf." }
}

$result = [ordered]@{
    SchemaVersion=5; ComputerName=$env:COMPUTERNAME; CapturedAtUtc=[DateTime]::UtcNow.ToString('o'); Mode=$PSCmdlet.ParameterSetName
    Found=$false; Products=@(); Attempted=$false; ExitCode=$null; RebootRequired=$false
    StillInstalled=$null; VerificationPassed=$false; Success=$false; Message=''; LogFile=$null
    Platform=$null; VMCIService=$null; VmciPublishedInf=$null; Actions=@(); Warnings=@()
    ObsoleteDevices=@(); RemovedDevices=@(); SkippedDevices=@(); RemainingDevices=@()
    VmciDriverRemovalAttempted=$false; VmciDriverRemoved=$false; VmciDriverRemovalMessage=''
    VmciServiceRemovalAttempted=$false; VmciServiceRemoved=$false; VmciServiceRemovalMessage=''
    VmciStartupDisabled=$false; ShutdownRequired=$false; NextBootMustBeProxmox=$false
    GuestPrepDevices=@(); RemovedGuestPrepDevices=@(); RemainingGuestPrepDevices=@(); HealthyVirtioScsiControllers=0
}
$exitCode = 0
try {
    $entries = @(Get-ToolsEntries | Sort-Object -Property RegistryPath -Unique)
    $result.Found = ($entries.Count -gt 0)
    $result.Products = @($entries | Select-Object DisplayName,DisplayVersion,Publisher,ProductCode,WindowsInstaller)
    $result.StillInstalled = $result.Found
    $result.Platform = Get-Platform
    $result.VMCIService = Get-VMCIService

    switch ($PSCmdlet.ParameterSetName) {
        'Detect' {
            $result.Success=$true; $result.VerificationPassed=$true
            $result.Message='Read-only inventory completed. VerificationPassed means detection succeeded, not that VMware components were removed.'
            break
        }
        'Uninstall' {
            Assert-Admin
            if ($DisableVmciAfterUninstall -and (-not $result.Platform.IsVmware -or $result.Platform.IsQemu)) {
                throw '--disable-vmci is a source-VMware-only final migration step.'
            }
            if ($entries.Count -gt 1) { throw "Found $($entries.Count) VMware Tools uninstall entries; refusing ambiguous uninstall." }
            if ($entries.Count -eq 1) {
                $entry=$entries[0]
                if (-not $entry.ProductCode -or [int]$entry.WindowsInstaller -ne 1) { throw 'No verified MSI product registration; refusing uninstall.' }
                if (-not $PSCmdlet.ShouldProcess("VMware Tools $($entry.DisplayVersion) [$($entry.ProductCode)]",'Silent MSI uninstall')) {
                    $result.Success=$true; $result.Message='No action (WhatIf or confirmation declined).'; break
                }
                if (-not (Test-Path -LiteralPath $LogDirectory -PathType Container)) { New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null }
                $result.LogFile = Join-Path $LogDirectory ("VMwareTools-Uninstall-{0:yyyyMMdd-HHmmss}-{1}.log" -f (Get-Date),$PID)
                $exe = Join-Path $env:WINDIR 'System32\msiexec.exe'
                $arguments = "/x $($entry.ProductCode) /qn /norestart REBOOT=ReallySuppress /L*v `"$($result.LogFile)`""
                $result.Attempted=$true
                $proc=Start-Process -FilePath $exe -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
                $result.ExitCode=[int]$proc.ExitCode
                $result.RebootRequired = ($result.ExitCode -in @(3010,1641))
                $result.StillInstalled = (@(Get-ToolsEntries).Count -gt 0)
                $accepted = ($result.ExitCode -in @(0,1605,1614,3010,1641))
                if (-not $accepted) { throw "VMware Tools MSI failed with exit code $($result.ExitCode)." }
                if ($result.StillInstalled) { throw 'VMware Tools remains registered after MSI uninstall.' }
                $result.Actions += 'VMware Tools MSI unregistered.'
            } else {
                $result.Actions += 'VMware Tools already absent from uninstall registry.'
            }
            # VMCI startup is changed only when explicitly requested and AFTER verified uninstall.
            # Rebooting back into VMware with VMCI disabled is not part of the supported workflow.
            if ($DisableVmciAfterUninstall) {
                if ($PSCmdlet.ShouldProcess('VMware VMCI kernel driver', 'Disable VMCI startup for next boot on Proxmox')) {
                    $result.Attempted=$true
                    $state = Disable-VmciForMigration $result.Platform
                    $result.VMCIService = Get-VMCIService
                    $result.VmciStartupDisabled = ($state -in @('Absent','Disabled'))
                    $result.Actions += "VMCI startup: $state"
                    $result.ShutdownRequired=$true
                    $result.NextBootMustBeProxmox=$true
                } else {
                    if ($WhatIfPreference) { $result.Success=$true; $result.Message='Preview only: VMCI startup would be disabled after VMware Tools removal.'; break }
                    throw 'VMCI disable was declined; source migration finalization incomplete.'
                }
            }
            $result.Success=$true
            $result.VerificationPassed=$true
            $result.Message=if ($DisableVmciAfterUninstall) {
                'VMware Tools absent; VMCI startup disabled or absent. Shut down source VM and boot next on Proxmox. Do not reboot into VMware.'
            } else { 'VMware Tools absent. VMware-specific drivers may remain until post-migration cleanup.' }
            if ($result.RebootRequired) {
                $result.Message += ' MSI requested a reboot; defer to destination boot on Proxmox.'
                if (-not $AllowRebootRequired) { $exitCode=2 }
            }
            break
        }
        'DisableVmci' {
            Assert-Admin; Assert-SafeProxmox $result.Platform
            if ($null -eq $result.VMCIService) { $result.Success=$true; $result.VerificationPassed=$true; $result.Message='VMCI driver service not installed.'; break }
            if (-not $PSCmdlet.ShouldProcess('vmci (VMware VMCI Bus Driver)','Disable service startup, without deleting driver')) { $result.Success=$true; $result.Message='No action (WhatIf or confirmation declined).'; break }
            $result.Attempted=$true
            $sc=Join-Path $env:WINDIR 'System32\sc.exe'
            $p=Start-Process -FilePath $sc -ArgumentList @('config','vmci','start=','disabled') -Wait -PassThru -NoNewWindow
            $result.ExitCode=[int]$p.ExitCode
            if ($result.ExitCode -ne 0) { throw "sc.exe config vmci failed: $($result.ExitCode)" }
            $result.VMCIService = Get-VMCIService
            $result.VerificationPassed=($null -ne $result.VMCIService -and $result.VMCIService.Start -eq 4)
            $result.Success=$result.VerificationPassed
            $result.RebootRequired=$true
            $result.Message='VMCI startup disabled. Reboot and verify Windows, network, storage and QEMU Guest Agent BEFORE considering driver-package removal. This script has not rebooted.'
            if (-not $result.Success) { $exitCode=3 }
            elseif (-not $AllowRebootRequired) { $exitCode=2 }
            break
        }
        'RemoveObsoleteDevices' {
            Assert-Admin; Assert-SafeProxmox $result.Platform
            if ($result.Found) { throw 'Uninstall VMware Tools before removing obsolete device instances.' }
            $candidates = @(Get-ObsoleteVmwareDevices)
            $result.ObsoleteDevices = $candidates
            $eligible = @($candidates | Where-Object { $_.Eligible })
            $ineligible = @($candidates | Where-Object { -not $_.Eligible })
            $result.SkippedDevices = @($ineligible)
            # An already cleaned guest has no candidates; still allow optional VMCI INF cleanup.
            $vmciDevice = @($eligible | Where-Object { $_.InstanceId -match '^PCI\\VEN_15AD&DEV_0740&' })
            if ($vmciDevice.Count -gt 0 -and $null -ne $result.VMCIService -and ($result.VMCIService.Start -ne 4 -or $result.VMCIService.State -eq 'Running')) {
                $result.RebootRequired=$true
                throw 'VMCI device found: disable VMCI and reboot on Proxmox before device cleanup.'
            }
            # Initialize/compile the native interop before the first destructive action.
            Initialize-SetupApi
            $failed = @()
            foreach ($dev in $eligible) {
                $id = [string]$dev.InstanceId
                if (-not $PSCmdlet.ShouldProcess($id, "Remove non-present $($dev.Kind) via SetupAPI DIF_REMOVE")) {
                    $result.SkippedDevices += $dev
                    continue
                }
                try {
                    # Recheck immediately before deletion to avoid stale snapshot errors.
                    $current = @(Get-PnpDevice -InstanceId $id -ErrorAction SilentlyContinue)
                    if ($current.Count -ne 1) { throw 'Device instance no longer uniquely found; skipped.' }
                    $presence = Get-PnpDeviceProperty -InstanceId $id -KeyName 'DEVPKEY_Device_IsPresent' -ErrorAction SilentlyContinue
                    if ($null -eq $presence -or $presence.Data -ne $false) { throw 'Device is present or its presence is unknown; skipped.' }
                    if (@(Get-PnpDevice -PresentOnly -InstanceId $id -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Device became present; skipped.' }
                    $result.Attempted = $true
                    [BareProx.NativeDeviceRemoval]::Remove($id)
                    $result.Actions += "DIF_REMOVE succeeded: $id"
                    $result.RemovedDevices += $dev
                } catch {
                    $failed += $id
                    $result.Warnings += ("Could not remove ${id}: " + $_.Exception.Message)
                }
            }
            $result.RemainingDevices = @(Get-ObsoleteVmwareDevices)
            $remainingIds = @($result.RemainingDevices | ForEach-Object { $_.InstanceId })
            $notRemoved = @($result.RemovedDevices | Where-Object { $remainingIds -contains $_.InstanceId })
            if ($notRemoved.Count -gt 0) { $result.Warnings += 'Some removed devices are still enumerated; reboot may be needed.'; $result.RebootRequired = $true }
            $result.VerificationPassed = ($failed.Count -eq 0 -and $notRemoved.Count -eq 0 -and $ineligible.Count -eq 0)
            $result.Success = ($failed.Count -eq 0)
            $result.Message = "Removed $($result.RemovedDevices.Count) VMware device instance(s); skipped $($result.SkippedDevices.Count); failures $($failed.Count)."
            if ($CleanupVmciDriver) {
                # Driver-package deletion is deliberately gated on a complete device cleanup.
                # A failed or skipped removal must not cascade into an INF deletion.
                if ($failed.Count -gt 0 -or $result.RemainingDevices.Count -gt 0 -or $ineligible.Count -gt 0) {
                    $result.VmciDriverRemovalMessage = 'Skipped: VMware device cleanup is incomplete.'
                    $result.Warnings += $result.VmciDriverRemovalMessage
                    $result.Success = $false; $result.VerificationPassed = $false; $exitCode = 3
                } elseif ($null -ne $result.VMCIService -and ($result.VMCIService.Start -ne 4 -or $result.VMCIService.State -eq 'Running')) {
                    $result.VmciDriverRemovalMessage = 'Skipped: disable VMCI, reboot and verify the driver is stopped before removing its package.'
                    $result.Warnings += $result.VmciDriverRemovalMessage
                    $result.RebootRequired = $true
                    $result.Success = $false; $result.VerificationPassed = $false; $exitCode = 2
                } else {
                    try {
                        $vmciInf = Find-VmciPublishedInf
                        $result.VmciPublishedInf = $vmciInf
                        if ($null -eq $vmciInf) {
                            $result.VmciDriverRemovalMessage = 'No VMCI driver package found in servicing inventory.'
                        } elseif ($PSCmdlet.ShouldProcess($vmciInf, 'Remove verified VMware VMCI INF with OS-compatible pnputil /delete-driver (no force)')) {
                            $result.VmciDriverRemovalAttempted = $true
                            $result.Attempted = $true
                            $result.ExitCode = Remove-VmciDriverVerified $vmciInf
                            $result.VmciDriverRemoved = $true
                            $result.VmciDriverRemovalMessage = "Verified removal of $vmciInf (vmci.inf)."
                            $result.Actions += $result.VmciDriverRemovalMessage
                        } else {
                            $result.VmciDriverRemovalMessage = 'VMCI driver removal skipped (WhatIf or confirmation declined).'
                        }
                    } catch {
                        $result.VmciDriverRemovalMessage = "VMCI package cleanup failed: $($_.Exception.Message)"
                        $result.Warnings += $result.VmciDriverRemovalMessage
                        $result.Success = $false
                        $result.VerificationPassed = $false
                        $exitCode = 3
                    }
                }
                $result.Message += " $($result.VmciDriverRemovalMessage)"
                # Only after full INF/device cleanup (including already-cleaned, repeated runs).
                # Failed INF operations and any skipped/remaining devices block service deletion.
                if ($result.Success -and $result.VerificationPassed -and $exitCode -eq 0 -and
                    $result.RemainingDevices.Count -eq 0 -and $ineligible.Count -eq 0 -and
                    $result.VmciDriverRemovalMessage -notmatch '^Skipped|^VMCI package cleanup failed') {
                    try {
                        $state = Remove-OrphanedVmciService $result.Platform
                        $result.VmciServiceRemoved = ($state -in @('Removed','Absent'))
                        $result.VmciServiceRemovalMessage = "VMCI service registration: $state."
                        if ($state -eq 'PendingReboot') {
                            $result.RebootRequired = $true
                            $result.Warnings += 'VMCI service deletion is pending; verify after reboot.'
                            $result.VerificationPassed = $false
                            $result.Success = $false
                            $exitCode = 2
                        }
                        if ($state -eq 'Removed') { $result.Actions += 'Deleted orphaned VMCI driver service via sc.exe delete vmci.' }
                        $result.VMCIService = Get-VMCIService
                    } catch {
                        $result.VmciServiceRemovalMessage = "VMCI service cleanup failed: $($_.Exception.Message)"
                        $result.Warnings += $result.VmciServiceRemovalMessage
                        $result.Success = $false
                        $result.VerificationPassed = $false
                        $exitCode = 3
                    }
                } else {
                    $result.VmciServiceRemovalMessage = 'Skipped: prerequisite VMware device/VMCI package cleanup incomplete.'
                }
                $result.Message += " $($result.VmciServiceRemovalMessage)"

            } else {
                $result.Message += ' Driver packages were not deleted; use -CleanupVmciDriver to request VMCI removal.'
            }
            if ($failed.Count -gt 0) { $exitCode = 3 }
            if ($result.RebootRequired -and $exitCode -eq 0 -and -not $AllowRebootRequired) { $exitCode = 2 }
            break
        }
        'RemoveGuestPrepDevices' {
            Assert-Admin; Assert-SafeProxmox $result.Platform
            if ($result.Found) { throw 'VMware Tools must be uninstalled before GuestPrep phantom cleanup.' }
            $result.HealthyVirtioScsiControllers = Assert-HealthyRealVirtioScsi
            $candidates = @(Get-GuestPrepPhantomDevices)
            $result.GuestPrepDevices = $candidates
            $failed = @()
            # SetupAPI must be available before performing any destructive operation.
            if ($candidates.Count -gt 0 -and -not $WhatIfPreference) { Initialize-SetupApi }
            foreach ($dev in $candidates) {
                $id = [string]$dev.InstanceId
                if (-not $PSCmdlet.ShouldProcess($id, 'Remove BareProx ROOT\VIOSCSI phantom instance via SetupAPI DIF_REMOVE (retain vioscsi driver)')) { continue }
                try {
                    # Revalidate all safety conditions immediately before removing each instance.
                    Assert-SafeProxmox $result.Platform
                    $null = Assert-HealthyRealVirtioScsi
                    $existing = @(Get-GuestPrepPhantomDevices | Where-Object { $_.InstanceId -eq $id })
                    if ($existing.Count -ne 1 -or $id -notmatch '^ROOT\\VIOSCSI\\[0-9]+$') { throw 'Phantom device is no longer uniquely identified.' }
                    Initialize-SetupApi
                    $result.Attempted = $true
                    [BareProx.NativeDeviceRemoval]::Remove($id)
                    $result.Actions += "DIF_REMOVE succeeded for phantom: $id"
                    $result.RemovedGuestPrepDevices += $dev
                } catch {
                    $failed += $id
                    $result.Warnings += ("Could not remove phantom ${id}: " + $_.Exception.Message)
                }
            }
            $result.RemainingGuestPrepDevices = @(Get-GuestPrepPhantomDevices)
            $result.VerificationPassed = ($failed.Count -eq 0 -and $result.RemainingGuestPrepDevices.Count -eq 0)
            $result.Success = ($failed.Count -eq 0)
            $result.Message = "Removed $($result.RemovedGuestPrepDevices.Count) GuestPrep phantom device(s); $($result.RemainingGuestPrepDevices.Count) remain; failures $($failed.Count). Real VirtIO SCSI PCI controllers and vioscsi driver were not modified."
            if ($failed.Count -gt 0 -or (-not $WhatIfPreference -and -not $result.VerificationPassed)) { $exitCode = 3 }
            break
        }
        'RemoveVmciPackage' {
            Assert-Admin; Assert-SafeProxmox $result.Platform
            $result.VmciPublishedInf=$VmciPublishedInf
            if ($null -ne $result.VMCIService -and $result.VMCIService.Start -ne 4) { throw 'VMCI service must be disabled before package removal.' }
            if ($null -ne $result.VMCIService -and $result.VMCIService.State -eq 'Running') { throw 'VMCI service still running. Reboot on Proxmox before package removal.' }
            $null=Assert-VmciInf $VmciPublishedInf
            if (-not $PSCmdlet.ShouldProcess($VmciPublishedInf,'Remove verified vmci.inf package using OS-compatible pnputil /delete-driver (no force)')) { $result.Success=$true; $result.Message='No action (WhatIf or confirmation declined).'; break }
            $result.Attempted=$true
            $result.VmciDriverRemovalAttempted=$true
            $result.ExitCode=Remove-VmciDriverVerified $VmciPublishedInf
            $result.VmciDriverRemoved=$true
            $result.VerificationPassed=($null -eq (Get-VmciPackage $VmciPublishedInf))
            $result.Success=$result.VerificationPassed
            $result.Message=if($result.Success){'Verified VMCI driver package removed.'}else{'Driver package may remain; verify manually.'}
            if (-not $result.Success) { $exitCode=3 }
            break
        }
    }
} catch {
    $result.Success=$false; $result.Message=$_.Exception.Message; $exitCode=1
}
try { Emit-Result $result } catch { [Console]::Error.WriteLine("Cannot write JSON: $($_.Exception.Message)"); exit 1 }
exit $exitCode
""";
}
