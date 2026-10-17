/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 * Copyright (C) 2025-2026 Tobias Modig
 * SPDX-License-Identifier: AGPL-3.0-only
 */
using BareProx.Models;

namespace BareProx.Services.Migration;

/// <summary>
/// Measures per-VM execution time, excluding idle queue time and the interval
/// between automated staging and cutover. Uses persisted queue logs so reports
/// continue working after a queue record has been removed.
/// </summary>
public static class MigrationExecutionTime
{
    private const string StartStage = "Execution started: staging";
    private const string EndStage = "Execution finished: staging";
    private const string StartCutover = "Execution started: cutover";
    private const string EndCutover = "Execution finished: cutover";

    public static bool IsTimingEvent(MigrationQueueLog log) =>
        log.Step == "Timing" && (log.Message == StartStage || log.Message == EndStage ||
        log.Message == StartCutover || log.Message == EndCutover);

    public static (DateTime StartUtc, DateTime EndUtc, TimeSpan Duration) Measure(
        IEnumerable<MigrationQueueLog> events)
    {
        var logs = events.OrderBy(x => x.Utc).ThenBy(x => x.Id).ToList();
        if (logs.Count == 0) return (default, default, TimeSpan.Zero);
        var end = logs[^1].Utc;
        var first = logs[0].Utc;
        var timing = logs.Where(IsTimingEvent).ToList();
        if (timing.Count > 0)
        {
            var started = (DateTime?)null;
            var total = TimeSpan.Zero;
            DateTime? firstStarted = null;
            foreach (var log in timing)
            {
                if (log.Message == StartStage || log.Message == StartCutover)
                {
                    // Close a previous unclosed interval conservatively at this boundary.
                    if (started.HasValue && log.Utc > started.Value)
                        total += log.Utc - started.Value;
                    started = log.Utc;
                    firstStarted ??= log.Utc;
                }
                else if (started.HasValue)
                {
                    if (log.Utc > started.Value) total += log.Utc - started.Value;
                    started = null;
                }
            }
            if (started.HasValue && end > started.Value) total += end - started.Value;
            return (firstStarted ?? first, end, total);
        }

        // Historical manual migrations log an explicit processing start.
        var manual = logs.FirstOrDefault(x => x.Step == "Item" &&
            (x.Message ?? "").StartsWith("Processing start (", StringComparison.Ordinal));
        if (manual != null) first = manual.Utc;
        else
        {
            // Legacy automatic histories lack timing markers. Exclude the explicit
            // queued/waiting event rather than measuring from queue dispatch.
            var active = logs.FirstOrDefault(x =>
                (x.Step == "Snapshot" || x.Step == "Stage" || x.Step == "Automation") &&
                !(x.Message ?? "").StartsWith("Automated migration queued; waiting", StringComparison.Ordinal) &&
                !(x.Message ?? "").StartsWith("Already staged; pending", StringComparison.Ordinal));
            if (active != null) first = active.Utc;
        }
        return (first, end, end >= first ? end - first : TimeSpan.Zero);
    }
}
