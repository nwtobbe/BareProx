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


using BareProx.Models;

namespace BareProx.Services.Jobs
{
    public interface IJobService
    {
        // Job-level
        Task<int> CreateJobAsync(string type, string relatedVm, string payloadJson, CancellationToken ct = default);

        /// <summary>
        /// Update transient progress/status text (not terminal).
        /// </summary>
        Task UpdateJobStatusAsync(int jobId, string status, string? error = null, CancellationToken ct = default);

        /// <summary>
        /// Complete with default terminal status "Completed" (backward compatible).
        /// </summary>
        Task CompleteJobAsync(int jobId, CancellationToken ct = default);

        /// <summary>
        /// Complete with explicit terminal status (e.g., "Warning", "Completed").
        /// </summary>
        Task CompleteJobAsync(int jobId, string finalStatus, CancellationToken ct = default);

        Task<bool> FailJobAsync(int jobId, string message, CancellationToken ct = default);

        // Per-VM result lifecycle
        Task<int> BeginVmAsync(int jobId, int vmid, string vmName, string hostName, string storageName, CancellationToken ct = default);
        Task MarkVmSkippedAsync(int jobVmResultId, string reason, CancellationToken ct = default);
        Task SetIoFreezeResultAsync(int jobVmResultId, bool attempted, bool succeeded, bool wasRunning, CancellationToken ct = default);

        Task MarkVmSnapshotRequestedAsync(
            int jobVmResultId,
            string snapshotName,
            string? upid,
            CancellationToken ct = default);

        Task MarkVmSnapshotTakenAsync(int jobVmResultId, CancellationToken ct = default);

        Task MarkVmSuccessAsync(int jobVmResultId, int? backupRecordId = null, CancellationToken ct = default);
        Task MarkVmFailureAsync(int jobVmResultId, string error, CancellationToken ct = default);
        Task MarkVmWarningAsync(int jobVmResultId, string? note = null, CancellationToken ct = default);
        // Per-VM logs
        Task LogVmAsync(int jobVmResultId, string message, string level = "Info", CancellationToken ct = default);
    }
}
