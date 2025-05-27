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
    }
}
