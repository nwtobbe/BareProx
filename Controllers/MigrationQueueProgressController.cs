using BareProx.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Controllers;

/// <summary>Read-only queue progress, using the existing durable migration log.</summary>
[ApiController]
[Route("Migration/QueueProgress")]
public sealed class MigrationQueueProgressController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    public MigrationQueueProgressController(ApplicationDbContext db) => _db = db;

    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var items = await _db.MigrationQueueItems.AsNoTracking()
            .Select(x => new { x.Id, x.Status }).ToListAsync(ct);
        var ids = items.Select(x => x.Id).ToArray();
        var logs = await _db.MigrationQueueLogs.AsNoTracking()
            .Where(x => ids.Contains(x.ItemId))
            .OrderByDescending(x => x.Utc)
            .Select(x => new { x.ItemId, x.Utc, x.Step, x.Level, x.Message })
            .ToListAsync(ct);
        var latest = logs.GroupBy(x => x.ItemId).ToDictionary(g => g.Key, g => g.First());
        // Don't report invented percentages. Pending automated jobs are NOT running.
        return Ok(items.Select(x =>
        {
            var has = latest.TryGetValue(x.Id, out var last);
            return new {
                id = x.Id,
                status = x.Status,
                phase = x.Status == "AutomationPending" && !has ? "Awaiting automated runner" :
                    x.Status == "Queued" ? "Waiting for Run Queue" :
                    has ? last!.Step : x.Status,
                activity = x.Status == "AutomationPending" && !has ? "Waiting for Run Queue" :
                    has ? last!.Message : "No activity recorded",
                level = has ? last!.Level : "Info",
                updatedUtc = has ? (DateTime?)last!.Utc : null
            };
        }).ToArray());
    }
}
