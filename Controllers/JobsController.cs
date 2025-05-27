using BareProx.Data;
using BareProx.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Controllers
{
    public class JobsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public JobsController(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index()
        {
            var jobs = await _context.Jobs.OrderByDescending(j => j.StartedAt).ToListAsync();
            return View(jobs);
        }

        public async Task<IActionResult> Cancel(int id)
        {
            var job = await _context.Jobs.FindAsync(id);
            if (job == null || job.Status == "Completed" || job.Status == "Cancelled")
                return NotFound();

            job.Status = "Cancelled";
            await _context.SaveChangesAsync();

            // Background process should check this flag and exit gracefully
            return RedirectToAction("Index");
        }
    }
}