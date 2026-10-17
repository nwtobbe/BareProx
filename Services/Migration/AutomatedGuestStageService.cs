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

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BareProx.Data;
using BareProx.Models;
using BareProx.Services;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Services.Migration;

/// <summary>
/// Non-disruptive first stage of automated Windows migration. Copies the checked-in
/// GuestPrep / VirtIO payload and a MAC map through vCenter Guest Operations.
/// Does NOT run GuestPrep, uninstall VMware Tools, power off, or migrate a VM.
/// </summary>
public sealed class AutomatedGuestStageService
{
    private readonly ApplicationDbContext _db;
    private readonly IEncryptionService _encryption;
    private readonly VCenterSettingsStore _settings;
    private readonly IProxmoxFileScanner _scanner;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<AutomatedGuestStageService> _logger;

    public AutomatedGuestStageService(ApplicationDbContext db, IEncryptionService encryption,
        VCenterSettingsStore settings, IProxmoxFileScanner scanner, IWebHostEnvironment env, ILogger<AutomatedGuestStageService> logger)
    {
        _db = db;
        _encryption = encryption;
        _settings = settings;
        _scanner = scanner;
        _env = env;
        _logger = logger;
    }

    public sealed record StageResult(int QueueId, string Vm, string StageDirectory,
        int FileCount, long UploadedBytes);

    // DiagnosticException.Message is deliberately assembled only from safe, fixed labels,
    // HTTP status numbers and HttpRequestError enum values. Never include raw exception
    // messages, request bodies, signed URLs, guest credentials or vCenter session tokens.
    public sealed class StageDiagnosticException : Exception
    {
        public StageDiagnosticException(string message, Exception? inner = null) : base(message, inner) { }
    }

    private static string NetworkFailure(string phase, Exception ex)
    {
        if (ex is HttpRequestException http)
            return $"{phase}: HTTP transport failed ({http.HttpRequestError}; inner={http.InnerException?.GetType().Name ?? "none"}; status={(http.StatusCode.HasValue ? ((int)http.StatusCode.Value).ToString() : "none")}).";
        if (ex is TaskCanceledException)
            return $"{phase}: request timed out or was canceled.";
        return $"{phase}: request failed ({ex.GetType().Name}).";
    }

    private static async Task<HttpResponseMessage> SendCheckedAsync(
        Func<Task<HttpResponseMessage>> send, string phase)
    {
        HttpResponseMessage response;
        try { response = await send(); }
        catch (HttpRequestException ex) { throw new StageDiagnosticException(NetworkFailure(phase, ex), ex); }
        catch (TaskCanceledException ex) { throw new StageDiagnosticException(NetworkFailure(phase, ex), ex); }
        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            response.Dispose();
            throw new StageDiagnosticException($"{phase}: HTTP {code}.");
        }
        return response;
    }

    private static string NormalizeUuid(string? value) =>
        new string((value ?? "").Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();

    private static string NormalizeMac(string? value)
    {
        var digits = new string((value ?? "").Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (digits.Length != 12) throw new InvalidOperationException("Invalid MAC address in queued NIC.");
        return string.Join(":", Enumerable.Range(0, 6).Select(i => digits.Substring(i * 2, 2)));
    }

    private async Task LogAsync(int id, string step, string message, CancellationToken ct)
    {
        // Do not include guest passwords, signed upload URLs or tokenized credentials.
        _db.MigrationQueueLogs.Add(new MigrationQueueLog {
            ItemId = id, Step = step, Level = "Info", Message = message, Utc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task<StageResult> StageAsync(int queueId, CancellationToken ct)
    {
        var item = await _db.MigrationQueueItems.SingleOrDefaultAsync(x => x.Id == queueId, ct)
            ?? throw new InvalidOperationException("Queue item not found.");
        if (item.Status != "AutomationStaging" && item.Status != "AutomationPending")
            throw new InvalidOperationException("Only AutomationStaging or AutomationPending entries may be staged.");
        if (NormalizeUuid(item.Uuid).Length != 32 || string.IsNullOrWhiteSpace(item.Name))
            throw new InvalidOperationException("Queued guest identity is invalid.");
        if (string.IsNullOrWhiteSpace(item.GuestUsername) || string.IsNullOrWhiteSpace(item.EncryptedGuestPassword))
            throw new InvalidOperationException("Verified guest credentials are missing.");
        var password = _encryption.Decrypt(item.EncryptedGuestPassword);
        if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("Stored guest password cannot be decrypted.");

        // Never infer an exact Windows release from a VMware guestOS family ID.
        var selection = await _db.MigrationSelections.AsNoTracking().FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("MigrationSelection missing.");
        var sourceVms = await _scanner.ScanForVmxAsync(selection.ClusterId,
            selection.ProxmoxHostId, selection.StorageIdentifier ?? "", ct);
        var sourceVm = sourceVms.SingleOrDefault(x =>
            string.Equals(x.VmxPath, item.VmxPath, StringComparison.Ordinal) &&
            NormalizeUuid(x.UuidBios) == NormalizeUuid(item.Uuid));
        if (sourceVm == null || !sourceVm.SnapshotCheckPassed || sourceVm.HasVmwareSnapshots)
            throw new InvalidOperationException("Source VM missing or VMware snapshot check failed.");

        // Defense in depth: block Windows Server 2012 R2 and older during automatic staging.
        if (sourceVm.GuestOs?.Trim() is not
            ("Windows Server 2016" or "Windows Server 2019" or
             "Windows Server 2022" or "Windows Server 2025"))
            throw new InvalidOperationException(
                "Automatic migration requires Windows Server 2016 or newer (2016, 2019, 2022, 2025). Windows Server 2012 R2 and older are blocked.");

        // Source NICs come from VMware VMX. Destination NICs come from the queued
        // Proxmox configuration; never infer the source from a VMware OUI prefix.
        using var nicJson = JsonDocument.Parse(item.NicsJson);
        if (nicJson.RootElement.ValueKind != JsonValueKind.Array ||
            nicJson.RootElement.GetArrayLength() == 0 ||
            nicJson.RootElement.GetArrayLength() != sourceVm.Nics.Count)
            throw new InvalidOperationException("Source/target NIC count mismatch; MAC mapping cannot be built safely.");
        var mapping = new List<object>();
        var destMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var nicIndex = 0; nicIndex < sourceVm.Nics.Count; nicIndex++)
        {
            var nic = nicJson.RootElement[nicIndex];
            var macElement = nic.TryGetProperty("mac", out var lower) ? lower : nic.GetProperty("Mac");
            var destinationMac = NormalizeMac(macElement.GetString());
            var sourceMac = NormalizeMac(sourceVm.Nics[nicIndex].Mac);
            if (!destMacs.Add(destinationMac) || !sourceMacs.Add(sourceMac))
                throw new InvalidOperationException("Duplicate source or target NIC MAC.");
            mapping.Add(new { SourceMac = sourceMac, DestinationMac = destinationMac });
        }
        var mapBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(mapping));
        var config = await _settings.ReadAsync(ct);
        if (string.IsNullOrWhiteSpace(config.Server) || string.IsNullOrWhiteSpace(config.EncryptedPassword))
            throw new InvalidOperationException("vCenter connection is not configured.");
        var url = config.Server.Contains("://") ? config.Server : "https://" + config.Server;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var serverUri) || serverUri.Scheme != "https" ||
            !string.IsNullOrEmpty(serverUri.UserInfo) || serverUri.AbsolutePath != "/" ||
            serverUri.Query.Length != 0 || serverUri.Fragment.Length != 0)
            throw new InvalidOperationException("Invalid vCenter URL.");
        using var handler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false };
        if (config.AllowUntrustedCertificate)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        using var client = new HttpClient(handler) { BaseAddress = serverUri, Timeout = TimeSpan.FromMinutes(10) };
        var basePath = "";
        await LogAsync(item.Id, "Stage", "Connecting to vCenter and matching source VM", ct);
        using (var login = new HttpRequestMessage(HttpMethod.Post, "/api/session"))
        {
            login.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                Encoding.UTF8.GetBytes(config.Username + ":" + _encryption.Decrypt(config.EncryptedPassword))));
            using var response = await SendCheckedAsync(() => client.SendAsync(login, ct), "vCenter session login");
            var sessionToken = JsonSerializer.Deserialize<string>(await response.Content.ReadAsStringAsync(ct));
            if (string.IsNullOrWhiteSpace(sessionToken)) throw new InvalidOperationException("Empty vCenter session token.");
            client.DefaultRequestHeaders.TryAddWithoutValidation("vmware-api-session-id", sessionToken);
        }
        try
        {
            using var listResponse = await SendCheckedAsync(() => client.GetAsync("/api/vcenter/vm", ct), "vCenter VM inventory");
            using var list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(ct));
            var matches = new List<string>();
            foreach (var vm in list.RootElement.EnumerateArray())
            {
                if (!vm.TryGetProperty("name", out var name) ||
                    !string.Equals(name.GetString(), item.Name, StringComparison.OrdinalIgnoreCase) ||
                    !vm.TryGetProperty("vm", out var id)) continue;
                var vmId = id.GetString();
                if (string.IsNullOrWhiteSpace(vmId)) continue;
                using var detailResp = await SendCheckedAsync(() => client.GetAsync("/api/vcenter/vm/" + Uri.EscapeDataString(vmId), ct), "vCenter VM identity lookup");
                using var detail = JsonDocument.Parse(await detailResp.Content.ReadAsStringAsync(ct));
                if (detail.RootElement.TryGetProperty("identity", out var identity) &&
                    identity.TryGetProperty("bios_uuid", out var uuid) &&
                    NormalizeUuid(uuid.GetString()) == NormalizeUuid(item.Uuid))
                {
                    if (!detail.RootElement.TryGetProperty("power_state", out var state) ||
                        state.GetString() != "POWERED_ON")
                        throw new InvalidOperationException("Source VM is not powered on.");
                    matches.Add(vmId);
                }
            }
            if (matches.Count != 1) throw new InvalidOperationException("VM name and BIOS UUID must resolve to exactly one vCenter VM.");
            var guestOs = sourceVm.GuestOs?.Trim();
            // A VMware windows8srv-64 identifier cannot distinguish 2012 from 2012 R2.
            // Verify the actual Windows version through VMware Guest Operations first.
            if (string.Equals(guestOs, "Windows Server 2012", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(guestOs, "Windows Server 2012 R2", StringComparison.OrdinalIgnoreCase))
            {
                var verifiedR2 = await Verify2012R2Async(client, matches[0], item.GuestUsername!, password, ct);
                if (!verifiedR2)
                    throw new InvalidOperationException("Guest OS version check did not confirm Windows Server 2012 R2 (6.3.9600). Automatic legacy driver staging blocked.");
                guestOs = "Windows Server 2012 R2";
            }
            var osDriver = guestOs switch
            {
                "Windows Server 2012 R2" => "2k12R2",
                "Windows Server 2016" => "2k16",
                "Windows Server 2019" => "2k19",
                "Windows Server 2022" => "2k22",
                "Windows Server 2025" => "2k25",
                _ => throw new InvalidOperationException("Unsupported Windows Server version for automated VirtIO installation.")
            };
            var files = new List<(string Local, string Guest)>();
            var web = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
            var sourceRoot = Path.Combine(web, "downloads");
            var stagingDir = @"C:\ProgramData\BareProx\Staging";
            void Add(string relativePath, string guestPath) =>
                files.Add((Path.Combine(sourceRoot, relativePath), guestPath));
            Add("BareProx.GuestPrep.exe", "BareProx.GuestPrep.exe");
            // Reuse the existing VirtIO assets in dev_BareProx, including 2k12R2.
            Add(Path.Combine("virtio", "virtio-win-gt-x64.msi"), "virtio-win-gt-x64.msi");
            Add(Path.Combine("virtio", "guest-agent", "qemu-ga-x86_64.msi"), "qemu-ga-x86_64.msi");
            foreach (var extension in new[] { "inf", "sys", "cat" })
                Add(Path.Combine("virtio", "vioscsi", osDriver, "amd64", "vioscsi." + extension),
                    @"vioscsi\" + "vioscsi." + extension);
            foreach (var file in files)
            {
                var fi = new FileInfo(file.Local);
                if (!fi.Exists || fi.Length == 0)
                    throw new FileNotFoundException("Required guest preparation asset missing/empty, staging blocked", file.Local);
            }
            await LogAsync(item.Id, "Stage", "Validated " + guestOs + " driver payload before transfer.", ct);
            basePath = "/api/vcenter/vm/" + Uri.EscapeDataString(matches[0]) + "/guest/filesystem";
            var credentials = new { type = "USERNAME_PASSWORD", user_name = item.GuestUsername,
                password, interactive_session = false };
            // vCenter may return HTTP 400 (ALREADY_EXISTS) when the guest directory
            // already exists. Confirm its existence inside the guest before continuing.
            // Do not delete or clear either directory: previous migration logs may live there.
            await EnsureGuestDirectoryAsync(client, matches[0], item.GuestUsername!, password,
                stagingDir, "VMware staging directory", ct);
            await EnsureGuestDirectoryAsync(client, matches[0], item.GuestUsername!, password,
                stagingDir + @"\vioscsi", "VMware vioscsi directory", ct);
            long bytes = 0;
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var info = new FileInfo(file.Local);
                if (!info.Exists || info.Length == 0)
                    throw new FileNotFoundException("Required GuestPrep asset is missing or empty", file.Local);
                await UploadAsync(client, handler, basePath, credentials, info.FullName,
                    stagingDir + @"\" + file.Guest, info.Length, serverUri, file.Guest, ct);
                bytes += info.Length;
                await LogAsync(item.Id, "Stage", "Transferred " + file.Guest + " (" + info.Length + " bytes)", ct);
            }
            var mapPath = Path.GetTempFileName();
            try
            {
                await File.WriteAllBytesAsync(mapPath, mapBytes, ct);
                await UploadAsync(client, handler, basePath, credentials, mapPath,
                    stagingDir + @"\mac-table.json", mapBytes.LongLength, serverUri, "mac-table.json", ct);
            }
            finally { File.Delete(mapPath); }
            bytes += mapBytes.Length;
            await LogAsync(item.Id, "Stage", "GuestPrep files and MAC map uploaded. No guest process has been started.", ct);
            return new StageResult(item.Id, item.Name, stagingDir, files.Count + 1, bytes);
        }
        finally
        {
            try { using var logout = await client.DeleteAsync("/api/session", CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning("vCenter logout could not complete: {Type}", ex.GetType().Name); }
        }

    }

    private static async Task<bool> Verify2012R2Async(HttpClient client, string vmId,
        string username, string password, CancellationToken ct)
    {
        var credentials = new { type = "USERNAME_PASSWORD", user_name = username,
            password, interactive_session = false };
        var vmPath = "/api/vcenter/vm/" + Uri.EscapeDataString(vmId);
        // Exit 0 only for the exact reported guest release; no guest changes or reboot.
        const string command = "-NoProfile -NonInteractive -Command \"$v=(Get-WmiObject Win32_OperatingSystem).Version; if ($v -eq '6.3.9600') { exit 0 } else { exit 42 }\"";
        using var payload = new StringContent(JsonSerializer.Serialize(new {
            credentials, spec = new {
                path = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                arguments = command, start_minimized = true
            }
        }), Encoding.UTF8, "application/json");
        using var start = await SendCheckedAsync(
            () => client.PostAsync(vmPath + "/guest/processes?action=create", payload, ct),
            "Windows version verification start");
        using var process = JsonDocument.Parse(await start.Content.ReadAsStringAsync(ct));
        if (process.RootElement.ValueKind != JsonValueKind.Number ||
            !process.RootElement.TryGetInt64(out var pid) || pid <= 0)
            throw new InvalidOperationException("Guest OS version verification did not return a PID.");
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var pollPayload = new StringContent(
                JsonSerializer.Serialize(new { credentials }), Encoding.UTF8, "application/json");
            using var poll = await SendCheckedAsync(
                () => client.PostAsync(vmPath + "/guest/processes/" + pid + "?action=get", pollPayload, ct),
                "Windows version verification status");
            using var status = JsonDocument.Parse(await poll.Content.ReadAsStringAsync(ct));
            if (status.RootElement.TryGetProperty("exit_code", out var code) &&
                code.ValueKind == JsonValueKind.Number)
                return code.GetInt32() == 0;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new TimeoutException("Timed out verifying Windows Server 2012 R2 guest version; staging blocked.");
    }

    // Existing directories are expected after a failed/retried migration.
    // An HTTP 400 alone is not evidence of ALREADY_EXISTS: verify through Guest Operations.
    private static async Task EnsureGuestDirectoryAsync(HttpClient client, string vmId,
        string username, string password, string directory, string phase, CancellationToken ct)
    {
        var credentials = new { type = "USERNAME_PASSWORD", user_name = username,
            password, interactive_session = false };
        var endpoint = "/api/vcenter/vm/" + Uri.EscapeDataString(vmId) +
            "/guest/filesystem/directories?action=create";
        using var payload = new StringContent(JsonSerializer.Serialize(new {
            credentials, path = directory, create_parents = true
        }), Encoding.UTF8, "application/json");
        using var response = await SendCheckedAsyncAllowExisting(client, endpoint, payload, phase, ct);
        if (response.IsSuccessStatusCode) return;

        // Only HTTP 400 is potentially 'already exists'. Any other HTTP status fails above.
        // Never swallow permission problems, invalid paths, or guest-operations failures.
        if (await GuestDirectoryExistsAsync(client, vmId, username, password, directory, ct))
            return;
        throw new StageDiagnosticException(phase +
            ": HTTP 400 and the directory could not be verified as existing inside the guest.");
    }

    private static async Task<HttpResponseMessage> SendCheckedAsyncAllowExisting(HttpClient client,
        string endpoint, HttpContent content, string phase, CancellationToken ct)
    {
        HttpResponseMessage response;
        try { response = await client.PostAsync(endpoint, content, ct); }
        catch (HttpRequestException ex) { throw new StageDiagnosticException(NetworkFailure(phase, ex), ex); }
        catch (TaskCanceledException ex) { throw new StageDiagnosticException(NetworkFailure(phase, ex), ex); }
        if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            return response;
        var code = (int)response.StatusCode;
        response.Dispose();
        throw new StageDiagnosticException(phase + ": HTTP " + code + ".");
    }

    private static async Task<bool> GuestDirectoryExistsAsync(HttpClient client, string vmId,
        string username, string password, string directory, CancellationToken ct)
    {
        // Directory is from our two fixed staging paths; quote it as a PowerShell literal.
        var literal = "'" + directory.Replace("'", "''") + "'";
        var script = "$exists=Test-Path -LiteralPath " + literal +
            " -PathType Container; if ($exists) { exit 0 } else { exit 42 }";
        var arguments = "-NoProfile -NonInteractive -Command \"" +
            script.Replace("\"", "\\\"") + "\"";
        var credentials = new { type = "USERNAME_PASSWORD", user_name = username,
            password, interactive_session = false };
        var vmPath = "/api/vcenter/vm/" + Uri.EscapeDataString(vmId);
        using var payload = new StringContent(JsonSerializer.Serialize(new {
            credentials, spec = new {
                path = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                arguments, start_minimized = true
            }
        }), Encoding.UTF8, "application/json");
        using var start = await SendCheckedAsync(
            () => client.PostAsync(vmPath + "/guest/processes?action=create", payload, ct),
            "Guest staging directory existence check start");
        using var process = JsonDocument.Parse(await start.Content.ReadAsStringAsync(ct));
        if (process.RootElement.ValueKind != JsonValueKind.Number ||
            !process.RootElement.TryGetInt64(out var pid) || pid <= 0)
            throw new StageDiagnosticException("Guest staging directory check did not return a PID.");
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var pollPayload = new StringContent(
                JsonSerializer.Serialize(new { credentials }), Encoding.UTF8, "application/json");
            using var poll = await SendCheckedAsync(
                () => client.PostAsync(vmPath + "/guest/processes/" + pid + "?action=get", pollPayload, ct),
                "Guest staging directory existence check status");
            using var status = JsonDocument.Parse(await poll.Content.ReadAsStringAsync(ct));
            if (status.RootElement.TryGetProperty("exit_code", out var code) &&
                code.ValueKind == JsonValueKind.Number)
                return code.GetInt32() == 0;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new TimeoutException("Timed out checking existing guest staging directory.");
    }

    private static async Task PostJsonAsync(HttpClient client, string path, object request,
        string phase, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
        using var response = await SendCheckedAsync(() => client.PostAsync(path, content, ct), phase);
    }

    private static async Task UploadAsync(HttpClient client, HttpClientHandler handler, string basePath,
        object credentials, string local, string destination, long size, Uri server,
        string fileLabel, CancellationToken ct)
    {
        // File label is a fixed local asset name, never a signed remote URL.
        using var content = new StringContent(JsonSerializer.Serialize(new {
            credentials, spec = new { path = destination, attributes = new { size, overwrite = true } }
        }), Encoding.UTF8, "application/json");
        using var response = await SendCheckedAsync(
            () => client.PostAsync(basePath + "?action=create", content, ct),
            $"vCenter transfer initialization [{fileLabel}]");
        string? rawUrl;
        try { rawUrl = JsonSerializer.Deserialize<string>(await response.Content.ReadAsStringAsync(ct)); }
        catch (JsonException ex)
        { throw new StageDiagnosticException($"vCenter transfer initialization [{fileLabel}]: invalid transfer URL response.", ex); }
        if (rawUrl == null)
            throw new StageDiagnosticException($"vCenter transfer initialization [{fileLabel}]: missing transfer URL.");
        if (rawUrl.Contains("https://*/", StringComparison.Ordinal))
            rawUrl = rawUrl.Replace("https://*/", "https://" + server.Authority + "/", StringComparison.Ordinal);
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uploadUri) ||
            uploadUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uploadUri.UserInfo))
            throw new StageDiagnosticException($"VMware host upload [{fileLabel}]: unsafe transfer URL.");
        // Transfer URL may reference ESXi. No VMware API session header is sent to the host.
        // We intentionally omit the host name/path/query from diagnostics.
        using var uploadClient = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(10) };
        await using var stream = File.OpenRead(local);
        using var uploadContent = new StreamContent(stream);
        uploadContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var put = await SendCheckedAsync(
            () => uploadClient.PutAsync(uploadUri, uploadContent, ct),
            $"VMware host HTTP PUT [{fileLabel}]");
    }
}
