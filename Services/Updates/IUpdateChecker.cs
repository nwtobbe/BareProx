using System.Threading;
using System.Threading.Tasks;

namespace BareProx.Services.Updates
{
    public sealed record UpdateInfo(
        bool IsUpdateAvailable,
        string CurrentVersion,
        string LatestVersion,
        string WhatsNewHtml,   // already HTML-ified
        string? LatestDate     // optional, parsed from CHANGELOG headings if present
    );

    public interface IUpdateChecker
    {
        Task<UpdateInfo> CheckAsync(CancellationToken ct = default);
    }
}
