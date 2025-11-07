
using System.Net.Http;

// Services/Proxmox/Authentication/ProxmoxAuthHeaders.cs)
namespace BareProx.Services.Proxmox.Authentication
{
    internal static class ProxmoxAuthHeaders
    {
        public static void ApplyApiToken(HttpClient client, string apiTokenId, string apiTokenSecret)
        {
            // Clear any cookie/CSRF from earlier
            client.DefaultRequestHeaders.Authorization = null;
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Remove("CSRFPreventionToken");

            // Tokens use this Authorization scheme (no CSRF required)
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "Authorization",
                $"PVEAPIToken={apiTokenId}={apiTokenSecret}"
            );
        }
    }
}
