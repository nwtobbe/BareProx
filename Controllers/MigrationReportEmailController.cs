/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 * Copyright (C) 2025-2026 Tobias Modig
 * SPDX-License-Identifier: AGPL-3.0-only
 */
using BareProx.Services;
using BareProx.Services.Migration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BareProx.Controllers;

public sealed class MigrationReportEmailPageModel
{
    public string ZoneName { get; set; } = "UTC";
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;
    public List<MigrationReportMailSchedule> Schedules { get; set; } = new();
    public MigrationReportMailRequest Immediate { get; set; } = new();
    public MigrationReportMailScheduleInput Scheduled { get; set; } = new();
    public string? Message { get; set; }
    public string? Error { get; set; }
}

#if !DEBUG
[Authorize]
#endif
[Route("Migration/Reports/Email")]
public sealed class MigrationReportEmailController : Controller
{
    private readonly MigrationReportMailService _mail;
    private readonly IAppTimeZoneService _zone;
    public MigrationReportEmailController(MigrationReportMailService mail, IAppTimeZoneService zone)
    { _mail = mail; _zone = zone; }

    [HttpGet("")]
    public async Task<IActionResult> Index(string? message, string? error, CancellationToken ct)
    {
        if (!await _mail.IsConfiguredAsync(ct))
        {
            TempData["MigrationReportEmailInfo"] = "Configure and enable SMTP in System Settings to use email reports.";
            return RedirectToAction("Index", "MigrationReports");
        }
        return View("~/Views/MigrationReports/Email.cshtml", new MigrationReportEmailPageModel
        {
            ZoneName = _zone.AppTimeZone.Id,
            Zone = _zone.AppTimeZone,
            Schedules = await _mail.ListAsync(ct),
            Message = message switch { "sent" => "Report sent successfully.", "created" => "Email schedule created.", "removed" => "Email schedule removed.", "enabled" => "Schedule updated.", _ => null },
            Error = error
        });
    }

    [HttpPost("Send")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Send([FromForm] MigrationReportMailRequest request, CancellationToken ct)
    {
        try
        {
            await _mail.SendNowAsync(request, ct);
            return RedirectToAction(nameof(Index), new { message = "sent" });
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or System.Net.Mail.SmtpException)
        { return RedirectToAction(nameof(Index), new { error = "Email could not be sent: " + ex.Message }); }
    }

    [HttpPost("Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add([FromForm] MigrationReportMailScheduleInput request, CancellationToken ct)
    {
        try
        {
            await _mail.AddScheduleAsync(request, ct);
            return RedirectToAction(nameof(Index), new { message = "created" });
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        { return RedirectToAction(nameof(Index), new { error = ex.Message }); }
    }

    [HttpPost("Remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        try { await _mail.DeleteScheduleAsync(id, ct); return RedirectToAction(nameof(Index), new { message = "removed" }); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPost("SetEnabled")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetEnabled(Guid id, bool enabled, CancellationToken ct)
    {
        try { await _mail.SetEnabledAsync(id, enabled, ct); return RedirectToAction(nameof(Index), new { message = "enabled" }); }
        catch (KeyNotFoundException) { return NotFound(); }
    }
}
