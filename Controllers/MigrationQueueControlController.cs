using BareProx.Data;
using BareProx.Services.Migration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Controllers;

/// <summary>Reports actual runner activity; guarded administration of persisted migration queue records.</summary>

[Route("Migration/QueueControl")]
public sealed class MigrationQueueControlController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IMigrationQueueRunner _runner;

    public MigrationQueueControlController(ApplicationDbContext db, IMigrationQueueRunner runner)
    {
        _db = db;
        _runner = runner;
    }

    [HttpGet("State")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult State()
    {
        var manual = _runner.IsRunning;
        var automated = MigrationAutomationQueueStageController.IsRunning;
        return Ok(new { running = manual || automated, manualRunning = manual, automatedRunning = automated });
    }

    /// <summary>
    /// Clears all persisted migration queue entries regardless of status, preserving migration queue logs.
    /// This does not cancel ongoing work on VMware, NetApp or Proxmox.
    /// </summary>
    [HttpPost("Clear")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Clear([FromForm] string? confirmation, CancellationToken ct)
    {
        if (!string.Equals(confirmation?.Trim(), "CLEAR", StringComparison.Ordinal))
            return BadRequest(new { ok = false, error = "Type CLEAR to confirm." });

        await MigrationQueueControlGate.Sync.WaitAsync(ct);
        try
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var removed = await _db.MigrationQueueItems.ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
            return Ok(new
            {
                ok = true,
                removed,
                message = "Migration queue cleared. All migration queue logs were preserved. Running VMware, NetApp or Proxmox operations have NOT been stopped."
            });
        }
        finally
        {
            MigrationQueueControlGate.Sync.Release();
        }
    }
}
