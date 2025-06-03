using Microsoft.Extensions.Options;
using BareProx.Models;
using TimeZoneConverter;

namespace BareProx.Services
{
    public class AppTimeZoneService : IAppTimeZoneService
    {
        private readonly IOptionsMonitor<ConfigSettings> _cfg;

        public AppTimeZoneService(IOptionsMonitor<ConfigSettings> cfg)
            => _cfg = cfg;

        private TimeZoneInfo ResolveTimeZone(string ianaCandidate, string windowsCandidate)
        {
            // 1) If the IANA field is non‐empty, try it directly
            if (!string.IsNullOrWhiteSpace(ianaCandidate))
            {
                try
                {
                    // If this is already a valid IANA on Linux (or Windows),
                    // TZConvert will return the correct TimeZoneInfo.
                    return TZConvert.GetTimeZoneInfo(ianaCandidate);
                }
                catch
                {
                    // If it’s invalid, we’ll fall back and try the Windows one below.
                }
            }

            // 2) If IANA failed or was empty, try the Windows field
            if (!string.IsNullOrWhiteSpace(windowsCandidate))
            {
                try
                {
                    // On Windows this will just do FindSystemTimeZoneById("W. Europe Standard Time").
                    // On Linux it will convert "W. Europe Standard Time"→"Europe/…"
                    // and return a valid IANA TimeZoneInfo.
                    return TZConvert.GetTimeZoneInfo(windowsCandidate);
                }
                catch
                {
                    // If that also fails, we fall back to Local.
                }
            }

            // 3) If neither worked (both empty/invalid), just use the local machine’s zone
            return TimeZoneInfo.Local;
        }

        public TimeZoneInfo AppTimeZone
        {
            get
            {
                var cfgVal = _cfg.CurrentValue;
                return ResolveTimeZone(
                    cfgVal.TimeZoneIana,
                    cfgVal.TimeZoneWindows
                );
            }
        }

        public DateTime ConvertUtcToApp(DateTime utc)
            => TimeZoneInfo.ConvertTimeFromUtc(utc, AppTimeZone);
    }
}
