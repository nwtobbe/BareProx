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

using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BareProx.Services
{
    /// <summary>
    /// Binds the "CertificateOptions" section from configuration (e.g. appsettings.json).
    /// OutputFolder: where to write (and load) the self-signed .pfx
    /// PfxFileName: the file name of the .pfx
    /// PfxPassword: passphrase used when exporting the .pfx
    /// SubjectName: e.g. "CN=localhost" or "CN=yourdomain.com"
    /// ValidDays: how many days the self-signed cert should be valid
    /// </summary>
    public class CertificateOptions
    {
        /// <summary>
        /// Folder (absolute or relative) where we store/load the .pfx.
        /// Example: "/config/Certs" or "Certificates" or "D:\\CertStore\\MyApp".
        /// </summary>
        public string OutputFolder { get; set; } = "Certificates";

        /// <summary>
        /// File name (inside OutputFolder) for the self-signed PFX.
        /// </summary>
        public string PfxFileName { get; set; } = "selfsigned.pfx";

        /// <summary>
        /// Password to protect the PFX on disk.
        /// </summary>
        public string PfxPassword { get; set; } = "changeit";

        /// <summary>
        /// The X.509 subject name for the certificate (e.g. "CN=localhost").
        /// </summary>
        public string SubjectName { get; set; } = "CN=localhost";

        /// <summary>
        /// How many days from "now" the certificate should remain valid.
        /// </summary>
        public int ValidDays { get; set; } = 365;
    }

    /// <summary>
    /// This service will generate (if needed) and load a self-signed X509Certificate2,
    /// then expose it via the CurrentCertificate property. Kestrel can pull it on each TLS handshake.
    /// </summary>
    public class SelfSignedCertificateService
    {
        private readonly CertificateOptions _options;
        private readonly ILogger<SelfSignedCertificateService> _logger;
        private readonly object _lock = new();

        // Cached loaded certificate
        private X509Certificate2 _certificate;

        // When regenerating, store the requested Subject Alternative Names (SANs)
        private string[] _lastRequestedSANs = new[] { "localhost" };

        public SelfSignedCertificateService(
            IOptions<CertificateOptions> options,
            ILogger<SelfSignedCertificateService> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        /// <summary>
        /// Kestrel will call this each time it needs the server certificate.
        /// We lazy‐load or generate on first access, then keep returning the same instance.
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

        /// <summary>
        /// If a PFX exists at the configured path, load it. Otherwise, create a new self-signed certificate,
        /// save it as a PFX (with the configured password), and then load+return it.
        /// Uses _lastRequestedSANs for the SAN extension.
        /// </summary>
        private X509Certificate2 LoadOrCreateCertificate()
        {
            // 1) Determine the absolute folder path
            string outputFolderPath;
            if (Path.IsPathRooted(_options.OutputFolder))
            {
                // If user specified an absolute path, use it directly
                outputFolderPath = _options.OutputFolder;
            }
            else
            {
                // Otherwise, treat OutputFolder as a subfolder under the app's working directory
                var contentRoot = Directory.GetCurrentDirectory();
                outputFolderPath = Path.Combine(contentRoot, _options.OutputFolder);
            }

            // Make sure the folder exists
            if (!Directory.Exists(outputFolderPath))
            {
                Directory.CreateDirectory(outputFolderPath);
                _logger.LogInformation("Created certificate folder at: {Folder}", outputFolderPath);
            }

            // 2) Build the full path to the PFX file
            var pfxPath = Path.Combine(outputFolderPath, _options.PfxFileName);

            // 3) If the file already exists, try loading it
            if (File.Exists(pfxPath))
            {
                try
                {
                    var cert = new X509Certificate2(
                        pfxPath,
                        _options.PfxPassword,
                        X509KeyStorageFlags.MachineKeySet
                        | X509KeyStorageFlags.PersistKeySet
                        | X509KeyStorageFlags.Exportable);

                    _logger.LogInformation(
                        "Loaded existing self-signed certificate. Subject={Subject}, Expires={Expiry:u}",
                        cert.Subject,
                        cert.NotAfter);
                    return cert;
                }
                catch (Exception ex)
                {
                    // If loading fails (corrupted file), delete and regenerate
                    _logger.LogWarning(
                        ex,
                        "Failed to load existing PFX at {PfxPath}. It will be deleted and a new one generated.",
                        pfxPath);
                    try { File.Delete(pfxPath); } catch { /* ignore deletion failures */ }
                }
            }

            // 4) Generate a new self-signed certificate
            _logger.LogInformation(
                "Generating new self-signed certificate with Subject={Subject}",
                _options.SubjectName);

            using (var rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest(
                    _options.SubjectName,
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);

                // Add DigitalSignature + KeyEncipherment usage
                request.CertificateExtensions.Add(
                    new X509KeyUsageExtension(
                        X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                        critical: true));

                // Add Enhanced Key Usage: Server Authentication (1.3.6.1.5.5.7.3.1)
                request.CertificateExtensions.Add(
                    new X509EnhancedKeyUsageExtension(
                        new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                        critical: true));

                // Add Subject Alternative Names from _lastRequestedSANs
                var sanBuilder = new SubjectAlternativeNameBuilder();
                foreach (var dns in _lastRequestedSANs ?? Array.Empty<string>())
                {
                    sanBuilder.AddDnsName(dns);
                }
                request.CertificateExtensions.Add(sanBuilder.Build());

                // Validity: from 1 day ago (to avoid clock skew) until ValidDays from now
                var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
                var notAfter = notBefore.AddDays(_options.ValidDays);

                using (var cert = request.CreateSelfSigned(notBefore, notAfter))
                {
                    // Export as PFX with password
                    var exportFlags = X509KeyStorageFlags.MachineKeySet
                                    | X509KeyStorageFlags.PersistKeySet
                                    | X509KeyStorageFlags.Exportable;

                    var certWithKey = new X509Certificate2(
                        cert.Export(X509ContentType.Pfx, _options.PfxPassword),
                        _options.PfxPassword,
                        exportFlags);

                    // Write the PFX to disk
                    try
                    {
                        File.WriteAllBytes(
                            pfxPath,
                            certWithKey.Export(X509ContentType.Pfx, _options.PfxPassword)
                        );
                        _logger.LogInformation("Wrote new self-signed PFX to {PfxPath}", pfxPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to write self-signed PFX to {PfxPath}", pfxPath);
                    }

                    return certWithKey;
                }
            }
        }

        /// <summary>
        /// Force deletion of any existing PFX and create a brand-new self-signed certificate
        /// using the given subjectName, validDays, and SAN DNS names array.
        /// After calling this, the next call to CurrentCertificate regenerates with these parameters.
        /// </summary>
        public void Regenerate(string subjectName, int validDays, string[] sanDnsNames)
        {
            // 1) Override options in memory
            _options.SubjectName = subjectName;
            _options.ValidDays = validDays;

            // 2) Store SANs for the next generation
            _lastRequestedSANs = sanDnsNames ?? Array.Empty<string>();

            // 3) Delete existing PFX file on disk (if present)
            string outputFolderPath;
            if (Path.IsPathRooted(_options.OutputFolder))
            {
                outputFolderPath = _options.OutputFolder;
            }
            else
            {
                var contentRoot = Directory.GetCurrentDirectory();
                outputFolderPath = Path.Combine(contentRoot, _options.OutputFolder);
            }

            var pfxPath = Path.Combine(outputFolderPath, _options.PfxFileName);
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

            // 4) Clear the cached certificate so that CurrentCertificate triggers regeneration
            lock (_lock)
            {
                _certificate = null;
            }

            // 5) Immediately regenerate by accessing CurrentCertificate
            var newCert = CurrentCertificate;
            _logger.LogInformation(
                "Regenerated certificate: Subject={Subject}, Expires={Expiry:u}",
                newCert.Subject,
                newCert.NotAfter
            );
        }
    }
}
