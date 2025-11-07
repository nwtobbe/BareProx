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

using BareProx.Data;
using BareProx.Models;
using BareProx.Services;
using BareProx.Services.Background;
using BareProx.Services.Features;
using BareProx.Services.Netapp;
using BareProx.Services.Notifications;
using BareProx.Services.Proxmox;
using BareProx.Services.Proxmox.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Renci.SshNet;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TimeZoneConverter;
using IOPath = System.IO.Path;
using IODirectory = System.IO.Directory;

namespace BareProx.Controllers
{
    public class SettingsController : Controller
    {
        private const string FF_Experimental = "ExperimentalExtra";

        private readonly IFeatureService _features;
        private readonly ApplicationDbContext _context;
        private readonly ProxmoxService _proxmoxService;
        private readonly IEncryptionService _encryptionService;
        private readonly string _configFile;
        private readonly CertificateService _certService;
        private readonly IHostApplicationLifetime _appLifetime;
        private readonly INetappVolumeService _netappVolumeService;
        private readonly IProxmoxAuthenticator _proxmoxAuthenticator;
        private readonly INetappAuthService _netappAuthService;
        private readonly IEmailSender _email;
        private readonly IDataProtector _protector;
        private readonly IOptionsMonitor<CertificateOptions> _certOptions;
        private readonly IConfiguration _configuration;
        private readonly IProxmoxClusterDiscoveryService _clusterDiscovery;
        private readonly ICollectionService _collector;
        private readonly IQueryDbFactory _qdb;

        // Live Proxmox discovery states (for wizard log polling)
        private static readonly ConcurrentDictionary<Guid, ProxmoxDiscoveryState> _proxmoxDiscoveryStates
            = new();

        #region Nested types (discovery DTOs)

        private class ProxmoxDiscoveryState
        {
            public List<string> Logs { get; } = new();
            public bool Completed { get; set; }
            public bool Success { get; set; }
            public string? Error { get; set; }
            public ProxmoxDiscoveryResult? Result { get; set; }
        }

        private record ProxmoxDiscoveryNode(
            string NodeName,
            string Ip,
            string? ReverseName,
            bool SshOk
        );

        private record ProxmoxDiscoveryResult(
            string ClusterName,
            List<ProxmoxDiscoveryNode> Nodes
        );

        public record StartProxmoxDiscoveryRequest(
            string SeedHost,
            string Username,
            string Password
        );

        public record CreateClusterFromDiscoveryRequest(
            string ClusterName,
            string Username,
            string Password,
            List<DiscoveryNodeDto> Nodes
        );

        public record DiscoveryNodeDto(
            string NodeName,
            string Ip,
            string HostAddress
        );

        #endregion

        public SettingsController(
            ApplicationDbContext context,
            IFeatureService features,
            ProxmoxService proxmoxService,
            IEncryptionService encryptionService,
            CertificateService certService,
            IHostApplicationLifetime appLifetime,
            INetappVolumeService netappVolumeService,
            IProxmoxAuthenticator proxmoxAuthenticator,
            INetappAuthService netappAuthService,
            IEmailSender email,
            IDataProtectionProvider dataProtectionProvider,
            IOptionsMonitor<CertificateOptions> certOptions,
            IConfiguration configuration,
            IProxmoxClusterDiscoveryService clusterDiscovery,
            ICollectionService collector,
            IQueryDbFactory qdb
        )
        {
            _context = context;
            _features = features;
            _proxmoxService = proxmoxService;
            _encryptionService = encryptionService;
            _configFile = IOPath.Combine("/config", "appsettings.json");
            _certService = certService;
            _appLifetime = appLifetime;
            _netappVolumeService = netappVolumeService;
            _proxmoxAuthenticator = proxmoxAuthenticator;
            _netappAuthService = netappAuthService;
            _email = email;
            _protector = dataProtectionProvider.CreateProtector("BareProx.EmailSettings.Password.v1");
            _clusterDiscovery = clusterDiscovery;
            _certOptions = certOptions;
            _configuration = configuration;
            _collector = collector;
            _qdb = qdb;
        }

        // =====================================================================
        // CONFIG PAGE
        // =====================================================================

        [HttpGet]
        public async Task<IActionResult> Config(CancellationToken ct)
        {
            JObject cfg = System.IO.File.Exists(_configFile)
                ? JObject.Parse(System.IO.File.ReadAllText(_configFile))
                : new JObject();

            var storedWindows = (string?)cfg["ConfigSettings"]?["TimeZoneWindows"];
            var storedIana = (string?)cfg["ConfigSettings"]?["TimeZoneIana"];

            string selectedWindowsId;
            if (!string.IsNullOrWhiteSpace(storedWindows))
            {
                selectedWindowsId = storedWindows.Trim();
            }
            else if (!string.IsNullOrWhiteSpace(storedIana))
            {
                try { selectedWindowsId = TZConvert.IanaToWindows(storedIana.Trim()); }
                catch { selectedWindowsId = GetLocalWindowsId(); }
            }
            else
            {
                selectedWindowsId = GetLocalWindowsId();
            }

            var cert = _certService.CurrentCertificate;

            var vm = new SettingsPageViewModel
            {
                Config = new ConfigSettingsViewModel
                {
                    TimeZoneWindows = selectedWindowsId,
                    TimeZoneIana = storedIana ?? ""
                },
                Regenerate = new RegenerateCertViewModel
                {
                    CurrentSubject = cert?.Subject,
                    CurrentNotBefore = cert?.NotBefore,
                    CurrentNotAfter = cert?.NotAfter,
                    CurrentThumbprint = cert?.Thumbprint,
                    RegenSubjectName = cert?.Subject ?? (_certOptions.CurrentValue?.SubjectName ?? "CN=localhost"),
                    RegenValidDays = _certOptions.CurrentValue?.ValidDays > 0
                        ? _certOptions.CurrentValue.ValidDays
                        : 365,
                    RegenSANs = "localhost"
                },
                Updates = new UpdateSettingsViewModel
                {
                    Enabled = (bool?)cfg["Updates"]?["Enabled"] ?? false,
                    FrequencyMinutes = (int?)cfg["Updates"]?["FrequencyMinutes"] ?? 360
                }
            };

            // Ensure EmailSettings row exists
            var es = await _context.EmailSettings.AsNoTracking().FirstOrDefaultAsync(e => e.Id == 1, ct);
            if (es == null)
            {
                es = new EmailSettings
                {
                    Id = 1,
                    Enabled = false,
                    SmtpPort = 587,
                    SecurityMode = "StartTls",
                    MinSeverity = "Info"
                };
                _context.EmailSettings.Add(es);
                await _context.SaveChangesAsync(ct);
            }

            vm.Email.Enabled = es.Enabled;
            vm.Email.SmtpHost = es.SmtpHost;
            vm.Email.SmtpPort = es.SmtpPort;
            vm.Email.SecurityMode = es.SecurityMode ?? "StartTls";
            vm.Email.Username = es.Username;
            vm.Email.From = es.From;
            vm.Email.DefaultRecipients = es.DefaultRecipients;
            vm.Email.OnBackupSuccess = es.OnBackupSuccess;
            vm.Email.OnBackupFailure = es.OnBackupFailure;
            vm.Email.OnRestoreSuccess = es.OnRestoreSuccess;
            vm.Email.OnRestoreFailure = es.OnRestoreFailure;
            vm.Email.OnWarnings = es.OnWarnings;
            vm.Email.MinSeverity = es.MinSeverity ?? "Info";

            vm.TimeZones = BuildTimeZoneSelectList(selectedWindowsId);
            ViewBag.ExperimentalExtra = await _features.IsEnabledAsync(FF_Experimental);

            return View("Config", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleExperimental()
        {
            var enabled = Request.Form.ContainsKey("ExperimentalExtra");
            await _features.SetAsync(FF_Experimental, enabled);
            TempData["Success"] = "Experimental features setting updated.";
            return RedirectToAction(nameof(Config));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Config([Bind(Prefix = "Config")] ConfigSettingsViewModel configVm)
        {
            if (!ModelState.IsValid)
            {
                var pageVm = BuildSettingsPageViewModel();
                pageVm.Config = configVm;
                pageVm.TimeZones = BuildTimeZoneSelectList(configVm.TimeZoneWindows);
                return View("Config", pageVm);
            }

            string ianaId;
            try { ianaId = TZConvert.WindowsToIana(configVm.TimeZoneWindows.Trim()); }
            catch { ianaId = configVm.TimeZoneWindows.Trim(); }

            JObject cfg = System.IO.File.Exists(_configFile)
                ? JObject.Parse(System.IO.File.ReadAllText(_configFile))
                : new JObject();

            if (cfg["ConfigSettings"] == null || cfg["ConfigSettings"]!.Type != JTokenType.Object)
                cfg["ConfigSettings"] = new JObject();

            var section = (JObject)cfg["ConfigSettings"]!;
            section["TimeZoneWindows"] = configVm.TimeZoneWindows.Trim();
            section["TimeZoneIana"] = ianaId;

            System.IO.File.WriteAllText(_configFile, cfg.ToString());
            TempData["Success"] = $"Default time zone “{configVm.TimeZoneWindows}” saved.";
            return RedirectToAction(nameof(Config));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult SaveUpdateSettings([Bind(Prefix = "Updates")] UpdateSettingsViewModel vm)
        {
            var allowed = new HashSet<int> { 1440, 2880, 10080 };
            if (!allowed.Contains(vm.FrequencyMinutes))
                vm.FrequencyMinutes = 10080;

            JObject cfg = System.IO.File.Exists(_configFile)
                ? JObject.Parse(System.IO.File.ReadAllText(_configFile))
                : new JObject();

            if (cfg["Updates"] == null || cfg["Updates"]!.Type != JTokenType.Object)
                cfg["Updates"] = new JObject();

            var upd = (JObject)cfg["Updates"]!;
            upd["Enabled"] = vm.Enabled;
            upd["FrequencyMinutes"] = vm.FrequencyMinutes;

            System.IO.File.WriteAllText(_configFile, cfg.ToString());
            TempData["Success"] = "Update checker settings saved.";
            return RedirectToAction(nameof(Config));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadPfx(IFormFile? pfxFile, string? pfxPassword, CancellationToken ct)
        {
            if (pfxFile is null || pfxFile.Length == 0)
            {
                TempData["Error"] = "Please choose a .pfx file.";
                return RedirectToAction(nameof(Config), new { pane = "certificates" });
            }

            byte[] pfxBytes;
            using (var ms = new MemoryStream())
            {
                await pfxFile.CopyToAsync(ms, ct);
                pfxBytes = ms.ToArray();
            }

            var plainPwd = string.IsNullOrWhiteSpace(pfxPassword) ? null : pfxPassword.Trim();
            try
            {
                _ = string.IsNullOrEmpty(plainPwd)
                    ? new X509Certificate2(pfxBytes)
                    : new X509Certificate2(pfxBytes, plainPwd);
            }
            catch (Exception ex)
            {
                TempData["Error"] =
                    $"The uploaded PFX could not be opened ({ex.GetType().Name}). Check the password and try again.";
                return RedirectToAction(nameof(Config), new { pane = "certificates" });
            }

            var configCertsDir = IOPath.Combine("/config", "Certs");
            IODirectory.CreateDirectory(configCertsDir);
            var targetPath = IOPath.Combine(configCertsDir, "https.pfx");

            if (System.IO.File.Exists(targetPath))
            {
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                var baseName = $"https.pfx.{stamp}";
                var backupPath = IOPath.Combine(configCertsDir, $"{baseName}.old");

                // Try the plain stamped name first, then add a short counter if that exists too
                const int maxAttempts = 10;
                var attempt = 0;

                while (true)
                {
                    try
                    {
                        System.IO.File.Move(targetPath, backupPath, overwrite: false);
                        break; // moved successfully
                    }
                    catch (IOException) when (attempt < maxAttempts)
                    {
                        // Likely name collision; try another variant
                        attempt++;
                        backupPath = IOPath.Combine(configCertsDir, $"{baseName}-{attempt:D2}.old");
                        continue;
                    }
                    catch
                    {
                        // Re-throw anything that's not a simple collision or after too many attempts
                        throw;
                    }
                }
            }

            await System.IO.File.WriteAllBytesAsync(targetPath, pfxBytes, ct);

            JObject cfg = System.IO.File.Exists(_configFile)
                ? JObject.Parse(System.IO.File.ReadAllText(_configFile))
                : new JObject();

            if (cfg["CertificateOptions"] == null || cfg["CertificateOptions"]!.Type != JTokenType.Object)
                cfg["CertificateOptions"] = new JObject();

            var certNode = (JObject)cfg["CertificateOptions"]!;
            certNode["OutputFolder"] = configCertsDir.Replace('\\', '/');
            certNode["PfxFileName"] = "https.pfx";

            string? encPwd = string.IsNullOrWhiteSpace(plainPwd) ? null : _encryptionService.Encrypt(plainPwd);
            certNode["PfxPasswordEnc"] = encPwd;
            certNode.Remove("PfxPassword");

            if (certNode["SubjectName"] == null) certNode["SubjectName"] = "CN=localhost";
            if (certNode["ValidDays"] == null) certNode["ValidDays"] = 365;

            System.IO.File.WriteAllText(_configFile, cfg.ToString());

            TempData["Success"] = "Certificate installed. Please restart to apply.";
            TempData["RestartRequired"] = true;
            return RedirectToAction(nameof(Config), new { pane = "certificates" });
        }

        private SettingsPageViewModel BuildSettingsPageViewModel()
        {
            JObject cfg = System.IO.File.Exists(_configFile)
                ? JObject.Parse(System.IO.File.ReadAllText(_configFile))
                : new JObject();

            var storedWindows = (string?)cfg["ConfigSettings"]?["TimeZoneWindows"];
            var storedIana = (string?)cfg["ConfigSettings"]?["TimeZoneIana"];

            string selectedWindowsId;
            if (!string.IsNullOrWhiteSpace(storedWindows))
            {
                selectedWindowsId = storedWindows.Trim();
            }
            else if (!string.IsNullOrWhiteSpace(storedIana))
            {
                try { selectedWindowsId = TZConvert.IanaToWindows(storedIana.Trim()); }
                catch { selectedWindowsId = GetLocalWindowsId(); }
            }
            else
            {
                selectedWindowsId = GetLocalWindowsId();
            }

            var cert = _certService.CurrentCertificate;

            var vm = new SettingsPageViewModel
            {
                Config = new ConfigSettingsViewModel
                {
                    TimeZoneWindows = selectedWindowsId,
                    TimeZoneIana = storedIana ?? ""
                },
                Regenerate = new RegenerateCertViewModel
                {
                    CurrentSubject = cert?.Subject,
                    CurrentNotBefore = cert?.NotBefore,
                    CurrentNotAfter = cert?.NotAfter,
                    CurrentThumbprint = cert?.Thumbprint,
                    RegenSubjectName = cert?.Subject ?? (_certOptions.CurrentValue?.SubjectName ?? "CN=localhost"),
                    RegenValidDays = _certOptions.CurrentValue?.ValidDays > 0
                        ? _certOptions.CurrentValue.ValidDays
                        : 365,
                    RegenSANs = "localhost"
                },
                Updates = new UpdateSettingsViewModel
                {
                    Enabled = (bool?)cfg["Updates"]?["Enabled"] ?? false,
                    FrequencyMinutes = (int?)cfg["Updates"]?["FrequencyMinutes"] ?? 360
                }
            };

            try
            {
                var es = _context.EmailSettings.AsNoTracking().FirstOrDefault(e => e.Id == 1);
                if (es != null)
                {
                    vm.Email.Enabled = es.Enabled;
                    vm.Email.SmtpHost = es.SmtpHost;
                    vm.Email.SmtpPort = es.SmtpPort;
                    vm.Email.SecurityMode = es.SecurityMode ?? "StartTls";
                    vm.Email.Username = es.Username;
                    vm.Email.From = es.From;
                    vm.Email.DefaultRecipients = es.DefaultRecipients;
                    vm.Email.OnBackupSuccess = es.OnBackupSuccess;
                    vm.Email.OnBackupFailure = es.OnBackupFailure;
                    vm.Email.OnRestoreSuccess = es.OnRestoreSuccess;
                    vm.Email.OnRestoreFailure = es.OnRestoreFailure;
                    vm.Email.OnWarnings = es.OnWarnings;
                    vm.Email.MinSeverity = es.MinSeverity ?? "Info";
                }
            }
            catch
            {
                // ignore, keep view rendering
            }

            vm.TimeZones = BuildTimeZoneSelectList(selectedWindowsId);
            return vm;
        }

        private IEnumerable<SelectListItem> BuildTimeZoneSelectList(string selectedWindowsId)
        {
            var allZones = TimeZoneInfo.GetSystemTimeZones();
            var items = allZones.Select(tzInfo =>
            {
                string windowsId;
                string ianaId;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    windowsId = tzInfo.Id;
                    ianaId = TZConvert.WindowsToIana(tzInfo.Id);
                }
                else
                {
                    ianaId = tzInfo.Id;
                    try { windowsId = TZConvert.IanaToWindows(tzInfo.Id); }
                    catch { windowsId = tzInfo.Id; }
                }

                var display = $"{tzInfo.DisplayName}  [{ianaId}]";
                return new SelectListItem
                {
                    Text = display,
                    Value = windowsId,
                    Selected = string.Equals(windowsId, selectedWindowsId, StringComparison.OrdinalIgnoreCase)
                };
            })
            .OrderBy(x => x.Text)
            .ToList();

            return items;
        }

        private string GetLocalWindowsId()
        {
            var local = TimeZoneInfo.Local.Id;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return local;

            try { return TZConvert.IanaToWindows(local); }
            catch { return local; }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult RegenerateCert([Bind(Prefix = "Regenerate")] RegenerateCertViewModel regenVm)
        {
            ModelState.Remove("Regenerate.CurrentSubject");
            ModelState.Remove("Regenerate.CurrentNotBefore");
            ModelState.Remove("Regenerate.CurrentNotAfter");
            ModelState.Remove("Regenerate.CurrentThumbprint");

            if (!ModelState.IsValid)
            {
                var pageVm = BuildSettingsPageViewModel();
                pageVm.Regenerate.RegenSubjectName = regenVm.RegenSubjectName;
                pageVm.Regenerate.RegenValidDays = regenVm.RegenValidDays;
                pageVm.Regenerate.RegenSANs = regenVm.RegenSANs;
                return View("Config", pageVm);
            }

            var sansList = Array.Empty<string>();
            if (!string.IsNullOrWhiteSpace(regenVm.RegenSANs))
            {
                sansList = regenVm.RegenSANs
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToArray();
            }

            _certService.Regenerate(
                subjectName: regenVm.RegenSubjectName,
                validDays: regenVm.RegenValidDays,
                sanDnsNames: sansList
            );

            TempData["Success"] = "Certificate regenerated successfully.";
            TempData["RestartRequired"] = true;
            return RedirectToAction(nameof(Config), new { pane = "certificates" });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult RestartApp()
        {
            HttpContext.Response.Headers.Add("Refresh", "3");
            _appLifetime.StopApplication();
            return Content("Application is restarting...");
        }

        // =====================================================================
        // PROXMOX: CLUSTERS + WIZARD
        // =====================================================================

        [HttpGet]
        public async Task<IActionResult> ProxmoxHub(int? selectedId = null, CancellationToken ct = default)
        {
            var clusters = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .ToListAsync(ct);

            // NEW: pull statuses from Query DB
            var statusMap = new Dictionary<int, InventoryClusterStatus>();
            await using (var q = await _qdb.CreateAsync(ct))
            {
                var ids = clusters.Select(c => c.Id).ToList();
                var rows = await q.InventoryClusterStatuses
                                  .Where(s => ids.Contains(s.ClusterId))
                                  .ToListAsync(ct);
                foreach (var r in rows)
                    statusMap[r.ClusterId] = r;
            }

            ProxmoxCluster? selected = null;
            SelectStorageViewModel? storageView = null;

            if (selectedId.HasValue)
            {
                selected = clusters.FirstOrDefault(c => c.Id == selectedId.Value);
                if (selected != null)
                    storageView = await BuildStorageViewAsync(selected.Id, ct);
            }

            var vm = new ProxmoxHubViewModel
            {
                Clusters = clusters,
                SelectedCluster = selected,
                StorageView = storageView,
                SelectedId = selectedId,
                Message = TempData["Message"] as string,
                ClusterStatuses = statusMap            // NEW
            };

            return View(vm);
        }


        [HttpPost]
        public async Task<IActionResult> AuthenticateCluster(int id, CancellationToken ct)
        {
            var success = await _proxmoxAuthenticator.AuthenticateAndStoreTokenCidAsync(id, ct);

            if (success)
            {
                // Only run collection if this cluster already has hosts
                var hasHosts = await _context.ProxmoxHosts
                    .AnyAsync(h => h.ClusterId == id, ct);

                if (hasHosts)
                    await _collector.RunProxmoxClusterStatusCheckAsync(ct);
            }

            TempData["Message"] = success
                ? "Authentication successful."
                : "Authentication failed.";
            return RedirectToAction(nameof(ProxmoxHub), new { selectedId = id });
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddCluster(string name, string username, string password, CancellationToken ct)
        {
            // Single-cluster guard (your UI is single-cluster mode)
            var existingCount = await _context.ProxmoxClusters.CountAsync(ct);
            if (existingCount > 0)
            {
                TempData["Message"] = "A cluster is already configured. Only one Proxmox cluster is supported.";
                return RedirectToAction(nameof(ProxmoxHub));
            }

            // Normalize inputs
            var clusterName = (name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(clusterName))
                clusterName = "ProxmoxCluster";

            var userTrim = string.IsNullOrWhiteSpace(username) ? "root@pam" : username.Trim();
            var pwd = password ?? string.Empty;

            // Token defaults
            var lifetimeDays = 180;                           // ~6 months
            var renewBeforeMinutes = 10080;                   // 7 days
            var lifetimeMinutes = checked(lifetimeDays * 24 * 60);
            if (renewBeforeMinutes >= lifetimeMinutes)
                renewBeforeMinutes = Math.Max(1, lifetimeMinutes - 1); // guard

            // Ensure token id has user!token format
            var tokenId = $"{userTrim}!bareprox";

            var cluster = new ProxmoxCluster
            {
                Name = clusterName,
                Username = userTrim,
                PasswordHash = _encryptionService.Encrypt(pwd),

                // removed: LastStatus / LastChecked
                UseApiToken = true,
                ApiTokenId = tokenId,
                ApiTokenLifetimeDays = lifetimeDays,
                ApiTokenRenewBeforeMinutes = renewBeforeMinutes
            };

            _context.ProxmoxClusters.Add(cluster);
            await _context.SaveChangesAsync(ct);

            // Write initial status to Query DB
            await UpsertClusterStatusAsync(
                cluster.Id,
                cluster.Name,
                lastStatus: "configured",
                lastStatusMessage: null,
                utcNow: DateTime.UtcNow,
                ct);

            var success = await _proxmoxAuthenticator.AuthenticateAndStoreTokenCidAsync(cluster.Id, ct);

            TempData["Message"] = success
                ? $"Cluster \"{cluster.Name}\" added and authenticated. Now add hosts to complete configuration."
                : $"Cluster \"{cluster.Name}\" added, but authentication failed. Please verify credentials.";

            return RedirectToAction(nameof(ProxmoxHub), new { selectedId = cluster.Id, tab = "storage" });
        }




        [HttpPost]
        public async Task<IActionResult> DeleteCluster(int id, CancellationToken ct)
        {
            using var tx = await _context.Database.BeginTransactionAsync(ct);

            var cluster = await _context.ProxmoxClusters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
            if (cluster == null) return RedirectToAction(nameof(ProxmoxHub));

            var hosts = await _context.ProxmoxHosts.Where(h => h.ClusterId == id).ToListAsync(ct);
            _context.ProxmoxHosts.RemoveRange(hosts);

            var selectedStorages = await _context.SelectedStorages.Where(s => s.ClusterId == id).ToListAsync(ct);
            _context.SelectedStorages.RemoveRange(selectedStorages);

            var trackedCluster = _context.ProxmoxClusters.Attach(cluster).Entity;
            _context.ProxmoxClusters.Remove(trackedCluster);

            await _context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await RemoveClusterStatusAsync(id, ct);

            TempData["Message"] = $"Cluster \"{cluster.Name}\" and related data were deleted.";
            return RedirectToAction(nameof(ProxmoxHub));
        }

        [HttpPost]
        public async Task<IActionResult> EditCluster(ProxmoxCluster cluster, CancellationToken ct)
        {
            var existing = await _context.ProxmoxClusters.FindAsync(cluster.Id, ct);
            if (existing == null) return NotFound();

            if (!string.IsNullOrWhiteSpace(cluster.Username))
                existing.Username = cluster.Username.Trim();

            if (!string.IsNullOrWhiteSpace(cluster.PasswordHash))
                existing.PasswordHash = _encryptionService.Encrypt(cluster.PasswordHash);

            await _context.SaveChangesAsync(ct);
            TempData["Message"] = $"Cluster \"{existing.Name}\" saved.";
            return RedirectToAction(nameof(ProxmoxHub), new { selectedId = existing.Id, tab = "edit" });
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditClusterAuth(
      ProxmoxClusterAuthOptionsVm vm,
      string? action,
      CancellationToken ct)
        {
            var c = await _context.ProxmoxClusters
                .Include(x => x.Hosts)
                .FirstOrDefaultAsync(x => x.Id == vm.Id, ct);
            if (c == null) return NotFound();

            // ---- Normalize + clamp lifetimes ----
            var lifetimeDays = vm.ApiTokenLifetimeDays ?? c.ApiTokenLifetimeDays ?? 180;
            if (lifetimeDays <= 0) lifetimeDays = 180;

            var lifetimeMinutes = checked(lifetimeDays * 24 * 60);
            var renewBefore = vm.ApiTokenRenewBeforeMinutes > 0
                ? vm.ApiTokenRenewBeforeMinutes
                : (c.ApiTokenRenewBeforeMinutes > 0 ? c.ApiTokenRenewBeforeMinutes : 1440);

            if (vm.UseApiToken && renewBefore >= lifetimeMinutes)
            {
                TempData["Message"] = $"Renew-before must be lower than the lifetime ({lifetimeMinutes} minutes).";
                return RedirectToAction(nameof(ProxmoxHub), new { selectedId = c.Id, tab = "edit" });
            }

            // Always persist lifetime settings (even if disabling)
            c.ApiTokenLifetimeDays = lifetimeDays;
            c.ApiTokenRenewBeforeMinutes = Math.Max(1, Math.Min(renewBefore, lifetimeMinutes - 1));

            // ---- Handle DISABLE: keep stored secret so user can re-enable later ----
            if (!vm.UseApiToken)
            {
                c.UseApiToken = false;
                await _context.SaveChangesAsync(ct);
                TempData["Message"] = "API token mode disabled. Using ticket/CSRF.";
                return RedirectToAction(nameof(ProxmoxHub), new { selectedId = c.Id, tab = "edit" });
            }

            // ---- Enabling / staying enabled: normalize ApiTokenId ----
            if (!string.IsNullOrWhiteSpace(vm.ApiTokenId))
            {
                var user = string.IsNullOrWhiteSpace(c.Username) ? "root@pam" : c.Username.Trim();
                var id = vm.ApiTokenId.Trim();
                c.ApiTokenId = id.Contains('!') ? id : $"{user}!{id}";
            }
            else if (string.IsNullOrWhiteSpace(c.ApiTokenId))
            {
                var user = string.IsNullOrWhiteSpace(c.Username) ? "root@pam" : c.Username.Trim();
                // stable per-cluster default
                c.ApiTokenId = $"{user}!bareprox-{c.Id}";
            }

            // Mark intent to use token mode
            c.UseApiToken = true;
            await _context.SaveChangesAsync(ct);

            // ---- Rotate now (force recreate) OR ensure present/valid ----
            bool ok;
            if (string.Equals(action, "RotateTokenNow", StringComparison.OrdinalIgnoreCase))
            {
                // Make it "expiring now" so authenticator will recreate
                c.ApiTokenExpiresUtc = DateTime.UtcNow.AddMinutes(1);
                await _context.SaveChangesAsync(ct);
                ok = await _proxmoxAuthenticator.AuthenticateAndStoreTokenCidAsync(c.Id, ct);
            }
            else
            {
                ok = await _proxmoxAuthenticator.AuthenticateAndStoreTokenCidAsync(c.Id, ct);
            }

            if (!ok)
            {
                // Could not create/rotate a token → revert to ticket mode so UI doesn't get stuck
                c.UseApiToken = false;
                await _context.SaveChangesAsync(ct);
                TempData["Message"] = "Failed to enable API token mode (couldn’t create/rotate token). Check credentials/permissions.";
                return RedirectToAction(nameof(ProxmoxHub), new { selectedId = c.Id, tab = "edit" });
            }

            TempData["Message"] = string.Equals(action, "RotateTokenNow", StringComparison.OrdinalIgnoreCase)
                ? "Token rotated and API token mode enabled."
                : "API token mode enabled.";
            return RedirectToAction(nameof(ProxmoxHub), new { selectedId = c.Id, tab = "edit" });
        }



        [HttpPost]
        public async Task<IActionResult> AddHost(int clusterId, string hostAddress, string hostname, CancellationToken ct)
        {
            var cluster = await _context.ProxmoxClusters.Include(c => c.Hosts)
                .FirstOrDefaultAsync(c => c.Id == clusterId, ct);
            if (cluster == null) return NotFound();

            _context.ProxmoxHosts.Add(new ProxmoxHost
            {
                HostAddress = hostAddress,
                Hostname = hostname,
                ClusterId = clusterId
            });
            await _context.SaveChangesAsync(ct);

            TempData["Message"] = $"Host \"{hostname}\" added.";
            return RedirectToAction(nameof(ProxmoxHub), new { selectedId = clusterId, tab = "edit" });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteHost(int id, CancellationToken ct)
        {
            var host = await _context.ProxmoxHosts.FindAsync(id, ct);
            if (host == null) return NotFound();

            var clusterId = host.ClusterId;
            _context.ProxmoxHosts.Remove(host);
            await _context.SaveChangesAsync(ct);

            TempData["Message"] = "Host removed.";
            return RedirectToAction(nameof(ProxmoxHub), new { selectedId = clusterId, tab = "edit" });
        }

        // ---------- Live discovery: start ----------

        [HttpPost]
        public async Task<IActionResult> StartProxmoxDiscovery(
     [FromBody] StartProxmoxDiscoveryRequest req,
     CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(req.SeedHost) ||
                string.IsNullOrWhiteSpace(req.Username) ||
                string.IsNullOrWhiteSpace(req.Password))
            {
                return BadRequest(new
                {
                    success = false,
                    error = "Missing seed host / username / password."
                });
            }

            var existingCount = await _context.ProxmoxClusters.CountAsync(ct);
            if (existingCount > 0)
            {
                return BadRequest(new
                {
                    success = false,
                    error = "A cluster is already configured."
                });
            }

            var id = Guid.NewGuid();
            var state = new ProxmoxDiscoveryState();
            _proxmoxDiscoveryStates[id] = state;

            // Run discovery in background; UI will poll a /status endpoint using this id.
            _ = Task.Run(async () =>
            {
                void LogToState(string msg)
                {
                    lock (state.Logs)
                    {
                        state.Logs.Add(msg);
                    }
                }

                try
                {
                    LogToState($"Starting Proxmox API discovery using seed host '{req.SeedHost}' and user '{req.Username}' ...");

                    // Use API-based discovery with live log callback.
                    var result = await _clusterDiscovery.DiscoverAsync(
                        req.SeedHost,
                        req.Username,
                        req.Password,
                        LogToState,
                        CancellationToken.None); // don't bind to HTTP request lifetime

                    if (!result.Success)
                    {
                        state.Error = result.Error ?? "Discovery failed.";
                        state.Success = false;
                        state.Completed = true;
                        LogToState("ERROR: " + state.Error);
                        return;
                    }

                    // Map service result -> wizard DTOs.
                    var nodes = result.Nodes
                        .OrderBy(n => n.NodeName, StringComparer.OrdinalIgnoreCase)
                        .Select(n => new ProxmoxDiscoveryNode(
                            n.NodeName,
                            n.IpAddress,
                            n.ReverseName,
                            n.SshOk // here: "API reachable"
                        ))
                        .ToList();

                    state.Result = new ProxmoxDiscoveryResult(
                        result.ClusterName ?? "ProxmoxCluster",
                        nodes);

                    state.Success = true;
                    state.Completed = true;
                    LogToState("Discovery completed successfully.");
                }
                catch (Exception ex)
                {
                    state.Error = ex.Message;
                    state.Success = false;
                    state.Completed = true;
                    LogToState("ERROR: " + ex.Message);
                }
            }, CancellationToken.None);

            // Return the discovery id immediately; frontend will poll using this id.
            return Ok(new { success = true, id });
        }


        // ---------- Live discovery: poll status ----------

        [HttpGet]
        public IActionResult GetProxmoxDiscoveryStatus(Guid id)
        {
            if (!_proxmoxDiscoveryStates.TryGetValue(id, out var state))
                return NotFound();

            List<string> logs;
            lock (state.Logs)
            {
                logs = state.Logs.ToList();
            }

            return Ok(new
            {
                success = state.Success,
                completed = state.Completed,
                error = state.Error,
                logs,
                result = state.Result == null
                    ? null
                    : new
                    {
                        clusterName = state.Result.ClusterName,
                        nodes = state.Result.Nodes.Select(n => new
                        {
                            nodeName = n.NodeName,
                            ip = n.Ip,
                            reverseName = n.ReverseName,
                            sshOk = n.SshOk
                        }).ToList()
                    }
            });
        }

        // ---------- Create cluster from discovery result ----------

        [HttpPost]
        public async Task<IActionResult> CreateClusterFromDiscovery(
     [FromBody] CreateClusterFromDiscoveryRequest req,
     CancellationToken ct)
        {
            // Basic payload validation
            if (string.IsNullOrWhiteSpace(req.ClusterName) ||
                string.IsNullOrWhiteSpace(req.Password) ||
                req.Nodes is null || req.Nodes.Count == 0)
            {
                return BadRequest("Invalid payload.");
            }

            // Normalize username (default to root@pam)
            var userTrim = (req.Username ?? "root@pam").Trim();

            // Enforce single-cluster mode
            var existingCount = await _context.ProxmoxClusters.CountAsync(ct);
            if (existingCount > 0)
                return BadRequest("A cluster is already configured.");

            // Pick a seed host and verify credentials before persisting anything
            var firstNode = req.Nodes.First();
            var seedHost = !string.IsNullOrWhiteSpace(firstNode.HostAddress)
                ? firstNode.HostAddress
                : firstNode.Ip;

            var verify = await _clusterDiscovery.VerifyAsync(
                seedHost,
                userTrim,
                req.Password,
                log: null,
                ct: ct);

            if (!verify.Success)
            {
                return BadRequest("Verification against seed node failed: " +
                                  (verify.Error ?? "Unknown error"));
            }

            // Create cluster with API token defaults enabled
            var cluster = new ProxmoxCluster
            {
                Name = req.ClusterName.Trim(),
                Username = userTrim,
                PasswordHash = _encryptionService.Encrypt(req.Password),

                // removed: LastStatus / LastChecked
                UseApiToken = true,
                ApiTokenId = $"{userTrim}!bareprox",
                ApiTokenLifetimeDays = 180,
                ApiTokenRenewBeforeMinutes = 10080
            };

            await using var tx = await _context.Database.BeginTransactionAsync(ct);
            _context.ProxmoxClusters.Add(cluster);
            await _context.SaveChangesAsync(ct);

            // Write initial status to Query DB
            await UpsertClusterStatusAsync(
                cluster.Id,
                cluster.Name,
                lastStatus: "configured",
                lastStatusMessage: null,
                utcNow: DateTime.UtcNow,
                ct);


            // Build host list
            var hosts = req.Nodes
                .Where(n => !string.IsNullOrWhiteSpace(n.Ip))
                .Select(n => new ProxmoxHost
                {
                    ClusterId = cluster.Id,
                    Hostname = string.IsNullOrWhiteSpace(n.NodeName)
                        ? (string.IsNullOrWhiteSpace(n.HostAddress) ? n.Ip : n.HostAddress)
                        : n.NodeName,
                    HostAddress = string.IsNullOrWhiteSpace(n.HostAddress) ? n.Ip : n.HostAddress
                })
                .ToList();

            if (hosts.Count == 0)
            {
                await tx.RollbackAsync(ct);
                return BadRequest("No usable nodes after validation.");
            }

            _context.ProxmoxHosts.AddRange(hosts);
            await _context.SaveChangesAsync(ct);

            await tx.CommitAsync(ct);

            // Auto-authenticate discovered cluster (will create/rotate token when UseApiToken = true)
            var success = await _proxmoxAuthenticator.AuthenticateAndStoreTokenCidAsync(cluster.Id, ct);

            if (success)
            {
                // Immediate health check to populate status fields
                await _collector.RunProxmoxClusterStatusCheckAsync(ct);
            }

            TempData["Message"] =
                success
                    ? $"Cluster \"{cluster.Name}\" discovered, added with {hosts.Count} host(s), and authenticated."
                    : $"Cluster \"{cluster.Name}\" discovered and added with {hosts.Count} host(s), but authentication failed. Please verify credentials.";

            return Ok(new { clusterId = cluster.Id, authenticated = success });
        }



        private async Task<SelectStorageViewModel> BuildStorageViewAsync(int clusterId, CancellationToken ct)
        {
            var cluster = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync(c => c.Id == clusterId, ct);

            if (cluster == null)
            {
                return new SelectStorageViewModel
                {
                    ClusterId = clusterId,
                    StorageList = new List<ProxmoxStorageDto>()
                };
            }

            var allNfsStorage = await _proxmoxService.GetNfsStorageAsync(cluster, ct);

            var selectedIds = await _context.Set<ProxSelectedStorage>()
                .Where(s => s.ClusterId == clusterId)
                .Select(s => s.StorageIdentifier)
                .ToHashSetAsync(StringComparer.OrdinalIgnoreCase, ct);

            var storageList = allNfsStorage.Select(s => new ProxmoxStorageDto
            {
                Id = s.Storage,
                Storage = s.Storage,
                Type = s.Type,
                Path = s.Path,
                Node = s.Node,
                IsSelected = selectedIds.Contains(s.Storage)
            }).ToList();

            return new SelectStorageViewModel
            {
                ClusterId = clusterId,
                StorageList = storageList
            };
        }

        // =====================================================================
        // NETAPP
        // =====================================================================

        [HttpGet]
        public async Task<IActionResult> NetappHub(int? selectedId = null, CancellationToken ct = default)
        {
            var list = await _context.NetappControllers.ToListAsync(ct);
            NetappController? selected = selectedId.HasValue
                ? list.FirstOrDefault(x => x.Id == selectedId.Value)
                : null;

            var vm = new NetappHubViewModel
            {
                Controllers = list,
                Selected = selected,
                SelectedId = selectedId,
                Message = TempData["Message"] as string
            };

            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            [FromForm] string Hostname,
            [FromForm] string IpAddress,
            [FromForm] bool IsPrimary,
            [FromForm] string Username,
            [FromForm] string PasswordHash,
            CancellationToken ct)
        {
            var ok = await _netappAuthService.TryAuthenticateAsync(IpAddress, Username, PasswordHash, ct);
            if (!ok)
            {
                TempData["Message"] = "Authentication failed. Please verify IP/username/password.";
                return RedirectToAction(nameof(NetappHub));
            }

            var controller = new NetappController
            {
                Hostname = Hostname,
                IpAddress = IpAddress,
                IsPrimary = IsPrimary,
                Username = Username,
                PasswordHash = _encryptionService.Encrypt(PasswordHash)
            };

            if (!ModelState.IsValid)
            {
                TempData["Message"] = "Validation failed.";
                return RedirectToAction(nameof(NetappHub));
            }

            _context.Add(controller);
            await _context.SaveChangesAsync(ct);

            TempData["Message"] = $"Controller \"{controller.Hostname}\" added.";
            return RedirectToAction(nameof(NetappHub), new { selectedId = controller.Id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(
            int id,
            string Hostname,
            string IpAddress,
            bool IsPrimary,
            string Username,
            string PasswordHash,
            CancellationToken ct)
        {
            var existing = await _context.NetappControllers.FindAsync(id, ct);
            if (existing == null) return RedirectToAction(nameof(NetappHub));

            if (!string.IsNullOrWhiteSpace(PasswordHash))
            {
                var okNew = await _netappAuthService.TryAuthenticateAsync(IpAddress, Username, PasswordHash, ct);
                if (!okNew)
                {
                    TempData["Message"] =
                        "Authentication failed with the new password. Changes were not saved.";
                    return RedirectToAction(nameof(NetappHub), new { selectedId = id, tab = "edit" });
                }

                existing.PasswordHash = _encryptionService.Encrypt(PasswordHash);
            }

            existing.Hostname = Hostname;
            existing.IpAddress = IpAddress;
            existing.IsPrimary = IsPrimary;
            existing.Username = Username;

            await _context.SaveChangesAsync(ct);

            TempData["Message"] = $"Controller \"{existing.Hostname}\" saved.";
            return RedirectToAction(nameof(NetappHub), new { selectedId = id });
        }

        [HttpPost, ValidateAntiForgeryToken, ActionName("Delete")]
        public async Task<IActionResult> DeleteConfirmed(int id, CancellationToken ct)
        {
            var controller = await _context.NetappControllers.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (controller == null) return NotFound();

            var snapMirrorRelations = await _context.SnapMirrorRelations
                .Where(r => r.SourceControllerId == id || r.DestinationControllerId == id)
                .ToListAsync(ct);
            if (snapMirrorRelations.Count > 0)
                _context.SnapMirrorRelations.RemoveRange(snapMirrorRelations);

            var selectedVolumes = await _context.SelectedNetappVolumes
                .Where(v => v.NetappControllerId == id)
                .ToListAsync(ct);
            if (selectedVolumes.Count > 0)
                _context.SelectedNetappVolumes.RemoveRange(selectedVolumes);

            _context.NetappControllers.Remove(controller);
            await _context.SaveChangesAsync(ct);

            TempData["Message"] = $"Controller \"{controller.Hostname}\" deleted.";
            return Ok();
        }

        [HttpGet]
        public async Task<IActionResult> GetVolumesTree(int storageId, CancellationToken ct)
        {
            try
            {
                var controller = await _context.NetappControllers
                    .FirstOrDefaultAsync(c => c.Id == storageId, ct);
                if (controller == null) return NotFound("Controller not found");

                var svms = await _netappVolumeService.GetVserversAndVolumesAsync(controller.Id, ct);
                if (svms == null || !svms.Any())
                    return NotFound("No volume data found for this controller");

                var tracked = await _context.SelectedNetappVolumes
                    .Where(v => v.NetappControllerId == storageId)
                    .ToListAsync(ct);

                static bool Matches(SelectedNetappVolumes s, string? uuid, string? volName, string? mountIp) =>
                    string.Equals(s.Uuid, uuid, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(s.VolumeName, volName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(s.MountIp, mountIp, StringComparison.OrdinalIgnoreCase);

                var treeDto = new NetappControllerTreeDto
                {
                    ControllerName = controller.Hostname,
                    Svms = svms.Select(svm => new NetappSvmDto
                    {
                        Name = svm.Name,
                        Volumes = svm.Volumes.Select(vol =>
                        {
                            var row = tracked.FirstOrDefault(s => Matches(s, vol.Uuid, vol.VolumeName, vol.MountIp));
                            var isSelected = row != null && row.Disabled != true;

                            return new NetappVolumeDto
                            {
                                VolumeName = vol.VolumeName,
                                Uuid = vol.Uuid,
                                MountIp = vol.MountIp,
                                ClusterId = vol.ClusterId,
                                IsSelected = isSelected,
                            };
                        }).ToList()
                    }).ToList()
                };

                return PartialView("_VolumesTreePartial", treeDto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetSelectedNetappVolumes(int controllerId, CancellationToken ct)
        {
            var uuids = await _context.SelectedNetappVolumes
                .Where(v => v.NetappControllerId == controllerId && v.Disabled != true)
                .Select(v => v.Uuid)
                .Where(u => u != null)
                .Distinct()
                .ToListAsync(ct);

            return Json(uuids);
        }

        [HttpGet]
        public async Task<IActionResult> GetInUseSelectedVolumeUuids(int controllerId, CancellationToken ct)
        {
            var query =
                from s in _context.BackupSchedules.AsNoTracking()
                where s.SelectedNetappVolumeId != null
                join v in _context.SelectedNetappVolumes.AsNoTracking()
                    on s.SelectedNetappVolumeId equals v.Id
                where v.NetappControllerId == controllerId
                select v.Uuid;

            var uuids = await query
                .Where(u => u != null)
                .Distinct()
                .ToListAsync(ct);

            return Json(uuids);
        }

        [HttpPost]
        public async Task<IActionResult> SaveSelectedStorage(SelectStorageViewModel model, CancellationToken ct)
        {
            var existing = await _context.SelectedStorages
                .Where(s => s.ClusterId == model.ClusterId)
                .ToListAsync(ct);

            var selectedSet = new HashSet<string>(
                model.SelectedStorageIds ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);

            var toRemove = existing.Where(e => !selectedSet.Contains(e.StorageIdentifier)).ToList();
            _context.SelectedStorages.RemoveRange(toRemove);

            var existingSet = existing
                .Select(e => e.StorageIdentifier)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var toAdd = selectedSet
                .Where(id => !existingSet.Contains(id))
                .Select(id => new ProxSelectedStorage
                {
                    ClusterId = model.ClusterId,
                    StorageIdentifier = id
                });

            _context.SelectedStorages.AddRange(toAdd);
            await _context.SaveChangesAsync(ct);

            _ = Task.Run(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(TimeSpan.FromSeconds(60));
                try
                {
                    await _collector.RunProxmoxClusterStatusCheckAsync(linked.Token);
                    await _collector.RunInventoryVmSideAsync(linked.Token);
                    await _collector.RunInventoryInfraSideAsync(CancellationToken.None);
                }
                catch { /* swallow/log */ }
            });

            TempData["Message"] = "Selected storage updated.";
            return RedirectToAction(nameof(ProxmoxHub), new { selectedId = model.ClusterId, tab = "storage" });
        }

        [HttpPost]
        public async Task<IActionResult> SaveNetappSelectedVolumes(
            [FromBody] List<NetappVolumeExportDto> volumes,
            CancellationToken ct)
        {
            var controllerId = volumes.FirstOrDefault()?.ClusterId ?? 0;
            if (controllerId == 0)
                return BadRequest("Missing controller id.");

            var existing = await _context.SelectedNetappVolumes
                .Where(v => v.NetappControllerId == controllerId)
                .ToListAsync(ct);

            static string Key(string? uuid, string? vol, string? ip) =>
                $"{uuid ?? ""}||{vol ?? ""}||{ip ?? ""}".ToLowerInvariant();

            var existingByKey = existing.ToDictionary(
                e => Key(e.Uuid, e.VolumeName, e.MountIp),
                e => e
            );

            var incomingKeys = new HashSet<string>(
                volumes.Select(v => Key(v.Uuid, v.VolumeName, v.MountIp))
            );

            foreach (var v in volumes)
            {
                var k = Key(v.Uuid, v.VolumeName, v.MountIp);
                if (existingByKey.TryGetValue(k, out var row))
                {
                    row.Disabled = false;
                    row.Vserver = v.Vserver;
                    row.VolumeName = v.VolumeName;
                    row.Uuid = v.Uuid;
                    row.MountIp = v.MountIp;
                    row.NetappControllerId = controllerId;
                }
                else
                {
                    _context.SelectedNetappVolumes.Add(new SelectedNetappVolumes
                    {
                        Vserver = v.Vserver,
                        VolumeName = v.VolumeName,
                        Uuid = v.Uuid,
                        MountIp = v.MountIp,
                        NetappControllerId = controllerId,
                        Disabled = false
                    });
                }
            }

            foreach (var e in existing)
            {
                var k = Key(e.Uuid, e.VolumeName, e.MountIp);
                if (!incomingKeys.Contains(k) && e.Disabled != true)
                    e.Disabled = true;
            }

            await _context.SaveChangesAsync(ct);
            await _netappVolumeService.UpdateAllSelectedVolumesAsync(ct);
            _ = Task.Run(async () =>
            {
                try { await _collector.RunInventoryInfraSideAsync(CancellationToken.None); }
                catch (Exception ex) {  }
            });
            TempData["Message"] = "Selected storage updated. Refresh started…";

            return Ok();
        }

        // =====================================================================
        // EMAIL: Save + Test
        // =====================================================================

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveEmailSettings(
            [Bind(Prefix = "Email")] EmailSettingsViewModel vm,
            CancellationToken ct)
        {
            var sec = (vm.SecurityMode ?? "StartTls").Trim();
            var port = vm.SmtpPort > 0
                ? vm.SmtpPort
                : (sec.Equals("SslTls", StringComparison.OrdinalIgnoreCase) ? 465
                   : sec.Equals("StartTls", StringComparison.OrdinalIgnoreCase) ? 587
                   : 25);

            var noAuth = sec.Equals("None", StringComparison.OrdinalIgnoreCase)
                         && port == 25
                         && string.IsNullOrWhiteSpace(vm.Username);

            if (vm.Enabled && string.IsNullOrWhiteSpace(vm.SmtpHost))
                ModelState.AddModelError("Email.SmtpHost", "SMTP Server is required.");

            var authRequired = vm.Enabled && !noAuth;
            var existing = await _context.EmailSettings.AsNoTracking()
                .FirstAsync(e => e.Id == 1, ct);

            if (authRequired && string.IsNullOrWhiteSpace(vm.Username))
                ModelState.AddModelError("Email.Username",
                    "Username is required unless using Security=None on port 25.");

            if (authRequired &&
                string.IsNullOrWhiteSpace(vm.Password) &&
                string.IsNullOrWhiteSpace(existing.ProtectedPassword))
            {
                ModelState.AddModelError("Email.Password",
                    "Password (or an already saved password) is required when authentication is used.");
            }

            if (!ModelState.IsValid)
            {
                TempData["Error"] = "Fix the highlighted errors.";
                return RedirectToAction(nameof(Config));
            }

            var s = await _context.EmailSettings.FirstAsync(e => e.Id == 1, ct);

            s.Enabled = vm.Enabled;
            s.SmtpHost = string.IsNullOrWhiteSpace(vm.SmtpHost) ? null : vm.SmtpHost.Trim();
            s.SmtpPort = port;
            s.SecurityMode = string.IsNullOrWhiteSpace(sec) ? "StartTls" : sec;

            if (noAuth)
            {
                s.Username = null;
                s.ProtectedPassword = null;
            }
            else
            {
                s.Username = string.IsNullOrWhiteSpace(vm.Username) ? null : vm.Username.Trim();
                if (!string.IsNullOrWhiteSpace(vm.Password))
                    s.ProtectedPassword = _protector.Protect(vm.Password);
            }

            s.From = string.IsNullOrWhiteSpace(vm.From) ? null : vm.From.Trim();
            s.DefaultRecipients = string.IsNullOrWhiteSpace(vm.DefaultRecipients)
                ? null
                : vm.DefaultRecipients.Trim();

            s.OnBackupSuccess = vm.OnBackupSuccess;
            s.OnBackupFailure = vm.OnBackupFailure;
            s.OnRestoreSuccess = vm.OnRestoreSuccess;
            s.OnRestoreFailure = vm.OnRestoreFailure;
            s.OnWarnings = vm.OnWarnings;
            s.MinSeverity = vm.MinSeverity;

            await _context.SaveChangesAsync(ct);
            TempData["Success"] = "Email settings saved.";
            return RedirectToAction(nameof(Config));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SendTestEmail(string to, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(to))
                    throw new InvalidOperationException("Please provide a recipient email.");

                var html =
                    $@"<p>This is a test email from <b>BareProx</b> at {DateTime.UtcNow:u} (UTC).</p>
                       <p>If you received this, SMTP settings work.</p>";

                await _email.SendAsync(to.Trim(), "BareProx test email", html, ct);
                TempData["Success"] = $"Test email sent to {to}.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Failed to send test email: {ex.Message}";
            }

            return RedirectToAction(nameof(Config));
        }


        private async Task UpsertClusterStatusAsync(
    int clusterId,
    string? clusterName,
    string lastStatus,
    string? lastStatusMessage,
    DateTime utcNow,
    CancellationToken ct)
        {
            await using var q = await _qdb.CreateAsync(ct);
            var row = await q.InventoryClusterStatuses
                .FirstOrDefaultAsync(x => x.ClusterId == clusterId, ct);

            if (row is null)
            {
                row = new InventoryClusterStatus
                {
                    ClusterId = clusterId,
                    ClusterName = clusterName,
                    HasQuorum = false,
                    OnlineHostCount = 0,
                    TotalHostCount = 0,
                    LastStatus = lastStatus,
                    LastStatusMessage = lastStatusMessage,
                    LastCheckedUtc = utcNow
                };
                q.InventoryClusterStatuses.Add(row);
            }
            else
            {
                row.ClusterName = clusterName ?? row.ClusterName;
                row.LastStatus = lastStatus;
                row.LastStatusMessage = lastStatusMessage;
                row.LastCheckedUtc = utcNow;
            }

            await q.SaveChangesAsync(ct);
        }

        private async Task RemoveClusterStatusAsync(int clusterId, CancellationToken ct)
        {
            await using var q = await _qdb.CreateAsync(ct);
            var row = await q.InventoryClusterStatuses
                .FirstOrDefaultAsync(x => x.ClusterId == clusterId, ct);
            if (row != null)
            {
                q.InventoryClusterStatuses.Remove(row);
                await q.SaveChangesAsync(ct);
            }
        }

    }


}
