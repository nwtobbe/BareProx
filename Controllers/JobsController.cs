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
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Controllers
{
    public class JobsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IAppTimeZoneService _tz;

        public JobsController(ApplicationDbContext context, IAppTimeZoneService tz)
        {
            _context = context;
            _tz = tz;
        }

        public async Task<IActionResult> Index(CancellationToken ct)
        {
            var jobs = await _context.Jobs
                .OrderByDescending(j => j.StartedAt)
                .ToListAsync(ct);

            var vm = jobs.Select(j => new JobViewModel
            {
                Id = j.Id,
                Type = j.Type,
                RelatedVm = j.RelatedVm,
                Status = j.Status,
                ErrorMessage = j.ErrorMessage,
                StartedAtLocal = _tz.ConvertUtcToApp(j.StartedAt),
                CompletedAtLocal = j.CompletedAt.HasValue
                    ? _tz.ConvertUtcToApp(j.CompletedAt.Value)
                    : (DateTime?)null
            }).ToList();

            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Cancel(int id, CancellationToken ct)
        {
            var job = await _context.Jobs.FindAsync(new object[] { id }, ct);
            if (job == null || job.Status is "Completed" or "Cancelled") return NotFound();

            job.Status = "Cancelled";
            await _context.SaveChangesAsync(ct);

            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<IActionResult> Table(
             string status = "",
             string search = "",
             string sortColumn = "StartedAt",
             bool asc = false,
             CancellationToken ct = default)
        {
            var query = _context.Jobs.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(status))
                query = query.Where(j => j.Status == status);

            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(j => j.Type.Contains(search) || j.RelatedVm.Contains(search));

            query = (sortColumn, asc) switch
            {
                ("Type", true) => query.OrderBy(j => j.Type),
                ("Type", false) => query.OrderByDescending(j => j.Type),
                ("RelatedVm", true) => query.OrderBy(j => j.RelatedVm),
                ("RelatedVm", false) => query.OrderByDescending(j => j.RelatedVm),
                ("Status", true) => query.OrderBy(j => j.Status),
                ("Status", false) => query.OrderByDescending(j => j.Status),
                ("CompletedAt", true) => query.OrderBy(j => j.CompletedAt),
                ("CompletedAt", false) => query.OrderByDescending(j => j.CompletedAt),
                _ when asc => query.OrderBy(j => j.StartedAt),
                _ => query.OrderByDescending(j => j.StartedAt)
            };

            var rawJobs = await query
                .Select(j => new
                {
                    j.Id,
                    j.Type,
                    j.RelatedVm,
                    j.Status,
                    j.ErrorMessage,
                    StartedAtUtc = j.StartedAt,
                    CompletedAtUtc = j.CompletedAt
                })
                .ToListAsync(ct);

            var vmList = rawJobs.Select(j => new JobViewModel
            {
                Id = j.Id,
                Type = j.Type,
                RelatedVm = j.RelatedVm,
                Status = j.Status,
                ErrorMessage = j.ErrorMessage,
                StartedAtLocal = _tz.ConvertUtcToApp(j.StartedAtUtc),
                CompletedAtLocal = j.CompletedAtUtc.HasValue
                    ? _tz.ConvertUtcToApp(j.CompletedAtUtc.Value)
                    : (DateTime?)null
            }).ToList();

            return PartialView("_JobsTable", vmList);
        }

        // Full page details (optional fallback / deep-link)
        [HttpGet]
        public async Task<IActionResult> Details(int id, CancellationToken ct)
        {
            var model = await LoadJobDetailsAsync(id, ct);
            if (model is null) return NotFound();
            return View(model); // Create Views/Jobs/Details.cshtml if you want a full-page view
        }

        // Modal content (AJAX)
        [HttpGet]
        public async Task<IActionResult> DetailsModal(int id, CancellationToken ct)
        {
            var model = await LoadJobDetailsAsync(id, ct);
            if (model is null) return NotFound();
            return PartialView("_JobDetailsModal", model); // Views/Jobs/_JobDetailsModal.cshtml
        }

        // Shared loader for both views
        private async Task<JobDetailsViewModel?> LoadJobDetailsAsync(int id, CancellationToken ct)
        {
            var job = await _context.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
            if (job == null) return null;

            // 1) Load VM results
            var vmResults = await _context.JobVmResults
                .AsNoTracking()
                .Where(v => v.JobId == id)
                .OrderBy(v => v.VMID)
                .Select(v => new JobVmResultViewModel
                {
                    Id = v.Id,
                    JobId = v.JobId,
                    VMID = v.VMID,
                    VmName = v.VmName,
                    HostName = v.HostName,
                    StorageName = v.StorageName,
                    Status = v.Status,
                    Reason = v.Reason,
                    ErrorMessage = v.ErrorMessage,
                    WasRunning = v.WasRunning,
                    IoFreezeAttempted = v.IoFreezeAttempted,
                    IoFreezeSucceeded = v.IoFreezeSucceeded,
                    SnapshotRequested = v.SnapshotRequested,
                    SnapshotTaken = v.SnapshotTaken,
                    ProxmoxSnapshotName = v.ProxmoxSnapshotName,
                    SnapshotUpid = v.SnapshotUpid,
                    StartedAtLocal = _tz.ConvertUtcToApp(v.StartedAtUtc),
                    CompletedAtLocal = v.CompletedAtUtc.HasValue ? _tz.ConvertUtcToApp(v.CompletedAtUtc.Value) : (DateTime?)null,
                    Logs = new() // fill in step 2
                })
                .ToListAsync(ct);

            // 2) Load all logs in one query and group them
            var vmIds = vmResults.Select(x => x.Id).ToList();
            var logs = await _context.JobVmLogs
                .AsNoTracking()
                .Where(l => vmIds.Contains(l.JobVmResultId))
                .OrderBy(l => l.TimestampUtc)
                .Select(l => new { l.JobVmResultId, l.Level, l.Message, l.TimestampUtc })
                .ToListAsync(ct);

            var grouped = logs.GroupBy(l => l.JobVmResultId)
                              .ToDictionary(g => g.Key, g => g.Select(l => new JobVmLogViewModel
                              {
                                  Level = l.Level,
                                  Message = l.Message,
                                  TimestampLocal = _tz.ConvertUtcToApp(l.TimestampUtc)
                              }).ToList());

            foreach (var v in vmResults)
                if (grouped.TryGetValue(v.Id, out var list)) v.Logs = list;

            return new JobDetailsViewModel
            {
                JobId = job.Id,
                Type = job.Type,
                RelatedVm = job.RelatedVm,
                Status = job.Status,
                ErrorMessage = job.ErrorMessage,
                StartedAtLocal = _tz.ConvertUtcToApp(job.StartedAt),
                CompletedAtLocal = job.CompletedAt.HasValue ? _tz.ConvertUtcToApp(job.CompletedAt.Value) : (DateTime?)null,
                VmResults = vmResults
            };
        }
    }

  
}
