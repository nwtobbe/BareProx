using BareProx.Services.Migration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace BareProx.Controllers;

[Route("Migration/Automation")]
public sealed class MigrationAutomationStageController : Controller
{
    [HttpPost("Stage/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Stage(int id, CancellationToken ct)
    {
        try
        {
            var stage = ActivatorUtilities.CreateInstance<AutomatedGuestStageService>(HttpContext.RequestServices);
            var result = await stage.StageAsync(id, ct);
            return Ok(new { ok = true, queueId = result.QueueId, result.Vm,
                result.StageDirectory, result.FileCount, result.UploadedBytes,
                note = "Guest files staged. No processes started; original VM remains powered on." });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Never serialize exception details that might contain a signed upload URL or guest credentials.
            return BadRequest(new { ok = false, error = "Guest staging failed. Inspect server logs and queue activity." });
        }
    }
}
