using Microsoft.AspNetCore.Mvc;
using BareProx.Services;
using BareProx.Services.Migration;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BareProx.Controllers;

// Uses the centrally configured authorization fallback policy in Program.cs.
// Debug builds have no fallback requirement; Release builds require authentication.
[Route("Migration")]
public sealed class VCenterVerificationController : Controller
{
    private readonly VCenterSettingsStore _settings;
    private readonly IEncryptionService _encryption;
    public VCenterVerificationController(VCenterSettingsStore settings, IEncryptionService encryption)
    {
        _settings = settings;
        _encryption = encryption;
    }
    [HttpPost("VerifyVCenter")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyVCenter([FromForm] VCenterVerifyRequest input, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        // A blank password may reuse the saved secret, but ONLY for the same endpoint and user.
        if (string.IsNullOrEmpty(input.Password))
        {
            var saved = await _settings.ReadAsync(ct);
            if (string.Equals(saved.Server, input.Server?.Trim(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(saved.Username, input.Username?.Trim(), StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(saved.EncryptedPassword))
                input.Password = _encryption.Decrypt(saved.EncryptedPassword);
        }
        if (string.IsNullOrWhiteSpace(input.Server) || string.IsNullOrWhiteSpace(input.Username) || string.IsNullOrEmpty(input.Password))
            return BadRequest(new { success = false, message = "Enter a vCenter address, username and password." });

        string server = input.Server.Trim();
        if (!server.Contains("://", StringComparison.Ordinal)) server = "https://" + server;
        if (!Uri.TryCreate(server, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(baseUri.Host) ||
            baseUri.UserInfo.Length > 0 || baseUri.AbsolutePath != "/" ||
            baseUri.Query.Length > 0 || baseUri.Fragment.Length > 0)
            return BadRequest(new { success = false, message = "Enter an HTTPS vCenter hostname or URL without a path." });

        // The cookie returned by /api/session is explicitly discarded; no session or
        // credentials are written to a database, log or configuration file.
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        // Opt-in ONLY for this vCenter verification call. Never change global TLS handling.
        if (input.AllowUntrustedCertificate)
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        var uri = new UriBuilder(baseUri) { Path = "/api/session", Query = "", Fragment = "" }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(input.Username + ":" + input.Password)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                return Ok(new { success = false, message = "vCenter rejected these credentials or account access." });
            if (!response.IsSuccessStatusCode)
                return Ok(new { success = false, message = $"vCenter session request returned HTTP {(int)response.StatusCode}." });
            // Successful authenticated session creation; close the session if a token is supplied.
            string responseBody = await response.Content.ReadAsStringAsync(ct);
            string? token = null;
            try { token = JsonSerializer.Deserialize<string>(responseBody); } catch (JsonException) { }
            if (!string.IsNullOrEmpty(token))
            {
                using var logout = new HttpRequestMessage(HttpMethod.Delete, uri);
                logout.Headers.TryAddWithoutValidation("vmware-api-session-id", token);
                try { using var _ = await client.SendAsync(logout, ct); } catch (HttpRequestException) { /* expiry is server-side */ }
            }
            return Ok(new { success = true, message = "Connected successfully. vCenter accepted the credentials." });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Ok(new { success = false, message = "vCenter connection timed out." });
        }
        catch (HttpRequestException)
        {
            return Ok(new { success = false, message = "Could not establish a trusted HTTPS connection to vCenter. Check the address and TLS certificate." });
        }
    }

    public sealed class VCenterVerifyRequest
    {
        public string? Server { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public bool AllowUntrustedCertificate { get; set; }
    }
}
