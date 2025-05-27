using BareProx.Models;
using System.Threading.Tasks;

namespace BareProx.Repositories
{
    public interface IBackupRepository
    {
        Task StoreBackupInfoAsync(BackupRecord record);
    }
}
