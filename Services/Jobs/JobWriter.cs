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


using BareProx.Data;
using BareProx.Models;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Services.Jobs
{
    public sealed class JobService : IJobService
    {
        private readonly IDbContextFactory<ApplicationDbContext> _dbf;
        public JobService(IDbContextFactory<ApplicationDbContext> dbf) => _dbf = dbf;

        // ---- Status constants to avoid string typos -----------------------------------------
        private static class JobStatus
        {
            public const string Running = "Running";
            public const string Completed = "Completed";
            public const string Failed = "Failed";
            public const string Warning = "Warning";
        }

        private static class VmStatus
        {
            public const string Pending = "Pending";
            public const string Success = "Success";
            public const string Failed = "Failed";
            public const string Skipped = "Skipped";
            public const string Warning = "Warning";
        }

        // ---- Helpers ------------------------------------------------------------------------
        private static async Task<JobVmResult?> GetVmRowAsync(ApplicationDbContext db, int id, CancellationToken ct)
            => await db.JobVmResults.FindAsync(new object?[] { id }, ct);

        // ---------------- Job-level -----------------------------------------------------------
        public async Task<int> CreateJobAsync(string type, string relatedVm, string payloadJson, CancellationToken ct = default)
        {
            using var db = await _dbf.CreateDbContextAsync(ct);

            var job = new Job
            {
                Type = string.IsNullOrWhiteSpace(type) ? "Unknown" : type.Trim(),
                Status = JobStatus.Running,
                RelatedVm = relatedVm?.Trim(),
                PayloadJson = payloadJson,
                StartedAt = DateTime.UtcNow
            };

            db.Jobs.Add(job);
            await db.SaveChangesAsync(ct);
            return job.Id;
        }

        public async Task UpdateJobStatusAsync(int jobId, string status, string? error = null, CancellationToken ct = default)
        {
            using var db = await _dbf.CreateDbContextAsync(ct);
            var job = await db.Jobs.FindAsync(new object?[] { jobId }, ct);
            if (job == null) return;

            job.Status = string.IsNullOrWhiteSpace(status) ? job.Status : status.Trim();
            if (!string.IsNullOrWhiteSpace(error))
                job.ErrorMessage = error;

            await db.SaveChangesAsync(ct);
        }

        public async Task CompleteJobAsync(int jobId, CancellationToken ct = default)
            => await CompleteJobCoreAsync(jobId, JobStatus.Completed, ct);

        // explicit terminal status (e.g. "Warning")
        public async Task CompleteJobAsync(int jobId, string finalStatus, CancellationToken ct = default)
        {
            finalStatus = string.IsNullOrWhiteSpace(finalStatus) ? JobStatus.Completed : finalStatus.Trim();
            await CompleteJobCoreAsync(jobId, finalStatus, ct);
        }

        private async Task CompleteJobCoreAsync(int jobId, string finalStatus, CancellationToken ct)
        {
            using var db = await _dbf.CreateDbContextAsync(ct);
            var job = await db.Jobs.FindAsync(new object?[] { jobId }, ct);
            if (job == null) return;

            job.Status = finalStatus;
            job.CompletedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
        }

        public async Task<bool> FailJobAsync(int jobId, string message, CancellationToken ct = default)
        {
            using var db = await _dbf.CreateDbContextAsync(ct);
            var job = await db.Jobs.FindAsync(new object?[] { jobId }, ct);
            if (job == null) return false;

            job.Status = JobStatus.Failed;
            job.ErrorMessage = message;
            job.CompletedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
            return true; // fixed: indicate success
        }

        // --------------- Per-VM rows ---------------------------------------------------------
        public async Task<int> BeginVmAsync(int jobId, int vmid, string vmName, string hostName, string storageName, CancellationToken ct = default)
        {
            using var db = await _dbf.CreateDbContextAsync(ct);
            var row = new JobVmResult
            {
                JobId = jobId,
                VMID = vmid,
                VmName = vmName,
                HostName = hostName,
                StorageName = storageName,
                Status = VmStatus.Pending,
                StartedAtUtc = DateTime.UtcNow
            };
            db.JobVmResults.Add(row);
            await db.SaveChangesAsync(ct);
            return row.Id;
        }

        public async Task MarkVmSkippedAsync(int jobVmResultId, string reason, CancellationToken ct = default)
        {
            using var db = await _dbf.CreateDbContextAsync(ct);
            var row = await GetVmRowAsync(db, jobVmResultId, ct);
            if (row == null) return;

            row.Status = VmStatus.Skipped;
            row.Reason = reason;
            row.CompletedAtUtc = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
        }

        public async Task SetIoFreezeResultAsync(int jobVmResultId, bool attempted, bool succeeded, bool wasRunning, CancellationToken ct = default)
        {
            using var db = await _dbf.CreateDbContextAsync(ct);
            var row = await GetVmRowAsync(db, jobVmResultId, ct);
            if (row == null) return;

            row.IoFreezeAttempted = attempted;
            row.IoFreezeSucceeded = succeeded;
            row.WasRunning = wasRunning;

            await db.SaveChangesAsync(ct);
        }

        public async Task MarkVmSnapshotRequestedAsync(int jobVmResultId, string snapshotName, string? upid, CancellationToken ct = default)
        {
            using var db = await _dbf.CreateDbContextAsync(ct);
            var row = await GetVmRowAsync(db, jobVmResultId, ct);
            if (row == null) return;

            row.SnapshotRequested = true;
            row.ProxmoxSnapshotName = snapshotName;
            row.SnapshotUpid = upid;

            await db.SaveChangesAsync(ct);
        }

        public async Task MarkVmSnapshotTakenAsync(int jobVmResultId, CancellationToken ct = default)
        {
            using var db = await _dbf.CreateDbContextAsync(ct);
            var row = await GetVmRowAsync(db, jobVmResultId, ct);
            if (row == null) return;

            row.SnapshotTaken = true;

            await db.SaveChangesAsync(ct);
        }

        public async Task MarkVmSuccessAsync(int jobVmResultId, int? backupRecordId = null, CancellationToken ct = default)
        {
            using var db = await _dbf.CreateDbContextAsync(ct);
            var row = await GetVmRowAsync(db, jobVmResultId, ct);
            if (row == null) return;

            row.Status = VmStatus.Success;
            row.BackupRecordId = backupRecordId;
            row.CompletedAtUtc = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
        }

        public async Task MarkVmFailureAsync(int jobVmResultId, string error, CancellationToken ct = default)
        {
            using var db = await _dbf.CreateDbContextAsync(ct);
            var row = await GetVmRowAsync(db, jobVmResultId, ct);
            if (row == null) return;

            row.Status = VmStatus.Failed;
            row.ErrorMessage = error;
            row.CompletedAtUtc = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
        }

        // per-VM "Warning"
        public async Task MarkVmWarningAsync(int jobVmResultId, string? note = null, CancellationToken ct = default)
        {
            using var db = await _dbf.CreateDbContextAsync(ct);
            var row = await GetVmRowAsync(db, jobVmResultId, ct);
            if (row == null) return;

            row.Status = VmStatus.Warning;
            if (!string.IsNullOrWhiteSpace(note))
                row.Reason = note; // or row.ErrorMessage = note;
            row.CompletedAtUtc = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
        }

        // --------------- Per-VM logs ---------------------------------------------------------
        public async Task LogVmAsync(int jobVmResultId, string message, string level = "Info", CancellationToken ct = default)
        {
            using var db = await _dbf.CreateDbContextAsync(ct);
            db.JobVmLogs.Add(new JobVmLog
            {
                JobVmResultId = jobVmResultId,
                Level = level,
                Message = message,
                TimestampUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }
    }
}
