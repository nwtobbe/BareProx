using Microsoft.Extensions.Options;
using BareProx.Models;
using TimeZoneConverter;
using System.Runtime.InteropServices;

namespace BareProx.Services
{
    public class AppTimeZoneService : IAppTimeZoneService
    {
        private readonly IOptionsMonitor<ConfigSettings> _cfg;

        public AppTimeZoneService(IOptionsMonitor<ConfigSettings> cfg)
            => _cfg = cfg;

        private string NormalizeTimeZoneId(string savedId)
        {
            if (string.IsNullOrWhiteSpace(savedId))
                return TimeZoneInfo.Local.Id;

            // 1) Try “as‐is” first
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(savedId);
                return tz.Id;
            }
            catch (TimeZoneNotFoundException)
            {
                // 2) If that failed, convert “other style” → OS style
                string alternate = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? TZConvert.IanaToWindows(savedId)
                    : TZConvert.WindowsToIana(savedId);

                try
                {
                    var tz2 = TimeZoneInfo.FindSystemTimeZoneById(alternate);
                    return tz2.Id;
                }
                catch
                {
                    // 3) Final fallback to local
                    return TimeZoneInfo.Local.Id;
                }
            }
        }

        public TimeZoneInfo AppTimeZone
        {
            get
            {
                // Grab the raw string from config
                var storedId = _cfg.CurrentValue.DefaultTimeZone;
                // Normalize to an ID that actually exists on this OS
                var validId = NormalizeTimeZoneId(storedId);
                return TimeZoneInfo.FindSystemTimeZoneById(validId);
            }
        }

        public DateTime ConvertUtcToApp(DateTime utc)
            => TimeZoneInfo.ConvertTimeFromUtc(utc, AppTimeZone);
    }
}
