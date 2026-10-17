using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BareProx.Data;
using BareProx.Models;
using BareProx.Services;
using Microsoft.EntityFrameworkCore;
using Renci.SshNet;
using BareProx.Services.Proxmox.Migration;

namespace BareProx.Services.Migration;

/// <summary>
/// Automated migration post-staging handoff with guarded VMware shutdown and Proxmox import.
/// </summary>
public sealed class AutomatedMigrationExecutionService
{
    private readonly ApplicationDbContext _db;
    private readonly VCenterSettingsStore _vcenter;
    private readonly IEncryptionService _encryption;
    private readonly IProxmoxFileScanner _scanner;
    private readonly IProxmoxMigration _pve;
    private readonly IMigrationExecutor _migration;
    private readonly IStorageSnapshotCoordinator _snapshots;
    private readonly ILogger<AutomatedMigrationExecutionService> _log;
    private const string Staging = @"C:\ProgramData\BareProx\Staging";
    // Per-execution settings are captured once before guest preparation/cutover.
    // A resumed paused item uses a fresh service instance and the saved cluster setting.
    private TimeSpan _guestPrepTimeout = TimeSpan.FromMinutes(35);
    private TimeSpan _vmwareShutdownTimeout = TimeSpan.FromMinutes(15);
    private TimeSpan _guestAgentStartupTimeout = TimeSpan.FromMinutes(90);
    private TimeSpan _guestVerificationTimeout = TimeSpan.FromMinutes(90);

    private void CaptureTimeoutSettings(MigrationSelection selected)
    {
        _guestPrepTimeout = TimeSpan.FromMinutes(Math.Clamp(selected.GuestPrepTimeoutMinutes, 5, 120));
        _vmwareShutdownTimeout = TimeSpan.FromMinutes(Math.Clamp(selected.VmwareShutdownTimeoutMinutes, 1, 120));
        _guestAgentStartupTimeout = TimeSpan.FromMinutes(Math.Clamp(selected.GuestAgentStartupTimeoutMinutes, 5, 180));
        _guestVerificationTimeout = TimeSpan.FromMinutes(Math.Clamp(selected.GuestVerificationTimeoutMinutes, 5, 180));
    }

    public AutomatedMigrationExecutionService(ApplicationDbContext db, VCenterSettingsStore vcenter,
        IEncryptionService encryption, IProxmoxFileScanner scanner, IProxmoxMigration pve, IMigrationExecutor migration,
        IStorageSnapshotCoordinator snapshots, ILogger<AutomatedMigrationExecutionService> log)
    {
        _db = db; _vcenter = vcenter; _encryption = encryption;
        _scanner = scanner; _pve = pve; _migration = migration; _snapshots = snapshots; _log = log;
    }

    private async Task Record(MigrationQueueItem item, string status, string step, string message, CancellationToken ct)
    {
        item.Status = status;
        _db.MigrationQueueLogs.Add(new MigrationQueueLog {
            ItemId = item.Id, Utc = DateTime.UtcNow, Level = "Info", Step = step, Message = message
        });
        await _db.SaveChangesAsync(ct);
    }

    // The default coordinator is shared with the manual queue runner. Automated
    // cutover needs the explicit per-VM overload, never a global queue lookup.
    private StorageSnapshotCoordinator RequireMigrationSnapshotCoordinator() =>
        _snapshots as StorageSnapshotCoordinator
        ?? throw new InvalidOperationException("Automated snapshot naming requires StorageSnapshotCoordinator.");

    private static string Uuid(string? value) =>
        new string((value ?? "").Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();

    private static async Task<JsonDocument> ReadJson(HttpResponseMessage response, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static async Task<JsonDocument> Post(HttpClient client, string path, object payload, CancellationToken ct)
    {
        using var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(path, body, ct);
        return await ReadJson(response, ct);
    }

    private static string GetString(JsonElement element, string key) =>
        element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public async Task ExecuteAsync(int id, CancellationToken ct)
    {
        var item = await _db.MigrationQueueItems.SingleAsync(x => x.Id == id, ct);
        if (item.Status != "AutomationStaged")
            throw new InvalidOperationException("GuestPrep requires a successfully staged queue item.");
        if (item.VmId is null or < 100 || Uuid(item.Uuid).Length != 32 ||
            string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.GuestUsername) ||
            string.IsNullOrWhiteSpace(item.EncryptedGuestPassword))
            throw new InvalidOperationException("Automated migration lacks required VM identity or credentials.");
        // Re-read the source VMX and snapshot chain immediately before touching Windows.
        // A snapshot may have appeared after the VM was originally added to the queue.
        var selected = await _db.MigrationSelections.AsNoTracking().FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Migration selection missing.");
        CaptureTimeoutSettings(selected);
        var inventory = await _scanner.ScanForVmxAsync(selected.ClusterId,
            selected.ProxmoxHostId, selected.StorageIdentifier ?? "", ct);
        var sourceVm = inventory.SingleOrDefault(x =>
            string.Equals(x.VmxPath, item.VmxPath, StringComparison.Ordinal) &&
            Uuid(x.UuidBios) == Uuid(item.Uuid));
        if (sourceVm == null || !sourceVm.SnapshotCheckPassed || sourceVm.HasVmwareSnapshots)
            throw new InvalidOperationException("VMX snapshot verification failed or VMware snapshot files were found. Source remains untouched.");
        // Critical: do not alter the source when the destination VMID is occupied.
        // The import executor checks again later, but that is AFTER VMware shutdown.
        var targetHost = await _db.ProxmoxHosts.AsNoTracking()
            .SingleAsync(x => x.Id == selected.ProxmoxHostId, ct);
        var destinationNode = !string.IsNullOrWhiteSpace(targetHost.Hostname)
            ? targetHost.Hostname : targetHost.HostAddress;
        if (string.IsNullOrWhiteSpace(destinationNode) ||
            !await _pve.IsVmidAvailableAsync(destinationNode, item.VmId!.Value, ct))
            throw new InvalidOperationException("Destination VMID " + item.VmId +
                " is occupied/unavailable. GuestPrep was not started.");
        var windowsPassword = _encryption.Decrypt(item.EncryptedGuestPassword);
        if (string.IsNullOrWhiteSpace(windowsPassword)) throw new InvalidOperationException("Windows credentials cannot be decrypted.");
        // Check source VMDK/snapshot status again as a separate gate at the queue level in future;
        // migration must not proceed unless the VM is successfully identified and powered on.
        var cfg = await _vcenter.ReadAsync(ct);
        var hostUrl = cfg.Server.Contains("://", StringComparison.Ordinal) ? cfg.Server : "https://" + cfg.Server;
        if (!Uri.TryCreate(hostUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Invalid vCenter endpoint.");
        using var handler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false };
        if (cfg.AllowUntrustedCertificate) handler.ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        using var vc = new HttpClient(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(50) };
        using (var login = new HttpRequestMessage(HttpMethod.Post, "/api/session"))
        {
            login.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                Encoding.UTF8.GetBytes(cfg.Username + ":" + _encryption.Decrypt(cfg.EncryptedPassword))));
            using var resp = await vc.SendAsync(login, ct);
            using var doc = await ReadJson(resp, ct);
            var session = doc.RootElement.GetString() ?? throw new InvalidOperationException("No vCenter session ID");
            if (session.Length == 0) throw new InvalidOperationException("Empty vCenter session ID");
            vc.DefaultRequestHeaders.TryAddWithoutValidation("vmware-api-session-id", session);
        }
        try
        {
            using var inventoryResponse = await vc.GetAsync("/api/vcenter/vm", ct);
            using var vCenterInventory = await ReadJson(inventoryResponse, ct);
            string? vm = null;
            foreach (var candidate in vCenterInventory.RootElement.EnumerateArray())
            {
                if (!string.Equals(GetString(candidate, "name"), item.Name, StringComparison.OrdinalIgnoreCase)) continue;
                var identifier = GetString(candidate, "vm");
                using var detailResponse = await vc.GetAsync("/api/vcenter/vm/" + Uri.EscapeDataString(identifier), ct);
                using var detail = await ReadJson(detailResponse, ct);
                if (!detail.RootElement.TryGetProperty("identity", out var identity) ||
                    Uuid(GetString(identity, "bios_uuid")) != Uuid(item.Uuid)) continue;
                if (vm != null) throw new InvalidOperationException("Ambiguous source VM identity.");
                vm = identifier;
                if (GetString(detail.RootElement, "power_state") != "POWERED_ON")
                    throw new InvalidOperationException("Source VMware VM is not powered on.");
            }
            if (vm == null) throw new InvalidOperationException("Source VM not found by exact name and BIOS UUID.");
            var vmPath = "/api/vcenter/vm/" + Uri.EscapeDataString(vm);
            var guest = new { type = "USERNAME_PASSWORD", user_name = item.GuestUsername,
                password = windowsPassword, interactive_session = false };

            await Record(item, "AutomationGuestPrep", "GuestPrep", "Starting GuestPrep with VMware Guest Operations.", ct);
            // Controlled GuestPrep preparation; no VMware Tools uninstall or shutdown.
            var arguments = "autoprep --virtio-scsi --inf \"" + Staging + @"\vioscsi\vioscsi.inf" +
                "\" --virtio-drivers --drivers-msi \"" + Staging + @"\virtio-win-gt-x64.msi" +
                "\" --qemu-agent --agent-msi \"" + Staging + @"\qemu-ga-x86_64.msi" +
                "\" --network --firstboot --mapping-json \"" + Staging + @"\mac-table.json" +
                "\" --workdir \"" + Staging + "\" --apply --yes";
            using var process = await Post(vc, vmPath + "/guest/processes?action=create", new {
                credentials = guest, spec = new { path = Staging + @"\BareProx.GuestPrep.exe",
                    arguments, working_directory = Staging, start_minimized = true }
            }, ct);
            if (process.RootElement.ValueKind != JsonValueKind.Number ||
                !process.RootElement.TryGetInt64(out var pid) || pid <= 0)
                throw new InvalidOperationException("vCenter Guest Operations returned no GuestPrep PID.");
            await Record(item, "AutomationPreparing", "GuestPrep",
                "GuestPrep started (PID " + pid + "). Waiting for verified process exit; no shutdown requested.", ct);
            var deadline = DateTime.UtcNow.Add(_guestPrepTimeout);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                using var status = await Post(vc, vmPath + "/guest/processes/" + pid + "?action=get",
                    new { credentials = guest }, ct);
                if (status.RootElement.TryGetProperty("exit_code", out var code) && code.ValueKind == JsonValueKind.Number)
                {
                    var exitCode = code.GetInt32();
                    if (exitCode != 0) throw new InvalidOperationException("GuestPrep failed, exit code " + exitCode);
                    using var powerResponse = await vc.GetAsync(vmPath, ct);
                    using var powerState = await ReadJson(powerResponse, ct);
                    if (GetString(powerState.RootElement, "power_state") != "POWERED_ON")
                        throw new InvalidOperationException("VMware VM unexpectedly powered off during preparation.");
                    await Record(item, "AutomationPrepared", "GuestPrep",
                        "Initial GuestPrep driver/network/firstboot stages passed (exit 0).", ct);
                    await CutoverAsync(item, vc, vmPath, guest, ct);
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(8), ct);
            }
            throw new TimeoutException("GuestPrep did not finish within " + _guestPrepTimeout.TotalMinutes + " minutes; source VM not shut down by BareProx.");
        }
        finally
        {
            try { using var response = await vc.DeleteAsync("/api/session", CancellationToken.None); }
            catch (Exception ex) { _log.LogWarning("vCenter session cleanup failed: {Type}", ex.GetType().Name); }
        }
    }
    /// <summary>
    /// Resume a paused shutdown from the saved queue state. Final GuestPrep has
    /// ALREADY been triggered; do not stage/re-run it or request another shutdown.
    /// </summary>
    public async Task ResumePausedAsync(int id, CancellationToken ct)
    {
        var item = await _db.MigrationQueueItems.SingleAsync(x => x.Id == id, ct);
        if (item.Status != "AutomationShutdownResuming")
            throw new InvalidOperationException("Only a claimed paused shutdown may be continued.");
        if (item.VmId is null or < 100 || Uuid(item.Uuid).Length != 32 ||
            string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.VmxPath))
            throw new InvalidOperationException("Saved migration identity is invalid. No import attempted.");

        var savedTimeoutSelection = await _db.MigrationSelections.AsNoTracking().FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Migration timeout settings missing.");
        CaptureTimeoutSettings(savedTimeoutSelection);
        var cfg = await _vcenter.ReadAsync(ct);
        var hostUrl = cfg.Server.Contains("://", StringComparison.Ordinal) ? cfg.Server : "https://" + cfg.Server;
        if (!Uri.TryCreate(hostUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Invalid vCenter endpoint.");
        using var handler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false };
        if (cfg.AllowUntrustedCertificate)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        using var vc = new HttpClient(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(50) };
        using (var login = new HttpRequestMessage(HttpMethod.Post, "/api/session"))
        {
            login.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                Encoding.UTF8.GetBytes(cfg.Username + ":" + _encryption.Decrypt(cfg.EncryptedPassword))));
            using var response = await vc.SendAsync(login, ct);
            using var document = await ReadJson(response, ct);
            var session = document.RootElement.GetString() ?? throw new InvalidOperationException("No vCenter session ID");
            if (session.Length == 0) throw new InvalidOperationException("Empty vCenter session ID");
            vc.DefaultRequestHeaders.TryAddWithoutValidation("vmware-api-session-id", session);
        }
        try
        {
            string? vm = null;
            using var inventoryResponse = await vc.GetAsync("/api/vcenter/vm", ct);
            using var inventory = await ReadJson(inventoryResponse, ct);
            foreach (var candidate in inventory.RootElement.EnumerateArray())
            {
                if (!string.Equals(GetString(candidate, "name"), item.Name, StringComparison.OrdinalIgnoreCase)) continue;
                var identifier = GetString(candidate, "vm");
                using var detailResponse = await vc.GetAsync("/api/vcenter/vm/" + Uri.EscapeDataString(identifier), ct);
                using var detail = await ReadJson(detailResponse, ct);
                if (!detail.RootElement.TryGetProperty("identity", out var identity) ||
                    Uuid(GetString(identity, "bios_uuid")) != Uuid(item.Uuid)) continue;
                if (vm != null) throw new InvalidOperationException("Ambiguous source VM identity.");
                vm = identifier;
            }
            if (vm == null) throw new InvalidOperationException("Source VMware VM not found by name and BIOS UUID.");
            var vmPath = "/api/vcenter/vm/" + Uri.EscapeDataString(vm);
            await Record(item, "AutomationShutdownResuming", "Shutdown",
                "Operator selected Continue. Rechecking VMware power state for up to " + _vmwareShutdownTimeout.TotalMinutes + " minutes; final GuestPrep will NOT run again.", ct);

            var deadline = DateTime.UtcNow.Add(_vmwareShutdownTimeout);
            bool poweredOff = false;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                using var response = await vc.GetAsync(vmPath, ct);
                using var detail = await ReadJson(response, ct);
                var power = GetString(detail.RootElement, "power_state");
                if (power == "POWERED_OFF") { poweredOff = true; break; }
                if (power != "POWERED_ON")
                    throw new InvalidOperationException("Unexpected VMware power state " + power);
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            if (!poweredOff)
            {
                await Record(item, "AutomationShutdownPaused", "Shutdown",
                    "Still powered on after another " + _vmwareShutdownTimeout.TotalMinutes + "-minute wait. Paused again; no import attempted. " +
                    "Inspect VMware console, then Continue or Cancel.", ct);
                return;
            }

            // Revalidate after the pause. Do not import a stale/changed VMware VM
            // or overwrite an occupied destination VMID.
            var selection = await _db.MigrationSelections.AsNoTracking().FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("Migration settings missing; import blocked.");
            var scanned = await _scanner.ScanForVmxAsync(selection.ClusterId,
                selection.ProxmoxHostId, selection.StorageIdentifier ?? "", ct);
            var source = scanned.SingleOrDefault(x =>
                string.Equals(x.VmxPath, item.VmxPath, StringComparison.Ordinal) &&
                Uuid(x.UuidBios) == Uuid(item.Uuid));
            if (source == null || !source.SnapshotCheckPassed || source.HasVmwareSnapshots)
                throw new InvalidOperationException("Source VMX/snapshot verification failed after pause; import blocked.");
            var targetHost = await _db.ProxmoxHosts.AsNoTracking()
                .SingleAsync(x => x.Id == selection.ProxmoxHostId, ct);
            var targetNode = !string.IsNullOrWhiteSpace(targetHost.Hostname)
                ? targetHost.Hostname : targetHost.HostAddress;
            if (string.IsNullOrWhiteSpace(targetNode) ||
                !await _pve.IsVmidAvailableAsync(targetNode, item.VmId.Value, ct))
                throw new InvalidOperationException("Destination VMID is occupied/unavailable; import blocked.");

            // Check source power again immediately before post-shutdown handling.
            using (var response = await vc.GetAsync(vmPath, ct))
            using (var detail = await ReadJson(response, ct))
            {
                if (GetString(detail.RootElement, "power_state") != "POWERED_OFF")
                    throw new InvalidOperationException("VMware source powered on again; import blocked.");
            }
            await Record(item, "AutomationPoweredOff", "Shutdown",
                "Continued after pause: VMware POWERED_OFF confirmed. Starting post-shutdown migration steps.", ct);
            await CompleteAfterShutdownAsync(item, ct);
        }
        finally
        {
            try { using var response = await vc.DeleteAsync("/api/session", CancellationToken.None); }
            catch (Exception ex) { _log.LogWarning("vCenter session cleanup failed: {Type}", ex.GetType().Name); }
        }
    }

    private async Task CutoverAsync(MigrationQueueItem item, HttpClient vc,
        string vmPath, object guest, CancellationToken ct)
    {
        // Configuration may have changed during the driver-install phase.
        // Re-check snapshot state and destination VMID immediately before shutdown.
        var current = await _db.MigrationSelections.AsNoTracking().FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Migration selection missing before cutover.");
        var source = (await _scanner.ScanForVmxAsync(current.ClusterId,
            current.ProxmoxHostId, current.StorageIdentifier ?? "", ct)).SingleOrDefault(x =>
            string.Equals(x.VmxPath, item.VmxPath, StringComparison.Ordinal) &&
            Uuid(x.UuidBios) == Uuid(item.Uuid));
        if (source == null || !source.SnapshotCheckPassed || source.HasVmwareSnapshots)
            throw new InvalidOperationException("VMware snapshots/source identity changed during preparation; cutover blocked.");
        var currentHost = await _db.ProxmoxHosts.AsNoTracking()
            .SingleAsync(x => x.Id == current.ProxmoxHostId, ct);
        var targetNode = !string.IsNullOrWhiteSpace(currentHost.Hostname)
            ? currentHost.Hostname : currentHost.HostAddress;
        if (string.IsNullOrWhiteSpace(targetNode) ||
            !await _pve.IsVmidAvailableAsync(targetNode, item.VmId!.Value, ct))
            throw new InvalidOperationException("Destination VMID became unavailable during GuestPrep; cutover blocked.");
        // GuestPrep refuses shutdown unless EVERY listed stage has passed. Its successful
        // shutdown is a required but not individually sufficient signal. Do not retry this
        // procedure blindly after an interruption.
        await Record(item, "AutomationCutover", "GuestPrep",
            "Running final GuestPrep verification, VMware Tools removal and scheduled shutdown.", ct);
        var args = "autoprep --virtio-scsi --inf \"" + Staging + @"\vioscsi\vioscsi.inf" +
            "\" --network --firstboot --mapping-json \"" + Staging + @"\mac-table.json" +
            "\" --uninstall-vmware-tools --disable-vmci --shutdown --workdir \"" + Staging +
            "\" --cleanup-workdir \"" + Staging + "\" --apply --yes";
        using var started = await Post(vc, vmPath + "/guest/processes?action=create", new {
            credentials = guest,
            spec = new { path = Staging + @"\BareProx.GuestPrep.exe",
                arguments = args, working_directory = Staging, start_minimized = true }
        }, ct);
        if (started.RootElement.ValueKind != JsonValueKind.Number ||
            !started.RootElement.TryGetInt64(out var pid) || pid <= 0)
            throw new InvalidOperationException("Failed to get final GuestPrep process PID.");
        await Record(item, "AutomationWaitingForShutdown", "Shutdown",
            "Final GuestPrep process started; waiting up to " + _vmwareShutdownTimeout.TotalMinutes + " minutes for vCenter POWERED_OFF. A timeout will PAUSE this VM for operator review.", ct);
        var deadline = DateTime.UtcNow.Add(_vmwareShutdownTimeout);
        bool poweredOff = false;
        bool toolsLost = false;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            // Prefer process exit information while VMware Tools is available.
            if (!toolsLost)
            {
                try
                {
                    using var status = await Post(vc, vmPath + "/guest/processes/" + pid + "?action=get",
                        new { credentials = guest }, ct);
                    if (status.RootElement.TryGetProperty("exit_code", out var code) &&
                        code.ValueKind == JsonValueKind.Number && code.GetInt32() != 0)
                        throw new InvalidOperationException("Final GuestPrep stage exited " + code.GetInt32());
                }
                catch (HttpRequestException) { toolsLost = true; }
                // VMware Tools is expected to disappear during uninstall; HTTP 4xx may also
                // mean loss of Guest Operations. Do NOT infer success from this.
            }
            using var response = await vc.GetAsync(vmPath, ct);
            using var detail = await ReadJson(response, ct);
            var power = GetString(detail.RootElement, "power_state");
            if (power == "POWERED_OFF") { poweredOff = true; break; }
            if (power != "POWERED_ON") throw new InvalidOperationException("Unexpected VMware state " + power);
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
        if (!poweredOff)
        {
            await Record(item, "AutomationShutdownPaused", "Shutdown",
                "Paused: VMware was still POWERED_ON after " + _vmwareShutdownTimeout.TotalMinutes + " minutes. No post-shutdown snapshot or Proxmox import was started. " +
                "Inspect VMware console and select Continue to wait again, or Cancel to stop BareProx's remaining steps. " +
                "Final GuestPrep has already requested shutdown and may still finish later.", ct);
            return;
        }
        await Record(item, "AutomationPoweredOff", "Shutdown",
            "VMware confirms source powered off. VMware original remains intact.", ct);
        await CompleteAfterShutdownAsync(item, ct);
    }

    // Shared by first cutover and deliberate operator continuation. Never call
    // GuestPrep/VMware Tools uninstall again when resuming a shutdown pause.
    private async Task CompleteAfterShutdownAsync(MigrationQueueItem item, CancellationToken ct)
    {
        var selection = await _db.MigrationSelections.AsNoTracking().FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Migration selection missing.");
        // The single VMID-specific NetApp snapshot is created by
        // MigrationAutomationQueueStageController BEFORE GuestPrep staging begins.
        // Do not create another snapshot after shutdown or when resuming a paused
        // shutdown: the importer must consume the original staging snapshot.
        // IMPORTANT: this is a pre-GuestPrep point in time; guest changes written
        // after the snapshot are not included in the imported disks.
        await Record(item, "AutomationSnapshot", "Snapshot",
            "Using the existing pre-staging VMID-specific NetApp snapshot; no additional snapshot created after shutdown.", ct);

        await Record(item, "AutomationImporting", "Import", "Creating Proxmox VM from NetApp snapshot.", ct);
        await _migration.ExecuteAsync(item, ct);
        await Record(item, "AutomationStarting", "Proxmox", "Import finished. Starting Proxmox VM.", ct);
        var cluster = await _db.ProxmoxClusters.AsNoTracking().SingleAsync(x => x.Id == selection.ClusterId, ct);
        var host = await _db.ProxmoxHosts.AsNoTracking().SingleAsync(x => x.Id == selection.ProxmoxHostId, ct);
        var address = host.HostAddress ?? throw new InvalidOperationException("Proxmox host address is missing.");
        var sshUsername = (cluster.Username ?? "").Split('@')[0];
        var sshPassword = _encryption.Decrypt(cluster.PasswordHash);
        var id = item.VmId ?? throw new InvalidOperationException("VMID missing.");
        // SSH.NET is synchronous: use the same host credentials as the existing scanner.
        using var ssh = new SshClient(address, sshUsername, sshPassword);
        ssh.Connect();
        using (var start = ssh.CreateCommand("qm start " + id))
        {
            start.CommandTimeout = TimeSpan.FromMinutes(3);
            start.Execute();
            if (start.ExitStatus != 0)
                throw new InvalidOperationException("Proxmox qm start failed (exit " + start.ExitStatus + ").");
        }
        await Record(item, "AutomationVerifyingBoot", "Proxmox",
            "VM powered on; waiting up to " + _guestAgentStartupTimeout.TotalMinutes + " minutes for QEMU Guest Agent (Windows Update may delay startup or reboot the guest).", ct);
        var agentDeadline = DateTime.UtcNow.Add(_guestAgentStartupTimeout);
        var nextProgressUtc = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < agentDeadline)
        {
            ct.ThrowIfCancellationRequested();
            // A nonzero exit code or transient agent unavailability is expected during
            // Windows Update/reboots. Never restart the VM or repeat cutover.
            try
            {
                using var ping = ssh.CreateCommand("qm agent " + id + " ping");
                ping.CommandTimeout = TimeSpan.FromSeconds(30);
                ping.Execute();
                if (ping.ExitStatus == 0)
                {
                    item.EncryptedGuestPassword = null; // No longer needed once source access has ended.
                    await Record(item, "AutomationBooted", "Proxmox",
                        "QEMU Guest Agent responded. Stored Windows password cleared; checking first-boot completion.", ct);
                    await VerifyGuestAsync(item, ssh, id, ct);
                    return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "QEMU Guest Agent not yet available for VMID {VmId}; continuing to wait.", id);
            }
            if (DateTime.UtcNow >= nextProgressUtc)
            {
                await Record(item, "AutomationVerifyingBoot", "Proxmox",
                    "Still waiting for QEMU Guest Agent. Windows may be configuring updates or rebooting; no automatic restart will be attempted.", ct);
                nextProgressUtc = DateTime.UtcNow.AddMinutes(5);
            }
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
        throw new TimeoutException("VM started in Proxmox but QEMU Guest Agent did not respond within " + _guestAgentStartupTimeout.TotalMinutes + " minutes. VM remains running; inspect console and Windows Update before manual recovery.");
    }

    // Read-only in-guest verification. This command is fixed by BareProx, never accepts user input.
    // The first-boot runner writes status.json outside the staging directory BEFORE removing the task.
    // A responsive QGA alone is not evidence that Windows post-migration steps have finished.
    private async Task VerifyGuestAsync(MigrationQueueItem item, SshClient ssh, int vmid, CancellationToken ct)
    {
        await Record(item, "AutomationVerifyingGuest", "VerifyGuest",
            "QGA is available; waiting for BareProx first-boot completion and verifying Windows guest state.", ct);
        const string probe = @"
$ErrorActionPreference='Stop'
Write-Output 'BAREPROX_STEP:qga-powershell-started'
$path=Join-Path $env:ProgramData 'BareProx\PostMigration\status.json'
Write-Output 'BAREPROX_STEP:checking-firstboot-status'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Write-Output 'BAREPROX_PENDING:status-file-not-created'; exit 10 }
try { $result=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
catch { Write-Output 'BAREPROX_PENDING:status-file-not-ready'; exit 10 }
if ($result.Stage -eq 'failed') { Write-Output 'BAREPROX_FAILED:firstboot-reported-failure'; exit 20 }
if ($result.Stage -ne 'complete' -or $result.Success -ne $true) { Write-Output 'BAREPROX_PENDING:firstboot-in-progress'; exit 10 }
Write-Output 'BAREPROX_STEP:firstboot-complete'
Write-Output 'BAREPROX_STEP:checking-windows'
$os=Get-CimInstance Win32_OperatingSystem
if (-not $os.Caption -or $os.Caption -notmatch 'Windows') { Write-Output 'BAREPROX_FAILED:windows-os-check'; exit 21 }
Write-Output 'BAREPROX_STEP:windows-verified'
Write-Output 'BAREPROX_STEP:checking-qemu-ga-service'
$agent=Get-Service -Name 'QEMU-GA' -ErrorAction SilentlyContinue
if (-not $agent -or $agent.Status -ne 'Running') { Write-Output 'BAREPROX_FAILED:qemu-ga-service'; exit 22 }
Write-Output 'BAREPROX_STEP:qemu-ga-service-running'
Write-Output 'BAREPROX_STEP:checking-firstboot-task'
$task=Get-ScheduledTask -TaskName 'BareProx-PostMigration' -ErrorAction SilentlyContinue
if ($task) { Write-Output 'BAREPROX_PENDING:firstboot-task-still-present'; exit 10 }
Write-Output 'BAREPROX_STEP:firstboot-task-removed'
Write-Output 'BAREPROX_OK:firstboot-complete;windows-running;qemu-ga-running;task-removed'
exit 0
";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(probe));
        var commandLine = "qm guest exec " + vmid +
            " --timeout 25 -- powershell.exe -NoProfile -NonInteractive -EncodedCommand " + encoded;
        var deadline = DateTime.UtcNow.Add(_guestVerificationTimeout);
        string last = "";
        string lastProgress = "";
        string lastProblem = "none";
        int transientErrors = 0;
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            if (attempt == 1 || attempt % 8 == 0)
                await LogVerificationProgressAsync(item, "Probe",
                    "Checking guest first-boot status via QGA (attempt " + attempt + ").", ct);
            try
            {
                // Do not reuse a previous probe's output after a QGA/SSH failure.
                last = "";
                using var cmd = ssh.CreateCommand(commandLine);
                cmd.CommandTimeout = TimeSpan.FromSeconds(40);
                var output = cmd.Execute();
                if (cmd.ExitStatus != 0)
                    throw new InvalidOperationException("QGA CLI returned a nonzero host exit status.");
                using var response = JsonDocument.Parse(output);
                var root = response.RootElement;
                // If QGA returns a pid, poll that same process rather than launching
                // overlapping PowerShell probes inside the guest.
                if (!root.TryGetProperty("exitcode", out var exitElement) ||
                    exitElement.ValueKind != JsonValueKind.Number)
                {
                    if (root.TryGetProperty("pid", out var pidElement) && pidElement.TryGetInt64(out var guestPid))
                        root = await PollGuestProcessAsync(ssh, vmid, guestPid, ct);
                    else
                        throw new InvalidOperationException("QGA response has no exitcode or pid.");
                }
                var exitCode = root.GetProperty("exitcode").GetInt32();
                var status = root.TryGetProperty("out-data", out var stdout) &&
                    stdout.ValueKind == JsonValueKind.String ? stdout.GetString() ?? "" : "";
                last = status.Trim();
                var progress = ExtractVerificationProgress(last);
                if (progress != lastProgress && progress.Length > 0)
                {
                    await LogVerificationProgressAsync(item, "Check", VerificationProgressDescription(progress), ct);
                    lastProgress = progress;
                }

                // A positive guest-reported failure is terminal. We do not retry
                // destructive first-boot cleanup or restart the migrated VM.
                if (last.Contains("BAREPROX_FAILED:", StringComparison.Ordinal))
                {
                    await Record(item, "AutomationVerificationFailed", "VerifyGuest",
                        "Guest first-boot verification failed: " + SafeProbeState(last) +
                        ". No automatic retry; inspect PostMigration/status.json and first-boot.log.", CancellationToken.None);
                    return;
                }
                if (exitCode == 0 && last.Contains("BAREPROX_OK:", StringComparison.Ordinal))
                {
                    await Record(item, "Done", "VerifyGuest",
                        "Verified: Windows is running; QEMU Guest Agent service is running; firstboot status is complete; scheduled task removed.", ct);
                    return;
                }
                if (exitCode == 10 && last.Contains("BAREPROX_PENDING:", StringComparison.Ordinal))
                {
                    lastProblem = "first-boot still pending (" + SafeProbeState(last) + ")";
                    transientErrors = 0;
                }
                else
                {
                    // A missing marker, a PowerShell startup error, or an unexpected
                    // QGA exit code is not proof that the first-boot task failed.
                    // Keep polling, but never classify these results as success.
                    lastProblem = "unexpected guest probe result (exit " + exitCode +
                        ", marker " + SafeProbeState(last) + ")";
                    transientErrors++;
                    if (transientErrors == 1 || transientErrors % 4 == 0)
                        await LogVerificationProgressAsync(item, "Retry",
                            "Guest verification probe not ready (attempt " + attempt +
                            ", exit " + exitCode + ", marker " + SafeProbeState(last) +
                            "); retrying within " + _guestVerificationTimeout.TotalMinutes + "-minute deadline.", ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // SSH/QGA/PowerShell can briefly fail during driver and device cleanup.
                // Classify safely: never log raw guest output or credentials.
                transientErrors++;
                lastProblem = "probe exception (" + ex.GetType().Name + ")";
                if (transientErrors == 1 || transientErrors % 4 == 0)
                {
                    await LogVerificationProgressAsync(item, "Retry",
                        "Guest probe temporarily unavailable (" + ex.GetType().Name +
                        ", attempt " + attempt + "); will retry until verification deadline.", ct);
                    _log.LogWarning(ex, "Transient VerifyGuest probe error for queue item {Id}, attempt {Attempt}",
                        item.Id, attempt);
                }
            }
            if (DateTime.UtcNow < deadline)
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
        await Record(item, "AutomationVerificationFailed", "VerifyGuest",
            "Timed out after " + _guestVerificationTimeout.TotalMinutes + " minutes waiting for verified first-boot completion. VM remains running; Windows Update may still be active. " +
            "Last recognized state: " + SafeProbeState(last) + "; last issue: " + lastProblem +
            ". No automatic retry or rollback.", CancellationToken.None);
    }

    private async Task LogVerificationProgressAsync(MigrationQueueItem item, string check, string message, CancellationToken ct)
    {
        // Progress rows keep the queue status AutomationVerifyingGuest unchanged.
        _db.MigrationQueueLogs.Add(new MigrationQueueLog
        {
            ItemId = item.Id,
            Utc = DateTime.UtcNow,
            Step = "VerifyGuest/" + check,
            Level = "Info",
            Message = message
        });
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("VerifyGuest item {Id}: {Message}", item.Id, message);
    }

    private static string ExtractVerificationProgress(string output)
    {
        // Last reported check or state. Match only hardcoded allowlisted strings;
        // never persist arbitrary guest output, PowerShell errors or paths.
        var markers = new[]
        {
            "BAREPROX_PENDING:status-file-not-created",
            "BAREPROX_PENDING:status-file-not-ready",
            "BAREPROX_PENDING:firstboot-in-progress",
            "BAREPROX_PENDING:firstboot-task-still-present",
            "BAREPROX_FAILED:firstboot-reported-failure",
            "BAREPROX_FAILED:windows-os-check",
            "BAREPROX_FAILED:qemu-ga-service",
            "BAREPROX_STEP:checking-firstboot-status",
            "BAREPROX_STEP:firstboot-complete",
            "BAREPROX_STEP:checking-windows",
            "BAREPROX_STEP:windows-verified",
            "BAREPROX_STEP:checking-qemu-ga-service",
            "BAREPROX_STEP:qemu-ga-service-running",
            "BAREPROX_STEP:checking-firstboot-task",
            "BAREPROX_STEP:firstboot-task-removed",
            "BAREPROX_OK:firstboot-complete;windows-running;qemu-ga-running;task-removed"
        };
        // Prefer terminal states over the last STEP marker.
        foreach (var marker in markers.Where(x => x.StartsWith("BAREPROX_PENDING:", StringComparison.Ordinal) ||
                                                  x.StartsWith("BAREPROX_FAILED:", StringComparison.Ordinal) ||
                                                  x.StartsWith("BAREPROX_OK:", StringComparison.Ordinal)))
            if (output.Contains(marker, StringComparison.Ordinal)) return marker;
        for (int i = markers.Length - 1; i >= 0; i--)
            if (output.Contains(markers[i], StringComparison.Ordinal)) return markers[i];
        return "";
    }

    private static string VerificationProgressDescription(string marker) => marker switch
    {
        "BAREPROX_PENDING:status-file-not-created" => "Waiting for GuestPrep first-boot status.json to be created.",
        "BAREPROX_PENDING:status-file-not-ready" => "GuestPrep status.json exists but is not readable yet.",
        "BAREPROX_PENDING:firstboot-in-progress" => "GuestPrep first-boot tasks are still running.",
        "BAREPROX_PENDING:firstboot-task-still-present" => "First-boot completed; waiting for BareProx-PostMigration task cleanup.",
        "BAREPROX_FAILED:firstboot-reported-failure" => "GuestPrep reported first-boot failure.",
        "BAREPROX_FAILED:windows-os-check" => "Windows OS identity check failed.",
        "BAREPROX_FAILED:qemu-ga-service" => "QEMU Guest Agent Windows service check failed.",
        "BAREPROX_STEP:checking-firstboot-status" => "Checking GuestPrep completion marker.",
        "BAREPROX_STEP:firstboot-complete" => "GuestPrep reports completion.",
        "BAREPROX_STEP:checking-windows" => "Checking Windows OS identity.",
        "BAREPROX_STEP:windows-verified" => "Windows OS identity verified.",
        "BAREPROX_STEP:checking-qemu-ga-service" => "Checking QEMU Guest Agent service.",
        "BAREPROX_STEP:qemu-ga-service-running" => "QEMU Guest Agent service is running.",
        "BAREPROX_STEP:checking-firstboot-task" => "Checking BareProx-PostMigration scheduled task cleanup.",
        "BAREPROX_STEP:firstboot-task-removed" => "First-boot scheduled task has been removed.",
        "BAREPROX_OK:firstboot-complete;windows-running;qemu-ga-running;task-removed" => "All guest checks passed.",
        _ => "Waiting for guest verification."
    };

    private static string SafeProbeState(string value)
    {
        // Only emit expected fixed probe markers, never arbitrary guest command output.
        foreach (var marker in new[] { "BAREPROX_OK:", "BAREPROX_PENDING:", "BAREPROX_FAILED:" })
        {
            var offset = value.IndexOf(marker, StringComparison.Ordinal);
            if (offset >= 0)
            {
                var s = value.Substring(offset, Math.Min(100, value.Length - offset));
                return new string(s.TakeWhile(c => char.IsLetterOrDigit(c) || c is ':' or ';' or '-' or '_').ToArray());
            }
        }
        return "no recognized guest marker";
    }

    private static async Task<JsonElement> PollGuestProcessAsync(SshClient ssh, int vmid, long pid, CancellationToken ct)
    {
        // Return a cloned JsonElement because JsonDocument is disposed on each poll.
        for (var attempt = 0; attempt < 12; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var status = ssh.CreateCommand("qm guest exec-status " + vmid + " " + pid);
            status.CommandTimeout = TimeSpan.FromSeconds(20);
            var output = status.Execute();
            if (status.ExitStatus != 0) throw new InvalidOperationException("QGA exec-status failed.");
            using var json = JsonDocument.Parse(output);
            var root = json.RootElement;
            if (root.TryGetProperty("exitcode", out var result) && result.ValueKind == JsonValueKind.Number)
                return root.Clone();
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new TimeoutException("QGA verification command did not return an exit code.");
    }

}
