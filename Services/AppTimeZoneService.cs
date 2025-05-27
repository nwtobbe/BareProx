using Microsoft.Extensions.Options;
using BareProx.Models;

namespace BareProx.Services
{
    public class AppTimeZoneService : IAppTimeZoneService
    {
        private readonly IOptionsMonitor<ConfigSettings> _cfg;

        public AppTimeZoneService(IOptionsMonitor<ConfigSettings> cfg)
            => _cfg = cfg;

        public TimeZoneInfo AppTimeZone
            => TimeZoneInfo.FindSystemTimeZoneById(
                _cfg.CurrentValue.DefaultTimeZone
            );

        public DateTime ConvertUtcToApp(DateTime utc)
            => TimeZoneInfo.ConvertTimeFromUtc(utc, AppTimeZone);
    }
}
