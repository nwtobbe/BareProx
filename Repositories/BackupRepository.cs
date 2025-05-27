using BareProx.Data;
using BareProx.Models;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;

namespace BareProx.Repositories
{
    public class BackupRepository : IBackupRepository
    {
        private readonly ApplicationDbContext _context;

        public BackupRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task StoreBackupInfoAsync(BackupRecord record)
        {
            _context.BackupRecords.Add(record);
            await _context.SaveChangesAsync();
        }
    }
}
