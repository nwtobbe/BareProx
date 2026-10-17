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

using BareProx.Models;
using BareProx.Services;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BareProx.Services.Migration;

/// <summary>
/// Read-only enrichment of SSH/VMX scan results. No guest operations are performed.
/// A matching name is NOT sufficient: BIOS UUID must also match.
/// </summary>
public sealed class VCenterVmStatusService
{
    private readonly VCenterSettingsStore _settings;
    private readonly IEncryptionService _encryption;
    private readonly ILogger<VCenterVmStatusService> _logger;

    public VCenterVmStatusService(VCenterSettingsStore settings, IEncryptionService encryption,
        ILogger<VCenterVmStatusService> logger)
    {
        _settings = settings;
        _encryption = encryption;
        _logger = logger;
    }

    private static string UuidKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        // VMX uuid.bios uses space-separated hexadecimal bytes; vCenter uses dashed UUIDs.
        var digits = new string(value.Where(Uri.IsHexDigit).ToArray());
        return digits.Length == 32 ? digits.ToLowerInvariant() : "";
    }

    public async Task EnrichAsync(IReadOnlyList<VmxItemDto> vms, CancellationToken ct)
    {
        if (vms.Count == 0) return;
        foreach (var vm in vms) { vm.PowerStatus = "Unknown"; vm.VmwareToolsStatus = "Unknown"; }
        var config = await _settings.ReadAsync(ct);
        if (string.IsNullOrWhiteSpace(config.Server) ||
            string.IsNullOrWhiteSpace(config.Username) ||
            string.IsNullOrWhiteSpace(config.EncryptedPassword)) return;

        try
        {
            string server = config.Server.Trim();
            if (!server.Contains("://", StringComparison.Ordinal)) server = "https://" + server;
            if (!Uri.TryCreate(server, UriKind.Absolute, out var baseUri) ||
                baseUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(baseUri.UserInfo) ||
                baseUri.AbsolutePath != "/" || baseUri.Query.Length > 0 || baseUri.Fragment.Length > 0)
                return;

            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            if (config.AllowUntrustedCertificate)
                handler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            using var client = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(25) };
            var password = _encryption.Decrypt(config.EncryptedPassword);
            using (var login = new HttpRequestMessage(HttpMethod.Post, "/api/session"))
            {
                login.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(config.Username + ":" + password)));
                using var loginResponse = await client.SendAsync(login, ct);
                if (!loginResponse.IsSuccessStatusCode) { _logger.LogWarning("vCenter status login failed: HTTP {Code}", (int)loginResponse.StatusCode); return; }
                var body = await loginResponse.Content.ReadAsStringAsync(ct);
                string? token = null;
                try { token = JsonSerializer.Deserialize<string>(body); } catch (JsonException) { }
                if (string.IsNullOrEmpty(token)) return;
                client.DefaultRequestHeaders.TryAddWithoutValidation("vmware-api-session-id", token);
            }

            try
            {
                using var listResponse = await client.GetAsync("/api/vcenter/vm", ct);
                if (!listResponse.IsSuccessStatusCode) { _logger.LogWarning("vCenter VM list failed: HTTP {Code}", (int)listResponse.StatusCode); return; }
                using var list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(ct));
                if (list.RootElement.ValueKind != JsonValueKind.Array) return;
                var byName = list.RootElement.EnumerateArray()
                    .Where(x => x.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    .GroupBy(x => x.GetProperty("name").GetString()!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.Select(y => y.GetProperty("vm").GetString() ?? "").Where(y => y.Length > 0).ToArray(), StringComparer.OrdinalIgnoreCase);
                var detailCache = new Dictionary<string, (string uuid, string power)>(StringComparer.Ordinal);
                var toolCache = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (var vm in vms)
                {
                    ct.ThrowIfCancellationRequested();
                    var expectedUuid = UuidKey(vm.UuidBios);
                    if (expectedUuid.Length == 0 || !byName.TryGetValue(vm.Name, out var candidates)) continue;
                    var matches = new List<string>();
                    foreach (var id in candidates)
                    {
                        if (!detailCache.TryGetValue(id, out var detail))
                        {
                            using var response = await client.GetAsync("/api/vcenter/vm/" + Uri.EscapeDataString(id), ct);
                            if (!response.IsSuccessStatusCode) continue;
                            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                            var root = json.RootElement;
                            var uuid = root.TryGetProperty("identity", out var identity) && identity.TryGetProperty("bios_uuid", out var bu) ? UuidKey(bu.GetString()) : "";
                            var power = root.TryGetProperty("power_state", out var ps) ? ps.GetString() ?? "Unknown" : "Unknown";
                            detail = (uuid, power);
                            detailCache[id] = detail;
                        }
                        if (detail.uuid == expectedUuid) matches.Add(id);
                    }
                    if (matches.Count != 1) continue;
                    var vmId = matches[0];
                    vm.PowerStatus = detailCache[vmId].power;
                    if (!toolCache.TryGetValue(vmId, out var tools))
                    {
                        tools = "Unknown";
                        using var response = await client.GetAsync("/api/vcenter/vm/" + Uri.EscapeDataString(vmId) + "/tools", ct);
                        if (response.IsSuccessStatusCode)
                        {
                            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                            var root = json.RootElement;
                            var run = root.TryGetProperty("run_state", out var r) ? r.GetString() : null;
                            // State is reported by vCenter, not inferred from the VMX.
                            tools = run switch
                            {
                                "RUNNING" => "Running",
                                "NOT_RUNNING" => "Not running",
                                "NOT_INSTALLED" => "Not installed",
                                _ => run ?? "Unknown"
                            };
                        }
                        toolCache[vmId] = tools;
                    }
                    vm.VmwareToolsStatus = tools;
                }
            }
            finally
            {
                using var logout = new HttpRequestMessage(HttpMethod.Delete, "/api/session");
                try { using var response = await client.SendAsync(logout, ct); } catch (HttpRequestException) { }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("vCenter status enrichment timed out; VM status remains Unknown.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            _logger.LogWarning("Unable to retrieve vCenter VM status: {ErrorType}", ex.GetType().Name);
        }
    }
}
