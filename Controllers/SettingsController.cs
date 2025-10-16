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
using BareProx.Services.Proxmox.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using System.IO;
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
        private readonly INetappService _netappService;
        private readonly IEncryptionService _encryptionService;
        private readonly string _configFile;
        private readonly SelfSignedCertificateService _certService;
        private readonly IHostApplicationLifetime _appLifetime;
        private readonly INetappVolumeService _netappVolumeService;
        private readonly IProxmoxAuthenticator _proxmoxAuthenticator;


        public SettingsController(
            ApplicationDbContext context,
            IFeatureService features,
            ProxmoxService proxmoxService,
            INetappService netappService,
            IEncryptionService encryptionService,
            SelfSignedCertificateService certService,
            IHostApplicationLifetime appLifetime,
            INetappVolumeService netappVolumeService,
            IProxmoxAuthenticator proxmoxAuthenticator)
        {
            _context = context;
            _features = features;
            _proxmoxService = proxmoxService;
            _netappService = netappService;
            _encryptionService = encryptionService;
            _configFile = Path.Combine("/config", "appsettings.json");
            _certService = certService;
            _appLifetime = appLifetime;
            _netappVolumeService = netappVolumeService;
            _proxmoxAuthenticator = proxmoxAuthenticator;
        }

        // ========================================================
        // GET: /Settings/Config
        // Builds and returns the composite SettingsPageViewModel.
        // ========================================================
        [HttpGet]
        public async Task<IActionResult> Config()
        {
            // 1) Load or create the JSON config file
            JObject cfg;
            if (System.IO.File.Exists(_configFile))
            {
                var text = System.IO.File.ReadAllText(_configFile);
                cfg = JObject.Parse(text);
            }
            else
            {
                cfg = new JObject();
            }

            // 2) Read stored Windows‐style and IANA‐style time zones (if present)
            var storedWindows = (string?)cfg["ConfigSettings"]?["TimeZoneWindows"];
            var storedIana = (string?)cfg["ConfigSettings"]?["TimeZoneIana"];

            // 3) Determine which Windows ID to select in the dropdown
            //    If storedWindows is nonempty, use it.
            //    Else if storedIana is nonempty, convert it → Windows.
            //    Else fallback to Local machine’s zone, converted → Windows if needed.
            string selectedWindowsId;
            if (!string.IsNullOrWhiteSpace(storedWindows))
            {
                selectedWindowsId = storedWindows.Trim();
            }
            else if (!string.IsNullOrWhiteSpace(storedIana))
            {
                try
                {
                    selectedWindowsId = TZConvert.IanaToWindows(storedIana.Trim());
                }
                catch
                {
                    // if conversion fails, fallback
                    selectedWindowsId = GetLocalWindowsId();
                }
            }
            else
            {
                selectedWindowsId = GetLocalWindowsId();
            }

            // 4) Get current certificate details
            var cert = _certService.CurrentCertificate;

            // 5) Build the composite view model
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
                }
            };

            // 6) Build the Time Zone dropdown
            vm.TimeZones = BuildTimeZoneSelectList(selectedWindowsId);

            // 7) Get the experimental feature flag state
            ViewBag.ExperimentalExtra = await _features.IsEnabledAsync("ExperimentalExtra");

            return View("Config", vm);
        }

        // POST from the checkbox form
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
        // POST: /Settings/Config
        // Binds only to ConfigSettingsViewModel (prefix="Config")
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

            // 1) Translate Windows → IANA (if conversion fails, assume the value is already IANA)
            string ianaId;
            try
            {
                ianaId = TZConvert.WindowsToIana(configVm.TimeZoneWindows.Trim());
            }
            catch
            {
                ianaId = configVm.TimeZoneWindows.Trim();
            }

            // 2) Load (or create) the root JObject
            JObject cfg = System.IO.File.Exists(_configFile)
                ? JObject.Parse(System.IO.File.ReadAllText(_configFile))
                : new JObject();

            // 3) Remove any old top‐level keys if they exist
            if (cfg["TimeZoneWindows"] != null)
                cfg.Remove("TimeZoneWindows");
            if (cfg["TimeZoneIana"] != null)
                cfg.Remove("TimeZoneIana");

            // 4) Make sure "ConfigSettings" exists
            if (cfg["ConfigSettings"] == null || cfg["ConfigSettings"].Type != JTokenType.Object)
            {
                cfg["ConfigSettings"] = new JObject();
            }

            // 5) Write under "ConfigSettings" only
            var section = (JObject)cfg["ConfigSettings"];
            section["TimeZoneWindows"] = configVm.TimeZoneWindows.Trim();
            section["TimeZoneIana"] = ianaId;

            // 6) Persist back to /config/appsettings.json
            System.IO.File.WriteAllText(_configFile, cfg.ToString());

            TempData["Success"] = $"Default time zone “{configVm.TimeZoneWindows}” saved.";
            return RedirectToAction(nameof(Config));
        }


        // ========================================================
        // Helper: rebuild the composite page model (for validation errors)
        // ========================================================
        private SettingsPageViewModel BuildSettingsPageViewModel()
        {
            // Re‐run the GET logic to fetch current state
            JObject cfg;
            if (System.IO.File.Exists(_configFile))
            {
                var text = System.IO.File.ReadAllText(_configFile);
                cfg = JObject.Parse(text);
            }
            else
            {
                cfg = new JObject();
            }

            var storedWindows = (string)cfg["DefaultTimeZoneWindows"];
            var storedIana = (string)cfg["DefaultTimeZoneIana"];

            string selectedWindowsId;
            if (!string.IsNullOrWhiteSpace(storedWindows))
            {
                selectedWindowsId = storedWindows.Trim();
            }
            else if (!string.IsNullOrWhiteSpace(storedIana))
            {
                try
                {
                    selectedWindowsId = TZConvert.IanaToWindows(storedIana.Trim());
                }
                catch
                {
                    selectedWindowsId = GetLocalWindowsId();
                }
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
                }
            };

            vm.TimeZones = BuildTimeZoneSelectList(selectedWindowsId);
            return vm;
        }

        // Helper: build a SelectList for all time zones
        private IEnumerable<SelectListItem> BuildTimeZoneSelectList(string selectedWindowsId)
        {
            var allZones = TimeZoneInfo.GetSystemTimeZones();
            var items = allZones.Select(tzInfo =>
            {
                string windowsId, ianaId;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // On Windows: tzInfo.Id is already a Windows ID
                    windowsId = tzInfo.Id;
                    ianaId = TZConvert.WindowsToIana(tzInfo.Id);
                }
                else
                {
                    // On Linux: tzInfo.Id is IANA. Try to map; if it fails, just use the IANA as the "value"
                    ianaId = tzInfo.Id;
                    try
                    {
                        windowsId = TZConvert.IanaToWindows(tzInfo.Id);
                    }
                    catch
                    {
                        windowsId = tzInfo.Id;
                    }
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


        // Helper: get the local machine’s “Windows” ID (or convert from IANA if on Linux)
        private string GetLocalWindowsId()
        {
            var local = TimeZoneInfo.Local.Id; // On Linux, this is IANA; on Windows, this is Windows.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return local;
            }
            else
            {
                try
                {
                    return TZConvert.IanaToWindows(local);
                }
                catch
                {
                    // If conversion fails, just return the IANA as a fallback
                    return local;
                }
            }
        }

        // ========================================================
        // ========================================================
        // ========================================================
        // POST: Settings/RegenerateCert
        // (regenerate self-signed certificate based on user inputs)
        // ========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult RegenerateCert([Bind(Prefix = "Regenerate")] RegenerateCertViewModel regenVm)
        {
            // 1) Validate only the Regenerate submodel

            ModelState.Remove("Regenerate.CurrentSubject");
            ModelState.Remove("Regenerate.CurrentNotBefore");
            ModelState.Remove("Regenerate.CurrentNotAfter");
            ModelState.Remove("Regenerate.CurrentThumbprint");

            if (!ModelState.IsValid)
            {
                // Re‐build the composite and return view with errors
                var pageVm = BuildSettingsPageViewModel();
                pageVm.Regenerate.RegenSubjectName = regenVm.RegenSubjectName;
                pageVm.Regenerate.RegenValidDays = regenVm.RegenValidDays;
                pageVm.Regenerate.RegenSANs = regenVm.RegenSANs;
                return View("Config", pageVm);
            }

            // 2) Parse SANs (comma-separated into string[])
            var sansList = Array.Empty<string>();
            if (!string.IsNullOrWhiteSpace(regenVm.RegenSANs))
            {
                sansList = regenVm.RegenSANs
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToArray();
            }

            // 3) Perform certificate regeneration
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
            // Graceful shutdown – ensure your hosting environment restarts the app
            HttpContext.Response.Headers.Add("Refresh", "3"); // optional: auto-refresh after 3 sec
            _appLifetime.StopApplication();
            return Content("Application is restarting...");
        }

        // POST: Settings/AuthenticateCluster
        [HttpPost]
        public async Task<IActionResult> AuthenticateCluster(int id, CancellationToken ct)
        {
            var success = await _proxmoxAuthenticator.AuthenticateAndStoreTokenCidAsync(id, ct);
            TempData["Message"] = success ? "Authentication successful." : "Authentication failed.";
            return RedirectToAction(nameof(ProxmoxHub), new { selectedId = id }); // CHANGED
        }

        [HttpGet]
        public async Task<IActionResult> NetappHub(int? selectedId = null, CancellationToken ct = default)
        {
            var list = await _context.NetappControllers.ToListAsync(ct);
            NetappController? selected = null;

            if (selectedId.HasValue)
                selected = list.FirstOrDefault(x => x.Id == selectedId.Value);

            var vm = new NetappHubViewModel
            {
                Controllers = list,
                Selected = selected,
                SelectedId = selectedId,
                Message = TempData["Message"] as string
            };

            return View(vm); // Views/Settings/NetappHub.cshtml
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
            return RedirectToAction(nameof(ProxmoxHub), new { selectedId = cluster.Id }); // CHANGED
        }

        [HttpPost]
        public async Task<IActionResult> DeleteCluster(int id, CancellationToken ct)
        {
            using var tx = await _context.Database.BeginTransactionAsync(ct);

            var cluster = await _context.ProxmoxClusters
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == id, ct);

            if (cluster == null)
                return RedirectToAction(nameof(ProxmoxHub));

            // 1) Delete hosts/nodes
            var hosts = await _context.ProxmoxHosts
                .Where(h => h.ClusterId == id)
                .ToListAsync(ct);
            _context.ProxmoxHosts.RemoveRange(hosts);

            // 2) Delete selected storage rows
            var selectedStorages = await _context.SelectedStorages
                .Where(s => s.ClusterId == id)
                .ToListAsync(ct);
            _context.SelectedStorages.RemoveRange(selectedStorages);

            // 4) Finally delete the cluster
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

            // Re-fill fields so the form shows user input back
            existing.Username = cluster.Username;
            // do NOT set password here unless provided

            if (!ModelState.IsValid)
            {
                return View("EditCluster", existing); // render the edit view directly
            }

            if (!string.IsNullOrWhiteSpace(cluster.PasswordHash))
                existing.PasswordHash = _encryptionService.Encrypt(cluster.PasswordHash);

            await _context.SaveChangesAsync(ct);
            TempData["Message"] = $"Cluster \"{existing.Name}\" saved.";
            return RedirectToAction(nameof(ProxmoxHub), new { selectedId = existing.Id });
        }

        [HttpPost]
        public async Task<IActionResult> AddHost(int clusterId, string hostAddress, string hostname, CancellationToken ct)
        {
            var cluster = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
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
            return RedirectToAction(nameof(ProxmoxHub), new { selectedId = clusterId }); // CHANGED
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
            return RedirectToAction(nameof(ProxmoxHub), new { selectedId = clusterId }); // CHANGED
        }

        // POST: Settings/NetappControllers/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(string Hostname, string IpAddress, bool IsPrimary, string Username, string PasswordHash, CancellationToken ct)
        {
            var controller = new NetappController
            {
                Hostname = Hostname,
                IpAddress = IpAddress,
                IsPrimary = IsPrimary,
                Username = Username,
                PasswordHash = _encryptionService.Encrypt(PasswordHash)
            };
            if (ModelState.IsValid)
            {
                _context.Add(controller);
                await _context.SaveChangesAsync(ct);
                TempData["Message"] = $"Controller \"{controller.Hostname}\" added.";
                return RedirectToAction(nameof(NetappHub), new { selectedId = controller.Id }); // ← to hub
            }
            TempData["Message"] = "Validation failed.";
            return RedirectToAction(nameof(NetappHub));
        }

        // POST: Settings/NetappControllers/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, string Hostname, string IpAddress, bool IsPrimary, string Username, string PasswordHash, CancellationToken ct)
        {
            var existing = await _context.NetappControllers.FindAsync(id, ct);
            if (existing == null) return RedirectToAction(nameof(NetappHub));

            if (ModelState.IsValid)
            {
                existing.Hostname = Hostname;
                existing.IpAddress = IpAddress;
                existing.IsPrimary = IsPrimary;
                existing.Username = Username;
                if (!string.IsNullOrWhiteSpace(PasswordHash))
                    existing.PasswordHash = _encryptionService.Encrypt(PasswordHash);

                await _context.SaveChangesAsync(ct);
                TempData["Message"] = $"Controller \"{existing.Hostname}\" saved.";
                return RedirectToAction(nameof(NetappHub), new { selectedId = id }); // ← back to hub
            }
            TempData["Message"] = "Validation failed.";
            return RedirectToAction(nameof(NetappHub), new { selectedId = id });
        }

        // GET: Settings/Delete/5
        [HttpPost, ValidateAntiForgeryToken, ActionName("Delete")]
        public async Task<IActionResult> DeleteConfirmed(int id, CancellationToken ct)
        {
            var controller = await _context.NetappControllers
                .FirstOrDefaultAsync(c => c.Id == id, ct);

            if (controller == null)
                return NotFound();

            // Remove relations referencing this controller (if your model has this DbSet)
            var snapMirrorRelations = await _context.SnapMirrorRelations
                .Where(r => r.SourceControllerId == id || r.DestinationControllerId == id)
                .ToListAsync(ct);
            if (snapMirrorRelations.Count > 0)
                _context.SnapMirrorRelations.RemoveRange(snapMirrorRelations);

            // Remove selected volumes tied to this controller
            var selectedVolumes = await _context.SelectedNetappVolumes
                .Where(v => v.NetappControllerId == id)
                .ToListAsync(ct);
            if (selectedVolumes.Count > 0)
                _context.SelectedNetappVolumes.RemoveRange(selectedVolumes);

            // Finally remove the controller itself
            _context.NetappControllers.Remove(controller);

            await _context.SaveChangesAsync(ct);

            TempData["Message"] = $"Controller \"{controller.Hostname}\" deleted.";
            return Ok(); // let JS redirect the page
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
            // Reuse your existing persistence logic as-is...
            var existing = await _context.SelectedStorages
                .Where(s => s.ClusterId == model.ClusterId)
                .ToListAsync(ct);

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

            return RedirectToAction(nameof(ProxmoxHub), new { selectedId = model.ClusterId }); // CHANGED
        }

        [HttpPost]
        public async Task<IActionResult> SaveNetappSelectedVolumes([FromBody] List<NetappVolumeExportDto> volumes, CancellationToken ct)
        {
            // your existing save logic…
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

            await _netappService.UpdateAllSelectedVolumesAsync(ct);

            return Ok();
        }
        private async Task<SelectStorageViewModel> BuildStorageViewAsync(int clusterId, CancellationToken ct)
        {
            // Reuse logic from ClusterStorage(...)
            var cluster = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync(c => c.Id == clusterId, ct);

            if (cluster == null)
            {
                return new SelectStorageViewModel { ClusterId = clusterId, StorageList = new List<ProxmoxStorageDto>() };
            }

            var allNfsStorage = await _proxmoxService.GetNfsStorageAsync(cluster, ct);

            var selectedIds = await _context.Set<ProxSelectedStorage>()
                .Where(s => s.ClusterId == clusterId)
                .Select(s => s.StorageIdentifier)
                .ToHashSetAsync(StringComparer.OrdinalIgnoreCase, ct);

            var storageList = allNfsStorage
                .Select(s => new ProxmoxStorageDto
                {
                    Id = s.Storage,              // keep your existing mapping
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
                {
                    storageView = await BuildStorageViewAsync(selected.Id, ct); // <-- reuse
                }
            }

            var vm = new ProxmoxHubViewModel
            {
                Clusters = clusters,
                SelectedCluster = selected,
                StorageView = storageView,
                SelectedId = selectedId,
                Message = TempData["Message"] as string
            };

            return View(vm); // Views/Settings/ProxmoxHub.cshtml
        }


    }
}
