using BareProx.Models;

namespace BareProx.Services
{
    public interface INetappVolumeService
    {
        Task<List<VserverDto>> GetVserversAndVolumesAsync(int controllerId, CancellationToken ct = default);
        Task<List<NetappMountInfo>> GetVolumesWithMountInfoAsync(int controllerId, CancellationToken ct = default);
    }
}
