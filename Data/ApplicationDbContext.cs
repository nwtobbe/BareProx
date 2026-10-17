/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 * Copyright (C) 2025-2026 Tobias Modig
 * SPDX-License-Identifier: AGPL-3.0-only
 */
using BareProx.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BareProx.Data
{
    public class ApplicationDbContext : IdentityDbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

        public DbSet<BareProx.Models.FeatureToggle> FeatureToggles { get; set; } = null!;
        public DbSet<EmailSettings> EmailSettings { get; set; } = null!;
        public DbSet<ProxmoxCluster> ProxmoxClusters { get; set; }
        public DbSet<ProxmoxHost> ProxmoxHosts { get; set; }
        public DbSet<NetappController> NetappControllers { get; set; }
        public DbSet<BackupSchedule> BackupSchedules { get; set; }
        public DbSet<BackupRecord> BackupRecords { get; set; }
        public DbSet<Job> Jobs { get; set; }
        public DbSet<JobVmResult> JobVmResults => Set<JobVmResult>();
        public DbSet<JobVmLog> JobVmLogs => Set<JobVmLog>();
        public DbSet<ProxSelectedStorage> SelectedStorages { get; set; }
        public DbSet<SelectedNetappVolumes> SelectedNetappVolumes { get; set; }
        public DbSet<SnapMirrorRelation> SnapMirrorRelations { get; set; } = null!;
        public DbSet<SnapMirrorPolicy> SnapMirrorPolicies { get; set; }
        public DbSet<SnapMirrorPolicyRetention> SnapMirrorPolicyRetentions { get; set; }
        public DbSet<MigrationSelection> MigrationSelections { get; set; }
        public DbSet<MigrationQueueItem> MigrationQueueItems { get; set; } = null!;
        public DbSet<MigrationQueueLog> MigrationQueueLogs => Set<MigrationQueueLog>();
        public DbSet<ProxmoxStorageDiskSnapshot> ProxmoxStorageDiskSnapshots { get; set; } = default!;
        public DbSet<MigrationVCenterSettings> MigrationVCenterSettings => Set<MigrationVCenterSettings>();

        // Stamp every newly-written migration event with a durable server name. This
        // covers the manual and automated pipelines without duplicating name lookups
        // in every logging call. Historical reports survive Clear queue.
        private void StampMigrationLogNames()
        {
            var pending = ChangeTracker.Entries<MigrationQueueLog>()
                .Where(e => e.State == EntityState.Added && string.IsNullOrWhiteSpace(e.Entity.ServerName))
                .Select(e => e.Entity).ToList();
            if (pending.Count == 0) return;

            var ids = pending.Select(x => x.ItemId).Distinct().ToArray();
            var names = ChangeTracker.Entries<MigrationQueueItem>()
                .Where(e => e.State != EntityState.Deleted && ids.Contains(e.Entity.Id))
                .Where(e => !string.IsNullOrWhiteSpace(e.Entity.Name))
                .ToDictionary(e => e.Entity.Id, e => e.Entity.Name!);
            foreach (var item in MigrationQueueItems.AsNoTracking()
                .Where(x => ids.Contains(x.Id))
                .Select(x => new { x.Id, x.Name }).ToList())
                if (!names.ContainsKey(item.Id) && !string.IsNullOrWhiteSpace(item.Name))
                    names[item.Id] = item.Name;

            foreach (var log in pending)
                if (names.TryGetValue(log.ItemId, out var name))
                    log.ServerName = name;
        }

        private async Task StampMigrationLogNamesAsync(CancellationToken ct)
        {
            var pending = ChangeTracker.Entries<MigrationQueueLog>()
                .Where(e => e.State == EntityState.Added && string.IsNullOrWhiteSpace(e.Entity.ServerName))
                .Select(e => e.Entity).ToList();
            if (pending.Count == 0) return;

            var ids = pending.Select(x => x.ItemId).Distinct().ToArray();
            var names = ChangeTracker.Entries<MigrationQueueItem>()
                .Where(e => e.State != EntityState.Deleted && ids.Contains(e.Entity.Id))
                .Where(e => !string.IsNullOrWhiteSpace(e.Entity.Name))
                .ToDictionary(e => e.Entity.Id, e => e.Entity.Name!);
            var stored = await MigrationQueueItems.AsNoTracking()
                .Where(x => ids.Contains(x.Id))
                .Select(x => new { x.Id, x.Name }).ToListAsync(ct);
            foreach (var item in stored)
                if (!names.ContainsKey(item.Id) && !string.IsNullOrWhiteSpace(item.Name))
                    names[item.Id] = item.Name;

            foreach (var log in pending)
                if (names.TryGetValue(log.ItemId, out var name))
                    log.ServerName = name;
        }

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            StampMigrationLogNames();
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
            => SaveChangesWithNamesAsync(acceptAllChangesOnSuccess, cancellationToken);

        private async Task<int> SaveChangesWithNamesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct)
        {
            await StampMigrationLogNamesAsync(ct);
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<MigrationQueueLog>().Property(x => x.ServerName).HasMaxLength(200);
            modelBuilder.Entity<MigrationVCenterSettings>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Server).HasMaxLength(512).IsRequired();
                entity.Property(x => x.Username).HasMaxLength(512).IsRequired();
                entity.Property(x => x.EncryptedPassword).IsRequired();
            });
            modelBuilder.Entity<SnapMirrorPolicy>()
                .HasMany(p => p.Retentions)
                .WithOne(r => r.Policy)
                .HasForeignKey(r => r.SnapMirrorPolicyId)
                .OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<SnapMirrorPolicy>().HasIndex(p => p.Uuid).IsUnique();
            modelBuilder.Entity<EmailSettings>().HasKey(e => e.Id);
            modelBuilder.Entity<EmailSettings>().HasData(new EmailSettings
            {
                Id = 1, Enabled = false, SmtpPort = 587,
                SecurityMode = "StartTls", MinSeverity = "Info"
            });
        }
    }
}
