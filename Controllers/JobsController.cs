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

        public async Task<IActionResult> Index()
        {
            var jobs = await _context.Jobs
                .OrderByDescending(j => j.StartedAt)
                .ToListAsync();

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

        public async Task<IActionResult> Cancel(int id)
        {
            var job = await _context.Jobs.FindAsync(id);
            if (job == null || job.Status == "Completed" || job.Status == "Cancelled")
                return NotFound();

            job.Status = "Cancelled";
            await _context.SaveChangesAsync();

            return RedirectToAction("Index");
        }

        [HttpGet]
        public async Task<IActionResult> Table(
             string status = "",
             string search = "",
             string sortColumn = "StartedAt",
             bool asc = false)
        {
            // 1) Grab raw job entities
            var query = _context.Jobs.AsNoTracking().AsQueryable();

            // 2) Project to your view model
            var jobs = query
                .Select(j => new JobViewModel
                {
                    Id = j.Id,
                    Type = j.Type,
                    RelatedVm = j.RelatedVm,
                    Status = j.Status,
                    StartedAtLocal = j.StartedAt,
                    CompletedAtLocal = j.CompletedAt,
                    ErrorMessage = j.ErrorMessage
                });

            // 3) Filter
            if (!string.IsNullOrWhiteSpace(status))
                jobs = jobs.Where(j => j.Status == status);
            if (!string.IsNullOrWhiteSpace(search))
                jobs = jobs.Where(j =>
                    j.Type.Contains(search) ||
                    j.RelatedVm.Contains(search));

            // 4) Sort
            jobs = (sortColumn, asc) switch
            {
                ("Type", true) => jobs.OrderBy(j => j.Type),
                ("Type", false) => jobs.OrderByDescending(j => j.Type),
                ("RelatedVm", true) => jobs.OrderBy(j => j.RelatedVm),
                ("RelatedVm", false) => jobs.OrderByDescending(j => j.RelatedVm),
                ("Status", true) => jobs.OrderBy(j => j.Status),
                ("Status", false) => jobs.OrderByDescending(j => j.Status),
                ("CompletedAt", true) => jobs.OrderBy(j => j.CompletedAtLocal),
                ("CompletedAt", false) => jobs.OrderByDescending(j => j.CompletedAtLocal),
                _ => asc
                                         ? jobs.OrderBy(j => j.StartedAtLocal)
                                         : jobs.OrderByDescending(j => j.StartedAtLocal)
            };

            var list = await jobs.ToListAsync();

            return PartialView("_JobsTable", list);
        }
    }
}
