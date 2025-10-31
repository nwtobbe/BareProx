﻿/*
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
using BareProx.Services.Migration;       // IProxmoxFileScanner
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
    /// <summary>
    /// Migration (debug/experimental) controller. Attribute-routed under /Migration.
    /// </summary>
    [AllowAnonymous]               // explicitly open (debug)
    [Route("[controller]")]        // base: /Migration
    public class MigrationController : Controller
    {
        private readonly ILogger<MigrationController> _logger;
        private readonly ApplicationDbContext _db;
        private readonly IProxmoxFileScanner _scanner;
        private readonly IMigrationQueueRunner _runner;
        private readonly IProxmoxMigration _migration; // NEW: replaces ProxmoxService for capability lookups

        public MigrationController(
            ILogger<MigrationController> logger,
            ApplicationDbContext db,
            IProxmoxFileScanner scanner,
            IMigrationQueueRunner runner,
            IProxmoxMigration migration)                // NEW
        {
            _logger = logger;
            _db = db;
            _scanner = scanner;
            _runner = runner;
            _migration = migration;                     // NEW
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
            var vm = new MigrationSettingsViewModel { SelectedClusterId = clusterId ?? 0 };
            await HydrateOptionsAsync(vm, ct);

            var saved = await _db.MigrationSelections.AsNoTracking()
                          .FirstOrDefaultAsync(x => x.ClusterId == vm.SelectedClusterId, ct);

            if (saved != null)
            {
                vm.SelectedHostId = saved.ProxmoxHostId;
                vm.SelectedStorageIdentifier = saved.StorageIdentifier;
            }
            else
            {
                if (vm.HostOptions.Any() && vm.SelectedHostId == 0)
                    vm.SelectedHostId = int.Parse(vm.HostOptions.First().Value);

                if (vm.StorageOptions.Any() && string.IsNullOrWhiteSpace(vm.SelectedStorageIdentifier))
                    vm.SelectedStorageIdentifier = vm.StorageOptions.First().Value;
            }

            return View(vm);
        }

        [HttpPost("Settings")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Settings(MigrationSettingsViewModel vm, CancellationToken ct = default)
        {
            await HydrateOptionsAsync(vm, ct);

            // Simple validation
            if (vm.SelectedClusterId == 0)
                ModelState.AddModelError(nameof(vm.SelectedClusterId), "Select a cluster.");

            if (!vm.HostOptions.Any(o => o.Value == vm.SelectedHostId.ToString()))
                ModelState.AddModelError(nameof(vm.SelectedHostId), "Select a valid host.");

            if (string.IsNullOrWhiteSpace(vm.SelectedStorageIdentifier) ||
                !vm.StorageOptions.Any(o => o.Value == vm.SelectedStorageIdentifier))
                ModelState.AddModelError(nameof(vm.SelectedStorageIdentifier), "Select a valid datastore.");

            if (!ModelState.IsValid)
            {
                TempData["ErrorMessage"] = "Please fix the validation errors.";
                return View(vm);
            }

            var existing = await _db.MigrationSelections
                                    .FirstOrDefaultAsync(x => x.ClusterId == vm.SelectedClusterId, ct);

            if (existing == null)
            {
                _db.MigrationSelections.Add(new MigrationSelection
                {
                    ClusterId = vm.SelectedClusterId,
                    ProxmoxHostId = vm.SelectedHostId,
                    StorageIdentifier = vm.SelectedStorageIdentifier!,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            else
            {
                existing.ProxmoxHostId = vm.SelectedHostId;
                existing.StorageIdentifier = vm.SelectedStorageIdentifier!;
                existing.UpdatedAt = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync(ct);

            TempData["SuccessMessage"] = "Settings saved.";
            return RedirectToAction(nameof(Settings), new { clusterId = vm.SelectedClusterId });
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

            var storages = await _db.SelectedStorages.AsNoTracking()
                .Where(s => s.ClusterId == vm.SelectedClusterId)
                .OrderBy(s => s.StorageIdentifier)
                .ToListAsync(ct);

            vm.StorageOptions = storages.Select(s => new SelectListItem
            {
                Value = s.StorageIdentifier,
                Text = s.StorageIdentifier
            }).ToList();
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
                var nets = await _migration.GetNodeNetworksAsync(node, ct);  // CHANGED
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
                var vnets = await _migration.GetSdnVnetsAsync(ct);          // CHANGED

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

                var storages = await _migration.GetNodeStoragesAsync(node, ct); // CHANGED
                _logger.LogDebug("Capabilities: node {Node} has {Count} storages.", node, storages.Count);

                // Keep newest iso per file name across all storages
                var newestByName = new Dictionary<string, (long ctime, string label, string value)>(StringComparer.OrdinalIgnoreCase);

                foreach (var s in storages)
                {
                    if (string.IsNullOrWhiteSpace(s.Storage)) continue;

                    IReadOnlyList<PveStorageContentItem> items;
                    try
                    {
                        items = await _migration.GetStorageContentAsync(node, s.Storage!, "iso", ct); // CHANGED
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
        [HttpPost("RunQueue")]
        [IgnoreAntiforgeryToken]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<IActionResult> RunQueue(CancellationToken ct)
        {
            var started = await _runner.StartAsync(ct);
            return Ok(new { ok = true, started });
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

            return Ok(items);
        }

        /// <summary>
        /// Add a queued item.
        /// </summary>
        [HttpPost("Queue")]
        [Consumes(MediaTypeNames.Application.Json)]
        [IgnoreAntiforgeryToken] // same-site fetch JSON; no form token
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<IActionResult> QueueAdd([FromBody] QueueItemDto dto, CancellationToken ct)
        {
            if (dto == null)
                return BadRequest(new { ok = false, error = "Empty payload" });

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
                Status = "Queued",
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
            if (dto == null) return BadRequest(new { ok = false, error = "Empty payload" });

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
    }
}
