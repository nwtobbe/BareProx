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

using BareProx.Data;
using BareProx.Models;
using BareProx.Services;
using BareProx.Services.Features;
using BareProx.Services.Netapp;
using BareProx.Services.Proxmox.Authentication;
using BareProx.Services.Notifications;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting; // IHostApplicationLifetime
using Newtonsoft.Json.Linq;
using System.Runtime.InteropServices;
using TimeZoneConverter;

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
        private readonly SelfSignedCertificateService _certService;
        private readonly IHostApplicationLifetime _appLifetime;
        private readonly INetappVolumeService _netappVolumeService;
        private readonly IProxmoxAuthenticator _proxmoxAuthenticator;
        private readonly INetappAuthService _netappAuthService;
        private readonly IEmailSender _email;
        private readonly IDataProtector _protector;

        public SettingsController(
            ApplicationDbContext context,
            IFeatureService features,
            ProxmoxService proxmoxService,
            IEncryptionService encryptionService,
            SelfSignedCertificateService certService,
            IHostApplicationLifetime appLifetime,
            INetappVolumeService netappVolumeService,
            IProxmoxAuthenticator proxmoxAuthenticator,
            INetappAuthService netappAuthService,
            IEmailSender email,
            IDataProtectionProvider dataProtectionProvider)
        {
            _context = context;
            _features = features;
            _proxmoxService = proxmoxService;
            _encryptionService = encryptionService;
            _configFile = Path.Combine("/config", "appsettings.json");
            _certService = certService;
            _appLifetime = appLifetime;
            _netappVolumeService = netappVolumeService;
            _proxmoxAuthenticator = proxmoxAuthenticator;
            _netappAuthService = netappAuthService;
            _email = email;
            _protector = dataProtectionProvider.CreateProtector("BareProx.EmailSettings.Password.v1");
        }

        // ========================================================
        // GET: /Settings/Config  (single GET — builds full view model)
        // ========================================================
        [HttpGet]
        public async Task<IActionResult> Config(CancellationToken ct)
        {
            // Load /config/appsettings.json (for time zone + updates)
            JObject cfg = System.IO.File.Exists(_configFile)
                ? JObject.Parse(System.IO.File.ReadAllText(_configFile))
                : new JObject();

            // Read stored TZs
            var storedWindows = (string?)cfg["ConfigSettings"]?["TimeZoneWindows"];
            var storedIana = (string?)cfg["ConfigSettings"]?["TimeZoneIana"];

            // Decide selected Windows TZ
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

            // Current certificate
            var cert = _certService.CurrentCertificate;

            // Base VM
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
                    RegenSubjectName = cert?.Subject ?? "CN=localhost",
                    RegenValidDays = 365,
                    RegenSANs = "localhost"
                },
                // NEW: Updates section from appsettings.json
                Updates = new UpdateSettingsViewModel
                {
                    Enabled = (bool?)cfg["Updates"]?["Enabled"] ?? false,
                    FrequencyMinutes = (int?)cfg["Updates"]?["FrequencyMinutes"] ?? 360
                }
            };

            // Email settings (ensure seeded row exists; migrations should handle this)
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
            // Password intentionally left blank

            // Time zone dropdown + feature flag
            vm.TimeZones = BuildTimeZoneSelectList(selectedWindowsId);
            ViewBag.ExperimentalExtra = await _features.IsEnabledAsync(FF_Experimental);

            return View("Config", vm);
        }

        // POST from the Experimental checkbox form
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleExperimental()
        {
            var enabled = Request.Form.ContainsKey("ExperimentalExtra");
            await _features.SetAsync(FF_Experimental, enabled);
            TempData["Success"] = "Experimental features setting updated.";
            return RedirectToAction(nameof(Config));
        }

        // ========================================================
        // POST: /Settings/Config (Time zone only)
        // ========================================================
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

            // Windows → IANA
            string ianaId;
            try { ianaId = TZConvert.WindowsToIana(configVm.TimeZoneWindows.Trim()); }
            catch { ianaId = configVm.TimeZoneWindows.Trim(); }

            // Load root JSON
            JObject cfg = System.IO.File.Exists(_configFile)
                ? JObject.Parse(System.IO.File.ReadAllText(_configFile))
                : new JObject();

            // Ensure "ConfigSettings" object
            if (cfg["ConfigSettings"] == null || cfg["ConfigSettings"]!.Type != JTokenType.Object)
                cfg["ConfigSettings"] = new JObject();

            // Persist only under ConfigSettings
            var section = (JObject)cfg["ConfigSettings"]!;
            section["TimeZoneWindows"] = configVm.TimeZoneWindows.Trim();
            section["TimeZoneIana"] = ianaId;

            System.IO.File.WriteAllText(_configFile, cfg.ToString());
            TempData["Success"] = $"Default time zone “{configVm.TimeZoneWindows}” saved.";
            return RedirectToAction(nameof(Config));
        }

        // ========================================================
        // NEW: POST — Save Update Checker settings
        // ========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult SaveUpdateSettings([Bind(Prefix = "Updates")] UpdateSettingsViewModel vm)
        {
            // Accept only a few safe frequencies
            var allowed = new HashSet<int> { 1440, 2880, 10080 };
            if (!allowed.Contains(vm.FrequencyMinutes))
            {
                // fallback to 360 if something odd was posted
                vm.FrequencyMinutes = 10080;
            }

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

        // Helper: rebuild page VM (now also fills Email + Updates)
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
                    RegenSubjectName = cert?.Subject ?? "CN=localhost",
                    RegenValidDays = 365,
                    RegenSANs = "localhost"
                },
                // NEW: Updates section
                Updates = new UpdateSettingsViewModel
                {
                    Enabled = (bool?)cfg["Updates"]?["Enabled"] ?? false,
                    FrequencyMinutes = (int?)cfg["Updates"]?["FrequencyMinutes"] ?? 360
                }
            };

            // Also populate Email from DB (best effort; avoid throwing here)
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
            catch { /* keep page rendering even if DB lookup fails here */ }

            vm.TimeZones = BuildTimeZoneSelectList(selectedWindowsId);
            return vm;
        }

        // Build the time zone dropdown
        private IEnumerable<SelectListItem> BuildTimeZoneSelectList(string selectedWindowsId)
        {
            var allZones = TimeZoneInfo.GetSystemTimeZones();
            var items = allZones.Select(tzInfo =>
            {
                string windowsId, ianaId;

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

        // Get the local machine’s “Windows” TZ id (convert from IANA if on Linux)
        private string GetLocalWindowsId()
        {
            var local = TimeZoneInfo.Local.Id; // On Linux it's IANA; on Windows it's Windows.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return local;

            try { return TZConvert.IanaToWindows(local); }
            catch { return local; }
        }

        // ========================================================
        // POST: Settings/RegenerateCert
        // ========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult RegenerateCert([Bind(Prefix = "Regenerate")] RegenerateCertViewModel regenVm)
        {
            // Only validate the Regenerate submodel
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
            return RedirectToAction(nameof(Config));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult RestartApp()
        {
            HttpContext.Response.Headers.Add("Refresh", "3");
            _appLifetime.StopApplication();
            return Content("Application is restarting...");
        }

        // ===================== Proxmox / NetApp admin bits (unchanged) =====================

        [HttpPost]
        public async Task<IActionResult> AuthenticateCluster(int id, CancellationToken ct)
        {
            var success = await _proxmoxAuthenticator.AuthenticateAndStoreTokenCidAsync(id, ct);
            TempData["Message"] = success ? "Authentication successful." : "Authentication failed.";
            return RedirectToAction(nameof(ProxmoxHub), new { selectedId = id });
        }

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
        public async Task<IActionResult> AddCluster(string name, string username, string password, CancellationToken ct)
        {
            var cluster = new ProxmoxCluster
            {
                Name = name,
                Username = username,
                PasswordHash = _encryptionService.Encrypt(password)
            };

            _context.ProxmoxClusters.Add(cluster);
            await _context.SaveChangesAsync(ct);

            TempData["Message"] = $"Cluster \"{cluster.Name}\" added.";
            return RedirectToAction(nameof(ProxmoxHub), new { selectedId = cluster.Id });
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

            TempData["Message"] = $"Cluster \"{cluster.Name}\" and related data were deleted.";
            return RedirectToAction(nameof(ProxmoxHub));
        }

        [HttpPost]
        public async Task<IActionResult> EditCluster(ProxmoxCluster cluster, CancellationToken ct)
        {
            var existing = await _context.ProxmoxClusters.FindAsync(cluster.Id, ct);
            if (existing == null) return NotFound();

            existing.Username = cluster.Username;

            if (!ModelState.IsValid)
                return View("EditCluster", existing);

            if (!string.IsNullOrWhiteSpace(cluster.PasswordHash))
                existing.PasswordHash = _encryptionService.Encrypt(cluster.PasswordHash);

            await _context.SaveChangesAsync(ct);
            TempData["Message"] = $"Cluster \"{existing.Name}\" saved.";
            return RedirectToAction(nameof(ProxmoxHub), new { selectedId = existing.Id });
        }

        [HttpPost]
        public async Task<IActionResult> AddHost(int clusterId, string hostAddress, string hostname, CancellationToken ct)
        {
            var cluster = await _context.ProxmoxClusters.Include(c => c.Hosts).FirstOrDefaultAsync(c => c.Id == clusterId, ct);
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
            return RedirectToAction(nameof(ProxmoxHub), new { selectedId = clusterId });
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
        public async Task<IActionResult> Edit(int id, string Hostname, string IpAddress, bool IsPrimary, string Username, string PasswordHash, CancellationToken ct)
        {
            var existing = await _context.NetappControllers.FindAsync(id, ct);
            if (existing == null) return RedirectToAction(nameof(NetappHub));

            if (!string.IsNullOrWhiteSpace(PasswordHash))
            {
                var okNew = await _netappAuthService.TryAuthenticateAsync(IpAddress, Username, PasswordHash, ct);
                if (!okNew)
                {
                    TempData["Message"] = "Authentication failed with the new password. Changes were not saved.";
                    return RedirectToAction(nameof(NetappHub), new { selectedId = id, tab = "edit" });
                }

                existing.PasswordHash = _encryptionService.Encrypt(PasswordHash);
            }

            existing.Hostname = Hostname;
            existing.IpAddress = IpAddress;
            existing.IsPrimary = IsPrimary;
            existing.Username = Username;
            if (!string.IsNullOrWhiteSpace(PasswordHash))
                existing.PasswordHash = _encryptionService.Encrypt(PasswordHash);

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
                var controller = await _context.NetappControllers.FirstOrDefaultAsync(c => c.Id == storageId, ct);
                if (controller == null) return NotFound("Controller not found");

                var svms = await _netappVolumeService.GetVserversAndVolumesAsync(controller.Id, ct);
                if (svms == null || !svms.Any())
                    return NotFound("No volume data found for this controller");

                var selected = await _context.SelectedNetappVolumes
                    .Where(v => v.NetappControllerId == storageId)
                    .ToListAsync(ct);

                var treeDto = new NetappControllerTreeDto
                {
                    ControllerName = controller.Hostname,
                    Svms = svms.Select(svm => new NetappSvmDto
                    {
                        Name = svm.Name,
                        Volumes = svm.Volumes.Select(vol => new NetappVolumeDto
                        {
                            VolumeName = vol.VolumeName,
                            Uuid = vol.Uuid,
                            MountIp = vol.MountIp,
                            ClusterId = vol.ClusterId,
                            IsSelected = selected.Any(s =>
                                s.Uuid == vol.Uuid &&
                                s.VolumeName == vol.VolumeName &&
                                s.MountIp == vol.MountIp)
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

        [HttpPost]
        public async Task<IActionResult> SaveSelectedStorage(SelectStorageViewModel model, CancellationToken ct)
        {
            var existing = await _context.SelectedStorages.Where(s => s.ClusterId == model.ClusterId).ToListAsync(ct);
            var selectedSet = new HashSet<string>(model.SelectedStorageIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            var toRemove = existing.Where(e => !selectedSet.Contains(e.StorageIdentifier)).ToList();
            _context.SelectedStorages.RemoveRange(toRemove);

            var existingSet = existing.Select(e => e.StorageIdentifier).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var toAdd = selectedSet
                .Where(id => !existingSet.Contains(id))
                .Select(id => new ProxSelectedStorage { ClusterId = model.ClusterId, StorageIdentifier = id });

            _context.SelectedStorages.AddRange(toAdd);
            await _context.SaveChangesAsync(ct);
            TempData["Message"] = "Selected storage updated.";

            return RedirectToAction(nameof(ProxmoxHub), new { selectedId = model.ClusterId });
        }

        [HttpPost]
        public async Task<IActionResult> SaveNetappSelectedVolumes([FromBody] List<NetappVolumeExportDto> volumes, CancellationToken ct)
        {
            var controllerId = volumes.FirstOrDefault()?.ClusterId ?? 0;

            var old = await _context.SelectedNetappVolumes
                .Where(v => v.NetappControllerId == controllerId)
                .ToListAsync(ct);
            _context.SelectedNetappVolumes.RemoveRange(old);

            var entities = volumes.Select(v => new SelectedNetappVolume
            {
                Vserver = v.Vserver,
                VolumeName = v.VolumeName,
                Uuid = v.Uuid,
                MountIp = v.MountIp,
                ClusterId = v.ClusterId,
                NetappControllerId = controllerId
            });

            _context.SelectedNetappVolumes.AddRange(entities);
            await _context.SaveChangesAsync(ct);

            await _netappVolumeService.UpdateAllSelectedVolumesAsync(ct);
            return Ok();
        }

        [HttpGet]
        public async Task<IActionResult> ProxmoxHub(int? selectedId = null, CancellationToken ct = default)
        {
            var clusters = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .ToListAsync(ct);

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
                Message = TempData["Message"] as string
            };

            return View(vm);
        }

        private async Task<SelectStorageViewModel> BuildStorageViewAsync(int clusterId, CancellationToken ct)
        {
            var cluster = await _context.ProxmoxClusters.Include(c => c.Hosts).FirstOrDefaultAsync(c => c.Id == clusterId, ct);
            if (cluster == null)
                return new SelectStorageViewModel { ClusterId = clusterId, StorageList = new List<ProxmoxStorageDto>() };

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

        // ===================== Email settings: Save + Test =====================

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveEmailSettings([Bind(Prefix = "Email")] EmailSettingsViewModel vm, CancellationToken ct)
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
            var existing = await _context.EmailSettings.AsNoTracking().FirstAsync(e => e.Id == 1, ct);

            if (authRequired && string.IsNullOrWhiteSpace(vm.Username))
                ModelState.AddModelError("Email.Username", "Username is required unless using Security=None on port 25.");

            if (authRequired && string.IsNullOrWhiteSpace(vm.Password) && string.IsNullOrWhiteSpace(existing.ProtectedPassword))
                ModelState.AddModelError("Email.Password", "Password (or an already saved password) is required when authentication is used.");

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
            s.DefaultRecipients = string.IsNullOrWhiteSpace(vm.DefaultRecipients) ? null : vm.DefaultRecipients.Trim();

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

                var html = $@"<p>This is a test email from <b>BareProx</b> at {DateTime.UtcNow:u} (UTC).</p>
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
    }
}
