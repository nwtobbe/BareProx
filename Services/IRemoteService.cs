/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 *
 * Copyright (C) 2025 Tobias Modig
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
using System.Security.Cryptography;

namespace BareProx.Services
{
    public interface IEncryptionService
    {
        string Encrypt(string plainText);
        string Decrypt(string cipherText);
    }

    public interface IRemoteApiClient
    {
        Task<HttpClient> CreateAuthenticatedClientAsync(string username, string encryptedPassword, string baseUrl, string clientName, bool isEncrypted = true, string? tokenHeaderName = null, string? tokenValue = null);
        Task<string> SendAsync(HttpClient client, HttpMethod method, string url, HttpContent? content = null);
        string EncodeBasicAuth(string username, string password);
    }

    public class RemoteApiClient : IRemoteApiClient
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IEncryptionService _encryptionService;
        private readonly ILogger<RemoteApiClient> _logger;


        public RemoteApiClient(IHttpClientFactory httpClientFactory, IEncryptionService encryptionService, ILogger<RemoteApiClient> logger)
        {
            _httpClientFactory = httpClientFactory;
            _encryptionService = encryptionService;
            _logger = logger;
        }

        public Task<HttpClient> CreateAuthenticatedClientAsync(string username, string encryptedPassword, string baseUrl, string clientName, bool isEncrypted = true, string? tokenHeaderName = null, string? tokenValue = null)
        {
            var password = isEncrypted ? _encryptionService.Decrypt(encryptedPassword) : encryptedPassword;
            var client = _httpClientFactory.CreateClient(clientName);
            client.BaseAddress = new Uri(baseUrl);

            if (string.IsNullOrEmpty(tokenHeaderName) || string.IsNullOrEmpty(tokenValue))
            {
                var creds = EncodeBasicAuth(username, password);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", creds);
            }
            else
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(tokenHeaderName, tokenValue);
            }

            return Task.FromResult(client);
        }

        public async Task<string> SendAsync(HttpClient client, HttpMethod method, string url, HttpContent? content = null)
        {
            var request = new HttpRequestMessage(method, url) { Content = content };
            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        public string EncodeBasicAuth(string username, string password)
        {
            return Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
        }
    }
    public class EncryptionService : IEncryptionService
    {
        private readonly string _key;
        private readonly ILogger<EncryptionService> _logger;
        public EncryptionService(IConfiguration config, ILogger<EncryptionService> logger)
        {
            _key = config["Encryption:Key"] ?? throw new InvalidOperationException("Missing Encryption key in configuration.");
            if (_key.Length != 32)
                throw new InvalidOperationException("Encryption key must be exactly 32 characters long.");
            _logger = logger;
        }

        public string Encrypt(string plainText)
        {
            using var aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes(_key);
            aes.GenerateIV();

            using var encryptor = aes.CreateEncryptor();
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var encrypted = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

            var result = new byte[aes.IV.Length + encrypted.Length];
            Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
            Buffer.BlockCopy(encrypted, 0, result, aes.IV.Length, encrypted.Length);

            return Convert.ToBase64String(result);
        }

        public string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText))
                return cipherText;

            try
            {
                var fullCipher = Convert.FromBase64String(cipherText);
                using var aes = Aes.Create();
                aes.Key = Encoding.UTF8.GetBytes(_key);

                int ivLength = aes.BlockSize / 8;
                if (fullCipher.Length <= ivLength)
                    throw new CryptographicException("Cipher text too short.");

                var iv = new byte[ivLength];
                var cipher = new byte[fullCipher.Length - ivLength];
                Buffer.BlockCopy(fullCipher, 0, iv, 0, ivLength);
                Buffer.BlockCopy(fullCipher, ivLength, cipher, 0, cipher.Length);
                aes.IV = iv;

                using var decryptor = aes.CreateDecryptor();
                var decrypted = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch (FormatException fe) when (LogAndContinue(fe, LogLevel.Warning, "Invalid Base64 in encrypted text. Returning original input."))
            {
                //_logger.LogWarning(fe, "Invalid Base64 in encrypted text. Returning original input.");
                return cipherText;
            }
            catch (OverflowException oe) when (LogAndContinue(oe, LogLevel.Warning, "Numeric overflow during decryption. Returning original input."))
            {
                //_logger.LogError(oe, "Arithmetic overflow during decryption. Returning original input.");
                return cipherText;
            }
            catch (CryptographicException ce) when (LogAndContinue(ce, LogLevel.Error, "Cryptographic error during decryption. Returning original input."))
            {
                //_logger.LogError(ce, "Cryptographic error during decryption. Returning original input.");
                return cipherText;
            }
            catch (Exception ex) when (LogAndContinue(ex, LogLevel.Error, "Unexpected error during decryption. Returning original input."))
            {
                //_logger.LogError(ex, "Unexpected error during decryption. Returning original input.");
                return cipherText;
            }
        }
        // Helper that logs and returns false so the catch body still runs
        private bool LogAndContinue(Exception ex, LogLevel level, string message)
        {
            _logger.Log(level, ex, message);
            return false;
        }
    }

}
