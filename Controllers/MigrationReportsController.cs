/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 * Copyright (C) 2025-2026 Tobias Modig
 * SPDX-License-Identifier: AGPL-3.0-only
 */
using System.Globalization;
using System.Text;
using BareProx.Data;
using BareProx.Models;
using BareProx.Services.Migration;
using BareProx.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Controllers;

public sealed class MigrationReportSummary
{
    public int ItemId { get; set; }
    public string Name { get; set; } = "";
    public int? VmId { get; set; }
    public string? Status { get; set; }
    public DateTime FirstUtc { get; set; }
    public DateTime LastUtc { get; set; }
    public int Entries { get; set; }
    public int Errors { get; set; }
    public int Warnings { get; set; }
    public int Steps { get; set; }
    /// <summary>Per-VM processing time; queued and between-phase wait is excluded.</summary>
    public TimeSpan RecordedDuration { get; set; }
    public DateTime ExecutionStartUtc { get; set; }
    public string RecordedDurationText
    {
        get
        {
            var duration = RecordedDuration;
            if (duration.TotalDays >= 1) return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m {duration.Seconds}s";
            if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s";
            if (duration.TotalMinutes >= 1) return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
            return $"{(int)duration.TotalSeconds}s";
        }
    }
    public bool Archived => string.IsNullOrEmpty(Status);
}

public sealed class MigrationReportsIndexViewModel
{
    public List<MigrationReportSummary> Reports { get; set; } = new();
    public int TotalReports { get; set; }
    public int TotalEntries { get; set; }
    public int ErrorReports { get; set; }
    public int WarningReports { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; } = 50;
    public int TotalPages => Math.Max(1, (TotalReports + PageSize - 1) / PageSize);
    public bool EmailConfigured { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;
}

public sealed class MigrationReportDetailViewModel
{
    public MigrationReportSummary Report { get; set; } = new();
    public List<MigrationQueueLog> Logs { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; } = 100;
    public int TotalPages => Math.Max(1, (Report.Entries + PageSize - 1) / PageSize);
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;
}

/// <summary>
/// Read-only migration history. Logs deliberately outlive deleted queue items.
/// </summary>
// Program.cs disables the fallback authorization policy in DEBUG.
// Match that behavior here: allow local debug testing, require login in Release.
#if !DEBUG
[Authorize]
#endif
[Route("Migration/Reports")]
public sealed class MigrationReportsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAppTimeZoneService _timeZone;
    public MigrationReportsController(ApplicationDbContext db, IAppTimeZoneService timeZone)
    {
        _db = db;
        _timeZone = timeZone;
    }

    [HttpGet("")]
    [HttpGet("Index")]
    public async Task<IActionResult> Index(int page = 1, CancellationToken ct = default)
    {
        page = Math.Clamp(page, 1, 1000000);
        const int pageSize = 50;
        var groups = _db.MigrationQueueLogs.AsNoTracking()
            .GroupBy(x => x.ItemId)
            .Select(g => new MigrationReportSummary
            {
                ItemId = g.Key,
                FirstUtc = g.Min(x => x.Utc),
                LastUtc = g.Max(x => x.Utc),
                Entries = g.Count(),
                Errors = g.Count(x => x.Level == "Error" || x.Level == "Critical"),
                Warnings = g.Count(x => x.Level == "Warning"),
                Steps = g.Select(x => x.Step).Distinct().Count()
            });
        var totalReports = await groups.CountAsync(ct);
        var totals = await _db.MigrationQueueLogs.AsNoTracking()
            .GroupBy(x => 1)
            .Select(g => new { Count = g.Count() })
            .FirstOrDefaultAsync(ct);
        var errorReports = await groups.CountAsync(x => x.Errors > 0, ct);
        var warningReports = await groups.CountAsync(x => x.Warnings > 0, ct);
        var rows = await groups.OrderByDescending(x => x.LastUtc).ThenByDescending(x => x.ItemId)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var ids = rows.Select(x => x.ItemId).ToList();
        var queue = await _db.MigrationQueueItems.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.Name, x.VmId, x.Status }).ToListAsync(ct);
        var byId = queue.ToDictionary(x => x.Id);
        foreach (var row in rows)
            if (byId.TryGetValue(row.ItemId, out var item))
            {
                row.Name = item.Name ?? "";
                row.VmId = item.VmId;
                row.Status = item.Status;
            }
        var reportLogs = await _db.MigrationQueueLogs.AsNoTracking()
            .Where(x => ids.Contains(x.ItemId)).OrderBy(x => x.Utc).ThenBy(x => x.Id).ToListAsync(ct);
        var elapsedById = reportLogs.GroupBy(x => x.ItemId).ToDictionary(
            g => g.Key, g => MigrationExecutionTime.Measure(g));
        foreach (var row in rows)
            if (elapsedById.TryGetValue(row.ItemId, out var measurement))
            {
                row.RecordedDuration = measurement.Duration;
                row.ExecutionStartUtc = measurement.StartUtc;
            }
        // Archived queue entries have no matching row, but their persisted log name remains.
        var logNames = await _db.MigrationQueueLogs.AsNoTracking()
            .Where(x => ids.Contains(x.ItemId) && x.ServerName != null && x.ServerName != "")
            .GroupBy(x => x.ItemId)
            .Select(g => new { ItemId = g.Key, Name = g.Max(x => x.ServerName) })
            .ToListAsync(ct);
        var logNamesById = logNames.ToDictionary(x => x.ItemId, x => x.Name);
        foreach (var row in rows)
            if (logNamesById.TryGetValue(row.ItemId, out var loggedName) && !string.IsNullOrWhiteSpace(loggedName))
                row.Name = loggedName!;
        var zone = _timeZone.AppTimeZone;
        return View(new MigrationReportsIndexViewModel
        {
            Reports = rows, TotalReports = totalReports, TotalEntries = totals?.Count ?? 0,
            ErrorReports = errorReports, WarningReports = warningReports,
            Page = page, PageSize = pageSize, Zone = zone, TimeZoneId = zone.Id,
            EmailConfigured = await _db.EmailSettings.AsNoTracking().AnyAsync(x => x.Id == 1 && x.Enabled && x.SmtpHost != null && x.SmtpHost != "", ct)
        });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Detail(int id, int page = 1, CancellationToken ct = default)
    {
        page = Math.Clamp(page, 1, 1000000);
        const int pageSize = 100;
        var report = await _db.MigrationQueueLogs.AsNoTracking().Where(x => x.ItemId == id)
            .GroupBy(x => x.ItemId).Select(g => new MigrationReportSummary
            {
                ItemId = g.Key, FirstUtc = g.Min(x => x.Utc), LastUtc = g.Max(x => x.Utc),
                Entries = g.Count(), Errors = g.Count(x => x.Level == "Error" || x.Level == "Critical"),
                Warnings = g.Count(x => x.Level == "Warning"),
                Steps = g.Select(x => x.Step).Distinct().Count()
            }).FirstOrDefaultAsync(ct);
        if (report == null) return NotFound();
        var allReportLogs = await _db.MigrationQueueLogs.AsNoTracking()
            .Where(x => x.ItemId == id).OrderBy(x => x.Utc).ThenBy(x => x.Id).ToListAsync(ct);
        var timing = MigrationExecutionTime.Measure(allReportLogs);
        report.RecordedDuration = timing.Duration;
        report.ExecutionStartUtc = timing.StartUtc;
        var item = await _db.MigrationQueueItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (item != null)
        {
            report.Name = item.Name ?? "";
            report.VmId = item.VmId;
            report.Status = item.Status;
        }
        var loggedName = await _db.MigrationQueueLogs.AsNoTracking()
            .Where(x => x.ItemId == id && x.ServerName != null && x.ServerName != "")
            .OrderByDescending(x => x.Id).Select(x => x.ServerName).FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(loggedName)) report.Name = loggedName;
        var logs = await _db.MigrationQueueLogs.AsNoTracking().Where(x => x.ItemId == id)
            .OrderBy(x => x.Utc).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return View(new MigrationReportDetailViewModel
        {
            Report = report, Logs = logs, Page = page, PageSize = pageSize, Zone = _timeZone.AppTimeZone
        });
    }

    [HttpGet("{id:int}/Csv")]
    public async Task<IActionResult> Csv(int id, CancellationToken ct = default)
    {
        if (!await _db.MigrationQueueLogs.AsNoTracking().AnyAsync(x => x.ItemId == id, ct)) return NotFound();
        var sb = new StringBuilder("Utc,ServerName,Step,Level,Message,DataJson\r\n");
        static string Cell(string? v)
        {
            // Prevent spreadsheet formula injection in diagnostic text and preserve CSV escaping.
            var t = v ?? "";
            if (t.Length > 0 && "=+-@\t\r".Contains(t[0])) t = "'" + t;
            return "\"" + t.Replace("\"", "\"\"") + "\"";
        }
        // Stream in pages to avoid an unbounded EF entity allocation.
        const int batch = 500;
        var lastId = 0;
        while (true)
        {
            var rows = await _db.MigrationQueueLogs.AsNoTracking()
                .Where(x => x.ItemId == id && x.Id > lastId).OrderBy(x => x.Id).Take(batch).ToListAsync(ct);
            if (rows.Count == 0) break;
            foreach (var x in rows)
            {
                sb.Append(Cell(x.Utc.ToString("O", CultureInfo.InvariantCulture))).Append(',')
                  .Append(Cell(x.ServerName)).Append(',')
                  .Append(Cell(x.Step)).Append(',').Append(Cell(x.Level)).Append(',')
                  .Append(Cell(x.Message)).Append(',').Append(Cell(x.DataJson)).Append("\r\n");
            }
            lastId = rows[^1].Id;
        }
        return File(new UTF8Encoding(true).GetBytes(sb.ToString()), "text/csv", $"BareProx-Migration-{id}.csv");
    }
}
