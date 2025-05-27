using System.ComponentModel.DataAnnotations;

namespace BareProx.Models
{

        public class ConfigSettingsViewModel
        {
            [Required]
            [Display(Name = "Default Time Zone")]
            public string TimeZoneId { get; set; }
        }
    public class ConfigSettings
    {
        public string DefaultTimeZone { get; set; } = TimeZoneInfo.Local.Id;
    }
}
