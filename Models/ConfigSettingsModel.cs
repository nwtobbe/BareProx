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

using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.ComponentModel.DataAnnotations;

namespace BareProx.Models
{

    public class RegenerateCertViewModel
    {

        [Display(Name = "Current Certificate Subject")]
        [BindNever]
        public string CurrentSubject { get; set; }

        [Display(Name = "Valid From")]
        [BindNever]
        public DateTime? CurrentNotBefore { get; set; }

        [Display(Name = "Valid Until")]
        [BindNever]
        public DateTime? CurrentNotAfter { get; set; }
        [BindNever]

        [Display(Name = "Thumbprint")]
        public string CurrentThumbprint { get; set; }

        // -------------------------------------------------------
        // Part C: Fields for regenerating a new certificate
        // -------------------------------------------------------

        [Display(Name = "New Common Name (e.g. CN=localhost)")]
        [Required]
        public string RegenSubjectName { get; set; } = "CN=localhost";

        [Display(Name = "Validity (days)")]
        [Range(1, 1825, ErrorMessage = "ValidDays must be between 1 and 1825 days.")]
        public int RegenValidDays { get; set; } = 365;

        [Display(Name = "Subject Alternative Names (comma-separated)")]
        public string RegenSANs { get; set; } = "localhost";
    }
    public class ConfigSettings
    {
        // This used to be “DefaultTimeZone”
        public string TimeZoneWindows { get; set; } = "";

        // New field for IANA names
        public string TimeZoneIana { get; set; } = "";

        // (…other settings …)
    }

    public class ConfigSettingsViewModel
    {
        // This is what the user actually selects in the dropdown:
        // the Windows‐style time-zone ID. 
        // We mark it [Required] so that validation will fail if nothing is chosen.
        [Display(Name = "Time Zone")]
        [Required(ErrorMessage = "Please select a time zone.")]
        public string TimeZoneWindows { get; set; }

        // We also expose an IANA field in the ViewModel, 
        // but mark it [BindNever] so that it isn’t bound from the form.
        // Instead, your POST handler will call TZConvert.WindowsToIana(...) 
        // and fill this in before persisting.
        [BindNever]
        public string TimeZoneIana { get; set; } = "";
    }

    /// <summary>
    /// Holds both the Time Zone sub‐model and the Certificate‐Regeneration sub‐model,
    /// plus a list of TimeZones for populating the dropdown.
    /// </summary>
    public class SettingsPageViewModel
    {
        public SettingsPageViewModel()
        {
            Config = new ConfigSettingsViewModel();
            Regenerate = new RegenerateCertViewModel();
            TimeZones = new List<SelectListItem>();
        }

        /// <summary>
        /// Sub‐model for the “Time Zone” form (prefix: "Config")
        /// </summary>
        public ConfigSettingsViewModel Config { get; set; }

        /// <summary>
        /// Sub‐model for the “Regenerate Certificate” form (prefix: "Regenerate")
        /// </summary>
        public RegenerateCertViewModel Regenerate { get; set; }

        /// <summary>
        /// List of time zones for the dropdown
        /// </summary>
        public IEnumerable<SelectListItem> TimeZones { get; set; }
    }

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
}
