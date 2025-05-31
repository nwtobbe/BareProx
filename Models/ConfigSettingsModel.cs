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
        public string DefaultTimeZone { get; set; } = TimeZoneInfo.Local.Id;
    }

    public class ConfigSettingsViewModel
    {
        [Display(Name = "Time Zone")]
        [Required]
        public string TimeZoneId { get; set; }
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
}
