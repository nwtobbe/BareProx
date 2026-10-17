/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 * Copyright (C) 2025-2026 Tobias Modig
 * SPDX-License-Identifier: AGPL-3.0-only
 */
using System.Globalization;
using System.Net.Mail;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BareProx.Data;
using BareProx.Services;
using BareProx.Services.Notifications;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Services.Migration;

public sealed class MigrationReportMailSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Recipient { get; set; } = "";
    public string Frequency { get; set; } = "Daily"; // Daily, Weekly, Monthly
    public int DayOfWeek { get; set; } = 1; // ISO: 1 Monday..7 Sunday
    public int DayOfMonth { get; set; } = 1; // 1..31 (clamped to last day)
    public string LocalTime { get; set; } = "08:00"; // HH:mm, in configured app time zone
    public string Range { get; set; } = "PreviousPeriod"; // PreviousPeriod, LastDays, SinceLast, All
    public int LastDays { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastSuccessUtc { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public string? LastError { get; set; }
    public DateTime? RetryAfterUtc { get; set; }
    public DateTime NextDueUtc { get; set; }
}

public sealed class MigrationReportMailRequest
{
    public string Recipient { get; set; } = "";
    public string Range { get; set; } = "All"; // All, Custom, LastDays, SinceLast
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public int LastDays { get; set; } = 1;
}

public sealed class MigrationReportMailScheduleInput
{
    public string Recipient { get; set; } = "";
    public string Frequency { get; set; } = "Daily";
    public int DayOfWeek { get; set; } = 1;
    public int DayOfMonth { get; set; } = 1;
    public string LocalTime { get; set; } = "08:00";
    public string Range { get; set; } = "PreviousPeriod";
    public int LastDays { get; set; } = 1;
}

public sealed class MigrationReportMailService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IAppTimeZoneService _timeZone;
    private readonly ILogger<MigrationReportMailService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private string StorePath => Path.Combine("/config", "MigrationReportEmailSchedules.json");

    public MigrationReportMailService(IServiceScopeFactory scopes, IAppTimeZoneService timeZone,
        ILogger<MigrationReportMailService> logger)
    {
        _scopes = scopes;
        _timeZone = timeZone;
        _logger = logger;
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.EmailSettings.AsNoTracking().AnyAsync(x => x.Id == 1 && x.Enabled && x.SmtpHost != null && x.SmtpHost != "", ct);
    }

    public static string ValidateRecipient(string recipient)
    {
        recipient = (recipient ?? "").Trim();
        if (recipient.Length == 0 || recipient.Length > 254 || recipient.Contains(',') || recipient.Contains(';') || recipient.Contains('\n') || recipient.Contains('\r'))
            throw new ArgumentException("Enter one valid email address.");
        var address = new MailAddress(recipient);
        if (!string.Equals(address.Address, recipient, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Enter a valid email address without a display name.");
        return recipient;
    }

    public async Task<List<MigrationReportMailSchedule>> ListAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await LoadAsync(ct); }
        finally { _gate.Release(); }
    }

    private async Task<List<MigrationReportMailSchedule>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(StorePath)) return new();
        var json = await File.ReadAllTextAsync(StorePath, ct);
        return JsonSerializer.Deserialize<List<MigrationReportMailSchedule>>(json, _json) ?? new();
    }

    private async Task SaveAsync(List<MigrationReportMailSchedule> schedules, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        var temp = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(schedules, _json), ct);
            File.Move(temp, StorePath, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    // DST: invalid spring-forward times move to first valid minute; ambiguous fall-back times
    // use the later UTC occurrence. Each slot has one persisted NextDueUtc.
    private DateTime ToUtc(DateTime local)
    {
        var zone = _timeZone.AppTimeZone;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        if (zone.IsAmbiguousTime(local))
        {
            var offset = zone.GetAmbiguousTimeOffsets(local).Min();
            return new DateTimeOffset(local, offset).UtcDateTime;
        }
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    private DateTime NextSlotAfter(MigrationReportMailSchedule schedule, DateTime utc)
    {
        var local = _timeZone.ConvertUtcToApp(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        var time = TimeSpan.ParseExact(schedule.LocalTime, @"hh\:mm", CultureInfo.InvariantCulture);
        for (int i = 0; i < 430; i++)
        {
            var day = local.Date.AddDays(i);
            bool match = schedule.Frequency switch
            {
                "Daily" => true,
                "Weekly" => ((int)day.DayOfWeek + 6) % 7 + 1 == schedule.DayOfWeek,
                "Monthly" => day.Day == Math.Min(schedule.DayOfMonth, DateTime.DaysInMonth(day.Year, day.Month)),
                _ => false
            };
            if (!match) continue;
            var candidate = ToUtc(day + time);
            if (candidate > utc) return candidate;
        }
        throw new InvalidOperationException("Cannot calculate the next schedule occurrence.");
    }

    public async Task AddScheduleAsync(MigrationReportMailScheduleInput input, CancellationToken ct)
    {
        var email = ValidateRecipient(input.Recipient);
        if (input.Frequency is not ("Daily" or "Weekly" or "Monthly")) throw new ArgumentException("Invalid schedule frequency.");
        if (input.Range is not ("PreviousPeriod" or "LastDays" or "SinceLast" or "All")) throw new ArgumentException("Invalid report range.");
        if (input.DayOfWeek is < 1 or > 7 || input.DayOfMonth is < 1 or > 31 || input.LastDays is < 1 or > 3650)
            throw new ArgumentException("Invalid schedule day or range.");
        if (!TimeSpan.TryParseExact(input.LocalTime, @"hh\:mm", CultureInfo.InvariantCulture, out var at) || at >= TimeSpan.FromDays(1))
            throw new ArgumentException("Time must be HH:mm (24 hour).");
        if (!await IsConfiguredAsync(ct)) throw new InvalidOperationException("Configure and enable SMTP email in System Settings first.");
        var item = new MigrationReportMailSchedule
        {
            Recipient = email, Frequency = input.Frequency, DayOfWeek = input.DayOfWeek,
            DayOfMonth = input.DayOfMonth, LocalTime = input.LocalTime, Range = input.Range,
            LastDays = input.LastDays, CreatedUtc = DateTime.UtcNow
        };
        item.NextDueUtc = NextSlotAfter(item, item.CreatedUtc);
        await _gate.WaitAsync(ct);
        try
        {
            var schedules = await LoadAsync(ct);
            schedules.Add(item);
            await SaveAsync(schedules, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteScheduleAsync(Guid id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var schedules = await LoadAsync(ct);
            if (schedules.RemoveAll(x => x.Id == id) == 0) throw new KeyNotFoundException("Schedule not found.");
            await SaveAsync(schedules, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var schedules = await LoadAsync(ct);
            var schedule = schedules.SingleOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("Schedule not found.");
            schedule.Enabled = enabled;
            if (enabled) { schedule.NextDueUtc = NextSlotAfter(schedule, DateTime.UtcNow); schedule.RetryAfterUtc = null; schedule.LastError = null; }
            await SaveAsync(schedules, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task SendNowAsync(MigrationReportMailRequest request, CancellationToken ct)
    {
        if (!await IsConfiguredAsync(ct)) throw new InvalidOperationException("SMTP email must be configured and enabled.");
        request.Recipient = ValidateRecipient(request.Recipient);
        var now = DateTime.UtcNow;
        DateTime? from = null;
        if (request.Range == "Custom")
        {
            if (request.FromDate == null || request.ToDate == null || request.ToDate.Value.Date < request.FromDate.Value.Date)
                throw new ArgumentException("Specify a valid From and To date.");
            from = ToUtc(request.FromDate.Value.Date);
            now = ToUtc(request.ToDate.Value.Date.AddDays(1)); // upper bound exclusive
        }
        else if (request.Range == "LastDays")
        {
            if (request.LastDays is < 1 or > 3650) throw new ArgumentException("Days must be between 1 and 3650.");
            from = now.AddDays(-request.LastDays);
        }
        else if (request.Range != "All") throw new ArgumentException("Choose All, Last days or Custom.");
        await SendForRangeAsync(request.Recipient, from, now, ct);
    }

    private async Task SendForRangeAsync(string recipient, DateTime? from, DateTime until, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var logs = db.MigrationQueueLogs.AsNoTracking().Where(x => x.Utc < until);
        // Include histories with activity inside the selected range; duration uses the complete timeline.
        var ids = await logs.Where(x => !from.HasValue || x.Utc >= from.Value)
            .Select(x => x.ItemId).Distinct().ToListAsync(ct);
        var summaries = await db.MigrationQueueLogs.AsNoTracking().Where(x => ids.Contains(x.ItemId) && x.Utc < until)
            .GroupBy(x => x.ItemId).Select(g => new
            {
                ItemId = g.Key, First = g.Min(x => x.Utc), Last = g.Max(x => x.Utc),
                Events = g.Count(), Errors = g.Count(x => x.Level == "Error" || x.Level == "Critical"),
                Warnings = g.Count(x => x.Level == "Warning"),
                Name = g.Max(x => x.ServerName)
            }).OrderByDescending(x => x.Last).ToListAsync(ct);
        var timingLogs = await db.MigrationQueueLogs.AsNoTracking()
            .Where(x => ids.Contains(x.ItemId) && x.Utc < until)
            .OrderBy(x => x.Utc).ThenBy(x => x.Id).ToListAsync(ct);
        var durations = timingLogs.GroupBy(x => x.ItemId).ToDictionary(
            g => g.Key, g => MigrationExecutionTime.Measure(g).Duration);
        var queueNames = await db.MigrationQueueItems.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.Name, x.Status }).ToDictionaryAsync(x => x.Id, ct);
        var enc = HtmlEncoder.Default;
        var zone = _timeZone.AppTimeZone;
        string When(DateTime d) => enc.Encode(_timeZone.ConvertUtcToApp(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        var html = new StringBuilder();
        html.Append("<div style='font-family:Segoe UI,Arial,sans-serif;color:#152b42'><h2>BareProx migration report</h2>")
            .Append("<p>Period: ").Append(from.HasValue ? When(from.Value) : "All history").Append(" — ").Append(When(until))
            .Append(" (").Append(enc.Encode(zone.Id)).Append(")</p>")
            .Append("<p><strong>").Append(summaries.Count).Append(" migration histories</strong> with recorded activity in this period.</p>")
            .Append("<table style='border-collapse:collapse;width:100%'><thead><tr style='background:#164779;color:white'>")
            .Append("<th style='padding:8px'>Server</th><th>Migration ID</th><th>Time taken</th><th>Events</th><th>Errors</th><th>Warnings</th><th>Queue status</th></tr></thead><tbody>");
        foreach (var s in summaries)
        {
            queueNames.TryGetValue(s.ItemId, out var q);
            var name = !string.IsNullOrWhiteSpace(s.Name) ? s.Name : q?.Name;
            var duration = durations.TryGetValue(s.ItemId, out var elapsed) ? elapsed : TimeSpan.Zero;
            var durationText = duration.TotalDays >= 1 ? $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m" :
                duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s" :
                duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes}m {duration.Seconds}s" : $"{(int)duration.TotalSeconds}s";
            html.Append("<tr style='border-bottom:1px solid #ddd'><td style='padding:8px'>").Append(enc.Encode(name ?? "Unknown server"))
                .Append("</td><td>#").Append(s.ItemId).Append("</td><td>").Append(durationText)
                .Append("</td><td>").Append(s.Events).Append("</td><td>").Append(s.Errors)
                .Append("</td><td>").Append(s.Warnings).Append("</td><td>")
                .Append(enc.Encode(q?.Status ?? "Archived")).Append("</td></tr>");
        }
        html.Append("</tbody></table><p style='color:#687688;font-size:12px'>Time taken excludes queue waiting and, for newly processed automated migrations, time waiting between staging and cutover. Errors are event counts, not necessarily final migration failures.</p></div>");
        await sender.SendAsync(recipient, $"BareProx migration report - {summaries.Count} histories", html.ToString(), ct);
    }

    private DateTime? ScheduleStart(MigrationReportMailSchedule item, DateTime due)
    {
        var localDue = _timeZone.ConvertUtcToApp(DateTime.SpecifyKind(due, DateTimeKind.Utc));
        DateTime PreviousPeriodStart()
        {
            var startLocal = item.Frequency switch
            {
                "Daily" => localDue.AddDays(-1),
                "Weekly" => localDue.AddDays(-7),
                "Monthly" => localDue.AddMonths(-1),
                _ => localDue.AddDays(-1)
            };
            return ToUtc(startLocal);
        }
        return item.Range switch
        {
            "All" => null,
            "SinceLast" => item.LastSuccessUtc ?? item.CreatedUtc,
            "LastDays" => due.AddDays(-item.LastDays),
            "PreviousPeriod" => PreviousPeriodStart(),
            _ => throw new InvalidOperationException("Unknown range")
        };
    }

    // The persisted NextDueUtc prevents the same reporting slot from being emailed twice
    // across service restarts. On failure it remains due, and the service retries later.
    public async Task RunDueAsync(CancellationToken ct)
    {
        if (!await IsConfiguredAsync(ct)) return;
        await _gate.WaitAsync(ct);
        try
        {
            var schedules = await LoadAsync(ct);
            foreach (var item in schedules.Where(x => x.Enabled && x.NextDueUtc <= DateTime.UtcNow && (x.RetryAfterUtc == null || x.RetryAfterUtc <= DateTime.UtcNow)))
            {
                ct.ThrowIfCancellationRequested();
                var cutoff = DateTime.UtcNow;
                item.LastAttemptUtc = cutoff;
                try
                {
                    var from = ScheduleStart(item, item.NextDueUtc);
                    await SendForRangeAsync(item.Recipient, from, cutoff, ct);
                    item.LastSuccessUtc = cutoff;
                    item.LastError = null;
                    item.RetryAfterUtc = null;
                    item.NextDueUtc = NextSlotAfter(item, cutoff);
                    _logger.LogInformation("Migration report emailed for schedule {ScheduleId}.", item.Id);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    item.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                    _logger.LogError(ex, "Migration report email failed for schedule {ScheduleId}.", item.Id);
                    // Retry at next poll; circuit-break with 5 minutes between failed attempts.
                    item.RetryAfterUtc = DateTime.UtcNow.AddMinutes(5);
                }
                await SaveAsync(schedules, ct);
            }
        }
        finally { _gate.Release(); }
    }
}

public sealed class MigrationReportMailWorker : BackgroundService
{
    private readonly MigrationReportMailService _mail;
    private readonly ILogger<MigrationReportMailWorker> _logger;
    public MigrationReportMailWorker(MigrationReportMailService mail, ILogger<MigrationReportMailWorker> logger)
    { _mail = mail; _logger = logger; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let Program.cs complete its startup DB migrations before checking schedules.
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try { await _mail.RunDueAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Migration report schedule check failed."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
