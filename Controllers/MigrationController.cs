using Microsoft.Extensions.Caching.Memory;
/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 *
 * Copyright (C) 2025-2026 Tobias Modig
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
using BareProx.Services.Migration;         // IProxmoxFileScanner
using BareProx.Services.Proxmox;
using BareProx.Services.Proxmox.Migration; // IProxmoxMigration
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Net.Mime;
using System.Text.Json;

namespace BareProx.Controllers
{
    // Add a controller-level route prefix so all endpoints live under /Migration/...
    [Route("[controller]")]
    public class MigrationController : Controller
    {
        private readonly ILogger<MigrationController> _logger;
        private readonly VCenterSettingsStore _vcenterSettings;
        private readonly ApplicationDbContext _db;
        private readonly QueryDbContext _qdb;
        private readonly ProxmoxService _proxmoxService;
        private readonly IProxmoxFileScanner _scanner;
        private readonly VCenterVmStatusService _vmStatus;
        private readonly IMigrationQueueRunner _runner;
        private readonly IProxmoxMigration _migration; // replaces ProxmoxService for capability lookups

        public MigrationController(
            ILogger<MigrationController> logger,
            ApplicationDbContext db,
            QueryDbContext qdb,
            ProxmoxService proxmoxService,
            IProxmoxFileScanner scanner,
            VCenterVmStatusService vmStatus,
            IMigrationQueueRunner runner,
            IProxmoxMigration migration, 
            VCenterSettingsStore vcenterSettings)
        {
            _logger = logger;
            _db = db;
            _qdb = qdb;
            _proxmoxService = proxmoxService;
            _scanner = scanner;
            _vmStatus = vmStatus;
            _runner = runner;
            _migration = migration;
            _vcenterSettings = vcenterSettings;
        }

        // =====================================================================
        // Views (MVC)
        // =====================================================================

        /// <summary>
        /// Migration run page (displays scan table and queue).
        /// </summary>
        [HttpGet("Migrate")]
        public async Task<IActionResult> Migrate(CancellationToken ct = default)
        {
            var sel = await _db.MigrationSelections
                               .AsNoTracking()
                               .FirstOrDefaultAsync(ct);

            string? clusterName = null, hostName = null, storageName = null;

            if (sel != null)
            {
                clusterName = await _db.ProxmoxClusters
                    .Where(c => c.Id == sel.ClusterId)
                    .Select(c => c.Name)
                    .FirstOrDefaultAsync(ct);

                hostName = await _db.ProxmoxHosts
                    .Where(h => h.Id == sel.ProxmoxHostId)
                    .Select(h => string.IsNullOrEmpty(h.Hostname) ? h.HostAddress : h.Hostname)
                    .FirstOrDefaultAsync(ct);

                storageName = await _db.SelectedStorages
                    .Where(s => s.ClusterId == sel.ClusterId && s.StorageIdentifier == sel.StorageIdentifier)
                    .Select(s => s.StorageIdentifier)
                    .FirstOrDefaultAsync(ct);
            }

            var vm = new MigratePageViewModel
            {
                ClusterName = clusterName,
                HostName = hostName,
                StorageName = storageName
            };

            return View(vm);
        }

        /// <summary>
        /// Help page.
        /// </summary>
        [HttpGet("Help")]
        public IActionResult Help() => View();

        // =====================================================================
        // Settings (cluster/host/datastore selection)
        // =====================================================================
        [HttpGet("Settings")]
        public async Task<IActionResult> Settings(int? clusterId = null, CancellationToken ct = default)
        {
            // 1) Decide which cluster to show
            var clusterIdToUse = clusterId ?? 0;

            // If not explicitly chosen, try to reuse the last saved selection's cluster
            if (clusterIdToUse == 0)
            {
                var lastSelClusterId = await _db.MigrationSelections
                    .AsNoTracking()
                    .OrderByDescending(x => x.UpdatedAt)
                    .Select(x => x.ClusterId)
                    .FirstOrDefaultAsync(ct);

                if (lastSelClusterId > 0)
                    clusterIdToUse = lastSelClusterId;
            }

            var vm = new MigrationSettingsViewModel
            {
                SelectedClusterId = clusterIdToUse,
                // Defaults for a cluster that has not yet been configured.
                // Saved values are loaded below without being overwritten.
                MaxParallelMigrations = 1,
                MaxParallelCutovers = 1
            };

            // 2) Hydrate dropdowns (this may set SelectedClusterId if still 0)
            await HydrateOptionsAsync(vm, ct);

            // If there are no clusters at all, we can't load or save anything
            if (vm.SelectedClusterId == 0)
            {
                vm.HasSavedConfigForCluster = false;
                await LoadVCenterSettingsAsync(vm, ct);
                return View(vm);
            }

            // 3) Load any saved config for the *actual* selected cluster
            var saved = await _db.MigrationSelections
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.ClusterId == vm.SelectedClusterId, ct);

            if (saved != null)
            {
                vm.HasSavedConfigForCluster = true;
                vm.SelectedHostId = saved.ProxmoxHostId;
                vm.SelectedStorageIdentifier = saved.StorageIdentifier;
                vm.MaxParallelMigrations = saved.MaxParallelMigrations;
                vm.MaxParallelCutovers = saved.MaxParallelCutovers;
                vm.GuestPrepTimeoutMinutes = saved.GuestPrepTimeoutMinutes;
                vm.VmwareShutdownTimeoutMinutes = saved.VmwareShutdownTimeoutMinutes;
                vm.GuestAgentStartupTimeoutMinutes = saved.GuestAgentStartupTimeoutMinutes;
                vm.GuestVerificationTimeoutMinutes = saved.GuestVerificationTimeoutMinutes;

                // Optional safety: if the saved host/storage no longer exists in options,
                // you can decide to mark as not configured or just show the raw values.

                // If the host is not in the list anymore, keep the ID but you might show
                // a warning in the view if you want.
                if (!vm.HostOptions.Any(o => o.Value == vm.SelectedHostId.ToString()))
                {
                    // Host removed or cluster changed; you could:
                    // vm.HasSavedConfigForCluster = false;
                }

                if (!vm.StorageOptions.Any(o => o.Value == vm.SelectedStorageIdentifier))
                {
                    // Storage no longer in intersection; again, you might:
                    // vm.HasSavedConfigForCluster = false;
                }
            }
            else
            {
                // 4) No saved config yet for this cluster → reasonable defaults
                vm.HasSavedConfigForCluster = false;
                vm.MaxParallelMigrations = 1;
                vm.MaxParallelCutovers = 1;

                if (vm.HostOptions.Any() && vm.SelectedHostId == 0)
                {
                    if (int.TryParse(vm.HostOptions.First().Value, out var hostId))
                        vm.SelectedHostId = hostId;
                }

                if (vm.StorageOptions.Any() && string.IsNullOrWhiteSpace(vm.SelectedStorageIdentifier))
                {
                    vm.SelectedStorageIdentifier = vm.StorageOptions.First().Value;
                }
            }
            await LoadVCenterSettingsAsync(vm, ct);
            return View(vm);
        }



        [HttpPost("Settings")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Settings(MigrationSettingsViewModel vm, CancellationToken ct = default)
        {
            await HydrateOptionsAsync(vm, ct);
            vm.VCenterPasswordSaved = !string.IsNullOrEmpty((await _vcenterSettings.ReadAsync(ct)).EncryptedPassword);

            // vCenter is optional; when configured require a complete endpoint and identity.
            if (string.IsNullOrWhiteSpace(vm.VCenterServer) != string.IsNullOrWhiteSpace(vm.VCenterUsername))
                ModelState.AddModelError(nameof(vm.VCenterServer), "Provide both vCenter server and username, or leave both empty.");
            if (!string.IsNullOrWhiteSpace(vm.VCenterServer))
            {
                var raw = vm.VCenterServer.Trim();
                if (!raw.Contains("://", StringComparison.Ordinal)) raw = "https://" + raw;
                if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length > 0 ||
                uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                    ModelState.AddModelError(nameof(vm.VCenterServer), "Enter a valid HTTPS vCenter hostname without a path.");
            }

            var priorVcenter = await _vcenterSettings.ReadAsync(ct);
            if (!string.IsNullOrWhiteSpace(vm.VCenterServer) &&
                string.IsNullOrEmpty(vm.VCenterPassword) &&
                (string.IsNullOrEmpty(priorVcenter.EncryptedPassword) ||
                 !string.Equals(priorVcenter.Server, vm.VCenterServer.Trim(), StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(priorVcenter.Username, vm.VCenterUsername?.Trim(), StringComparison.Ordinal)))
                ModelState.AddModelError(nameof(vm.VCenterPassword), "Enter the vCenter password for this server and username.");

            if (vm.SelectedClusterId == 0)
                ModelState.AddModelError(nameof(vm.SelectedClusterId), "Select a cluster.");

            if (!vm.HostOptions.Any(o => o.Value == vm.SelectedHostId.ToString()))
                ModelState.AddModelError(nameof(vm.SelectedHostId), "Select a valid host.");

            if (string.IsNullOrWhiteSpace(vm.SelectedStorageIdentifier) ||
                !vm.StorageOptions.Any(o => o.Value == vm.SelectedStorageIdentifier))
                ModelState.AddModelError(nameof(vm.SelectedStorageIdentifier), "Select a valid datastore.");

            if (vm.MaxParallelMigrations is < 1 or > 8)
                ModelState.AddModelError(nameof(vm.MaxParallelMigrations), "Select between 1 and 8 parallel migrations.");

            if (vm.MaxParallelCutovers is < 1 or > 8)
                ModelState.AddModelError(nameof(vm.MaxParallelCutovers), "Select between 1 and 8 automated cutovers.");

            if (vm.GuestPrepTimeoutMinutes is < 5 or > 120)
                ModelState.AddModelError(nameof(vm.GuestPrepTimeoutMinutes), "Select between 5 and 120 minutes.");
            if (vm.VmwareShutdownTimeoutMinutes is < 1 or > 120)
                ModelState.AddModelError(nameof(vm.VmwareShutdownTimeoutMinutes), "Select between 1 and 120 minutes.");
            if (vm.GuestAgentStartupTimeoutMinutes is < 5 or > 180)
                ModelState.AddModelError(nameof(vm.GuestAgentStartupTimeoutMinutes), "Select between 5 and 180 minutes.");
            if (vm.GuestVerificationTimeoutMinutes is < 5 or > 180)
                ModelState.AddModelError(nameof(vm.GuestVerificationTimeoutMinutes), "Select between 5 and 180 minutes.");

            if (!ModelState.IsValid)
            {
                TempData["ErrorMessage"] = "Please fix the validation errors.";
                return View(vm);
            }

            // ---------------------------------------------------------------------
            // Local helpers – used only with in-memory LINQ, not EF expression trees
            // ---------------------------------------------------------------------
            static string NormalizePath(string? path)
            {
                if (string.IsNullOrWhiteSpace(path)) return string.Empty;
                var p = path.Trim();
                if (p.EndsWith("/"))
                    p = p.TrimEnd('/');
                return p;
            }

            static bool IpMatches(string? nfsIps, string? serverIp)
            {
                if (string.IsNullOrWhiteSpace(nfsIps) || string.IsNullOrWhiteSpace(serverIp))
                    return false;

                var ip = serverIp.Trim();
                var candidates = nfsIps
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                return candidates.Any(c => string.Equals(c, ip, StringComparison.OrdinalIgnoreCase));
            }

            int? selectedNetappVolumeId = null;

            // 1) Inventory storage for this cluster + PVE storage id (pure EF → OK)
            var invStorage = await _qdb.InventoryStorages
                .AsNoTracking()
                .FirstOrDefaultAsync(i =>
                    i.ClusterId == vm.SelectedClusterId &&
                    i.StorageId == vm.SelectedStorageIdentifier,
                    ct);

            if (invStorage != null)
            {
                var serverIp = invStorage.Server;
                var export = NormalizePath(invStorage.Export);

                if (!string.IsNullOrWhiteSpace(serverIp) && !string.IsNullOrWhiteSpace(export))
                {
                    // 2) Load all inventory NetApp volumes into memory
                    var invNetappVolumes = await _qdb.InventoryNetappVolumes
                        .AsNoTracking()
                        .ToListAsync(ct);

                    // 3) Now use helpers in LINQ-to-objects (NOT EF)
                    var invVol = invNetappVolumes.FirstOrDefault(v =>
                        IpMatches(v.NfsIps, serverIp) &&
                        string.Equals(NormalizePath(v.JunctionPath), export, StringComparison.OrdinalIgnoreCase));

                    if (invVol != null)
                    {
                        // 4) Find enabled SelectedNetappVolumes row by UUID (simple EF predicate → OK)
                        var selVol = await _db.SelectedNetappVolumes
                            .AsNoTracking()
                            .FirstOrDefaultAsync(v =>
                                v.Disabled != true &&
                                v.Uuid == invVol.VolumeUuid,
                                ct);

                        if (selVol != null)
                        {
                            selectedNetappVolumeId = selVol.Id;
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Migration Settings: no enabled SelectedNetappVolume found for UUID {Uuid} (cluster {ClusterId}, storage {StorageId})",
                                invVol.VolumeUuid, vm.SelectedClusterId, vm.SelectedStorageIdentifier);
                        }
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Migration Settings: no InventoryNetappVolume match for server {Server} export {Export} " +
                            "(cluster {ClusterId}, storage {StorageId})",
                            serverIp, export, vm.SelectedClusterId, vm.SelectedStorageIdentifier);
                    }
                }
            }
            else
            {
                _logger.LogWarning(
                    "Migration Settings: InventoryStorage not found for cluster {ClusterId}, storage {StorageId}",
                    vm.SelectedClusterId, vm.SelectedStorageIdentifier);
            }

            // Optional fallback: old name-based mapping
            if (selectedNetappVolumeId == null)
            {
                var fallbackId = await _db.SelectedNetappVolumes
                    .AsNoTracking()
                    .Where(v => v.Disabled != true &&
                                v.VolumeName == vm.SelectedStorageIdentifier)
                    .Select(v => (int?)v.Id)
                    .FirstOrDefaultAsync(ct);

                if (fallbackId != null)
                {
                    _logger.LogInformation(
                        "Migration Settings: falling back to name-based SelectedNetappVolume match for storage {StorageId}",
                        vm.SelectedStorageIdentifier);
                    selectedNetappVolumeId = fallbackId;
                }
            }

            // ---------------------------------------------------------------------
            // Save MigrationSelection
            // ---------------------------------------------------------------------
            var existing = await _db.MigrationSelections
                .FirstOrDefaultAsync(x => x.ClusterId == vm.SelectedClusterId, ct);

            if (existing == null)
            {
                _db.MigrationSelections.Add(new MigrationSelection
                {
                    ClusterId = vm.SelectedClusterId,
                    ProxmoxHostId = vm.SelectedHostId,
                    StorageIdentifier = vm.SelectedStorageIdentifier!,
                    SelectedNetappVolumeId = selectedNetappVolumeId,
                    MaxParallelMigrations = vm.MaxParallelMigrations,
                    MaxParallelCutovers = vm.MaxParallelCutovers,
                    GuestPrepTimeoutMinutes = vm.GuestPrepTimeoutMinutes,
                    VmwareShutdownTimeoutMinutes = vm.VmwareShutdownTimeoutMinutes,
                    GuestAgentStartupTimeoutMinutes = vm.GuestAgentStartupTimeoutMinutes,
                    GuestVerificationTimeoutMinutes = vm.GuestVerificationTimeoutMinutes,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            else
            {
                existing.ProxmoxHostId = vm.SelectedHostId;
                existing.StorageIdentifier = vm.SelectedStorageIdentifier!;
                existing.SelectedNetappVolumeId = selectedNetappVolumeId;
                existing.MaxParallelMigrations = vm.MaxParallelMigrations;
                existing.MaxParallelCutovers = vm.MaxParallelCutovers;
                existing.GuestPrepTimeoutMinutes = vm.GuestPrepTimeoutMinutes;
                existing.VmwareShutdownTimeoutMinutes = vm.VmwareShutdownTimeoutMinutes;
                existing.GuestAgentStartupTimeoutMinutes = vm.GuestAgentStartupTimeoutMinutes;
                existing.GuestVerificationTimeoutMinutes = vm.GuestVerificationTimeoutMinutes;
                existing.UpdatedAt = DateTime.UtcNow;
            }

            // Save the vCenter secret before committing the database selection.
            // A blank password keeps the current encrypted secret only if the endpoint and username match.
            try
            {
                await _vcenterSettings.SetAsync(vm.VCenterServer, vm.VCenterUsername,
                    vm.VCenterPassword, vm.VCenterAllowUntrustedCertificate, ct);
            }
            catch (InvalidOperationException ex)
            {
                ModelState.AddModelError(nameof(vm.VCenterPassword), ex.Message);
                TempData["ErrorMessage"] = "Please fix the validation errors.";
                return View(vm);
            }
            await _db.SaveChangesAsync(ct);

            TempData["SuccessMessage"] = "Settings saved.";
            return RedirectToAction(nameof(Settings), new { clusterId = vm.SelectedClusterId });
        }



        private async Task LoadVCenterSettingsAsync(MigrationSettingsViewModel vm, CancellationToken ct)
        {
            var vc = await _vcenterSettings.ReadAsync(ct);
            vm.VCenterServer = vc.Server;
            vm.VCenterUsername = vc.Username;
            vm.VCenterAllowUntrustedCertificate = vc.AllowUntrustedCertificate;
            vm.VCenterPasswordSaved = !string.IsNullOrEmpty(vc.EncryptedPassword);
        }

        /// <summary>
        /// Fills dropdowns for the Settings view (clusters/hosts/storages).
        /// </summary>
        private async Task HydrateOptionsAsync(MigrationSettingsViewModel vm, CancellationToken ct)
        {
            var clusters = await _db.ProxmoxClusters
                .AsNoTracking()
                .OrderBy(c => c.Name)
                .ToListAsync(ct);

            vm.ClusterOptions = clusters.Select(c => new SelectListItem
            {
                Value = c.Id.ToString(),
                Text = c.Name
            }).ToList();

            if (vm.SelectedClusterId == 0 && vm.ClusterOptions.Any())
                vm.SelectedClusterId = int.Parse(vm.ClusterOptions.First().Value);

            var hosts = await _db.ProxmoxHosts.AsNoTracking()
                .Where(h => h.ClusterId == vm.SelectedClusterId)
                .OrderBy(h => h.Hostname ?? h.HostAddress)
                .ToListAsync(ct);

            vm.HostOptions = hosts.Select(h => new SelectListItem
            {
                Value = h.Id.ToString(),
                Text = string.IsNullOrWhiteSpace(h.Hostname) ? h.HostAddress : h.Hostname!
            }).ToList();

            // ---------------------------------------------------------------------
            // Storage options
            // ---------------------------------------------------------------------
            static string NormalizePath(string? path)
            {
                if (string.IsNullOrWhiteSpace(path)) return string.Empty;
                var p = path.Trim();
                if (p.EndsWith("/"))
                    p = p.TrimEnd('/');
                return p;
            }

            static bool IpMatches(string? nfsIps, string? serverIp)
            {
                if (string.IsNullOrWhiteSpace(nfsIps) || string.IsNullOrWhiteSpace(serverIp))
                    return false;

                var ip = serverIp.Trim();
                var candidates = nfsIps
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                return candidates.Any(c => string.Equals(c, ip, StringComparison.OrdinalIgnoreCase));
            }

            // Proxmox-selected storages for this cluster (main DB)
            var selectedStorages = await _db.SelectedStorages
                .AsNoTracking()
                .Where(s => s.ClusterId == vm.SelectedClusterId)
                .ToListAsync(ct);

            // Inventory storages for this cluster (Query DB)
            var invStorages = await _qdb.InventoryStorages
                .AsNoTracking()
                .Where(i => i.ClusterId == vm.SelectedClusterId)
                .ToListAsync(ct);

            // All inventory NetApp volumes (Query DB)
            var invNetappVolumes = await _qdb.InventoryNetappVolumes
                .AsNoTracking()
                .ToListAsync(ct);

            // All *enabled* NetApp volumes that the user has selected (main DB)
            var selectedNetappVolumes = await _db.SelectedNetappVolumes
                .AsNoTracking()
                .Where(v => v.Disabled != true)   // instead of !v.Disabled
                .ToListAsync(ct);

            var storageOptions = new List<SelectListItem>();

            foreach (var s in selectedStorages)
            {
                if (string.IsNullOrWhiteSpace(s.StorageIdentifier))
                    continue;

                // 1) Match SelectedStorage -> InventoryStorage on (ClusterId, StorageId)
                var inv = invStorages.FirstOrDefault(i =>
                    string.Equals(i.StorageId, s.StorageIdentifier, StringComparison.OrdinalIgnoreCase));

                if (inv == null)
                    continue; // storage not discovered in QueryDB → skip

                var serverIp = inv.Server;
                var exportPath = NormalizePath(inv.Export);

                if (string.IsNullOrWhiteSpace(serverIp) || string.IsNullOrWhiteSpace(exportPath))
                    continue;

                // 2) Match InventoryStorage(Server, Export) -> InventoryNetappVolumes(NfsIps, JunctionPath)
                var invVol = invNetappVolumes.FirstOrDefault(v =>
                    IpMatches(v.NfsIps, serverIp) &&
                    string.Equals(NormalizePath(v.JunctionPath), exportPath, StringComparison.OrdinalIgnoreCase));

                if (invVol == null)
                    continue; // no matching NetApp volume in inventory → skip

                // 3) Check that the volume exists in SelectedNetappVolumes and is NOT disabled
                var selVol = selectedNetappVolumes.FirstOrDefault(v =>
                    string.Equals(v.Uuid, invVol.VolumeUuid, StringComparison.OrdinalIgnoreCase));

                if (selVol == null)
                    continue; // not selected on NetApp side (or disabled) → skip

                // 4) Build display label
                var pveName = s.StorageIdentifier;
                var netappName = selVol.VolumeName;

                string label;
                if (string.Equals(pveName, netappName, StringComparison.OrdinalIgnoreCase))
                    label = pveName; // same name on both sides
                else
                    label = $"{pveName} ↔ {netappName}"; // show mapping when names differ

                storageOptions.Add(new SelectListItem
                {
                    Value = pveName, // what the wizard will send back in vm.SelectedStorageIdentifier
                    Text = label
                });
            }

            // Sort by label for nicer UX
            vm.StorageOptions = storageOptions
                .OrderBy(x => x.Text, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }



        // =====================================================================
        // Capabilities (node bridges, SDN, VirtIO ISOs, catalogs)
        // =====================================================================

        /// <summary>
        /// Returns capability lists used to populate the wizard (CPU types, SCSI, NICs, bridges/VLANs, VirtIO ISOs).
        /// </summary>
        [HttpGet("Capabilities")]
        [Produces(MediaTypeNames.Application.Json)]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> Capabilities(CancellationToken ct)
        {
            var caps = new MigrationCapabilities
            {
                Cpus = CapabilityCatalog.DefaultCpus,
                Nics = CapabilityCatalog.DefaultNics,
                ScsiControllers = CapabilityCatalog.DefaultScsi,
                OsTypes = CapabilityCatalog.DefaultOs,
                Bridges = Array.Empty<BridgeOption>(),
                Vlans = Array.Empty<VlanOption>(),
                VirtioIsos = Array.Empty<IsoOption>()
            };

            var sel = await _db.MigrationSelections.AsNoTracking().FirstOrDefaultAsync(ct);
            if (sel == null) return Ok(caps);

            var host = await _db.ProxmoxHosts.AsNoTracking()
                              .FirstOrDefaultAsync(h => h.Id == sel.ProxmoxHostId, ct);
            if (host == null) return Ok(caps);

            var node = string.IsNullOrWhiteSpace(host.Hostname) ? host.HostAddress : host.Hostname!;

            var bridges = new List<BridgeOption>();
            var vlans = new List<VlanOption>();

            // Bridges from /network
            try
            {
                var nets = await _migration.GetNodeNetworksAsync(node, ct);
                bridges.AddRange(
                    nets.Where(n => string.Equals(n.Type, "bridge", StringComparison.OrdinalIgnoreCase))
                        .Select(n => new BridgeOption(n.Iface ?? "vmbr0"))
                        .GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.First())
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bridge fetch failed for node {Node}", node);
            }

            // SDN bridges + VLANs
            try
            {
                var vnets = await _migration.GetSdnVnetsAsync(ct);

                bridges.AddRange(
                    vnets.Select(v => new BridgeOption($"{v.Vnet} (SDN)"))
                         .GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                         .Select(g => g.First())
                );

                vlans.AddRange(
                    vnets.Where(v => v.Tag.HasValue)
                         .Select(v => new VlanOption(v.Tag!.Value.ToString()))
                         .GroupBy(x => x.Tag, StringComparer.OrdinalIgnoreCase)
                         .Select(g => g.First())
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SDN vNet fetch failed");
            }

            // VirtIO ISO discovery from storages
            try
            {
                static string ExtractFileName(string? name, string? volid, string? volId, string? volume)
                {
                    if (!string.IsNullOrWhiteSpace(name)) return name!;
                    var path = volid ?? volId ?? volume ?? string.Empty;
                    var slash = path.LastIndexOf('/');
                    return slash >= 0 && slash + 1 < path.Length ? path[(slash + 1)..] : path;
                }

                var storages = await _migration.GetNodeStoragesAsync(node, ct);
                _logger.LogDebug("Capabilities: node {Node} has {Count} storages.", node, storages.Count);

                // Keep newest iso per file name across all storages
                var newestByName = new Dictionary<string, (long ctime, string label, string value)>(StringComparer.OrdinalIgnoreCase);

                foreach (var s in storages)
                {
                    if (string.IsNullOrWhiteSpace(s.Storage)) continue;

                    IReadOnlyList<PveStorageContentItem> items;
                    try
                    {
                        items = await _migration.GetStorageContentAsync(node, s.Storage!, "iso", ct);
                    }
                    catch (Exception listEx)
                    {
                        _logger.LogDebug(listEx, "Skipping storage {Storage} due to content list error.", s.Storage);
                        continue;
                    }

                    foreach (var i in items)
                    {
                        var fileName = ExtractFileName(i.Name, i.Volid, i.VolId, i.Volume);
                        if (string.IsNullOrWhiteSpace(fileName)) continue;

                        var lower = fileName.ToLowerInvariant();
                        if (!(lower.StartsWith("virtio") && lower.EndsWith(".iso")))
                            continue;

                        var ctime = i.Ctime ?? 0L;
                        var value = i.VolId ?? i.Volid ?? i.Volume ?? $"{s.Storage}:iso/{fileName}";
                        var label = $"{s.Storage}:{fileName}";

                        if (!newestByName.TryGetValue(fileName, out var cur) || ctime > cur.ctime)
                            newestByName[fileName] = (ctime, label, value);
                    }
                }

                var isoList = newestByName.Values
                    .OrderByDescending(x => x.ctime)                                   // newest first
                    .ThenByDescending(x => x.label, StringComparer.OrdinalIgnoreCase)  // stable-ish
                    .Select(x => new IsoOption(x.value, x.label))
                    .ToList();

                _logger.LogDebug("Capabilities: found {Count} VirtIO ISOs.", isoList.Count);
                caps.VirtioIsos = isoList;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VirtIO ISO enumeration failed on node {Node}", node);
            }

            caps.Bridges = bridges.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
            caps.Vlans = vlans.OrderBy(v => v.Tag, StringComparer.OrdinalIgnoreCase).ToList();

            return Ok(caps);
        }

        // =====================================================================
        // Scan (SSH)
        // =====================================================================

        /// <summary>
        /// Real scan over SSH for .vmx files on the chosen datastore.
        /// </summary>
        [HttpGet("Scan")]
        [Produces(MediaTypeNames.Application.Json)]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> Scan(CancellationToken ct)
        {
            var sel = await _db.MigrationSelections.AsNoTracking().FirstOrDefaultAsync(ct);
            if (sel == null)
            {
                _logger.LogInformation("Scan requested but no MigrationSelection saved yet.");
                return Ok(Array.Empty<object>());
            }

            try
            {
                var items = await _scanner.ScanForVmxAsync(sel.ClusterId, sel.ProxmoxHostId, sel.StorageIdentifier, ct);
                await _vmStatus.EnrichAsync(items, ct);
                return Ok(items);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scan failed for cluster {ClusterId}, host {HostId}, storage {Storage}.",
                    sel.ClusterId, sel.ProxmoxHostId, sel.StorageIdentifier);
                return Ok(Array.Empty<object>()); // fail-soft for UI
            }
        }

        // =====================================================================
        // Queue (list/add/edit/remove + logs)
        // =====================================================================

        /// <summary>
        /// Starts the queue runner. (JSON; no CSRF token.)
        /// </summary>
        // Explicit runner state for the Migration page. Never infer activity from queue rows.
        [HttpGet("QueueState")]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        [Produces(MediaTypeNames.Application.Json)]
        public IActionResult QueueState()
        {
            var manualRunning = _runner.IsRunning;
            var automatedRunning = MigrationAutomationQueueStageController.IsRunning;
            return Ok(new { running = manualRunning || automatedRunning, manualRunning, automatedRunning });
        }

        [HttpPost("RunQueue")]
        [IgnoreAntiforgeryToken]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<IActionResult> RunQueue(CancellationToken ct)
        {
            await MigrationQueueControlGate.Sync.WaitAsync(ct);
            try
            {
                if (MigrationAutomationQueueStageController.IsRunning)
                    return Conflict(new { ok = false, started = false, error = "Automated migration is already running." });
                var started = await _runner.StartAsync(ct);
                return Ok(new { ok = true, started });
            }
            finally { MigrationQueueControlGate.Sync.Release(); }
        }

        private static string ToJsonStringOrEmpty(object? obj, string? already)
        {
            if (!string.IsNullOrWhiteSpace(already)) return already!;
            if (obj == null) return "[]";
            try { return JsonSerializer.Serialize(obj); }
            catch { return "[]"; }
        }

        /// <summary>
        /// List queued items (newest first).
        /// </summary>
        [HttpGet("Queue")]
        [Produces(MediaTypeNames.Application.Json)]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> QueueList(CancellationToken ct)
        {
            var items = await _db.MigrationQueueItems
                .AsNoTracking()
                .OrderByDescending(x => x.CreatedAtUtc)
                .ToListAsync(ct);

            return Ok(items.Select(item => new
            {
                item.Id, item.VmId, item.Name, item.CpuType, item.OsType,
                item.PrepareVirtio, item.MountVirtioIso, item.VirtioIsoName,
                item.ScsiController, item.VmxPath, item.Uuid, item.Uefi,
                item.MemoryMiB, item.Sockets, item.Cores, item.DisksJson,
                item.NicsJson, item.Status, item.CreatedAtUtc,
                HasVerifiedGuestCredentials = !string.IsNullOrWhiteSpace(item.GuestUsername) &&
                    !string.IsNullOrWhiteSpace(item.EncryptedGuestPassword)
            }));
        }

        private async Task<string?> ValidateNoVmwareSnapshotsAsync(QueueItemDto dto, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(dto.VmxPath))
                return "VMX source path is required to verify VMware snapshots.";
            var selection = await _db.MigrationSelections.AsNoTracking().FirstOrDefaultAsync(ct);
            if (selection == null) return "Migration datastore is not configured.";
            try
            {
                var scanned = await _scanner.ScanForVmxAsync(selection.ClusterId, selection.ProxmoxHostId,
                    selection.StorageIdentifier, ct);
                var vm = scanned.SingleOrDefault(x => string.Equals(x.VmxPath, dto.VmxPath,
                    StringComparison.Ordinal));
                if (vm == null) return "Source VM could not be verified on the datastore.";
                if (!vm.SnapshotCheckPassed) return vm.SnapshotWarning ?? "VMware snapshot check could not be completed.";
                if (vm.HasVmwareSnapshots) return vm.SnapshotWarning ?? "VMware snapshot detected; queueing blocked.";
                return null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VMware snapshot validation failed before queueing.");
                return "Could not verify VMware snapshots. Queueing is blocked until the datastore scan succeeds.";
            }
        }

        /// <summary>
        /// Add a queued item.
        /// </summary>
        [HttpPost("Queue")]
        [Consumes(MediaTypeNames.Application.Json)]
        [IgnoreAntiforgeryToken] // same-site fetch JSON; no form token
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<IActionResult> QueueAdd([FromBody] QueueItemDto dto,
            [FromServices] IMemoryCache verificationCache, CancellationToken ct)
        {
            if (dto == null)
                return BadRequest(new { ok = false, error = "Empty payload" });
            var snapshotError = await ValidateNoVmwareSnapshotsAsync(dto, ct);
            if (snapshotError != null)
                return BadRequest(new { ok = false, error = snapshotError });

            // The browser's Automated flag is untrusted. Re-resolve the VMware source VM
            // and enforce supported guest-OS candidates before accepting automatic queue items.
            // Legacy Windows guests are excluded from automatic migration.
            // Manual migrations are deliberately unaffected.
            if (dto.Automated)
            {
                var selection = await _db.MigrationSelections.AsNoTracking().FirstOrDefaultAsync(ct);
                if (selection == null)
                    return BadRequest(new { ok = false, error = "Migration datastore is not configured." });
                var sourceVms = await _scanner.ScanForVmxAsync(selection.ClusterId,
                    selection.ProxmoxHostId, selection.StorageIdentifier ?? "", ct);
                var sourceVm = sourceVms.SingleOrDefault(x =>
                    string.Equals(x.VmxPath, dto.VmxPath, StringComparison.Ordinal) &&
                    string.Equals(new string((x.UuidBios ?? "").Where(Uri.IsHexDigit).ToArray()),
                        new string((dto.Uuid ?? "").Where(Uri.IsHexDigit).ToArray()),
                        StringComparison.OrdinalIgnoreCase));
                if (sourceVm?.GuestOs?.Trim() is not
                    ("Windows Server 2016" or "Windows Server 2019" or
             "Windows Server 2022" or "Windows Server 2025"))
                    return BadRequest(new { ok = false, error =
                        "Automatic migration requires Windows Server 2016 or newer (2016, 2019, 2022, 2025). Windows Server 2012 R2 and older are blocked." });
            }

            VerifiedGuestProof.VerifiedCredentials? verifiedCredentials = null;
            if (dto.Automated)
            {
                // A verified guest is required, even if the browser sends a forged request.
                // A verification token is single-use and bound to the guest BIOS UUID.
                var principalName = User.Identity?.IsAuthenticated == true ? User.Identity.Name ?? "" : "";
                verifiedCredentials = VerifiedGuestProof.Consume(verificationCache, dto.GuestVerificationToken,
                    dto.Uuid, principalName);
                if (verifiedCredentials is null || string.IsNullOrWhiteSpace(verifiedCredentials.Username) ||
                    string.IsNullOrWhiteSpace(verifiedCredentials.EncryptedPassword))
                    return BadRequest(new { ok = false, error =
                        "Verify Windows guest credentials again before adding automated migration to the queue." });
                if (dto.VmId is null or < 100 || string.IsNullOrWhiteSpace(dto.Name))
                    return BadRequest(new { ok = false, error = "VM name and valid VMID are required." });
            }

            var entity = new MigrationQueueItem
            {
                VmId = dto.VmId,
                Name = dto.Name?.Trim(),
                CpuType = dto.CpuType?.Trim(),
                OsType = dto.OsType?.Trim(),
                PrepareVirtio = dto.PrepareVirtio,
                MountVirtioIso = dto.MountVirtioIso,
                VirtioIsoName = dto.VirtioIsoName?.Trim(),
                ScsiController = dto.ScsiController?.Trim(),
                VmxPath = dto.VmxPath?.Trim(),
                Uuid = dto.Uuid?.Trim(),
                Uefi = dto.Uefi,
                MemoryMiB = dto.MemoryMiB,
                Sockets = dto.Sockets,
                Cores = dto.Cores,
                DisksJson = ToJsonStringOrEmpty(dto.Disks, dto.DisksJson),
                NicsJson = ToJsonStringOrEmpty(dto.Nics, dto.NicsJson),
                GuestUsername = verifiedCredentials?.Username,
                EncryptedGuestPassword = verifiedCredentials?.EncryptedPassword,
                Status = dto.Automated ? "AutomationPending" : "Queued",
                CreatedAtUtc = DateTime.UtcNow
            };

            try
            {
                _db.MigrationQueueItems.Add(entity);
                await _db.SaveChangesAsync(ct);
                _logger.LogInformation("QueueAdd saved Id={Id} Name={Name}", entity.Id, entity.Name);
                return Ok(new { ok = true, id = entity.Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "QueueAdd failed");
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        /// <summary>
        /// Edit a queued item.
        /// </summary>
        [HttpPut("Queue/{id:int}")]
        [Consumes(MediaTypeNames.Application.Json)]
        [IgnoreAntiforgeryToken]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<IActionResult> QueueEdit(int id, [FromBody] QueueItemDto dto, CancellationToken ct)
        {
            var entity = await _db.MigrationQueueItems.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (entity == null) return NotFound(new { ok = false, error = "Not found" });
            if (entity.Status == "AutomationPending")
                return BadRequest(new { ok = false, error = "Automated migration plan cannot be edited after guest verification. Remove and requeue it." });
            if (dto == null) return BadRequest(new { ok = false, error = "Empty payload" });
            var snapshotError = await ValidateNoVmwareSnapshotsAsync(dto, ct);
            if (snapshotError != null)
                return BadRequest(new { ok = false, error = snapshotError });

            entity.VmId = dto.VmId;
            entity.Name = dto.Name?.Trim();
            entity.CpuType = dto.CpuType?.Trim();
            entity.OsType = dto.OsType?.Trim();
            entity.PrepareVirtio = dto.PrepareVirtio;
            entity.MountVirtioIso = dto.MountVirtioIso;
            entity.VirtioIsoName = dto.VirtioIsoName?.Trim();
            entity.ScsiController = dto.ScsiController?.Trim();
            entity.VmxPath = dto.VmxPath?.Trim();
            entity.Uuid = dto.Uuid?.Trim();
            entity.Uefi = dto.Uefi;
            entity.MemoryMiB = dto.MemoryMiB;
            entity.Sockets = dto.Sockets;
            entity.Cores = dto.Cores;
            entity.DisksJson = ToJsonStringOrEmpty(dto.Disks, dto.DisksJson);
            entity.NicsJson = ToJsonStringOrEmpty(dto.Nics, dto.NicsJson);

            try
            {
                await _db.SaveChangesAsync(ct);
                _logger.LogInformation("QueueEdit updated Id={Id}", entity.Id);
                return Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "QueueEdit failed Id={Id}", id);
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        /// <summary>
        /// Remove a queued item.
        /// </summary>
        [HttpDelete("Queue/{id:int}")]
        [IgnoreAntiforgeryToken]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<IActionResult> QueueRemove(int id, CancellationToken ct)
        {
            var entity = await _db.MigrationQueueItems.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (entity == null) return NotFound(new { ok = false, error = "Not found" });

            // Never remove a record for a migration that may still be executing.
            // Reject unknown statuses by default.
            var state = (entity.Status ?? "").Trim().ToLowerInvariant();
            // Any persisted failure is terminal, including the detailed automation
            // states (PrepFailed, ImportFailed, StartFailed, BootVerifyFailed,
            // PostShutdownFailed, CutoverFailed, etc.). Never remove active states.
            var removable = state is "queued" or "automationpending"
                or "done" or "completed"
                or "canceled" or "cancelled"
                or "automationcanceled" or "automationcancelled"
                || state.EndsWith("failed", StringComparison.Ordinal);
            if (!removable)
                return Conflict(new { ok = false,
                    error = "This migration has started or its state is unknown. Only queued, failed, canceled or completed items may be removed." });

            try
            {
                _db.MigrationQueueItems.Remove(entity);
                await _db.SaveChangesAsync(ct);
                _logger.LogInformation("QueueRemove deleted Id={Id}", id);
                return Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "QueueRemove failed Id={Id}", id);
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        /// <summary>
        /// Per-item logs (for the "Logs" modal). Newest first if <paramref name="newestFirst"/> is true.
        /// </summary>
        [HttpGet("QueueLogs")]
        [Produces(MediaTypeNames.Application.Json)]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> QueueLogs(long id, int take = 200, bool newestFirst = false, CancellationToken ct = default)
        {
            take = Math.Clamp(take, 1, 1000);

            var query = _db.MigrationQueueLogs
                .AsNoTracking()
                .Where(x => x.ItemId == id);

            query = newestFirst
                ? query.OrderByDescending(x => x.Utc)
                : query.OrderBy(x => x.Utc);

            var logs = await query
                .Take(take)
                .Select(x => new
                {
                    utc = x.Utc.ToString("u"),
                    level = x.Level,   // "Info" | "Warning" | "Error"
                    step = x.Step,
                    message = x.Message
                })
                .ToListAsync(ct);

            return Ok(logs);
        }

        /// <summary>
        /// Suggests the next VMID that is not already used in the migration queue.
        /// This is only a helper for the wizard; the executor still validates against Proxmox.
        /// </summary>
        [HttpGet("NextVmId")]
        [Produces(MediaTypeNames.Application.Json)]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> NextVmId(CancellationToken ct = default)
        {
            // 1) Find current migration selection (cluster)
            var sel = await _db.MigrationSelections
                .AsNoTracking()
                .FirstOrDefaultAsync(ct);

            if (sel == null)
            {
                _logger.LogWarning("NextVmId: no MigrationSelection exists; falling back to queue-only logic.");
                var fallback = await ComputeNextVmIdFromQueueOnly(ct);
                return Ok(new { vmId = fallback, source = "queue-only" });
            }

            var cluster = await _db.ProxmoxClusters
                .Include(c => c.Hosts)
                .FirstOrDefaultAsync(c => c.Id == sel.ClusterId, ct);

            if (cluster == null)
            {
                _logger.LogWarning("NextVmId: cluster {ClusterId} not found; falling back to queue-only.", sel.ClusterId);
                var fallback = await ComputeNextVmIdFromQueueOnly(ct);
                return Ok(new { vmId = fallback, source = "queue-only" });
            }

            var used = new HashSet<int>();

            // 2) VMIDs currently in use on the cluster (/cluster/resources?type=vm)
            try
            {
                var clusterVmIds = await _proxmoxService.GetClusterVmIdsAsync(cluster, ct);
                foreach (var id in clusterVmIds.Where(x => x > 0))
                    used.Add(id);

                _logger.LogDebug("NextVmId: {Count} VMIDs from /cluster/resources for cluster {ClusterId}.",
                    used.Count, cluster.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NextVmId: error when calling GetClusterVmIdsAsync for cluster {ClusterId}", cluster.Id);
            }

            // 3) VMIDs from migration queue that are *not* in a final state
            try
            {
                var queueItems = await _db.MigrationQueueItems
                    .AsNoTracking()
                    .Where(x => x.VmId != null && x.VmId > 0)
                    .ToListAsync(ct);

                foreach (var item in queueItems)
                {
                    if (!IsFinalStatus(item.Status) && item.VmId.HasValue && item.VmId.Value > 0)
                        used.Add(item.VmId.Value);
                }

                _logger.LogDebug("NextVmId: used count after including active queue items = {Count}.", used.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NextVmId: failed to inspect MigrationQueueItems.");
            }

            // 4) Ask Proxmox for /cluster/nextid and use it as a starting suggestion
            int candidate = 100; // never hand out below 100

            try
            {
                var nextFromCluster = await _proxmoxService.GetClusterNextVmIdAsync(cluster, ct);
                if (nextFromCluster.HasValue && nextFromCluster.Value > 0)
                {
                    candidate = Math.Max(candidate, nextFromCluster.Value);
                }

                _logger.LogDebug("NextVmId: /cluster/nextid suggested {Candidate}.", candidate);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NextVmId: failed to call GetClusterNextVmIdAsync for cluster {ClusterId}", cluster.Id);
            }

            // If Proxmox suggestion is below or equal to max used, bump it at least above max used + 1
            if (used.Count > 0)
            {
                var maxUsed = used.Max();
                candidate = Math.Max(candidate, maxUsed + 1);
            }

            // 5) Final safety: make sure candidate is not in "used"
            while (used.Contains(candidate))
                candidate++;

            return Ok(new
            {
                vmId = candidate,
                source = "cluster+resources+queue"
            });
        }
        private static bool IsFinalStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status))
                return false;

            switch (status.Trim().ToLowerInvariant())
            {
                case "done":
                case "completed":
                case "failed":
                case "cancelled":
                case "canceled":
                    return true;
                default:
                    return false;
            }
        }

        private async Task<int> ComputeNextVmIdFromQueueOnly(CancellationToken ct)
        {
            var queueItems = await _db.MigrationQueueItems
                .AsNoTracking()
                .Where(x => x.VmId != null && x.VmId > 0)
                .ToListAsync(ct);

            var used = new HashSet<int>();

            foreach (var item in queueItems)
            {
                if (!IsFinalStatus(item.Status) && item.VmId.HasValue && item.VmId.Value > 0)
                    used.Add(item.VmId.Value);
            }

            var candidate = 100;

            if (used.Count > 0)
            {
                var max = used.Max();
                candidate = Math.Max(candidate, max + 1);
            }

            while (used.Contains(candidate))
                candidate++;

            return candidate;
        }

    }
}
