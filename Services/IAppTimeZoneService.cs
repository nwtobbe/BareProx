namespace BareProx.Services
{
    public interface IAppTimeZoneService
    {
        TimeZoneInfo AppTimeZone { get; }
        DateTime ConvertUtcToApp(DateTime utc);
    }
}