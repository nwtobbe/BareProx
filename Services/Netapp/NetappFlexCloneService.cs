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



using System.Text.Json;
using System.Text;
using BareProx.Data;
using BareProx.Models;
using Microsoft.EntityFrameworkCore;
using System.Net;
using Polly;
using BareProx.Services.Netapp;

namespace BareProx.Services.Netapp
{
    public class NetappFlexCloneService : INetappFlexCloneService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<NetappFlexCloneService> _logger;
        private readonly IAppTimeZoneService _tz;
        private readonly INetappAuthService _authService;
        private readonly INetappVolumeService _volumeService;


        public NetappFlexCloneService(
            ApplicationDbContext context,
            ILogger<NetappFlexCloneService> logger,
            IAppTimeZoneService tz,
            INetappAuthService authService,
            INetappVolumeService volumeService)
        {
            _context = context;
            _logger = logger;
            _tz = tz;
            _authService = authService;
            _volumeService = volumeService;
        }

        public async Task<List<string>> ListFlexClonesAsync(int controllerId, CancellationToken ct = default)
        {
            var controller = await _context.NetappControllers.FindAsync(controllerId, ct);
            if (controller == null)
                throw new InvalidOperationException("Controller not found");

            var client = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            var url = $"{baseUrl}storage/volumes?name=restore_*";
            var resp = await client.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement
                      .GetProperty("records")
                      .EnumerateArray()
                      .Select(r => r.GetProperty("name").GetString())
                      .Where(n => !string.IsNullOrEmpty(n))
                      .Distinct()
                      .ToList();
        }
        public async Task<FlexCloneResult> CloneVolumeFromSnapshotAsync(
           string volumeName,
           string snapshotName,
           string cloneName,
           int controllerId,
           CancellationToken ct = default)
        {
            // 1) Fetch controller
            var controller = await _context.NetappControllers.FindAsync(controllerId, ct);
            if (controller == null)
                return new FlexCloneResult { Success = false, Message = "Controller not found." };

            var httpClient = _authService.CreateAuthenticatedClient(controller, out var baseUrl);

            // 2) Lookup volume UUID + SVM name
            var volLookupUrl = $"{baseUrl}storage/volumes?name={Uri.EscapeDataString(volumeName)}&fields=uuid,svm.name";
            var volResp = await httpClient.GetAsync(volLookupUrl, ct);
            if (!volResp.IsSuccessStatusCode)
            {
                var err = await volResp.Content.ReadAsStringAsync(ct);
                return new FlexCloneResult { Success = false, Message = $"Volume lookup failed: {err}" };
            }

            using var volDoc = JsonDocument.Parse(await volResp.Content.ReadAsStringAsync(ct));
            var volRecs = volDoc.RootElement.GetProperty("records");
            if (volRecs.GetArrayLength() == 0)
                return new FlexCloneResult { Success = false, Message = $"Volume '{volumeName}' not found." };

            var volEntry = volRecs[0];
            var volumeUuid = volEntry.GetProperty("uuid").GetString()!;
            var svmName = volEntry.GetProperty("svm").GetProperty("name").GetString()!;

            // 3) Lookup snapshot UUID under that volume (optional if cloning snapshot)
            var snapUuid = (string?)null;
            if (!string.IsNullOrWhiteSpace(snapshotName))
            {
                var snapLookupUrl = $"{baseUrl}storage/volumes/{volumeUuid}/snapshots?fields=name,uuid";
                var snapResp = await httpClient.GetAsync(snapLookupUrl, ct);
                if (!snapResp.IsSuccessStatusCode)
                {
                    var err = await snapResp.Content.ReadAsStringAsync(ct);
                    return new FlexCloneResult { Success = false, Message = $"Snapshot lookup failed: {err}" };
                }

                using var snapDoc = JsonDocument.Parse(await snapResp.Content.ReadAsStringAsync(ct));
                var snapRec = snapDoc.RootElement.GetProperty("records")
                    .EnumerateArray()
                    .FirstOrDefault(e => e.GetProperty("name").GetString() == snapshotName);

                if (snapRec.ValueKind != JsonValueKind.Object)
                    return new FlexCloneResult { Success = false, Message = $"Snapshot '{snapshotName}' not found." };

                snapUuid = snapRec.GetProperty("uuid").GetString();
            }

            // 4) Build the FlexClone payload
            var payload = new Dictionary<string, object>
            {
                ["name"] = cloneName,
                ["clone"] = new Dictionary<string, object>
                {
                    ["parent_volume"] = new { uuid = volumeUuid },
                    ["is_flexclone"] = true
                },
                ["svm"] = new { name = svmName }
            };
            if (snapUuid != null)
            {
                // only include parent_snapshot if cloning a snapshot
                ((Dictionary<string, object>)payload["clone"])["parent_snapshot"] = new { uuid = snapUuid };
            }

            var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );
            var cloneResp = await httpClient.PostAsync($"{baseUrl}storage/volumes", content, ct);

            // 5) Handle the 202 Accepted and parse the Job UUID
            if (cloneResp.StatusCode == HttpStatusCode.Accepted)
            {
                using var respDoc = JsonDocument.Parse(await cloneResp.Content.ReadAsStringAsync(ct));
                var jobUuid = respDoc
                    .RootElement
                    .GetProperty("job")
                    .GetProperty("uuid")
                    .GetString();

                return new FlexCloneResult
                {
                    Success = true,
                    CloneVolumeName = cloneName,
                    JobUuid = jobUuid    // new property to track the async job
                };
            }

            // 6) On failure, bubble up the message
            var body = await cloneResp.Content.ReadAsStringAsync(ct);
            return new FlexCloneResult
            {
                Success = false,
                Message = $"Clone failed ({(int)cloneResp.StatusCode}): {body}"
            };
        }


    }
}