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
using BareProx.Services.Proxmox;
using BareProx.Services.Proxmox.Authentication;
using BareProx.Services.Proxmox.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace BareProx.Controllers
{
    public class ProxmoxController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IQueryDbFactory _qdbf;
        private readonly ILogger<ProxmoxController> _logger;
        private readonly IProxmoxAuthenticator _auth;
        private readonly IProxmoxHelpersService _helpers;

        public ProxmoxController(
            ApplicationDbContext context,
            IQueryDbFactory qdbf,
            ILogger<ProxmoxController> logger,
            IProxmoxAuthenticator auth,
            IProxmoxHelpersService helpers)
        {
            _context = context;
            _qdbf = qdbf;
            _logger = logger;
            _auth = auth;
            _helpers = helpers;
        }

        // --------------------------------------------------------------------
        // List VMs for selected storages
        // --------------------------------------------------------------------
        // Optional clusterId avoids accidentally binding to "first" cluster.
        public async Task<IActionResult> ListVMs(int? clusterId, CancellationToken ct)
        {
            // 1) Load target cluster (no tracking)
            var clusterQuery = _context.ProxmoxClusters.AsNoTracking();

            var cluster = clusterId is int requestedClusterId
                ? await clusterQuery.FirstOrDefaultAsync(c => c.Id == requestedClusterId, ct)
                : await clusterQuery.FirstOrDefaultAsync(ct);

            if (cluster == null)
            {
                ViewBag.Warning = "No Proxmox clusters are configured.";
                return View(new List<StorageWithVMsDto>());
            }

            // 2) Which storages are selected (authoritative selection)
            var selectedStorageNames = await _context.SelectedStorages
                .AsNoTracking()
                .Where(s => s.ClusterId == cluster.Id)
                .Select(s => s.StorageIdentifier)
                .ToListAsync(ct);

            if (selectedStorageNames.Count == 0)
            {
                ViewBag.Warning = "No Proxmox storage has been selected for backup for this cluster.";
                return View(new List<StorageWithVMsDto>());
            }

            var selectedStorageSet = new HashSet<string>(selectedStorageNames, StringComparer.OrdinalIgnoreCase);

            await using var qdb = await _qdbf.CreateAsync(ct);

            // 3) Candidate storages from inventory (must be image-capable, and in selection)
            var invStorages = await qdb.InventoryStorages
                .AsNoTracking()
                .Where(s => s.ClusterId == cluster.Id
                         && s.IsImageCapable
                         && selectedStorageSet.Contains(s.StorageId))
                .ToListAsync(ct);

            if (invStorages.Count == 0)
            {
                ViewBag.Warning = "Selected storages were not found in the inventory yet. Give the collector a minute or two.";
                return View(new List<StorageWithVMsDto>());
            }

            // 4) VM-disks for those storages -> collect VM IDs per storage
            var storageIds = invStorages.Select(s => s.StorageId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var diskPairs = await qdb.InventoryVmDisks
                .AsNoTracking()
                .Where(d => d.ClusterId == cluster.Id && storageIds.Contains(d.StorageId))
                .Select(d => new { d.StorageId, d.VmId })
                .ToListAsync(ct);

            // Build storage -> distinct VM IDs
            var vmIdsByStorage = diskPairs
                .GroupBy(x => x.StorageId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => x.VmId).Distinct().ToList(),
                    StringComparer.OrdinalIgnoreCase);

            // Union of all VM IDs we need details for
            var allVmIds = vmIdsByStorage.Values.SelectMany(x => x).Distinct().ToList();

            // Fetch VM details
            var invVms = await qdb.InventoryVms
                .AsNoTracking()
                .Where(v => v.ClusterId == cluster.Id && allVmIds.Contains(v.VmId))
                .ToDictionaryAsync(v => v.VmId, ct);

            // ---------- Helper normalizers ----------
            static string Nx(string? s) => (s ?? "").Trim();

            static string NormPath(string? p)
            {
                p = Nx(p);
                if (string.IsNullOrEmpty(p)) return p;
                var q = p.Replace('\\', '/');
                if (!q.StartsWith('/')) q = "/" + q;
                if (q.Length > 1 && q.EndsWith('/')) q = q.TrimEnd('/');
                return q;
            }

            static IEnumerable<string> SplitIps(string? ips)
                => Nx(ips).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                          .Select(x => x.Trim());

            // 5) Build lookup: (Server IP, Export/JunctionPath) -> InventoryNetappVolume
            var invNavByServerExport =
                new Dictionary<(string Server, string Export), InventoryNetappVolume>();

            var allInvNav = await qdb.InventoryNetappVolumes
                .AsNoTracking()
                .ToListAsync(ct);

            foreach (var nav in allInvNav)
            {
                var junc = NormPath(nav.JunctionPath);
                if (string.IsNullOrEmpty(junc)) continue;

                foreach (var ip in SplitIps(nav.NfsIps))
                {
                    var key = (Server: ip, Export: junc);
                    // prefer first; if duplicate keys appear, prefer primary
                    if (!invNavByServerExport.ContainsKey(key))
                    {
                        invNavByServerExport[key] = nav;
                    }
                    else
                    {
                        var existing = invNavByServerExport[key];
                        if ((nav.IsPrimary == true) && (existing.IsPrimary != true))
                            invNavByServerExport[key] = nav;
                    }
                }
            }

            // 6) SelectedNetappVolumes: build maps by UUID (preferred) and by VolumeName (fallback)
            var selRows = await _context.SelectedNetappVolumes
                .AsNoTracking()
                .Where(v => v.Disabled != true)
                .Select(v => new
                {
                    v.Id,
                    v.NetappControllerId,
                    Uuid = v.Uuid,
                    v.VolumeName
                })
                .ToListAsync(ct);

            var selByUuid = new Dictionary<(int ControllerId, string Uuid), int>();
            var selByName = new Dictionary<(int ControllerId, string VolumeName), int>();

            foreach (var r in selRows)
            {
                if (!string.IsNullOrWhiteSpace(r.Uuid))
                {
                    var k = (r.NetappControllerId, r.Uuid!);
                    if (!selByUuid.ContainsKey(k)) selByUuid[k] = r.Id;
                }
                if (!string.IsNullOrWhiteSpace(r.VolumeName))
                {
                    var k = (r.NetappControllerId, r.VolumeName);
                    if (!selByName.ContainsKey(k)) selByName[k] = r.Id;
                }
            }

            // 7) Replication map (primary -> secondary) using UUIDs
            var allUuids = invStorages
                .Select(s => s.NetappVolumeUuid)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var repRows = await qdb.InventoryVolumeReplications
                .AsNoTracking()
                .Where(r => allUuids.Contains(r.PrimaryVolumeUuid))
                .Select(r => new { r.PrimaryVolumeUuid })
                .ToListAsync(ct);

            var replicablePrimary = new HashSet<string>(
                repRows.Select(r => r.PrimaryVolumeUuid),
                StringComparer.OrdinalIgnoreCase);

            // 8) Build DTOs
            var model = new List<StorageWithVMsDto>(invStorages.Count);

            foreach (var s in invStorages.OrderBy(x => x.StorageId, StringComparer.OrdinalIgnoreCase))
            {
                var storageName = s.StorageId;

                // VMs for this storage
                var vmList = new List<ProxmoxVM>();
                if (vmIdsByStorage.TryGetValue(storageName, out var vmIds) && vmIds.Count > 0)
                {
                    foreach (var id in vmIds)
                    {
                        if (invVms.TryGetValue(id, out var v))
                        {
                            vmList.Add(new ProxmoxVM
                            {
                                Id = v.VmId,
                                Name = string.IsNullOrWhiteSpace(v.Name) ? $"VM {v.VmId}" : v.Name,
                                HostName = v.NodeName,
                                HostAddress = null // not stored in inventory; optional
                            });
                        }
                    }
                }

                // Resolve NetApp mapping via (Server, Export) → (NfsIps, JunctionPath)
                int controllerId = 0;
                bool locking = false;
                string? volumeUuid = null;
                string? resolvedVolumeName = null;

                var serverKey = Nx(s.Server);
                var exportKey = NormPath(s.Export);

                if (!string.IsNullOrEmpty(serverKey) &&
                    !string.IsNullOrEmpty(exportKey) &&
                    invNavByServerExport.TryGetValue((serverKey, exportKey), out var matchedNav))
                {
                    controllerId = matchedNav.NetappControllerId;
                    locking = matchedNav.SnapshotLockingEnabled == true;
                    volumeUuid = matchedNav.VolumeUuid;
                    resolvedVolumeName = matchedNav.VolumeName;
                }
                else if (!string.IsNullOrWhiteSpace(s.NetappVolumeUuid))
                {
                    // fallback: use previously stored UUID on the inventory storage
                    var nav = allInvNav.FirstOrDefault(v =>
                        string.Equals(v.VolumeUuid, s.NetappVolumeUuid, StringComparison.OrdinalIgnoreCase));
                    if (nav is not null)
                    {
                        controllerId = nav.NetappControllerId;
                        locking = nav.SnapshotLockingEnabled == true;
                        volumeUuid = nav.VolumeUuid;
                        resolvedVolumeName = nav.VolumeName;
                    }
                }

                // Decide SelectedNetappVolumeId
                int? selectedId = null;
                if (controllerId != 0)
                {
                    // prefer UUID mapping
                    if (!string.IsNullOrWhiteSpace(volumeUuid) &&
                        selByUuid.TryGetValue((controllerId, volumeUuid), out var viaUuid))
                    {
                        selectedId = viaUuid;
                    }
                    else
                    {
                        // fallback by actual NetApp VolumeName, then legacy by Proxmox storage id
                        if (!string.IsNullOrWhiteSpace(resolvedVolumeName) &&
                            selByName.TryGetValue((controllerId, resolvedVolumeName), out var viaName))
                        {
                            selectedId = viaName;
                        }
                        else if (selByName.TryGetValue((controllerId, storageName), out var viaLegacyName))
                        {
                            selectedId = viaLegacyName;
                        }
                    }
                }

                var dto = new StorageWithVMsDto
                {
                    StorageName = storageName,
                    VMs = vmList,
                    ClusterId = cluster.Id,
                    NetappControllerId = controllerId,
                    SelectedNetappVolumeId = selectedId,
                    SnapshotLockingEnabled = locking,
                    IsReplicable = !string.IsNullOrWhiteSpace(volumeUuid)
                                   && replicablePrimary.Contains(volumeUuid),
                    VolumeUuid = volumeUuid
                };

                // Only add non-empty storages (keep old behavior)
                if (dto.VMs.Count > 0)
                    model.Add(dto);
            }

            if (model.Count == 0)
            {
                ViewBag.Warning = "No VMs found on the selected storage.";
                return View(model);
            }

            return View(model);
        }
    }
}
