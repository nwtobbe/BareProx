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
using System.Text.Json;

namespace BareProx.Services.Proxmox.Helpers
{
    public interface IProxmoxHelpersService
    {
        IEnumerable<ProxmoxHost> GetQueryableHosts(ProxmoxCluster cluster);
        ProxmoxHost GetHostByNodeName(ProxmoxCluster cluster, string nodeName);

        // Config parsing / rewriting
        public Dictionary<string, string> FlattenConfig(JsonElement config);
        string ExtractOldVmidFromConfig(Dictionary<string, string> payload);
        void UpdateDiskPathsInConfig(Dictionary<string, string> payload, string oldVmid, string newVmid, string cloneStorageName);

        // JSON helpers
        bool TryGetTruthy(JsonElement parent, string name);
        long TryGetInt64(JsonElement parent, string name);
        
        // Path & quoting helpers
        string ToPosix(string path);
        string GetDirPosix(string path);
        string EscapeBash(string value);

        // NEW:
        Task<List<ProxmoxCluster>> LoadAllClustersAsync(CancellationToken ct = default);

        // NEW: resolve (cluster,host) from a preloaded set
        (ProxmoxCluster Cluster, ProxmoxHost Host)? ResolveClusterAndHostFromLoaded(
            IEnumerable<ProxmoxCluster> clusters,
            string nodeOrAddress);

    }
}
