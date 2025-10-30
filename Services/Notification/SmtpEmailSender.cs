/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 *
 * Copyright (C) 2025 Tobias Modig
 *
 * This file is part of BareProx.
 *
 * BareProx is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * BareProx is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with BareProx. If not, see <https://www.gnu.org/licenses/>.
 */

using System.Net;
using System.Net.Mail;
using System.Text;
using BareProx.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Services.Notifications;

public sealed class SmtpEmailSender : IEmailSender
{
    private readonly ApplicationDbContext _db;
    private readonly IDataProtector _protector;

    public SmtpEmailSender(ApplicationDbContext db, IDataProtectionProvider dp)
    {
        _db = db;
        _protector = dp.CreateProtector("BareProx.EmailSettings.Password.v1");
    }

    public async Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        var s = await _db.EmailSettings.AsNoTracking().FirstAsync(e => e.Id == 1, ct);

        if (!s.Enabled)
            throw new InvalidOperationException("Email notifications are disabled.");

        if (string.IsNullOrWhiteSpace(s.SmtpHost))
            throw new InvalidOperationException("SMTP host is not configured.");

        // Fallback 'From' if user didn't configure one
        var from = string.IsNullOrWhiteSpace(s.From) ? "bareprox@localhost" : s.From.Trim();

        // Normalize security + port
        var sec = (s.SecurityMode ?? "StartTls").Trim();
        var port = s.SmtpPort > 0
            ? s.SmtpPort
            : (sec.Equals("SslTls", StringComparison.OrdinalIgnoreCase) ? 465
               : sec.Equals("StartTls", StringComparison.OrdinalIgnoreCase) ? 587
               : 25);

        var noAuth = sec.Equals("None", StringComparison.OrdinalIgnoreCase)
                     && port == 25
                     && string.IsNullOrWhiteSpace(s.Username);

        // Unprotect password only if we actually need it
        string? pwd = null;
        if (!noAuth && !string.IsNullOrWhiteSpace(s.Username) && !string.IsNullOrWhiteSpace(s.ProtectedPassword))
        {
            pwd = _protector.Unprotect(s.ProtectedPassword);
        }

        using var msg = new MailMessage
        {
            From = new MailAddress(from),
            Subject = subject ?? string.Empty,
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8,
            IsBodyHtml = true,
            Body = htmlBody ?? string.Empty
        };
        msg.To.Add(new MailAddress(to));

        using var client = new SmtpClient(s.SmtpHost!, port)
        {
            DeliveryMethod = SmtpDeliveryMethod.Network
        };

        // Security:
        // - EnableSsl=true: SmtpClient negotiates STARTTLS on 587 when advertised; on 465 it does SSL-on-connect.
        // - EnableSsl=false: plain text (used for "None"/25).
        client.EnableSsl = sec is "StartTls" or "SslTls";

        // Auth:
        if (noAuth)
        {
            // Anonymous: no credentials
            client.UseDefaultCredentials = false;
            client.Credentials = null;
        }
        else if (!string.IsNullOrWhiteSpace(s.Username))
        {
            client.UseDefaultCredentials = false;
            client.Credentials = new NetworkCredential(s.Username.Trim(), pwd ?? string.Empty);
        }
        else
        {
            // If security isn't "None" but no username was provided, leave credentials null.
            client.UseDefaultCredentials = false;
            client.Credentials = null;
        }

        await client.SendMailAsync(msg, ct);
    }
}
