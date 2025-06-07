/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 *
 * Copyright (C) 2025 Tobias Modig
 *
 * This file is part of BareProx.
 *
 * BareProx is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * BareProx is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with BareProx. If not, see <https://www.gnu.org/licenses/>.
 */

using BareProx.Models;
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

        public DbSet<SnapMirrorPolicy> SnapMirrorPolicies { get; set; }
        public DbSet<SnapMirrorPolicyRetention> SnapMirrorPolicyRetentions { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // SnapMirrorPolicy has many Retentions
            modelBuilder.Entity<SnapMirrorPolicy>()
                .HasMany(p => p.Retentions)
                .WithOne(r => r.Policy)
                .HasForeignKey(r => r.SnapMirrorPolicyId)
                .OnDelete(DeleteBehavior.Cascade);

            // (Optional) Unique index on UUID
            modelBuilder.Entity<SnapMirrorPolicy>()
                .HasIndex(p => p.Uuid)
                .IsUnique();
        }
    }
}
