using Microsoft.Extensions.Caching.Memory;
using BareProx.Services;
using BareProx.Services.Migration;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BareProx.Controllers;

/// <summary>Read-only Windows credential test using VMware Guest Operations.</summary>
[Route("Migration/Guest")]
public sealed class MigrationGuestController : Controller
{
    public sealed class VerifyRequest
    {
        public string Name { get; set; } = "";
        public string BiosUuid { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }

    private static string UuidKey(string? v)
    {
        var chars = new string((v ?? "").Where(Uri.IsHexDigit).ToArray());
        return chars.Length == 32 ? chars.ToLowerInvariant() : "";
    }

    [HttpPost("Verify")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Verify(
        [FromBody] VerifyRequest input,
        [FromServices] VCenterSettingsStore settings,
        [FromServices] IEncryptionService encryption,
        [FromServices] IMemoryCache cache,
        CancellationToken ct)
    {
        if (input == null || string.IsNullOrWhiteSpace(input.Name) ||
            UuidKey(input.BiosUuid).Length != 32 || string.IsNullOrWhiteSpace(input.Username) ||
            string.IsNullOrEmpty(input.Password))
            return BadRequest(new { ok = false, message = "VM identity and Windows credentials are required." });
        var config = await settings.ReadAsync(ct);
        if (string.IsNullOrWhiteSpace(config.Server) || string.IsNullOrWhiteSpace(config.Username) ||
            string.IsNullOrWhiteSpace(config.EncryptedPassword))
            return Ok(new { ok = false, message = "vCenter settings or credentials are missing." });

        var server = config.Server.Trim();
        if (!server.Contains("://", StringComparison.Ordinal)) server = "https://" + server;
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return BadRequest(new { ok = false, message = "Invalid vCenter server URL." });

        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            if (config.AllowUntrustedCertificate)
                handler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            using var client = new HttpClient(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(40) };
            using (var login = new HttpRequestMessage(HttpMethod.Post, "/api/session"))
            {
                var vcPassword = encryption.Decrypt(config.EncryptedPassword);
                login.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(config.Username + ":" + vcPassword)));
                using var response = await client.SendAsync(login, ct);
                if (!response.IsSuccessStatusCode)
                    return Ok(new { ok = false, message = "vCenter login failed (HTTP " + (int)response.StatusCode + ")." });
                var token = JsonSerializer.Deserialize<string>(await response.Content.ReadAsStringAsync(ct));
                if (string.IsNullOrWhiteSpace(token))
                    return Ok(new { ok = false, message = "No vCenter session token was returned." });
                client.DefaultRequestHeaders.TryAddWithoutValidation("vmware-api-session-id", token);
            }
            try
            {
                using var listResponse = await client.GetAsync("/api/vcenter/vm", ct);
                if (!listResponse.IsSuccessStatusCode)
                    return Ok(new { ok = false, message = "Cannot read vCenter VM inventory." });
                using var list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(ct));
                if (list.RootElement.ValueKind != JsonValueKind.Array)
                    return Ok(new { ok = false, message = "Unexpected vCenter inventory response." });
                var matches = new List<string>();
                foreach (var vm in list.RootElement.EnumerateArray())
                {
                    if (!vm.TryGetProperty("name", out var name) ||
                        !string.Equals(name.GetString(), input.Name, StringComparison.OrdinalIgnoreCase) ||
                        !vm.TryGetProperty("vm", out var id)) continue;
                    var vmId = id.GetString();
                    if (string.IsNullOrWhiteSpace(vmId)) continue;
                    using var detailResponse = await client.GetAsync(
                        "/api/vcenter/vm/" + Uri.EscapeDataString(vmId), ct);
                    if (!detailResponse.IsSuccessStatusCode) continue;
                    using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync(ct));
                    var root = detail.RootElement;
                    if (!root.TryGetProperty("identity", out var identity) ||
                        !identity.TryGetProperty("bios_uuid", out var biosUuid) ||
                        UuidKey(biosUuid.GetString()) != UuidKey(input.BiosUuid)) continue;
                    if (!root.TryGetProperty("power_state", out var power) ||
                        !string.Equals(power.GetString(), "POWERED_ON", StringComparison.OrdinalIgnoreCase))
                        return Ok(new { ok = false, message = "Source VM must be powered on to verify Windows guest credentials." });
                    matches.Add(vmId);
                }
                if (matches.Count != 1)
                    return Ok(new { ok = false, message = "VM could not be uniquely matched by name and BIOS UUID." });
                var path = "/api/vcenter/vm/" + Uri.EscapeDataString(matches[0]) + "/guest/filesystem/files?action=list";
                var payload = new
                {
                    credentials = new { type = "USERNAME_PASSWORD", user_name = input.Username,
                        password = input.Password, interactive_session = false },
                    path = @"C:\Windows"
                };
                using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var verify = await client.PostAsync(path, content, ct);
                if (!verify.IsSuccessStatusCode)
                    return Ok(new { ok = false, message = "Guest authentication / permissions failed (HTTP " +
                        (int)verify.StatusCode + ")." });
                var proof = VerifiedGuestProof.Issue(cache, input.BiosUuid, input.Username,
                    User.Identity?.IsAuthenticated == true ? User.Identity.Name ?? "" : "",
                    encryption.Encrypt(input.Password));
                return Ok(new { ok = true, message = "Windows credentials verified through VMware Tools. Valid for 15 minutes.",
                    verificationToken = proof });
            }
            finally
            {
                try { using var logout = await client.DeleteAsync("/api/session", CancellationToken.None); }
                catch (HttpRequestException) { }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Ok(new { ok = false, message = "vCenter verification timed out." });
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or
            InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            return Ok(new { ok = false, message = "vCenter verification failed (" + ex.GetType().Name + ")." });
        }
    }
}
