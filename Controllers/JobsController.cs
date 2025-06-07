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
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Controllers
{
    public class JobsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IAppTimeZoneService _tz;

        public JobsController(
             ApplicationDbContext context,
             IAppTimeZoneService tz)
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
            })
            .ToList();

            return View(vm);
        }

        public async Task<IActionResult> Cancel(int id, CancellationToken ct)
        {
            var job = await _context.Jobs.FindAsync(new object[] { id }, ct);
            if (job == null || job.Status == "Completed" || job.Status == "Cancelled")
                return NotFound();

            job.Status = "Cancelled";
            await _context.SaveChangesAsync(ct);

            return RedirectToAction("Index");
        }

        [HttpGet]
        public async Task<IActionResult> Table(
             string status = "",
             string search = "",
             string sortColumn = "StartedAt",
             bool asc = false,
             CancellationToken ct = default)
        {
            // 1) Start with an IQueryable<Job>—no ConvertUtcToApp calls here
            var query = _context.Jobs
                .AsNoTracking();

            // 2) Apply filters (still purely on database‐side properties)
            if (!string.IsNullOrWhiteSpace(status))
                query = query.Where(j => j.Status == status);

            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(j =>
                    j.Type.Contains(search) ||
                    j.RelatedVm.Contains(search));

            // 3) Apply sorting ON THE UTC COLUMNS (StartedAt / CompletedAt)
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
                _ => asc
                         ? query.OrderBy(j => j.StartedAt)
                         : query.OrderByDescending(j => j.StartedAt)
            };

            // 4) Fetch the Job entities (only needed columns—no conversion yet)
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

            // 5) Now that we’re in‐memory, project into JobViewModel and convert timestamps
            var vmList = rawJobs
                .Select(j => new JobViewModel
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
                })
                .ToList();

            return PartialView("_JobsTable", vmList);
        }
    }
}
