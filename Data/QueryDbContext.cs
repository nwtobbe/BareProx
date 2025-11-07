/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 *
 * Copyright (C) 2025 Tobias Modig
 *
 * This file is part of BareProx.
 *
 * BareProx is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * BareProx is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with BareProx. If not, see <https://www.gnu.org/licenses/>.
 */

using Microsoft.EntityFrameworkCore;

using BareProx.Models;

namespace BareProx.Data
{
    public sealed class QueryDbContext : DbContext
    {
        public QueryDbContext(DbContextOptions<QueryDbContext> options)
            : base(options)
        {
        }

        public DbSet<InventoryMetadata> InventoryMetadata => Set<InventoryMetadata>();
        public DbSet<InventoryStorage> InventoryStorages => Set<InventoryStorage>();
        public DbSet<InventoryVm> InventoryVms => Set<InventoryVm>();
        public DbSet<InventoryVmDisk> InventoryVmDisks => Set<InventoryVmDisk>();
        public DbSet<InventoryNetappVolume> InventoryNetappVolumes => Set<InventoryNetappVolume>();
        public DbSet<InventoryVolumeReplication> InventoryVolumeReplications => Set<InventoryVolumeReplication>();
        public DbSet<InventoryClusterStatus> InventoryClusterStatuses => Set<InventoryClusterStatus>();
        public DbSet<InventoryHostStatus> InventoryHostStatuses => Set<InventoryHostStatus>();
        public DbSet<NetappSnapshot> NetappSnapshots { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<InventoryMetadata>(e =>
            {
                e.HasKey(x => x.Key);
                e.Property(x => x.Key).IsRequired();
                e.Property(x => x.Value).IsRequired(false);
            });

            modelBuilder.Entity<InventoryStorage>(e =>
            {
                e.HasKey(x => new { x.ClusterId, x.StorageId });

                e.Property(x => x.ClusterId).IsRequired();
                e.Property(x => x.StorageId).IsRequired();
                e.Property(x => x.Type).IsRequired();
                e.Property(x => x.ContentFlags).IsRequired();
                e.Property(x => x.IsImageCapable).IsRequired();
                e.Property(x => x.Shared).IsRequired();
                e.Property(x => x.LastSeenUtc).IsRequired();
                e.Property(x => x.LastScanStatus).IsRequired();

                e.HasIndex(x => x.NetappVolumeUuid);
            });

            modelBuilder.Entity<InventoryVm>(e =>
            {
                e.HasKey(x => new { x.ClusterId, x.VmId });

                e.Property(x => x.ClusterId).IsRequired();
                e.Property(x => x.VmId).IsRequired();
                e.Property(x => x.Name).IsRequired();
                e.Property(x => x.NodeName).IsRequired();
                e.Property(x => x.Type).IsRequired();
                e.Property(x => x.Status).IsRequired();
                e.Property(x => x.LastSeenUtc).IsRequired();

                e.HasIndex(x => new { x.ClusterId, x.NodeName });
            });

            modelBuilder.Entity<InventoryVmDisk>(e =>
            {
                e.HasKey(x => new { x.ClusterId, x.VmId, x.StorageId, x.VolId });

                e.Property(x => x.ClusterId).IsRequired();
                e.Property(x => x.VmId).IsRequired();
                e.Property(x => x.StorageId).IsRequired();
                e.Property(x => x.VolId).IsRequired();
                e.Property(x => x.NodeName).IsRequired();
                e.Property(x => x.LastSeenUtc).IsRequired();

                e.HasIndex(x => new { x.ClusterId, x.StorageId });
                e.HasIndex(x => new { x.ClusterId, x.VmId });

                e.HasOne<InventoryVm>()
                    .WithMany()
                    .HasForeignKey(x => new { x.ClusterId, x.VmId })
                    .OnDelete(DeleteBehavior.Cascade);

                e.HasOne<InventoryStorage>()
                    .WithMany()
                    .HasForeignKey(x => new { x.ClusterId, x.StorageId })
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<InventoryNetappVolume>(e =>
            {
                e.HasKey(x => x.VolumeUuid);

                e.Property(x => x.VolumeUuid).IsRequired();
                e.Property(x => x.NetappControllerId).IsRequired();
                e.Property(x => x.SvmName).IsRequired();
                e.Property(x => x.VolumeName).IsRequired();
                e.Property(x => x.IsPrimary).IsRequired();
                e.Property(x => x.LastSeenUtc).IsRequired();

                e.HasIndex(x => x.NetappControllerId);
                e.HasIndex(x => x.JunctionPath);
            });

            modelBuilder.Entity<InventoryVolumeReplication>(e =>
            {
                e.HasKey(x => new { x.PrimaryVolumeUuid, x.SecondaryVolumeUuid });

                e.Property(x => x.PrimaryVolumeUuid).IsRequired();
                e.Property(x => x.SecondaryVolumeUuid).IsRequired();
                e.Property(x => x.LastSeenUtc).IsRequired();

                e.HasOne<InventoryNetappVolume>()
                    .WithMany()
                    .HasForeignKey(x => x.PrimaryVolumeUuid)
                    .OnDelete(DeleteBehavior.Cascade);

                e.HasOne<InventoryNetappVolume>()
                    .WithMany()
                    .HasForeignKey(x => x.SecondaryVolumeUuid)
                    .OnDelete(DeleteBehavior.Cascade);
            });


            modelBuilder.Entity<InventoryClusterStatus>()
                .HasKey(x => x.ClusterId);

            modelBuilder.Entity<InventoryHostStatus>()
                .HasKey(x => new { x.ClusterId, x.HostId });

            // Nice-to-have indexes for reads
            modelBuilder.Entity<InventoryHostStatus>()
                .HasIndex(x => x.ClusterId);

            modelBuilder.Entity<NetappSnapshot>()
               .HasIndex(x => new { x.JobId, x.SnapshotName })
               .IsUnique();
            modelBuilder.Entity<NetappSnapshot>()
              .HasIndex(x => x.PrimaryControllerId);
            modelBuilder.Entity<NetappSnapshot>()
              .HasIndex(x => x.SecondaryControllerId);
        }
    }

  
}

