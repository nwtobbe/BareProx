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
using BareProx.Services;
using BareProx.Services.Netapp;
using BareProx.Services.Proxmox;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BareProx.Controllers
{
    public class WaflController : Controller
    {
        private readonly IDbContextFactory<ApplicationDbContext> _dbf;
        private readonly IDbContextFactory<QueryDbContext> _qdbf;

        private readonly INetappFlexCloneService _netappflexcloneService;
        private readonly INetappExportNFSService _netappExportNFSService;
        private readonly INetappSnapmirrorService _netappSnapmirrorService;
        private readonly INetappVolumeService _netappVolumeService;
        private readonly INetappSnapshotService _netappSnapshotService;
        private readonly ProxmoxService _proxmoxService;
        private readonly ILogger<WaflController> _log;

        public WaflController(
            IDbContextFactory<ApplicationDbContext> dbf,
            IDbContextFactory<QueryDbContext> qdbf,
            INetappFlexCloneService netappflexcloneService,
            INetappExportNFSService netappExportNFSService,
            INetappSnapmirrorService netappSnapmirrorService,
            INetappVolumeService netappVolumeService,
            INetappSnapshotService netappSnapshotService,
            ProxmoxService proxmoxService,
            ILogger<WaflController> log)
        {
            _dbf = dbf;
            _qdbf = qdbf;
            _netappflexcloneService = netappflexcloneService;
            _netappExportNFSService = netappExportNFSService;
            _netappSnapmirrorService = netappSnapmirrorService;
            _netappVolumeService = netappVolumeService;
            _netappSnapshotService = netappSnapshotService;
            _proxmoxService = proxmoxService;
            _log = log;
        }

        public async Task<IActionResult> Snapshots(int? clusterId, CancellationToken ct)
        {
            await using var db = await _dbf.CreateDbContextAsync(ct);
            await using var qdb = await _qdbf.CreateDbContextAsync(ct);

            var clusters = await db.ProxmoxClusters
                .Include(c => c.Hosts)
                .AsNoTracking()
                .ToListAsync(ct);

            if (clusters.Count == 0)
            {
                ViewBag.Warning = "No Proxmox clusters are configured.";
                return View(new List<NetappControllerTreeDto>());
            }

            var selectedCluster = clusterId.HasValue
                ? clusters.FirstOrDefault(c => c.Id == clusterId.Value) ?? clusters[0]
                : clusters[0];

            ViewBag.Clusters = clusters;
            ViewBag.SelectedClusterId = selectedCluster.Id;

            var netappControllers = await db.NetappControllers
                .AsNoTracking()
                .ToListAsync(ct);

            if (netappControllers.Count == 0)
            {
                ViewBag.Warning = "No NetApp controllers are configured.";
                return View(new List<NetappControllerTreeDto>());
            }

            var selectedVolumes = await db.SelectedNetappVolumes
                .AsNoTracking()
                .Where(v => v.Disabled != true)
                .ToListAsync(ct);

            var allVolumeNames = selectedVolumes
                .Select(v => v.VolumeName)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var indexedByVolume = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            if (allVolumeNames.Count > 0)
            {
                var snapMeta = await qdb.NetappSnapshots
                    .AsNoTracking()
                    .Where(s =>
                        allVolumeNames.Contains(s.PrimaryVolume) ||
                        allVolumeNames.Contains(s.SecondaryVolume))
                    .Select(s => new
                    {
                        s.JobId,
                        s.SnapshotName,
                        s.PrimaryVolume,
                        s.SecondaryVolume
                    })
                    .ToListAsync(ct);

                var jobIds = snapMeta
                    .Select(s => s.JobId)
                    .Distinct()
                    .ToList();

                if (jobIds.Count > 0)
                {
                    var indexedJobIds = await db.ProxmoxStorageDiskSnapshots
                        .AsNoTracking()
                        .Where(d => jobIds.Contains(d.JobId))
                        .Select(d => d.JobId)
                        .Distinct()
                        .ToListAsync(ct);

                    var indexedJobSet = new HashSet<int>(indexedJobIds);

                    foreach (var rec in snapMeta.Where(r => indexedJobSet.Contains(r.JobId)))
                    {
                        void Add(string? volName)
                        {
                            if (string.IsNullOrWhiteSpace(volName))
                                return;

                            if (!indexedByVolume.TryGetValue(volName, out var set))
                            {
                                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                indexedByVolume[volName] = set;
                            }

                            set.Add(rec.SnapshotName);
                        }

                        Add(rec.PrimaryVolume);
                        Add(rec.SecondaryVolume);
                    }
                }
            }

            var volumeLookup = selectedVolumes.ToLookup(v => v.NetappControllerId);
            var result = new List<NetappControllerTreeDto>();

            using var gate = new SemaphoreSlim(4);
            var tasks = new List<Task>();

            foreach (var controller in netappControllers)
            {
                var controllerDto = new NetappControllerTreeDto
                {
                    ControllerId = controller.Id,
                    ControllerName = controller.Hostname,
                    IsPrimary = controller.IsPrimary,
                    Svms = new List<NetappSvmDto>()
                };

                var groupedBySvm = volumeLookup[controller.Id].GroupBy(v => v.Vserver);
                foreach (var svmGroup in groupedBySvm)
                {
                    var svmDto = new NetappSvmDto
                    {
                        Name = svmGroup.Key,
                        Volumes = new List<NetappVolumeDto>()
                    };

                    foreach (var vol in svmGroup)
                    {
                        var volumeDto = new NetappVolumeDto
                        {
                            VolumeName = vol.VolumeName,
                            Vserver = vol.Vserver,
                            MountIp = vol.MountIp,
                            Uuid = vol.Uuid,
                            ClusterId = vol.NetappControllerId,
                            IsSelected = true
                        };

                        if (!string.IsNullOrWhiteSpace(vol.VolumeName) &&
                            indexedByVolume.TryGetValue(vol.VolumeName, out var snapSet))
                        {
                            volumeDto.IndexedSnapshotNames = snapSet;
                        }

                        svmDto.Volumes.Add(volumeDto);

                        tasks.Add(Task.Run(async () =>
                        {
                            await gate.WaitAsync(ct);
                            try
                            {
                                var snaps = await _netappSnapshotService.GetSnapshotsAsync(
                                    vol.NetappControllerId,
                                    vol.VolumeName,
                                    ct);

                                volumeDto.Snapshots = snaps?
                                    .Where(s => !string.IsNullOrWhiteSpace(s))
                                    .OrderByDescending(s => s, StringComparer.OrdinalIgnoreCase)
                                    .ToList()
                                    ?? new List<string>();
                            }
                            catch (Exception ex)
                            {
                                _log.LogWarning(ex,
                                    "Failed to list snapshots for controller {ControllerId}, volume {Volume}",
                                    vol.NetappControllerId, vol.VolumeName);
                                volumeDto.Snapshots = new List<string>();
                            }
                            finally
                            {
                                gate.Release();
                            }
                        }, ct));
                    }

                    controllerDto.Svms.Add(svmDto);
                }

                result.Add(controllerDto);
            }

            await Task.WhenAll(tasks);
            return View(result);
        }

        public async Task<IActionResult> SnapMirrorGraph(CancellationToken ct)
        {
            await using var db = await _dbf.CreateDbContextAsync(ct);
            await using var qdb = await _qdbf.CreateDbContextAsync(ct);

            var snapshotList = await qdb.NetappSnapshots.AsNoTracking().ToListAsync(ct);

            var policies = await db.SnapMirrorPolicies.AsNoTracking().ToListAsync(ct);
            var retentions = await db.SnapMirrorPolicyRetentions.AsNoTracking().ToListAsync(ct);
            var relationsRaw = await db.SnapMirrorRelations.AsNoTracking().ToListAsync(ct);

            static string Key(int cid, string? vol) => $"{cid}||{(vol ?? string.Empty).ToLowerInvariant()}";

            var enabledVolKeys = (await db.SelectedNetappVolumes
                    .AsNoTracking()
                    .Where(v => v.Disabled != true && v.VolumeName != null && v.VolumeName != "")
                    .Select(v => new { v.NetappControllerId, v.VolumeName })
                    .ToListAsync(ct))
                .Select(x => Key(x.NetappControllerId, x.VolumeName))
                .ToHashSet(StringComparer.Ordinal);

            var relationsFiltered = relationsRaw.Where(r =>
                enabledVolKeys.Contains(Key(r.SourceControllerId, r.SourceVolume)) &&
                enabledVolKeys.Contains(Key(r.DestinationControllerId, r.DestinationVolume))
            ).ToList();

            var policyByUuid = policies
                .Where(p => !string.IsNullOrEmpty(p.Uuid))
                .ToDictionary(p => p.Uuid!, p => p, StringComparer.OrdinalIgnoreCase);

            var retentionsByPolicyId = retentions
                .GroupBy(r => r.SnapMirrorPolicyId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var controllerNames = await db.NetappControllers
                .AsNoTracking()
                .ToDictionaryAsync(
                    c => c.Id,
                    c => string.IsNullOrWhiteSpace(c.Hostname) ? $"#{c.Id}" : c.Hostname,
                    ct);

            var relations = new List<SnapMirrorRelationGraphDto>();
            foreach (var rel in relationsFiltered)
            {
                policyByUuid.TryGetValue(rel.PolicyUuid ?? "", out var policy);
                var relRetentions = retentionsByPolicyId.GetValueOrDefault(policy?.Id ?? 0, new List<SnapMirrorPolicyRetention>());
                int R(string lbl) => relRetentions.FirstOrDefault(r => r.Label == lbl)?.Count ?? 0;
                string? locked = relRetentions.FirstOrDefault(r => r.Period != null)?.Period;

                relations.Add(new SnapMirrorRelationGraphDto
                {
                    SourceController = controllerNames.GetValueOrDefault(rel.SourceControllerId, $"#{rel.SourceControllerId}"),
                    DestinationController = controllerNames.GetValueOrDefault(rel.DestinationControllerId, $"#{rel.DestinationControllerId}"),
                    SourceVolume = rel.SourceVolume,
                    DestinationVolume = rel.DestinationVolume,
                    SourceControllerId = rel.SourceControllerId,
                    DestinationControllerId = rel.DestinationControllerId,
                    Health = rel.healthy ? "Healthy" : "Unhealthy",
                    LagTime = FormatIso8601Duration(rel.lag_time),
                    RelationUuid = rel.Uuid,
                    PolicyName = policy?.Name ?? "",
                    PolicyType = policy?.Type ?? "",
                    HourlyRetention = R("hourly"),
                    DailyRetention = R("daily"),
                    WeeklyRetention = R("weekly"),
                    LockedPeriod = FormatIso8601Duration(locked),
                });
            }

            foreach (var rel in relations)
            {
                var primarySnaps = snapshotList.Where(s =>
                    s.PrimaryControllerId == rel.SourceControllerId &&
                    string.Equals(s.PrimaryVolume, rel.SourceVolume, StringComparison.OrdinalIgnoreCase) &&
                    s.ExistsOnPrimary == true);

                rel.HourlySnapshotsPrimary = primarySnaps.Count(s => s.SnapmirrorLabel == "hourly");
                rel.DailySnapshotsPrimary = primarySnaps.Count(s => s.SnapmirrorLabel == "daily");
                rel.WeeklySnapshotsPrimary = primarySnaps.Count(s => s.SnapmirrorLabel == "weekly");

                var secondarySnaps = snapshotList.Where(s =>
                    s.SecondaryControllerId == rel.DestinationControllerId &&
                    string.Equals(s.SecondaryVolume, rel.DestinationVolume, StringComparison.OrdinalIgnoreCase) &&
                    s.ExistsOnSecondary == true);

                rel.HourlySnapshotsSecondary = secondarySnaps.Count(s => s.SnapmirrorLabel == "hourly");
                rel.DailySnapshotsSecondary = secondarySnaps.Count(s => s.SnapmirrorLabel == "daily");
                rel.WeeklySnapshotsSecondary = secondarySnaps.Count(s => s.SnapmirrorLabel == "weekly");
            }

            return View(relations);
        }

        [HttpGet]
        public async Task<IActionResult> GetSnapshotsForVolume(
            string volume,
            int ClusterId,
            CancellationToken ct)
        {
            var snapshots = await _netappSnapshotService.GetSnapshotsAsync(ClusterId, volume, ct);

            snapshots = snapshots?
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .OrderByDescending(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? new List<string>();

            return Json(new { snapshots });
        }

        [HttpGet]
        public async Task<IActionResult> GetNfsIps(
            string vserver,
            int controllerId,
            CancellationToken ct)
        {
            var ips = await _netappExportNFSService.GetNfsEnabledIpsAsync(controllerId, vserver, ct);
            return Json(new { ips });
        }

        [HttpPost]
        public async Task<IActionResult> MountSnapshot(MountSnapshotViewModel model, CancellationToken ct)
        {
            if (!ModelState.IsValid) return BadRequest("Invalid input.");

            string? cloneName = null;

            try
            {
                var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                cloneName = $"restore_{model.VolumeName}_{timestamp}";

                if (model.ControllerId <= 0)
                    return BadRequest("Invalid controller ID.");

                var result = await _netappflexcloneService.CloneVolumeFromSnapshotAsync(
                    model.VolumeName, model.SnapshotName, cloneName, model.ControllerId, ct);

                if (!result.Success)
                    return BadRequest("Failed to create FlexClone: " + result.Message);

                await using (var db = await _dbf.CreateDbContextAsync(ct))
                await using (var qdb = await _qdbf.CreateDbContextAsync(ct))
                {
                    if (model.IsSecondary)
                    {
                        var snapshotRecord = await qdb.NetappSnapshots
                            .AsNoTracking()
                            .FirstOrDefaultAsync(s =>
                                s.SnapshotName == model.SnapshotName &&
                                s.SecondaryVolume == model.VolumeName &&
                                s.SecondaryControllerId == model.ControllerId, ct);

                        int primaryControllerId;
                        string primaryVolumeName;

                        if (snapshotRecord != null)
                        {
                            primaryControllerId = snapshotRecord.PrimaryControllerId;
                            primaryVolumeName = snapshotRecord.PrimaryVolume;
                        }
                        else
                        {
                            var smr = await db.SnapMirrorRelations
                                .AsNoTracking()
                                .FirstOrDefaultAsync(r =>
                                    r.DestinationControllerId == model.ControllerId &&
                                    r.DestinationVolume == model.VolumeName, ct);

                            if (smr == null)
                            {
                                await TryDeleteCloneAsync(cloneName, model.ControllerId, ct);
                                return BadRequest($"No snapshot metadata or SnapMirrorRelation found for '{model.SnapshotName}' on '{model.VolumeName}'.");
                            }

                            primaryControllerId = smr.SourceControllerId;
                            primaryVolumeName = smr.SourceVolume;
                        }

                        var primaryMeta = await db.SelectedNetappVolumes
                            .AsNoTracking()
                            .Where(v => v.NetappControllerId == primaryControllerId &&
                                        v.VolumeName == primaryVolumeName &&
                                        v.Disabled != true)
                            .Select(v => new
                            {
                                v.ExportPolicyName,
                                v.Vserver
                            })
                            .FirstOrDefaultAsync(ct);

                        if (primaryMeta == null || string.IsNullOrWhiteSpace(primaryMeta.ExportPolicyName))
                        {
                            await TryDeleteCloneAsync(cloneName, model.ControllerId, ct);
                            return StatusCode(500, "Could not find export policy on primary volume.");
                        }

                        if (string.IsNullOrWhiteSpace(primaryMeta.Vserver))
                        {
                            await TryDeleteCloneAsync(cloneName, model.ControllerId, ct);
                            return StatusCode(500, "Could not determine primary SVM name.");
                        }

                        var secondarySvmName = await db.SelectedNetappVolumes
                            .AsNoTracking()
                            .Where(v => v.NetappControllerId == model.ControllerId &&
                                        v.VolumeName == model.VolumeName &&
                                        v.Disabled != true)
                            .Select(v => v.Vserver)
                            .FirstOrDefaultAsync(ct);

                        if (string.IsNullOrWhiteSpace(secondarySvmName))
                        {
                            await TryDeleteCloneAsync(cloneName, model.ControllerId, ct);
                            return StatusCode(500, "Could not determine secondary SVM name for clone.");
                        }

                        var copied = await _netappExportNFSService.EnsureExportPolicyExistsOnSecondaryAsync(
                            exportPolicyName: primaryMeta.ExportPolicyName!,
                            primaryControllerId: primaryControllerId,
                            secondaryControllerId: model.ControllerId,
                            primarySvmName: primaryMeta.Vserver!,
                            secondarySvmName: secondarySvmName,
                            ct: ct);

                        if (!copied)
                        {
                            await TryDeleteCloneAsync(cloneName, model.ControllerId, ct);
                            return StatusCode(500, "Failed to ensure export policy exists on secondary controller.");
                        }

                        var ok = await _netappExportNFSService.SetExportPolicyAsync(
                            cloneName, primaryMeta.ExportPolicyName!, model.ControllerId, ct);

                        if (!ok)
                        {
                            await TryDeleteCloneAsync(cloneName, model.ControllerId, ct);
                            return StatusCode(500, "Failed to set export policy on clone from primary info.");
                        }
                    }
                    else
                    {
                        var exported = await _netappExportNFSService.CopyExportPolicyAsync(
                            model.VolumeName, cloneName, model.ControllerId, ct);

                        if (!exported)
                        {
                            await TryDeleteCloneAsync(cloneName, model.ControllerId, ct);
                            return StatusCode(500, "Failed to apply export policy on cloned volume.");
                        }
                    }

                    var volumeInfo = await _netappVolumeService.LookupVolumeAsync(result.CloneVolumeName!, model.ControllerId, ct);
                    if (volumeInfo == null)
                    {
                        await TryDeleteCloneAsync(cloneName, model.ControllerId, ct);
                        return StatusCode(500, $"Failed to find UUID for cloned volume '{result.CloneVolumeName}'.");
                    }

                    var exportOk = await _netappExportNFSService.SetVolumeExportPathAsync(
                        volumeInfo.Uuid,
                        $"/{cloneName}",
                        model.ControllerId,
                        ct);

                    if (!exportOk)
                    {
                        await TryDeleteCloneAsync(cloneName, model.ControllerId, ct);
                        return StatusCode(500, "Failed to set export path on cloned volume.");
                    }

                    var cluster = await db.ProxmoxClusters
                        .Include(c => c.Hosts)
                        .AsNoTracking()
                        .FirstOrDefaultAsync(ct);

                    if (cluster == null)
                    {
                        await TryDeleteCloneAsync(cloneName, model.ControllerId, ct);
                        return NotFound("Proxmox cluster not found.");
                    }

                    var host = cluster.Hosts.FirstOrDefault();
                    if (host == null)
                    {
                        await TryDeleteCloneAsync(cloneName, model.ControllerId, ct);
                        return NotFound("No Proxmox hosts available in the cluster.");
                    }

                    var mounted = await _proxmoxService.MountNfsStorageViaApiAsync(
                        cluster,
                        node: host.Hostname!,
                        storageName: cloneName,
                        serverIp: model.MountIp,
                        exportPath: $"/{cloneName}",
                        snapshotChainActive: false,
                        ct: ct
                    );

                    if (!mounted)
                    {
                        await TryDeleteCloneAsync(cloneName, model.ControllerId, ct);
                        return StatusCode(500, "Failed to mount clone on Proxmox.");
                    }
                }

                TempData["Message"] = $"Snapshot {model.SnapshotName} cloned and mounted as {cloneName}.";
                return RedirectToAction("Snapshots");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "MountSnapshot failed for volume {Volume} snapshot {Snapshot}.",
                    model.VolumeName, model.SnapshotName);

                if (!string.IsNullOrWhiteSpace(cloneName))
                    await TryDeleteCloneAsync(cloneName, model.ControllerId, ct);

                return StatusCode(500, "Mount failed: " + ex.Message);
            }
        }

        [HttpPost]
        public async Task<IActionResult> UpdateSnapMirror(
            string relationUuid,
            CancellationToken ct)
        {
            try
            {
                var result = await _netappSnapmirrorService
                    .TriggerSnapMirrorUpdateAsync(relationUuid, ct);

                if (result)
                    return Json(new { success = true, message = "Update triggered." });

                return Json(new { success = false, message = "Failed to update SnapMirror relationship." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        public static string FormatIso8601Duration(string? isoDuration)
        {
            if (string.IsNullOrEmpty(isoDuration)) return "";
            var regex = new Regex(@"^P((?<days>\d+)D)?(T((?<hours>\d+)H)?((?<minutes>\d+)M)?((?<seconds>\d+)S)?)?$");
            var match = regex.Match(isoDuration);
            if (!match.Success) return isoDuration;

            var parts = new List<string>(4);
            if (int.TryParse(match.Groups["days"].Value, out var d) && d > 0) parts.Add($"{d}d");
            if (int.TryParse(match.Groups["hours"].Value, out var h) && h > 0) parts.Add($"{h}h");
            if (int.TryParse(match.Groups["minutes"].Value, out var m) && m > 0) parts.Add($"{m}m");
            if (int.TryParse(match.Groups["seconds"].Value, out var s) && s > 0) parts.Add($"{s}s");
            return string.Join(" ", parts);
        }

        [HttpGet]
        public async Task<IActionResult> GetSnapshotVmDisks(
            int clusterId,
            string volumeName,
            string snapshotName,
            CancellationToken ct)
        {
            await using var main = await _dbf.CreateDbContextAsync(ct);
            await using var qdb = await _qdbf.CreateDbContextAsync(ct);

            volumeName = volumeName?.Trim() ?? string.Empty;
            snapshotName = snapshotName?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(volumeName) ||
                string.IsNullOrWhiteSpace(snapshotName) ||
                clusterId <= 0)
            {
                return Json(new { vms = Array.Empty<object>() });
            }

            var snapMeta = await qdb.NetappSnapshots
                .AsNoTracking()
                .Where(s =>
                    s.SnapshotName == snapshotName &&
                    (s.PrimaryVolume == volumeName || s.SecondaryVolume == volumeName))
                .Select(s => new
                {
                    s.JobId,
                    s.PrimaryVolume,
                    s.SecondaryVolume
                })
                .ToListAsync(ct);

            if (snapMeta.Count == 0)
                return Json(new { vms = Array.Empty<object>() });

            var jobIds = snapMeta
                .Select(s => s.JobId)
                .Distinct()
                .ToList();

            if (jobIds.Count == 0)
                return Json(new { vms = Array.Empty<object>() });

            var clusterJobIds = await (
                from b in main.BackupRecords.AsNoTracking()
                join h in main.ProxmoxHosts.AsNoTracking()
                    on b.HostName equals h.Hostname
                where jobIds.Contains(b.JobId)
                      && h.ClusterId == clusterId
                select b.JobId
            )
            .Distinct()
            .ToListAsync(ct);

            if (clusterJobIds.Count == 0)
                return Json(new { vms = Array.Empty<object>() });

            var diskRows = await main.ProxmoxStorageDiskSnapshots
                .AsNoTracking()
                .Where(d =>
                    clusterJobIds.Contains(d.JobId) &&
                    d.ClusterId == clusterId)
                .ToListAsync(ct);

            if (diskRows.Count == 0)
                return Json(new { vms = Array.Empty<object>() });

            var vmNames = await main.JobVmResults
                .AsNoTracking()
                .Where(r => clusterJobIds.Contains(r.JobId))
                .GroupBy(r => r.VMID)
                .Select(g => new
                {
                    VmId = g.Key,
                    VmName = g.OrderByDescending(x => x.Id)
                              .Select(x => x.VmName)
                              .FirstOrDefault()
                })
                .ToListAsync(ct);

            var vmNameLookup = vmNames
                .Select(x => new
                {
                    VmId = Convert.ToInt32(x.VmId),
                    x.VmName
                })
                .Where(x => x.VmId != 0)
                .ToDictionary(
                    x => x.VmId,
                    x => string.IsNullOrWhiteSpace(x.VmName) ? $"VM {x.VmId}" : x.VmName!
                );

            var vms = diskRows
                .GroupBy(d => d.VMID)
                .Select(g =>
                {
                    var vmId = Convert.ToInt32(g.Key);
                    var disks = g
                        .OrderBy(x => x.VolId)
                        .Select(x => new
                        {
                            VolId = x.VolId,
                            ContentType = x.ContentType,
                            Format = x.Format,
                            SizeBytes = x.SizeBytes
                        })
                        .ToList();

                    return new
                    {
                        VmId = vmId,
                        VmName = vmNameLookup.TryGetValue(vmId, out var name) ? name : $"VM {vmId}",
                        Disks = disks
                    };
                })
                .Where(x => x.VmId != 0)
                .OrderBy(v => v.VmName)
                .ToList();

            return Json(new { vms });
        }

        [HttpPost]
        public async Task<IActionResult> PrepareAttachDisk(
            int clusterId,
            int targetVmId,
            string volumeName,
            string snapshotName,
            int controllerId,
            string sourceVolid,
            int? sourceVmId,
            string? busType,
            int? preferredIndex,
            long? sourceSizeBytes,
            string? sourceFormat,
            CancellationToken ct)
        {
            try
            {
                await using var db = await _dbf.CreateDbContextAsync(ct);
                await using var qdb = await _qdbf.CreateDbContextAsync(ct);

                var cluster = await db.ProxmoxClusters
                    .Include(c => c.Hosts)
                    .FirstOrDefaultAsync(c => c.Id == clusterId, ct);

                if (cluster == null)
                {
                    return Json(new { ok = false, error = $"Cluster {clusterId} not found." });
                }

                var invVm = await qdb.InventoryVms
                    .AsNoTracking()
                    .FirstOrDefaultAsync(v => v.ClusterId == clusterId && v.VmId == targetVmId, ct);

                if (invVm == null)
                {
                    return Json(new { ok = false, error = $"VM {targetVmId} not found in inventory." });
                }

                var nodeName = invVm.NodeName;
                if (string.IsNullOrWhiteSpace(nodeName))
                {
                    return Json(new { ok = false, error = $"VM {targetVmId} has no node name in inventory." });
                }

                var cfgJson = await _proxmoxService.GetVmConfigAsync(cluster, nodeName, targetVmId, ct);
                using var cfgDoc = JsonDocument.Parse(cfgJson);
                if (!cfgDoc.RootElement.TryGetProperty("config", out var cfgObj) ||
                    cfgObj.ValueKind != JsonValueKind.Object)
                {
                    return Json(new { ok = false, error = "VM config missing or invalid." });
                }

                var diskRegex = new Regex(@"^(scsi|virtio|ide|sata|efidisk|tpmstate)\d+$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

                var existing = new List<object>();
                var diskKeys = new List<string>();

                foreach (var prop in cfgObj.EnumerateObject())
                {
                    if (!diskRegex.IsMatch(prop.Name))
                        continue;

                    var val = prop.Value.GetString() ?? string.Empty;
                    existing.Add(new { key = prop.Name, value = val });
                    diskKeys.Add(prop.Name);
                }

                var diskKeySet = new HashSet<string>(diskKeys, StringComparer.OrdinalIgnoreCase);

                var validBuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "scsi", "virtio", "ide", "sata"
                };

                string bus = "scsi";
                if (!string.IsNullOrWhiteSpace(busType) && validBuses.Contains(busType))
                {
                    bus = busType.ToLowerInvariant();
                }

                int maxKeyIndexForBus = -1;

                if (diskKeys.Count > 0)
                {
                    var keyInfos = diskKeys
                        .Select(k =>
                        {
                            var m = Regex.Match(k, @"^(scsi|virtio|ide|sata|efidisk|tpmstate)(\d+)$",
                                RegexOptions.IgnoreCase);
                            var b = m.Success ? m.Groups[1].Value.ToLowerInvariant() : "scsi";
                            var idx = m.Success && int.TryParse(m.Groups[2].Value, out var i) ? i : 0;
                            return new { Bus = b, Index = idx };
                        })
                        .ToList();

                    if (string.IsNullOrWhiteSpace(busType) || !validBuses.Contains(bus))
                    {
                        var majority = keyInfos
                            .Where(x => validBuses.Contains(x.Bus))
                            .GroupBy(x => x.Bus)
                            .OrderByDescending(g => g.Count())
                            .FirstOrDefault();

                        if (majority != null)
                            bus = majority.Key;
                    }

                    maxKeyIndexForBus = keyInfos
                        .Where(x => string.Equals(x.Bus, bus, StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.Index)
                        .DefaultIfEmpty(-1)
                        .Max();
                }

                int nextKeyIndex = maxKeyIndexForBus + 1;

                if (preferredIndex.HasValue && preferredIndex.Value >= 0)
                {
                    var candidateKey = $"{bus}{preferredIndex.Value}";
                    if (!diskKeySet.Contains(candidateKey))
                        nextKeyIndex = preferredIndex.Value;
                }

                var suggestedKey = $"{bus}{nextKeyIndex}";

                var filePartRaw = sourceVolid.Split(':', 2).Length == 2
                    ? sourceVolid.Split(':', 2)[1]
                    : sourceVolid;

                var basePathPart = filePartRaw.Split(',', 2)[0];
                var ext = Path.GetExtension(basePathPart);
                if (string.IsNullOrWhiteSpace(ext))
                    ext = ".qcow2";

                int maxDiskNumber = -1;
                var diskFileRegex = new Regex($@"vm-{targetVmId}-disk-(\d+)\.",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

                foreach (var prop in cfgObj.EnumerateObject())
                {
                    if (!diskRegex.IsMatch(prop.Name))
                        continue;

                    var val = prop.Value.GetString() ?? string.Empty;
                    var colonIdx = val.IndexOf(':');
                    if (colonIdx < 0)
                        continue;

                    var afterColon = val.Substring(colonIdx + 1);
                    var pathPart = afterColon.Split(',', 2)[0];

                    var m = diskFileRegex.Match(pathPart);
                    if (m.Success && int.TryParse(m.Groups[1].Value, out var diskNo))
                    {
                        if (diskNo > maxDiskNumber)
                            maxDiskNumber = diskNo;
                    }
                }

                var newDiskNumber = maxDiskNumber + 1;
                if (newDiskNumber < 0) newDiskNumber = 0;

                var suggestedFileName = $"vm-{targetVmId}-disk-{newDiskNumber}{ext}";
                var suggestedRelPath = $"{targetVmId}/{suggestedFileName}";

                var options = new List<string> { "backup=0" };

                if (!string.IsNullOrWhiteSpace(sourceFormat))
                    options.Add($"format={sourceFormat}");

                if (sourceSizeBytes.GetValueOrDefault() > 0)
                {
                    var sizeGiB = sourceSizeBytes.Value / (1024d * 1024d * 1024d);
                    var sizeGiBInt = (int)Math.Round(sizeGiB, MidpointRounding.AwayFromZero);
                    if (sizeGiBInt > 0)
                        options.Add($"size={sizeGiBInt}G");
                }

                var optionsStr = string.Join(",", options);
                var suggestedValue = $"<clone-storage>:{suggestedRelPath},{optionsStr}";

                return Json(new
                {
                    ok = true,
                    existingDisks = existing,
                    suggested = new { key = suggestedKey, value = suggestedValue }
                });
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "PrepareAttachDisk failed for cluster {ClusterId}, vm {VmId}.",
                    clusterId, targetVmId);

                return Json(new { ok = false, error = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> MountDiskFromSnapshot(
            int clusterId,
            int targetVmId,
            string volumeName,
            string snapshotName,
            int controllerId,
            string sourceVolid,
            int? sourceVmId,
            string diskKey,
            string diskValue,
            CancellationToken ct)
        {
            string? cloneName = null;

            try
            {
                if (string.IsNullOrWhiteSpace(volumeName) ||
                    string.IsNullOrWhiteSpace(snapshotName) ||
                    string.IsNullOrWhiteSpace(sourceVolid) ||
                    string.IsNullOrWhiteSpace(diskKey) ||
                    string.IsNullOrWhiteSpace(diskValue))
                {
                    return Json(new { ok = false, error = "Missing required parameters." });
                }

                await using var db = await _dbf.CreateDbContextAsync(ct);
                await using var qdb = await _qdbf.CreateDbContextAsync(ct);

                var cluster = await db.ProxmoxClusters
                    .Include(c => c.Hosts)
                    .FirstOrDefaultAsync(c => c.Id == clusterId, ct);

                if (cluster == null)
                    return Json(new { ok = false, error = $"Cluster {clusterId} not found." });

                var invVm = await qdb.InventoryVms
                    .AsNoTracking()
                    .FirstOrDefaultAsync(v => v.ClusterId == clusterId && v.VmId == targetVmId, ct);

                if (invVm == null)
                    return Json(new { ok = false, error = $"VM {targetVmId} not found in inventory." });

                var nodeName = invVm.NodeName;
                if (string.IsNullOrWhiteSpace(nodeName))
                    return Json(new { ok = false, error = "Target VM node name missing in inventory." });

                var hostForStatus = cluster.Hosts.FirstOrDefault();
                if (hostForStatus == null)
                    return Json(new { ok = false, error = "No Proxmox hosts available in the cluster." });

                cloneName = $"attach_{targetVmId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
                var cloneResult = await _netappflexcloneService.CloneVolumeFromSnapshotAsync(
                    volumeName,
                    snapshotName,
                    cloneName,
                    controllerId,
                    ct);

                if (!cloneResult.Success)
                    return Json(new { ok = false, error = $"FlexClone failed: {cloneResult.Message}" });

                var controllerEntity = await db.NetappControllers
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == controllerId, ct);

                var isSecondary = controllerEntity?.IsPrimary == false;

                if (isSecondary)
                {
                    var snapshotRecord = await qdb.NetappSnapshots
                        .AsNoTracking()
                        .Where(s =>
                            s.SnapshotName == snapshotName &&
                            s.SecondaryControllerId == controllerId &&
                            s.SecondaryVolume == volumeName)
                        .OrderByDescending(s => s.Id)
                        .FirstOrDefaultAsync(ct);

                    int primaryControllerId;
                    string primaryVolumeName;

                    if (snapshotRecord != null)
                    {
                        primaryControllerId = snapshotRecord.PrimaryControllerId;
                        primaryVolumeName = snapshotRecord.PrimaryVolume;
                    }
                    else
                    {
                        var smr = await db.SnapMirrorRelations
                            .AsNoTracking()
                            .FirstOrDefaultAsync(r =>
                                r.DestinationControllerId == controllerId &&
                                r.DestinationVolume == volumeName, ct);

                        if (smr == null)
                        {
                            await TryDeleteCloneAsync(cloneName, controllerId, ct);
                            return Json(new
                            {
                                ok = false,
                                error = $"No snapshot metadata or SnapMirrorRelation found for '{snapshotName}' on '{volumeName}'."
                            });
                        }

                        primaryControllerId = smr.SourceControllerId;
                        primaryVolumeName = smr.SourceVolume;
                    }

                    var primaryMeta = await db.SelectedNetappVolumes
                        .AsNoTracking()
                        .Where(v => v.NetappControllerId == primaryControllerId &&
                                    v.VolumeName == primaryVolumeName &&
                                    v.Disabled != true)
                        .Select(v => new
                        {
                            v.ExportPolicyName,
                            v.Vserver
                        })
                        .FirstOrDefaultAsync(ct);

                    if (primaryMeta == null || string.IsNullOrWhiteSpace(primaryMeta.ExportPolicyName))
                    {
                        await TryDeleteCloneAsync(cloneName, controllerId, ct);
                        return Json(new { ok = false, error = "Could not find export policy on primary volume." });
                    }

                    if (string.IsNullOrWhiteSpace(primaryMeta.Vserver))
                    {
                        await TryDeleteCloneAsync(cloneName, controllerId, ct);
                        return Json(new { ok = false, error = "Could not determine primary SVM name." });
                    }

                    var secondarySvmName = await db.SelectedNetappVolumes
                        .AsNoTracking()
                        .Where(v => v.NetappControllerId == controllerId &&
                                    v.VolumeName == volumeName &&
                                    v.Disabled != true)
                        .Select(v => v.Vserver)
                        .FirstOrDefaultAsync(ct);

                    if (string.IsNullOrWhiteSpace(secondarySvmName))
                    {
                        await TryDeleteCloneAsync(cloneName, controllerId, ct);
                        return Json(new { ok = false, error = "Could not determine SVM name for clone on secondary." });
                    }

                    var copied = await _netappExportNFSService.EnsureExportPolicyExistsOnSecondaryAsync(
                        exportPolicyName: primaryMeta.ExportPolicyName!,
                        primaryControllerId: primaryControllerId,
                        secondaryControllerId: controllerId,
                        primarySvmName: primaryMeta.Vserver!,
                        secondarySvmName: secondarySvmName,
                        ct: ct);

                    if (!copied)
                    {
                        await TryDeleteCloneAsync(cloneName, controllerId, ct);
                        return Json(new { ok = false, error = "Failed to ensure export policy exists on secondary controller." });
                    }

                    var okPolicy = await _netappExportNFSService.SetExportPolicyAsync(
                        cloneName,
                        primaryMeta.ExportPolicyName!,
                        controllerId,
                        ct);

                    if (!okPolicy)
                    {
                        await TryDeleteCloneAsync(cloneName, controllerId, ct);
                        return Json(new { ok = false, error = "Failed to set export policy on clone from primary info." });
                    }
                }
                else
                {
                    var exported = await _netappExportNFSService.CopyExportPolicyAsync(
                        volumeName,
                        cloneName,
                        controllerId,
                        ct);

                    if (!exported)
                    {
                        await TryDeleteCloneAsync(cloneName, controllerId, ct);
                        return Json(new { ok = false, error = "Failed to apply export policy on cloned volume." });
                    }
                }

                var volInfo = await _netappVolumeService.LookupVolumeAsync(cloneName, controllerId, ct)
                             ?? throw new InvalidOperationException($"UUID not found for clone '{cloneName}'.");

                var exportPath = $"/{cloneName}";
                var exportOk = await _netappExportNFSService.SetVolumeExportPathAsync(
                    volInfo.Uuid,
                    exportPath,
                    controllerId,
                    ct);

                if (!exportOk)
                {
                    await TryDeleteCloneAsync(cloneName, controllerId, ct);
                    return Json(new { ok = false, error = "Failed to set export path on cloned volume." });
                }

                var mounts = await _netappVolumeService.GetVolumesWithMountInfoAsync(controllerId, ct);
                var cloneMount = mounts.FirstOrDefault(m =>
                    m.VolumeName.Equals(cloneName, StringComparison.OrdinalIgnoreCase));

                if (cloneMount == null)
                {
                    await TryDeleteCloneAsync(cloneName, controllerId, ct);
                    return Json(new { ok = false, error = $"Mount info not found for clone '{cloneName}'." });
                }

                var mountOk = await _proxmoxService.MountNfsStorageViaApiAsync(
                    cluster,
                    nodeName,
                    cloneName,
                    cloneMount.MountIp,
                    exportPath,
                    snapshotChainActive: false,
                    ct: ct);

                if (!mountOk)
                {
                    await TryDeleteCloneAsync(cloneName, controllerId, ct);
                    return Json(new { ok = false, error = "Failed to mount clone on Proxmox node." });
                }

                var srcPartRaw = sourceVolid.Split(':', 2).Length == 2
                    ? sourceVolid.Split(':', 2)[1]
                    : sourceVolid;

                srcPartRaw = srcPartRaw.Split(',', 2)[0];
                var srcSegments = srcPartRaw.Split('/', 2);
                if (srcSegments.Length < 2)
                {
                    return Json(new { ok = false, error = $"Source volid has unexpected format: {sourceVolid}" });
                }

                var srcVmFolder = srcSegments[0];
                var srcFileName = srcSegments[1];

                var commaIdx = diskValue.IndexOf(',');
                var valueHead = commaIdx >= 0 ? diskValue.Substring(0, commaIdx) : diskValue;
                var tailOptions = commaIdx >= 0 ? diskValue.Substring(commaIdx + 1) : "backup=0";

                var headParts = valueHead.Split(':', 2);
                if (headParts.Length < 2)
                {
                    return Json(new { ok = false, error = $"Disk value has unexpected format: {diskValue}" });
                }

                var dstRelPath = headParts[1];
                var dstSegments = dstRelPath.Split('/', 2);
                if (dstSegments.Length < 2)
                {
                    return Json(new { ok = false, error = $"Disk value path has unexpected format: {dstRelPath}" });
                }

                var dstVmFolder = dstSegments[0];
                var dstFileName = dstSegments[1];

                var symlinkResult = await _proxmoxService.PrepareAttachSymlinkOnCloneAsync(
                    nodeName,
                    cloneName,
                    srcVmFolder,
                    dstVmFolder,
                    srcFileName,
                    dstFileName,
                    ct);

                if (!symlinkResult.Ok)
                {
                    return Json(new
                    {
                        ok = false,
                        error = $"Failed to prepare attach symlink on cloned storage. Disk not attached. {symlinkResult.Error}"
                    });
                }

                var finalDiskValue = $"{cloneName}:{dstRelPath},{tailOptions}";

                var attachOk = await _proxmoxService.AttachDiskToVmAsync(
                    cluster,
                    nodeName,
                    targetVmId,
                    diskKey,
                    finalDiskValue,
                    ct);

                if (!attachOk)
                {
                    return Json(new
                    {
                        ok = false,
                        error = "Disk attach failed via Proxmox API (storage is still mounted; manual cleanup may be required)."
                    });
                }

                return Json(new
                {
                    ok = true,
                    message = $"Disk attached as {diskKey}: {finalDiskValue}"
                });
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "MountDiskFromSnapshot failed for cluster {ClusterId}, vm {VmId}.",
                    clusterId, targetVmId);

                if (!string.IsNullOrWhiteSpace(cloneName))
                    await TryDeleteCloneAsync(cloneName, controllerId, ct);

                return Json(new { ok = false, error = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetClusterVms(int clusterId, CancellationToken ct)
        {
            if (clusterId <= 0)
                return Json(new { vms = Array.Empty<object>() });

            await using var qdb = await _qdbf.CreateDbContextAsync(ct);

            var vms = await qdb.InventoryVms
                .AsNoTracking()
                .Where(v => v.ClusterId == clusterId)
                .OrderBy(v => v.NodeName)
                .ThenBy(v => v.VmId)
                .Select(v => new
                {
                    vmId = v.VmId,
                    name = string.IsNullOrWhiteSpace(v.Name) ? $"VM {v.VmId}" : v.Name,
                    nodeName = v.NodeName ?? string.Empty
                })
                .ToListAsync(ct);

            return Json(new { vms });
        }

        private static string NormalizeBusType(string? busType)
        {
            if (string.IsNullOrWhiteSpace(busType))
                return string.Empty;

            var b = busType.Trim().ToLowerInvariant();
            return b switch
            {
                "scsi" => "scsi",
                "sata" => "sata",
                "ide" => "ide",
                "virtio" => "virtio",
                _ => string.Empty
            };
        }

        private static string RewriteSnapshotFilePart(string filePart, int targetVmId, int diskIndex)
        {
            if (string.IsNullOrWhiteSpace(filePart))
                return filePart;

            filePart = Regex.Replace(
                filePart,
                @"vm-\d+",
                $"vm-{targetVmId}",
                RegexOptions.IgnoreCase);

            var diskRegex = new Regex(@"-disk-(\d+)", RegexOptions.IgnoreCase);
            if (diskRegex.IsMatch(filePart))
            {
                filePart = diskRegex.Replace(filePart, $"-disk-{diskIndex}", 1);
            }

            return filePart;
        }

        private async Task TryDeleteCloneAsync(string cloneName, int controllerId, CancellationToken ct)
        {
            try
            {
                await _netappVolumeService.DeleteVolumeAsync(cloneName, controllerId, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to delete clone '{CloneName}' on controller {ControllerId}.", cloneName, controllerId);
            }
        }
    }
}