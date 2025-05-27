using BareProx.Services;
using BareProx.Data;
using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;
using BareProx.Models;
using Microsoft.EntityFrameworkCore;

using DbConfigModel = BareProx.Models.DatabaseConfig;

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

        public SettingsController(
            ApplicationDbContext context,
            ProxmoxService proxmoxService,
            INetappService netappService,
            IEncryptionService encryptionService,
            IWebHostEnvironment env)
        {
            _context = context;
            _proxmoxService = proxmoxService;
            _netappService = netappService;
            _encryptionService = encryptionService;
            _env = env;
            _configFile = Path.Combine(_env.ContentRootPath, "DatabaseConfig.json");
        }

        // helper properties to resolve only when needed:
        private ApplicationDbContext DbContext =>
            HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
        private ProxmoxService ProxmoxService =>
            HttpContext.RequestServices.GetRequiredService<ProxmoxService>();
        private INetappService NetappService =>
            HttpContext.RequestServices.GetRequiredService<INetappService>();


        // GET: /Settings/Config
        [HttpGet]
        public IActionResult Config()
        {
            DbConfigModel model;
            if (System.IO.File.Exists(_configFile))
            {
                var json = System.IO.File.ReadAllText(_configFile);
                var root = JObject.Parse(json);
                model = root["DatabaseConfig"]?.ToObject<DbConfigModel>()
                        ?? new DbConfigModel();
            }
            else
            {
                model = new DbConfigModel();
            }
            return View(model);
        }

        // POST: Settings/System
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Config(DbConfigModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            JObject root;
            if (System.IO.File.Exists(_configFile))
            {
                root = JObject.Parse(System.IO.File.ReadAllText(_configFile));
            }
            else
            {
                root = new JObject();
            }

            // Overwrite or set the DatabaseConfig section
            root["DatabaseConfig"] = JObject.FromObject(model);

            System.IO.File.WriteAllText(
            _configFile,
            root.ToString(Newtonsoft.Json.Formatting.Indented)
        );

            TempData["SuccessMessage"] = "Configuration saved. Please restart the app to apply.";
            return RedirectToAction(nameof(Config));
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

        [HttpPost]
        public async Task<IActionResult> AutoDetectHosts(int clusterId)
        {
            var cluster = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync(c => c.Id == clusterId);

            if (cluster == null)
                return NotFound();

            // You need to implement the call to Proxmox API here using your service
            var hostsFromApi = await _proxmoxService.GetHostsForClusterAsync(cluster);

            foreach (var hostAddress in hostsFromApi)
            {
                if (!cluster.Hosts.Any(h => h.HostAddress == hostAddress))
                {
                    var newHost = new ProxmoxHost
                    {
                        ClusterId = clusterId,
                        HostAddress = hostAddress
                    };
                    _context.ProxmoxHosts.Add(newHost);
                }
            }
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
