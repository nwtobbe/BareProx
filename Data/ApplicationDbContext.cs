using BareProx.Models;
using BareProx.Services;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Data
{
    public class ApplicationDbContext : IdentityDbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }
        public DbSet<ProxmoxCluster> ProxmoxClusters { get; set; }
        public DbSet<ProxmoxHost> ProxmoxHosts { get; set; }
        public DbSet<NetappController> NetappControllers { get; set; }
        public DbSet<BackupSchedule> BackupSchedules { get; set; }
        public DbSet<BackupRecord> BackupRecords { get; set; }
        public DbSet<Job> Jobs { get; set; }
        public DbSet<ProxSelectedStorage> SelectedStorages { get; set; }
        public DbSet<SelectedNetappVolume> SelectedNetappVolumes { get; set; }
        public DbSet<NetappSnapshot> NetappSnapshots { get; set; } = null!;
        public DbSet<SnapMirrorRelation> SnapMirrorRelations { get; set; } = null!;

    }
}
