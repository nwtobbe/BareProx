using System.Net.Http;
using System.Threading.Tasks;

namespace BareProx.Services
{
    public interface IRemoteApiClient
    {
        Task<HttpClient> CreateAuthenticatedClientAsync(
            string username,
            string encryptedPassword,
            string baseUrl,
            string clientName,
            bool isEncrypted = true,
            string? tokenHeaderName = null,
            string? tokenValue = null);

        Task<string> SendAsync(HttpClient client, HttpMethod method, string url, HttpContent? content = null);

        string EncodeBasicAuth(string username, string password);
    }
}
