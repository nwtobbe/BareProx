using BareProx.Data;
using BareProx.Models;
using BareProx.Services.Migration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Threading;

namespace BareProx.Controllers;

/// <summary>
/// Automated migration orchestration. Each VM receives its own VMID-specific
/// NetApp snapshot immediately before its guest staging. No VM is staged when
/// its safety snapshot cannot be created. A separate post-shutdown snapshot
/// remains part of AutomatedMigrationExecutionService.
/// </summary>
[Route("Migration/Automation/Queue")]
public sealed class MigrationAutomationQueueStageController : Controller
{
    private static int _running;
    public static bool IsRunning => Volatile.Read(ref _running) != 0;

    [HttpPost("StagePending")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StagePending(
        [FromServices] ApplicationDbContext db,
        [FromServices] IServiceScopeFactory scopeFactory,
        [FromServices] IHostApplicationLifetime lifetime,
        [FromServices] ILogger<MigrationAutomationQueueStageController> logger,
        CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return Conflict(new { ok = false, error = "Automated migration is already running." });

        try
        {
            var items = await db.MigrationQueueItems
                .Where(x => x.Status == "AutomationPending" || x.Status == "AutomationStaged")
                .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id)
                .ToListAsync(ct);
            if (items.Count == 0)
            {
                Interlocked.Exchange(ref _running, 0);
                return Ok(new { ok = true, count = 0, message = "No automated VMs awaiting preparation." });
            }

            var selection = await db.MigrationSelections.AsNoTracking().FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("Configure Migration Settings before starting automated migration.");
            var parallelStaging = Math.Clamp(selection.MaxParallelMigrations, 1, 8);
            var parallelCutovers = Math.Clamp(selection.MaxParallelCutovers, 1, 8);
            var selectedVolumeId = selection.SelectedNetappVolumeId;
            var storageIdentifier = selection.StorageIdentifier;

            foreach (var item in items)
            {
                if (item.Status != "AutomationStaged") item.Status = "AutomationStaging";
                db.MigrationQueueLogs.Add(new MigrationQueueLog
                {
                    ItemId = item.Id, Utc = DateTime.UtcNow, Step = "Stage", Level = "Info",
                    Message = item.Status == "AutomationStaged"
                        ? "Already staged; pending cutover (no new staging snapshot)."
                        : "Automated migration queued; waiting for VMID-specific NetApp safety snapshot before staging."
                });
            }
            await db.SaveChangesAsync(ct);
            var ids = items.Select(x => x.Id).ToArray();
            var stopping = lifetime.ApplicationStopping;

            _ = Task.Run(async () =>
            {
                try
                {
                    // A worker takes the safety snapshot FOR ITS OWN VM before
                    // uploading any GuestPrep payload. No shared/batch snapshot.
                    var stageQueue = new ConcurrentQueue<int>(ids);
                    // Pipeline: each successful staging immediately becomes eligible for
                    // a cutover worker. Never wait for the entire staging batch.
                    var readyForCutover = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
                    {
                        SingleReader = false,
                        SingleWriter = false
                    });
                    async Task StageWorkerAsync()
                    {
                        while (!stopping.IsCancellationRequested && stageQueue.TryDequeue(out var id))
                        {
                            using var scope = scopeFactory.CreateScope();
                            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                            try
                            {
                                var item = await context.MigrationQueueItems
                                    .FirstOrDefaultAsync(x => x.Id == id, stopping);
                                if (item == null || (item.Status != "AutomationStaging" && item.Status != "AutomationStaged"))
                                    continue;

                                context.MigrationQueueLogs.Add(new MigrationQueueLog
                                {
                                    ItemId = id, Utc = DateTime.UtcNow, Step = "Timing", Level = "Info",
                                    Message = "Execution started: staging"
                                });
                                await context.SaveChangesAsync(stopping);

                                // Even a previously staged VM needs a fresh per-VM safety
                                // snapshot before its next cutover attempt.
                                if (item.VmId is null or < 100)
                                    throw new InvalidOperationException("Destination Proxmox VMID is missing or invalid; snapshot and migration blocked.");
                                var snapper = scope.ServiceProvider.GetRequiredService<IStorageSnapshotCoordinator>();
                                if (selectedVolumeId.HasValue)
                                    await snapper.EnsureSnapshotsByVolumeIdsAsync(new[] { selectedVolumeId.Value }, stopping, item.VmId.Value);
                                else if (!string.IsNullOrWhiteSpace(storageIdentifier))
                                    await snapper.EnsureSnapshotsAsync(new[] { storageIdentifier }, stopping, item.VmId.Value);
                                else
                                    throw new InvalidOperationException("No NetApp snapshot source configured; migration blocked.");

                                context.MigrationQueueLogs.Add(new MigrationQueueLog
                                {
                                    ItemId = id, Utc = DateTime.UtcNow, Step = "Snapshot", Level = "Info",
                                    Message = $"Initial VMID {item.VmId} NetApp safety snapshot created before staging/cutover."
                                });
                                await context.SaveChangesAsync(stopping);

                                if (item.Status == "AutomationStaging")
                                {
                                    context.MigrationQueueLogs.Add(new MigrationQueueLog
                                    {
                                        ItemId = id, Utc = DateTime.UtcNow, Step = "Stage", Level = "Info",
                                        Message = "VMID safety snapshot verified; uploading GuestPrep files via vCenter."
                                    });
                                    await context.SaveChangesAsync(stopping);
                                    var stage = ActivatorUtilities.CreateInstance<AutomatedGuestStageService>(scope.ServiceProvider);
                                    var result = await stage.StageAsync(id, stopping);
                                    item.Status = "AutomationStaged";
                                    context.MigrationQueueLogs.Add(new MigrationQueueLog
                                    {
                                        ItemId = id, Utc = DateTime.UtcNow, Step = "Stage", Level = "Info",
                                        Message = $"Uploaded {result.FileCount} files ({result.UploadedBytes} bytes)."
                                    });
                                    await context.SaveChangesAsync(stopping);
                                }
                                context.MigrationQueueLogs.Add(new MigrationQueueLog
                                {
                                    ItemId = id, Utc = DateTime.UtcNow, Step = "Timing", Level = "Info",
                                    Message = "Execution finished: staging"
                                });
                                await context.SaveChangesAsync(stopping);
                                // Publish only after AutomationStaged is committed. The
                                // consumer will validate status again before cutover.
                                await readyForCutover.Writer.WriteAsync(id, stopping);
                            }
                            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
                            catch (Exception ex)
                            {
                                await RecordFailureAsync(context, id, ex, logger, stopping);
                                context.MigrationQueueLogs.Add(new MigrationQueueLog
                                {
                                    ItemId = id, Utc = DateTime.UtcNow, Step = "Timing", Level = "Info",
                                    Message = "Execution finished: staging"
                                });
                                await context.SaveChangesAsync(stopping);
                            }
                        }
                    }
                    async Task CutoverWorkerAsync()
                    {
                        await foreach (var id in readyForCutover.Reader.ReadAllAsync(stopping))
                        {
                            using var scope = scopeFactory.CreateScope();
                            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                            try
                            {
                                var item = await context.MigrationQueueItems.AsNoTracking()
                                    .FirstOrDefaultAsync(x => x.Id == id, stopping);
                                if (item?.Status != "AutomationStaged") continue;
                                context.MigrationQueueLogs.Add(new MigrationQueueLog
                                {
                                    ItemId = id, Utc = DateTime.UtcNow, Step = "Timing", Level = "Info",
                                    Message = "Execution started: cutover"
                                });
                                await context.SaveChangesAsync(stopping);
                                var execution = ActivatorUtilities.CreateInstance<AutomatedMigrationExecutionService>(scope.ServiceProvider);
                                await execution.ExecuteAsync(id, stopping);
                                context.MigrationQueueLogs.Add(new MigrationQueueLog
                                {
                                    ItemId = id, Utc = DateTime.UtcNow, Step = "Timing", Level = "Info",
                                    Message = "Execution finished: cutover"
                                });
                                await context.SaveChangesAsync(stopping);
                            }
                            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
                            catch (Exception ex)
                            {
                                await RecordFailureAsync(context, id, ex, logger, stopping);
                                context.MigrationQueueLogs.Add(new MigrationQueueLog
                                {
                                    ItemId = id, Utc = DateTime.UtcNow, Step = "Timing", Level = "Info",
                                    Message = "Execution finished: cutover"
                                });
                                await context.SaveChangesAsync(stopping);
                            }
                        }
                    }
                    // Start both worker pools together. A cutover worker is
                    // immediately awakened when any stage worker completes a VM.
                    // Finishing the producer closes the channel; consumers drain it
                    // and exit. Staging failures never enter the cutover channel.
                    var cutoverTasks = Enumerable.Range(0, Math.Min(parallelCutovers, ids.Length))
                        .Select(_ => CutoverWorkerAsync()).ToArray();
                    try
                    {
                        await Task.WhenAll(Enumerable.Range(0, Math.Min(parallelStaging, ids.Length))
                            .Select(_ => StageWorkerAsync()));
                    }
                    finally
                    {
                        readyForCutover.Writer.TryComplete();
                    }
                    await Task.WhenAll(cutoverTasks);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    logger.LogWarning("Automated migration stopped during application shutdown; inspect unfinished VMs before retrying.");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Automated migration worker orchestration failed.");
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        foreach (var id in ids)
                        {
                            var item = await context.MigrationQueueItems.FindAsync(new object[] { id }, CancellationToken.None);
                            if (item?.Status is "AutomationStaging" or "AutomationStaged")
                            {
                                item.Status = "AutomationStageFailed";
                                context.MigrationQueueLogs.Add(new MigrationQueueLog
                                {
                                    ItemId = id, Utc = DateTime.UtcNow, Step = "Snapshot", Level = "Error",
                                    Message = "Automated migration stopped before cutover: " + ex.GetType().Name
                                });
                            }
                        }
                        await context.SaveChangesAsync(CancellationToken.None);
                    }
                    catch (Exception persistEx)
                    {
                        logger.LogError(persistEx, "Failed to record automated orchestration error.");
                    }
                }
                finally { Interlocked.Exchange(ref _running, 0); }
            }, CancellationToken.None);

            return Accepted(new
            {
                ok = true, count = ids.Length, parallelStaging, parallelCutovers,
                message = "Automated migration pipeline started. Each staged VM can begin cutover immediately when a cutover worker is available."
            });
        }
        catch
        {
            Interlocked.Exchange(ref _running, 0);
            throw;
        }
    }

    /// <summary>
    /// Continue an automatic migration that timed out waiting for VMware shutdown.
    /// This never repeats GuestPrep or requests shutdown a second time.
    /// Only one automated runner/continuation is active in this process at once.
    /// </summary>
    [HttpPost("{id:int}/Continue")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ContinuePaused(
        int id,
        [FromServices] ApplicationDbContext db,
        [FromServices] IServiceScopeFactory scopeFactory,
        [FromServices] IHostApplicationLifetime lifetime,
        [FromServices] ILogger<MigrationAutomationQueueStageController> logger,
        CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return Conflict(new { ok = false, error = "Another automatic migration/continuation is running. Try again after it completes." });
        try
        {
            // Atomic compare-and-set makes double-clicks and concurrent browsers safe.
            var claimed = await db.MigrationQueueItems
                .Where(x => x.Id == id && x.Status == "AutomationShutdownPaused")
                .ExecuteUpdateAsync(x => x.SetProperty(i => i.Status, "AutomationShutdownResuming"), ct);
            if (claimed != 1)
            {
                Interlocked.Exchange(ref _running, 0);
                return Conflict(new { ok = false, error = "This VM is no longer paused waiting for shutdown." });
            }
            _ = Task.Run(async () =>
            {
                using var scope = scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var stopping = lifetime.ApplicationStopping;
                try
                {
                    context.MigrationQueueLogs.Add(new MigrationQueueLog
                    {
                        ItemId = id, Utc = DateTime.UtcNow, Step = "Timing", Level = "Info",
                        Message = "Execution started: cutover"
                    });
                    await context.SaveChangesAsync(stopping);
                    var service = ActivatorUtilities.CreateInstance<AutomatedMigrationExecutionService>(scope.ServiceProvider);
                    await service.ResumePausedAsync(id, stopping);
                    context.MigrationQueueLogs.Add(new MigrationQueueLog
                    {
                        ItemId = id, Utc = DateTime.UtcNow, Step = "Timing", Level = "Info",
                        Message = "Execution finished: cutover"
                    });
                    await context.SaveChangesAsync(stopping);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    // A restart/interruption must never trigger a blind GuestPrep retry.
                    try
                    {
                        await context.MigrationQueueItems
                            .Where(x => x.Id == id && x.Status == "AutomationShutdownResuming")
                            .ExecuteUpdateAsync(x => x.SetProperty(i => i.Status, "AutomationShutdownPaused"), CancellationToken.None);
                    }
                    catch (Exception ex) { logger.LogError(ex, "Could not preserve pause after shutdown for item {Id}", id); }
                }
                catch (Exception ex)
                {
                    await RecordFailureAsync(context, id, ex, logger, CancellationToken.None);
                    try
                    {
                        context.MigrationQueueLogs.Add(new MigrationQueueLog
                        {
                            ItemId = id, Utc = DateTime.UtcNow, Step = "Timing", Level = "Info",
                            Message = "Execution finished: cutover"
                        });
                        await context.SaveChangesAsync(CancellationToken.None);
                    }
                    catch (Exception logEx) { logger.LogWarning(logEx, "Could not save timing for item {Id}", id); }
                }
                finally { Interlocked.Exchange(ref _running, 0); }
            }, CancellationToken.None);
            return Accepted(new { ok = true, id, message = "Continue accepted. Rechecking VMware shutdown without repeating GuestPrep." });
        }
        catch
        {
            Interlocked.Exchange(ref _running, 0);
            throw;
        }
    }

    /// <summary>
    /// Cancel the remaining BareProx cutover work for a paused VM. This does NOT
    /// reverse GuestPrep or cancel a Windows shutdown already requested.
    /// </summary>
    [HttpPost("{id:int}/Cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelPaused(int id,
        [FromServices] ApplicationDbContext db, CancellationToken ct)
    {
        var changed = await db.MigrationQueueItems
            .Where(x => x.Id == id && x.Status == "AutomationShutdownPaused")
            .ExecuteUpdateAsync(x => x
                .SetProperty(i => i.Status, "AutomationCanceled")
                .SetProperty(i => i.EncryptedGuestPassword, (string?)null), ct);
        if (changed != 1)
            return Conflict(new { ok = false, error = "Only a paused shutdown can be cancelled." });
        db.MigrationQueueLogs.Add(new MigrationQueueLog
        {
            ItemId = id, Utc = DateTime.UtcNow, Step = "Shutdown", Level = "Warning",
            Message = "Operator cancelled the paused migration. No further BareProx snapshot/import will run. " +
                "Windows may still complete the already-requested shutdown. Check the VMware console; this does NOT undo GuestPrep."
        });
        await db.SaveChangesAsync(ct);
        return Ok(new { ok = true, id, message = "Cancelled. Verify VMware manually; an earlier shutdown request may still complete." });
    }

    private static async Task RecordFailureAsync(
        ApplicationDbContext context, int id, Exception ex,
        ILogger<MigrationAutomationQueueStageController> logger, CancellationToken ct)
    {
        logger.LogError(ex, "Automated migration item {ItemId} failed.", id);
        try
        {
            context.ChangeTracker.Clear();
            var item = await context.MigrationQueueItems.FindAsync(new object[] { id }, ct);
            var prior = item?.Status ?? "Unknown";
            if (item != null)
            {
                item.Status = prior switch
                {
                    "AutomationStaging" => "AutomationStageFailed",
                    "AutomationImporting" => "AutomationImportFailed",
                    "AutomationStarting" => "AutomationStartFailed",
                    "AutomationVerifyingBoot" => "AutomationBootVerifyFailed",
                    "AutomationPoweredOff" or "AutomationSnapshot" => "AutomationPostShutdownFailed",
                    "AutomationCutover" or "AutomationWaitingForShutdown" or "AutomationShutdownResuming" => "AutomationCutoverFailed",
                    _ => "AutomationPrepFailed"
                };
            }
            context.MigrationQueueLogs.Add(new MigrationQueueLog
            {
                ItemId = id, Utc = DateTime.UtcNow, Step = prior == "AutomationStaging" ? "Snapshot/Stage" : "Automation", Level = "Error",
                Message = $"Stopped during {prior}: " +
                    (ex is AutomatedGuestStageService.StageDiagnosticException ? ex.Message : ex.GetType().Name) +
                    ". Check VMware and Proxmox states BEFORE recovery. No automatic retry."
            });
            await context.SaveChangesAsync(ct);
        }
        catch (Exception logError)
        {
            logger.LogError(logError, "Could not save automated migration failure for {ItemId}", id);
        }
    }
}
