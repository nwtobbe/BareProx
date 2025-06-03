using BareProx.Services;
using BareProx.Data;
using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;
using BareProx.Models;
using Microsoft.EntityFrameworkCore;
using TimeZoneConverter;

using DbConfigModel = BareProx.Models.DatabaseConfigModels;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.Runtime.InteropServices;


namespace BareProx.Controllers
{
    public class SettingsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ProxmoxService _proxmoxService;
        private readonly INetappService _netappService;
        private readonly IEncryptionService _encryptionService;
        private readonly IWebHostEnvironment _env;
        private readonly string _configFile;
        private readonly SelfSignedCertificateService _certService;
        private readonly IHostApplicationLifetime _appLifetime;


        public SettingsController(
            ApplicationDbContext context,
            ProxmoxService proxmoxService,
            INetappService netappService,
            IEncryptionService encryptionService,
            IWebHostEnvironment env,
            SelfSignedCertificateService certService,
    IHostApplicationLifetime appLifetime)
        {
            _context = context;
            _proxmoxService = proxmoxService;
            _netappService = netappService;
            _encryptionService = encryptionService;
            _env = env;
            _configFile = Path.Combine("/config", "appsettings.json");
            _certService = certService;
            _appLifetime = appLifetime;

        }

        // helper properties to resolve only when needed:
        private ApplicationDbContext DbContext =>
            HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
        private ProxmoxService ProxmoxService =>
            HttpContext.RequestServices.GetRequiredService<ProxmoxService>();
        private INetappService NetappService =>
            HttpContext.RequestServices.GetRequiredService<INetappService>();



        // ========================================================
        // GET: /Settings/Config
        // Builds and returns the composite SettingsPageViewModel.
        // ========================================================
        [HttpGet]
        public IActionResult Config()
        {
            // 1) Load or create the JSON config file (for DefaultTimeZone)
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
            // 1) Read stored time zone (string), or null if not present
            var storedTzId = (string)cfg["DefaultTimeZone"];

            TimeZoneInfo tz = null;

            if (!string.IsNullOrWhiteSpace(storedTzId))
            {
                // Try to interpret exactly as‐is
                try
                {
                    tz = TimeZoneInfo.FindSystemTimeZoneById(storedTzId);
                }
                catch (TimeZoneNotFoundException)
                {
                    // If that fails, try to convert Windows->IANA or IANA->Windows and retry
                    string altId = null;

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        // We’re on Windows: maybe the stored ID is IANA, so convert to Windows
                        try
                        {
                            altId = TZConvert.IanaToWindows(storedTzId);
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                    else
                    {
                        // We’re on Linux/macOS: maybe the stored ID is Windows, so convert to IANA
                        try
                        {
                            altId = TZConvert.WindowsToIana(storedTzId);
                        }
                        catch
                        {
                            // ignore
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(altId))
                    {
                        try
                        {
                            tz = TimeZoneInfo.FindSystemTimeZoneById(altId);
                        }
                        catch
                        {
                            // still failed—leave tz=null
                        }
                    }
                }
            }

            if (tz == null)
            {
                // Either nothing was stored, or we couldn’t parse/convert it
                tz = TimeZoneInfo.Local;
            }

            var currentTz = tz.Id;

            // 3) Get current certificate details
            var cert = _certService.CurrentCertificate;

            // 4) Build the composite view model
            var vm = new SettingsPageViewModel
            {
                Config = new ConfigSettingsViewModel
                {
                    TimeZoneId = currentTz
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

            // 5) Build the Time Zone dropdown
            var zones = TimeZoneInfo.GetSystemTimeZones()
                         .Select(z => new { z.Id, z.DisplayName });
            vm.TimeZones = new SelectList(zones, "Id", "DisplayName", vm.Config.TimeZoneId);

            return View("Config", vm);
        } 

        // ========================================================
        // POST: /Settings/Config
        // Binds only to ConfigSettingsViewModel (prefix="Config")
        // ========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Config([Bind(Prefix = "Config")] ConfigSettingsViewModel configVm)
        {
            // 1) Validate only the Config submodel
            if (!ModelState.IsValid)
            {
                // Re‐build the composite and return view with errors
                var pageVm = BuildSettingsPageViewModel();
                pageVm.Config = configVm;
                var zones = TimeZoneInfo.GetSystemTimeZones()
                             .Select(z => new { z.Id, z.DisplayName });
                pageVm.TimeZones = new SelectList(zones, "Id", "DisplayName", configVm.TimeZoneId);
                return View("Config", pageVm);
            }

            // 2) Save the chosen time zone into /config/appsettings.json
            JObject cfg = System.IO.File.Exists(_configFile)
                ? JObject.Parse(System.IO.File.ReadAllText(_configFile))
                : new JObject();

            cfg["DefaultTimeZone"] = configVm.TimeZoneId;
            System.IO.File.WriteAllText(_configFile, cfg.ToString());

            TempData["Success"] = $"Default time zone \"{configVm.TimeZoneId}\" updated.";

            return RedirectToAction(nameof(Config));
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

            var currentTz = (string)cfg["DefaultTimeZone"]
                            ?? TimeZoneInfo.Local.Id;

            var cert = _certService.CurrentCertificate;

            var vm = new SettingsPageViewModel
            {
                Config = new ConfigSettingsViewModel
                {
                    TimeZoneId = currentTz
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

            var zones = TimeZoneInfo.GetSystemTimeZones()
                         .Select(z => new { z.Id, z.DisplayName });
            vm.TimeZones = new SelectList(zones, "Id", "DisplayName", vm.Config.TimeZoneId);

            return vm;
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

        // GET: Settings/Proxmox
        public async Task<IActionResult> Proxmox()
        {
            var clusters = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .ToListAsync();

            return View(clusters);
        }

        // POST: Settings/AuthenticateCluster
        [HttpPost]
        public async Task<IActionResult> AuthenticateCluster(int id)
        {
            var success = await _proxmoxService.AuthenticateAndStoreTokenAsync(id);
            TempData["Message"] = success ? "Authentication successful." : "Authentication failed.";
            return RedirectToAction("Proxmox");
        }

        [HttpPost]
        public async Task<IActionResult> AddCluster(string name, string username, string password)
        {
            var cluster = new ProxmoxCluster
            {
                Name = name,
                Username = username,
                PasswordHash = _encryptionService.Encrypt(password)
            };

            _context.ProxmoxClusters.Add(cluster);
            await _context.SaveChangesAsync();
            return RedirectToAction("Proxmox");
        }

        [HttpPost]
        public async Task<IActionResult> DeleteCluster(int id)
        {
            var cluster = await _context.ProxmoxClusters.FindAsync(id);
            if (cluster != null)
            {
                _context.ProxmoxClusters.Remove(cluster);
                await _context.SaveChangesAsync();
            }

            return RedirectToAction("Proxmox");
        }
        [HttpGet]
        public async Task<IActionResult> ClusterStorage(int clusterId)
        {
            var cluster = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync(c => c.Id == clusterId);

            if (cluster == null)
            {
                ViewBag.Warning = "Proxmox cluster not found.";
                return View(new SelectStorageViewModel { ClusterId = clusterId });
            }

            var allNfsStorage = await _proxmoxService.GetNfsStorageAsync(cluster);

            var selectedIds = await _context.Set<ProxSelectedStorage>()
                .Where(s => s.ClusterId == clusterId)
                .Select(s => s.StorageIdentifier)
                .ToHashSetAsync(StringComparer.OrdinalIgnoreCase);

            var storageList = allNfsStorage
                .Select(s => new ProxmoxStorageDto
                {
                    Id = s.Storage, // use storage name as ID
                    Storage = s.Storage,
                    Type = s.Type,
                    Path = s.Path,
                    Node = s.Node,
                    IsSelected = selectedIds.Contains(s.Storage)
                }).ToList();

            var model = new SelectStorageViewModel
            {
                ClusterId = clusterId,
                StorageList = storageList
            };

            return View(model);
        }



        [HttpGet]
        public async Task<IActionResult> EditCluster(int id)
        {
            var cluster = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (cluster == null) return NotFound();

            return View(cluster);
        }

        [HttpPost]
        public async Task<IActionResult> EditCluster(ProxmoxCluster cluster)
        {
            if (ModelState.IsValid)
            {
                var existingCluster = await _context.ProxmoxClusters.FindAsync(cluster.Id);
                if (existingCluster == null) return NotFound();

                existingCluster.Username = cluster.Username;
                existingCluster.PasswordHash = _encryptionService.Encrypt(cluster.PasswordHash);

                await _context.SaveChangesAsync();

                return RedirectToAction("Proxmox");
            }
            return View(cluster);
        }

        [HttpPost]
        public async Task<IActionResult> AddHost(int clusterId, string hostAddress, string hostname)
        {
            var cluster = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync(c => c.Id == clusterId);

            if (cluster == null)
                return NotFound();

            var newHost = new ProxmoxHost
            {
                HostAddress = hostAddress,
                Hostname = hostname,
                ClusterId = clusterId
            };

            _context.ProxmoxHosts.Add(newHost);
            await _context.SaveChangesAsync();

            return RedirectToAction("EditCluster", new { id = clusterId });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteHost(int id)
        {
            var host = await _context.ProxmoxHosts.FindAsync(id);
            if (host == null)
                return NotFound();

            var clusterId = host.ClusterId;

            _context.ProxmoxHosts.Remove(host);
            await _context.SaveChangesAsync();

            return RedirectToAction("EditCluster", new { id = clusterId });
        }


        // GET: Settings/NetappControllers
        public async Task<IActionResult> Index()
        {
            var controllers = await _context.NetappControllers.ToListAsync();
            return View("~/Views/Settings/IndexNC.cshtml", controllers);
        }

        // GET: Settings/NetappControllers/Create
        public IActionResult Create()
        {
            return View("~/Views/Settings/CreateNC.cshtml");
        }

        // POST: Settings/NetappControllers/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(string Hostname, string IpAddress, bool IsPrimary, string Username, string PasswordHash)
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
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }

            return View("~/Views/Settings/CreateNC.cshtml", controller);
        }

        // GET: Settings/NetappControllers/Edit/5
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound();

            var controller = await _context.NetappControllers.FindAsync(id);
            if (controller == null) return NotFound();

            controller.PasswordHash = string.Empty; // clear for editing
            return View("~/Views/Settings/EditNC.cshtml", controller);
        }

        // POST: Settings/NetappControllers/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, string Hostname, string IpAddress, bool IsPrimary, string Username, string PasswordHash)
        {
            var existingController = await _context.NetappControllers.FindAsync(id);
            if (existingController == null) return NotFound();

            if (ModelState.IsValid)
            {
                try
                {
                    existingController.Hostname = Hostname;
                    existingController.IpAddress = IpAddress;
                    existingController.IsPrimary = IsPrimary;
                    existingController.Username = Username;
                    if (!string.IsNullOrWhiteSpace(PasswordHash))
                    {
                        existingController.PasswordHash = _encryptionService.Encrypt(PasswordHash);
                    }

                    await _context.SaveChangesAsync();
                    return RedirectToAction(nameof(Index));
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!NetappControllerExists(id)) return NotFound();
                    else throw;
                }
            }

            return View("~/Views/Settings/EditNC.cshtml", existingController);
        }

        // GET: Settings/Delete/5
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null) return NotFound();

            var controller = await _context.NetappControllers.FirstOrDefaultAsync(c => c.Id == id);
            if (controller == null) return NotFound();

            return View("~/Views/Settings/DeleteNC.cshtml", controller);
        }

        // POST: Settings/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var controller = await _context.NetappControllers.FindAsync(id);
            if (controller != null)
            {
                _context.NetappControllers.Remove(controller);
                await _context.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }

        private bool NetappControllerExists(int id)
        {
            return _context.NetappControllers.Any(e => e.Id == id);
        }
        [HttpGet]
        public async Task<IActionResult> GetVolumesTree(int storageId)
        {
            try
            {
                var controller = await _context.NetappControllers.FirstOrDefaultAsync(c => c.Id == storageId);
                if (controller == null) return NotFound("Controller not found");

                var svms = await _netappService.GetVserversAndVolumesAsync(controller.Id);
                if (svms == null || !svms.Any())
                    return NotFound("No volume data found for this controller");

                var selected = await _context.SelectedNetappVolumes
                    .Where(v => v.NetappControllerId == storageId)
                    .ToListAsync();

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
        public async Task<IActionResult> SaveSelectedStorage(SelectStorageViewModel model)
        {
            var existing = await _context.SelectedStorages
                .Where(s => s.ClusterId == model.ClusterId)
                .ToListAsync();

            var selectedSet = new HashSet<string>(model.SelectedStorageIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            // 1. Delete deselected
            var toRemove = existing
                .Where(e => !selectedSet.Contains(e.StorageIdentifier))
                .ToList();

            _context.SelectedStorages.RemoveRange(toRemove);

            // 2. Add new ones not in DB
            var existingSet = existing.Select(e => e.StorageIdentifier).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var toAdd = selectedSet
                .Where(id => !existingSet.Contains(id))
                .Select(id => new ProxSelectedStorage
                {
                    ClusterId = model.ClusterId,
                    StorageIdentifier = id
                });

            _context.SelectedStorages.AddRange(toAdd);

            await _context.SaveChangesAsync();
            TempData["Message"] = "Selected storage updated.";
            return RedirectToAction("ClusterStorage", new { clusterId = model.ClusterId });
        }

        [HttpPost]
        public async Task<IActionResult> SaveNetappSelectedVolumes([FromBody] List<NetappVolumeExportDto> volumes)
        {
            var controllerId = volumes.FirstOrDefault()?.ClusterId ?? 0;

            // Clear previous selections for this controller
            var old = await _context.SelectedNetappVolumes
                .Where(v => v.NetappControllerId == controllerId)
                .ToListAsync();
            _context.SelectedNetappVolumes.RemoveRange(old);

            // Add new ones
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
            await _context.SaveChangesAsync();

            return Ok();
        }

    }
}
