using BareProx.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Controllers;

/// <summary>Persists per-cluster migration worker limits from the shared Settings Save action.</summary>
[Authorize]
[Route("Migration/Concurrency")]
public sealed class MigrationConcurrencyController : Controller
{
    private readonly ApplicationDbContext _db;

    public MigrationConcurrencyController(ApplicationDbContext db) => _db = db;

    [HttpPost("Save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(int clusterId, int maxParallelMigrations,
        int maxParallelCutovers, CancellationToken ct)
    {
        var json = Request.Headers.Accept.Any(v => v != null && v.Contains("application/json", StringComparison.OrdinalIgnoreCase));
        IActionResult Failed(string message)
        {
            if (json) return BadRequest(new { ok = false, error = message });
            TempData["ErrorMessage"] = message;
            return RedirectToAction("Settings", "Migration", new { clusterId });
        }

        if (maxParallelMigrations is < 1 or > 8 || maxParallelCutovers is < 1 or > 8)
            return Failed("Parallel migration and cutover limits must be between 1 and 8.");

        var selection = await _db.MigrationSelections
            .FirstOrDefaultAsync(x => x.ClusterId == clusterId, ct);
        if (selection == null)
            return Failed("Migration selection was not saved for this cluster.");

        selection.MaxParallelMigrations = maxParallelMigrations;
        selection.MaxParallelCutovers = maxParallelCutovers;
        selection.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        if (json) return Ok(new { ok = true });
        TempData["SuccessMessage"] = "Migration concurrency settings saved.";
        return RedirectToAction("Settings", "Migration", new { clusterId });
    }
}
