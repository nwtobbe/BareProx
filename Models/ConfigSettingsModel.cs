/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 *
 * Copyright (C) 2025 Tobias Modig
 *
 * This file is part of BareProx.
 *
 * BareProx is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * BareProx is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with BareProx. If not, see <https://www.gnu.org/licenses/>.
 */


using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.ComponentModel.DataAnnotations;

namespace BareProx.Models
{

    // -------------------------------------------------------
    // Settings page aggregate VM (Time Zone + Cert + Email)
    // -------------------------------------------------------
    public class SettingsPageViewModel
    {
        public SettingsPageViewModel()
        {
            Config = new ConfigSettingsViewModel();
            Regenerate = new RegenerateCertViewModel();
            Email = new EmailSettingsViewModel();
            TimeZones = new List<SelectListItem>();
        }

        /// <summary>Sub-model for the “Time Zone” form (prefix: "Config")</summary>
        public ConfigSettingsViewModel Config { get; set; }

        /// <summary>Sub-model for the “Regenerate Certificate” form (prefix: "Regenerate")</summary>
        public RegenerateCertViewModel Regenerate { get; set; }

        /// <summary>Sub-model for the “Email Notifications” form (prefix: "Email")</summary>
        public EmailSettingsViewModel Email { get; set; }
        public UpdateSettingsViewModel Updates { get; set; } = new();
        public AcmeSettingsViewModel? Acme { get; set; }

        /// <summary>List of time zones for the dropdown</summary>
        public IEnumerable<SelectListItem> TimeZones { get; set; }
    }


    public class UpdateSettingsViewModel
    {
        public bool Enabled { get; set; }
        /// <summary>How often to check, in minutes. Default 360 (6h).</summary>
        public int FrequencyMinutes { get; set; } = 360;
    }

    // -------------------------------------------------------
    // Certificate regeneration view model
    // -------------------------------------------------------
    public class RegenerateCertViewModel
    {
        [Display(Name = "Current Certificate Subject")]
        [BindNever]
        public string CurrentSubject { get; set; } = string.Empty;

        [Display(Name = "Valid From")]
        [BindNever]
        public DateTime? CurrentNotBefore { get; set; }

        [Display(Name = "Valid Until")]
        [BindNever]
        public DateTime? CurrentNotAfter { get; set; }

        [Display(Name = "Thumbprint")]
        [BindNever]
        public string CurrentThumbprint { get; set; } = string.Empty;

        // Part C: Fields for regenerating a new certificate
        [Display(Name = "New Common Name (e.g. CN=localhost)")]
        [Required]
        public string RegenSubjectName { get; set; } = "CN=localhost";

        [Display(Name = "Validity (days)")]
        [Range(1, 1825, ErrorMessage = "ValidDays must be between 1 and 1825 days.")]
        public int RegenValidDays { get; set; } = 365;

        [Display(Name = "Subject Alternative Names (comma-separated)")]
        public string RegenSANs { get; set; } = "localhost";
    }

    // -------------------------------------------------------
    // Core config settings (persisted)
    // -------------------------------------------------------
    public class ConfigSettings
    {
        // This used to be “DefaultTimeZone”
        public string TimeZoneWindows { get; set; } = string.Empty;

        // New field for IANA names
        public string TimeZoneIana { get; set; } = string.Empty;

        // (…other settings …)
    }

    // -------------------------------------------------------
    // Time zone selection VM shown on Settings page
    // -------------------------------------------------------
    public class ConfigSettingsViewModel
    {
        [Display(Name = "Time Zone")]
        [Required(ErrorMessage = "Please select a time zone.")]
        public string TimeZoneWindows { get; set; } = string.Empty;

        // Filled by server-side mapping Windows -> IANA
        [BindNever]
        public string TimeZoneIana { get; set; } = string.Empty;
    }

    // -------------------------------------------------------
    // NEW: Email notification settings
    // -------------------------------------------------------
    public class EmailSettingsViewModel
    {
        [Display(Name = "Enable email notifications")]
        public bool Enabled { get; set; }

        [Display(Name = "SMTP Server")]
        [MaxLength(255)]
        public string? SmtpHost { get; set; }

        [Display(Name = "Port")]
        [Range(1, 65535)]
        public int SmtpPort { get; set; } = 587;

        /// <summary>
        /// "None" | "StartTls" | "SslTls"
        /// </summary>
        [Display(Name = "Security")]
        [RegularExpression("None|StartTls|SslTls", ErrorMessage = "Security must be None, StartTls, or SslTls.")]
        public string SecurityMode { get; set; } = "StartTls";

        [Display(Name = "Username")]
        [MaxLength(255)]
        public string? Username { get; set; }

        // Leave blank in POST to keep existing (handled in controller)
        [Display(Name = "Password / App Password")]
        [DataType(DataType.Password)]
        public string? Password { get; set; }

        [Display(Name = "From Address")]
        [EmailAddress]
        public string? From { get; set; }

        // Comma-separated
        [Display(Name = "Default Recipients")]
        [MaxLength(1024)]
        public string? DefaultRecipients { get; set; }

        // Notification switches
        [Display(Name = "Notify on backup success")]
        public bool OnBackupSuccess { get; set; }

        [Display(Name = "Notify on backup failure")]
        public bool OnBackupFailure { get; set; } = true;

        [Display(Name = "Notify on restore success")]
        public bool OnRestoreSuccess { get; set; }

        [Display(Name = "Notify on restore failure")]
        public bool OnRestoreFailure { get; set; } = true;

        [Display(Name = "Notify on warnings / system alerts")]
        public bool OnWarnings { get; set; } = true;

        /// <summary>
        /// "Info" | "Warning" | "Error" | "Critical"
        /// </summary>
        [Display(Name = "Minimum severity to notify")]
        [RegularExpression("Info|Warning|Error|Critical", ErrorMessage = "Severity must be Info, Warning, Error, or Critical.")]
        public string MinSeverity { get; set; } = "Info";
    }



    // -------------------------------------------------------
    // Other VMs
    // -------------------------------------------------------
    public sealed class ProxmoxHubViewModel
    {
        // Tab 1: list of clusters
        public IEnumerable<ProxmoxCluster> Clusters { get; set; } = new List<ProxmoxCluster>();

        // The selected cluster to manage in tabs 2 & 3
        public ProxmoxCluster? SelectedCluster { get; set; }

        // Tab 3: storage selection data (same as your SelectStorageViewModel)
        public SelectStorageViewModel? StorageView { get; set; }

        // Optional message bubble
        public string? Message { get; set; }

        // For pre-selecting a cluster via querystring ?selectedId=...
        public int? SelectedId { get; set; }
    }

    public class NetappHubViewModel
    {
        public IEnumerable<NetappController> Controllers { get; set; } = new List<NetappController>();
        public NetappController? Selected { get; set; }
        public int? SelectedId { get; set; }
        public string? Message { get; set; }
    }

    public class AcmeSettingsViewModel
    {
        [EmailAddress]
        public string? Email { get; set; }

        // Comma-separated domains: example.com,www.example.com
        public string? Domains { get; set; }

        // "Http01" for now (future: "Dns01")
        public string? Method { get; set; } = "Http01";

        public bool AgreeToTos { get; set; }

        // Read-only/status fields to show progress/errors in the UI
        public string? LastStatus { get; set; }
        public string? LastError { get; set; }
    }
}
