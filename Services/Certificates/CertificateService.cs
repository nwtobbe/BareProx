/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 *
 * Copyright (C) 2025 Tobias Modig
 *
 * This file is part of BareProx.
 *
 * BareProx is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * BareProx is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with BareProx. If not, see <https://www.gnu.org/licenses/>.
 */

using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BareProx.Services
{
    /// <summary>
    /// Binds the "CertificateOptions" section from configuration (e.g. /config/appsettings.json).
    /// Notes:
    ///  - Prefer PfxPasswordEnc (encrypted via IEncryptionService). PfxPassword remains for backward compatibility.
    /// </summary>
    public class CertificateOptions
    {
        /// <summary>
        /// Folder (absolute or relative) where we store/load the .pfx.
        /// Example: "/config/certs" or "Certificates".
        /// </summary>
        public string OutputFolder { get; set; } = "Certificates";

        /// <summary>
        /// File name (inside OutputFolder) for the PFX we load/generate.
        /// For real certs you typically set: "https.pfx".
        /// For self-signed default: "selfsigned.pfx".
        /// </summary>
        public string PfxFileName { get; set; } = "selfsigned.pfx";

        /// <summary>
        /// LEGACY: plaintext PFX password. Prefer PfxPasswordEnc.
        /// </summary>
        public string? PfxPassword { get; set; } = "changeit";

        /// <summary>
        /// Encrypted PFX password (Base64Url) produced by IEncryptionService.Encrypt(...).
        /// If set, this takes precedence over PfxPassword.
        /// </summary>
        public string? PfxPasswordEnc { get; set; }

        /// <summary>
        /// Subject name for self-signed fallback (e.g. "CN=localhost").
        /// </summary>
        public string SubjectName { get; set; } = "CN=localhost";

        /// <summary>
        /// Validity window (days) for self-signed fallback.
        /// </summary>
        public int ValidDays { get; set; } = 365;
    }

    /// <summary>
    /// Loads a real PFX (if present) using an encrypted password from config; otherwise generates a self-signed one.
    /// Exposes the result via CurrentCertificate for Kestrel to use at HTTPS startup.
    /// Also ensures the "CertificateOptions" block exists in /config/appsettings.json
    /// with normalized values and persists any generated defaults there.
    /// </summary>
    public class CertificateService
    {
        private readonly IOptionsMonitor<CertificateOptions> _optionsMonitor;
        private readonly IEncryptionService _encryption;
        private readonly ILogger<CertificateService> _logger;
        private readonly object _lock = new();

        private X509Certificate2? _certificate;
        private string[] _lastRequestedSANs = new[] { "localhost" };

        // Config file we maintain on disk
        private const string AppSettingsPath = "/config/appsettings.json";
        private const string DesiredFolder = "/config/Certs"; // canonical form we enforce

        public CertificateService(
            IOptionsMonitor<CertificateOptions> options,
            IEncryptionService encryption,
            ILogger<CertificateService> logger)
        {
            _optionsMonitor = options;
            _encryption = encryption;
            _logger = logger;
        }

        private CertificateOptions Options => _optionsMonitor.CurrentValue;

        /// <summary>
        /// Kestrel asks for the server certificate here. We lazy-load on first access.
        /// </summary>
        public X509Certificate2 CurrentCertificate
        {
            get
            {
                if (_certificate == null)
                {
                    lock (_lock)
                    {
                        if (_certificate == null)
                        {
                            _certificate = LoadOrCreateCertificate();
                        }
                    }
                }
                return _certificate;
            }
        }

        private string ResolveFolderPath(string folder)
        {
            if (Path.IsPathRooted(folder)) return folder;
            var contentRoot = Directory.GetCurrentDirectory();
            return Path.Combine(contentRoot, folder);
        }

        // ---------- Config persistence helpers ----------

        private static JObject LoadConfig()
        {
            try
            {
                if (!File.Exists(AppSettingsPath)) return new JObject();
                var json = File.ReadAllText(AppSettingsPath);
                return string.IsNullOrWhiteSpace(json) ? new JObject() : JObject.Parse(json);
            }
            catch
            {
                return new JObject();
            }
        }

        private static void SaveConfig(JObject root)
        {
            Directory.CreateDirectory("/config");
            File.WriteAllText(AppSettingsPath, root.ToString(Formatting.Indented));
        }

        private static string Canon(string? p) => (p ?? string.Empty).Replace('\\', '/');

        /// <summary>
        /// Ensures the CertificateOptions block exists and is normalized. If we’re generating self-signed,
        /// we’ll also ensure a password exists and set PfxFileName to "selfsigned.pfx".
        /// If we’re using a real PFX (existing file), we keep its file name; otherwise we set selfsigned.
        /// </summary>
        private void EnsureCertificateOptionsPersisted(bool selfSignedContext, string? selfSignedPasswordToPersist)
        {
            var root = LoadConfig();

            var cert = root["CertificateOptions"] as JObject ?? new JObject();
            if (root["CertificateOptions"] is null) root["CertificateOptions"] = cert;

            // Normalize OutputFolder to /config/certs
            var currentFolder = Canon((string?)cert["OutputFolder"]);
            if (string.IsNullOrWhiteSpace(currentFolder) || !string.Equals(currentFolder, DesiredFolder, StringComparison.Ordinal))
            {
                cert["OutputFolder"] = DesiredFolder;
                // reflect in runtime options object for this process
                Options.OutputFolder = DesiredFolder;
            }

            // Subject & ValidDays defaults (don’t clobber existing)
            if (string.IsNullOrWhiteSpace((string?)cert["SubjectName"]))
                cert["SubjectName"] = Options.SubjectName ?? "CN=localhost";
            if ((int?)cert["ValidDays"] is null || (int)cert["ValidDays"] <= 0)
                cert["ValidDays"] = Options.ValidDays > 0 ? Options.ValidDays : 365;

            // Password logic:
            // - If encrypted provided in config, leave it as-is.
            // - Else if plaintext exists, keep it.
            // - Else if we’re creating self-signed now, persist the password we’re using.
            var hasEnc = !string.IsNullOrWhiteSpace((string?)cert["PfxPasswordEnc"]);
            var hasPlain = !string.IsNullOrWhiteSpace((string?)cert["PfxPassword"]);
            if (!hasEnc && !hasPlain && selfSignedContext)
            {
                var pwd = selfSignedPasswordToPersist ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
                cert["PfxPassword"] = pwd;
                Options.PfxPassword = pwd; // reflect in-memory
            }

            // File name:
            if (selfSignedContext)
            {
                cert["PfxFileName"] = "selfsigned.pfx";
                Options.PfxFileName = "selfsigned.pfx";
            }
            else
            {
                // If user didn’t set anything, assume real cert naming
                if (string.IsNullOrWhiteSpace((string?)cert["PfxFileName"]))
                {
                    cert["PfxFileName"] = "https.pfx";
                    Options.PfxFileName = "https.pfx";
                }
            }

            SaveConfig(root);
        }

        // ---------- Main flow ----------

        /// <summary>
        /// 1) Normalize/persist CertificateOptions block in config.
        /// 2) Try to load the configured PFX (real cert) using encrypted/plain/empty password.
        /// 3) If missing or load fails, generate a self-signed PFX and persist settings back to config.
        /// </summary>
        private X509Certificate2 LoadOrCreateCertificate()
        {
            // First, normalize and make sure the block exists (not self-signed context yet)
            EnsureCertificateOptionsPersisted(selfSignedContext: false, selfSignedPasswordToPersist: null);

            // Always normalize the runtime OutputFolder to /config/certs
            Options.OutputFolder = DesiredFolder;

            var folder = ResolveFolderPath(Options.OutputFolder);
            Directory.CreateDirectory(folder);

            var pfxPath = Path.Combine(folder, Options.PfxFileName);

            // --- Try to load a real PFX first (using encrypted or plaintext password) ---
            var cert = TryLoadConfiguredPfx(pfxPath);
            if (cert != null)
            {
                _logger.LogInformation("Using configured HTTPS certificate: Path={PfxPath}, Subject={Subject}, NotAfter={NotAfter:u}",
                    pfxPath, cert.Subject, cert.NotAfter);
                return cert;
            }

            // --- Fallback: generate self-signed into the configured path ---
            _logger.LogWarning(
                "Falling back to self-signed certificate. Will generate new PFX at {PfxPath} (Subject={Subject}, ValidDays={Days}).",
                pfxPath, Options.SubjectName, Options.ValidDays);

            // Pick or generate a password we will PERSIST
            var selfPwd = ResolvePfxPasswordForWrite(generateIfMissing: true);

            // Ensure the config reflects self-signed + password + normalized folder/name
            EnsureCertificateOptionsPersisted(selfSignedContext: true, selfSignedPasswordToPersist: selfPwd);

            // Update runtime props after persistence
            Options.OutputFolder = DesiredFolder;
            Options.PfxFileName = "selfsigned.pfx";

            pfxPath = Path.Combine(ResolveFolderPath(Options.OutputFolder), Options.PfxFileName);
            return GenerateSelfSignedToPath(pfxPath, selfPwd);
        }

        /// <summary>
        /// Try to load the PFX at pfxPath with the appropriate password:
        /// - First prefers PfxPasswordEnc (decrypt), then falls back to PfxPassword (plaintext),
        /// - Finally tries empty password for unprotected PFX.
        /// </summary>
        private X509Certificate2? TryLoadConfiguredPfx(string pfxPath)
        {
            if (!File.Exists(pfxPath))
            {
                _logger.LogInformation("Configured PFX not found at {Path}.", pfxPath);
                return null;
            }

            var flags = X509KeyStorageFlags.MachineKeySet
                        | X509KeyStorageFlags.PersistKeySet
                        | X509KeyStorageFlags.Exportable;

            // 1) Encrypted password path
            if (!string.IsNullOrWhiteSpace(Options.PfxPasswordEnc))
            {
                try
                {
                    var decrypted = _encryption.Decrypt(Options.PfxPasswordEnc);
                    var cert = new X509Certificate2(pfxPath, decrypted, flags);
                    _logger.LogDebug("Loaded PFX with encrypted password.");
                    return cert;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load PFX with encrypted password at {Path}.", pfxPath);
                }
            }

            // 2) Plaintext password fallback (legacy)
            if (!string.IsNullOrWhiteSpace(Options.PfxPassword))
            {
                try
                {
                    var cert = new X509Certificate2(pfxPath, Options.PfxPassword, flags);
                    _logger.LogDebug("Loaded PFX with plaintext password (legacy).");
                    return cert;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load PFX with plaintext password at {Path}.", pfxPath);
                }
            }

            // 3) Empty password (unprotected PFX)
            try
            {
                var cert = new X509Certificate2(pfxPath, (string?)null, flags);
                _logger.LogDebug("Loaded PFX with empty password (unprotected).");
                return cert;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load PFX with empty password at {Path}.", pfxPath);
            }

            // If all attempts failed, return null to trigger self-signed fallback.
            return null;
        }

        /// <summary>
        /// Resolve a password to use when exporting/writing a self-signed PFX.
        /// Preference:
        ///  - if PfxPasswordEnc exists -> decrypted value,
        ///  - else if PfxPassword exists -> that,
        ///  - else generate a new random password if requested, otherwise "changeit".
        /// </summary>
        private string ResolvePfxPasswordForWrite(bool generateIfMissing)
        {
            if (!string.IsNullOrWhiteSpace(Options.PfxPasswordEnc))
            {
                try { return _encryption.Decrypt(Options.PfxPasswordEnc); }
                catch { /* fall through */ }
            }
            if (!string.IsNullOrWhiteSpace(Options.PfxPassword))
                return Options.PfxPassword!;

            return generateIfMissing
                ? Convert.ToHexString(RandomNumberGenerator.GetBytes(12))
                : "changeit";
        }

        private X509Certificate2 GenerateSelfSignedToPath(string pfxPath, string writePassword)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                Options.SubjectName,
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    critical: true));

            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // ServerAuth
                    critical: true));

            var sanBuilder = new SubjectAlternativeNameBuilder();
            foreach (var dns in _lastRequestedSANs ?? Array.Empty<string>())
                sanBuilder.AddDnsName(dns);
            request.CertificateExtensions.Add(sanBuilder.Build());

            var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
            var notAfter = notBefore.AddDays(Options.ValidDays);

            using var temp = request.CreateSelfSigned(notBefore, notAfter);

            var exportFlags = X509KeyStorageFlags.MachineKeySet
                            | X509KeyStorageFlags.PersistKeySet
                            | X509KeyStorageFlags.Exportable;

            var bytes = temp.Export(X509ContentType.Pfx, writePassword);
            var certWithKey = new X509Certificate2(bytes, writePassword, exportFlags);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(pfxPath)!);
                File.WriteAllBytes(pfxPath, certWithKey.Export(X509ContentType.Pfx, writePassword));
                _logger.LogInformation("Wrote self-signed PFX to {PfxPath}", pfxPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write self-signed PFX to {PfxPath}", pfxPath);
            }

            return certWithKey;
        }

        /// <summary>
        /// Force deletion of any existing PFX and create a brand-new self-signed certificate
        /// using the given subjectName, validDays, and SAN DNS names array.
        /// After this, CurrentCertificate returns the new one.
        /// We also persist the normalized block back into appsettings.json.
        /// </summary>
        public void Regenerate(string subjectName, int validDays, string[] sanDnsNames)
        {
            _lastRequestedSANs = sanDnsNames ?? Array.Empty<string>();

            // Update in-memory options for this run
            Options.SubjectName = subjectName;
            Options.ValidDays = validDays;
            Options.OutputFolder = DesiredFolder;
            Options.PfxFileName = "selfsigned.pfx";

            var folder = ResolveFolderPath(Options.OutputFolder);
            Directory.CreateDirectory(folder);
            var pfxPath = Path.Combine(folder, Options.PfxFileName);

            if (File.Exists(pfxPath))
            {
                try
                {
                    File.Delete(pfxPath);
                    _logger.LogInformation("Deleted existing PFX at {PfxPath} for regeneration.", pfxPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete existing PFX at {PfxPath}. It may be in use.", pfxPath);
                }
            }

            // Ensure config is updated to self-signed context (with a password)
            var selfPwd = ResolvePfxPasswordForWrite(generateIfMissing: true);
            EnsureCertificateOptionsPersisted(selfSignedContext: true, selfSignedPasswordToPersist: selfPwd);

            lock (_lock) { _certificate = null; }

            var newCert = CurrentCertificate;
            _logger.LogInformation("Regenerated certificate: Subject={Subject}, Expires={Expiry:u}",
                newCert.Subject, newCert.NotAfter);
        }
    }
}
