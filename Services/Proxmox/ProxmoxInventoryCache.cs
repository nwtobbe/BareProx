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

using System.Collections.Concurrent;
using BareProx.Models;
using BareProx.Services.Proxmox;
using Microsoft.Extensions.Caching.Memory;

namespace BareProx.Services;

public sealed class ProxmoxInventoryCache : IProxmoxInventoryCache
{
    private readonly IMemoryCache _memory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProxmoxInventoryCache> _logger;

    private const string VM_LIST_PREFIX = "px:vms:";
    private const string ELIGIBLE_PREFIX = "px:elig:";
    private static readonly TimeSpan DefaultAbsoluteExpiry = TimeSpan.FromMinutes(15);

    // Stampede guard per key
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _perKeyGates = new();

    // Track keys per cluster for broad invalidation
    private static readonly ConcurrentDictionary<int, ConcurrentDictionary<string, byte>> _clusterKeys = new();

    // Track ONLY "eligible" keys by (clusterId, controllerId) for targeted invalidation
    // Key format for eligible: $"{ELIGIBLE_PREFIX}{clusterId}:{controllerId}:{filterPart}"
    private static readonly ConcurrentDictionary<(int clusterId, int controllerId), ConcurrentDictionary<string, byte>>
        _controllerEligibleKeys = new();

    public ProxmoxInventoryCache(
        IMemoryCache memory,
        IServiceScopeFactory scopeFactory,
        ILogger<ProxmoxInventoryCache> logger)
    {
        _memory = memory;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // ---------- VM list by storages (existing) ----------
    public async Task<Dictionary<string, List<ProxmoxVM>>> GetVmsByStorageListAsync(
        ProxmoxCluster cluster,
        IEnumerable<string> storageNames,
        CancellationToken ct,
        TimeSpan? maxAge = null,
        bool forceRefresh = false)
        => await GetOrRefreshAsync(
            makeKey: () => MakeVmListKey(cluster.Id, storageNames),
            clusterId: cluster?.Id ?? 0,
            maxAge: maxAge,
            forceRefresh: forceRefresh,
            loader: async () =>
            {
                using var scope = _scopeFactory.CreateScope();
                var prox = scope.ServiceProvider.GetRequiredService<ProxmoxService>();
                var list = NormalizeStorages(storageNames);
                return await prox.GetVmsByStorageListAsyncToCache(cluster!, list, ct);
            });

    // ---------- Eligible backup storage with VMs (cached heavy path when no filter) ----------
    public async Task<Dictionary<string, List<ProxmoxVM>>> GetEligibleBackupStorageWithVMsAsync(
        ProxmoxCluster cluster,
        int netappControllerId,
        IEnumerable<string>? storageFilterNames,
        CancellationToken ct,
        TimeSpan? maxAge = null,
        bool forceRefresh = false)
        => await GetOrRefreshAsync(
            makeKey: () => MakeEligibleKey(cluster.Id, netappControllerId, storageFilterNames),
            clusterId: cluster?.Id ?? 0,
            maxAge: maxAge,
            forceRefresh: forceRefresh,
            loader: async () =>
            {
                using var scope = _scopeFactory.CreateScope();
                var prox = scope.ServiceProvider.GetRequiredService<ProxmoxService>();

                var filterList = NormalizeStorages(storageFilterNames ?? Enumerable.Empty<string>());
                if (filterList.Count > 0)
                {
                    // Cheap path: just list VMs on the requested storages
                    return await prox.GetVmsByStorageListAsyncToCache(cluster!, filterList, ct);
                }

                // Heavy path: discover eligible NetApp volumes ∩ Proxmox and map VMs
                // DO NOT call GetEligibleBackupStorageWithVMsAsync here to avoid recursion.
                return await prox.GetFilteredStorageWithVMsAsync(cluster!.Id, netappControllerId, ct);
            });

    // ---------- Invalidation ----------
    public void InvalidateCluster(int clusterId)
    {
        if (_clusterKeys.TryGetValue(clusterId, out var keys))
        {
            foreach (var kv in keys.Keys)
                _memory.Remove(kv);
            _clusterKeys.TryRemove(clusterId, out _);
        }

        // Also remove any controller-scoped keys for this cluster
        foreach (var entry in _controllerEligibleKeys.Keys)
        {
            if (entry.clusterId == clusterId &&
                _controllerEligibleKeys.TryGetValue(entry, out var ckeys))
            {
                foreach (var k in ckeys.Keys)
                    _memory.Remove(k);
                _controllerEligibleKeys.TryRemove(entry, out _);
            }
        }
    }

    public void InvalidateAll()
    {
        foreach (var kv in _clusterKeys)
            InvalidateCluster(kv.Key);
    }

    /// <summary>
    /// Invalidate only "eligible" cache entries for a specific (clusterId, controllerId).
    /// Optionally, provide a <paramref name="storageFilterToken"/> to only remove keys that contain that token
    /// (useful when your lookups used storageFilterNames and you toggled a single volume).
    /// </summary>
    public void InvalidateEligibleForController(int clusterId, int controllerId, string? storageFilterToken = null)
    {
        if (_controllerEligibleKeys.TryGetValue((clusterId, controllerId), out var keys))
        {
            foreach (var k in keys.Keys)
            {
                if (string.IsNullOrWhiteSpace(storageFilterToken) ||
                    k.Contains(storageFilterToken, StringComparison.OrdinalIgnoreCase))
                {
                    _memory.Remove(k);
                    keys.TryRemove(k, out _);
                }
            }

            if (keys.IsEmpty)
            {
                _controllerEligibleKeys.TryRemove((clusterId, controllerId), out _);
            }
        }
    }

    // ---------- shared core ----------
    private async Task<Dictionary<string, List<ProxmoxVM>>> GetOrRefreshAsync(
        Func<string> makeKey,
        int clusterId,
        TimeSpan? maxAge,
        bool forceRefresh,
        Func<Task<Dictionary<string, List<ProxmoxVM>>>> loader)
    {
        if (clusterId <= 0) throw new ArgumentException("Cluster Id must be positive.");

        var key = makeKey();

        if (!forceRefresh &&
            _memory.TryGetValue(key, out CacheEntry cached) &&
            (maxAge is null || cached.LastUpdated >= DateTimeOffset.UtcNow - maxAge.Value))
        {
            _logger.LogDebug("Cache HIT {Key}", key);
            return cached.Data;
        }

        _logger.LogDebug("Cache MISS/REFRESH {Key} (force={Force})", key, forceRefresh);

        var gate = _perKeyGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (!forceRefresh &&
                _memory.TryGetValue(key, out cached) &&
                (maxAge is null || cached.LastUpdated >= DateTimeOffset.UtcNow - maxAge.Value))
            {
                _logger.LogDebug("Cache LATE-HIT {Key}", key);
                return cached.Data;
            }

            var fresh = await loader();

            var entry = new CacheEntry
            {
                Data = fresh,
                LastUpdated = DateTimeOffset.UtcNow
            };

            _memory.Set(key, entry, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = DefaultAbsoluteExpiry,
                Size = EstimateSize(fresh) // required if cache has SizeLimit
            });

            TrackKey(clusterId, key);

            // If this is an "eligible" key, also track under (clusterId, controllerId)
            if (key.StartsWith(ELIGIBLE_PREFIX, StringComparison.Ordinal))
            {
                // Expected eligible format:
                // 0: "px:elig"
                // 1: "{clusterId}"
                // 2: "{controllerId}"
                // 3: "{filterPart}" (may be "__ALL__" or joined names)
                var parts = key.Split(':', 4); // keep filter as a single tail if it contains ':'
                if (parts.Length >= 3 && int.TryParse(parts[2], out var controllerId))
                {
                    TrackEligibleKey(clusterId, controllerId, key);
                }
            }

            return fresh;
        }
        finally
        {
            gate.Release();
        }
    }

    // ---------- key builders ----------
    private static string MakeVmListKey(int clusterId, IEnumerable<string> storages)
    {
        var list = NormalizeStorages(storages);
        var storagesPart = list.Count == 0 ? "__ALL__" : string.Join("|", list);
        return $"{VM_LIST_PREFIX}{clusterId}:{storagesPart}";
    }

    private static string MakeEligibleKey(int clusterId, int controllerId, IEnumerable<string>? storageFilterNames)
    {
        var list = NormalizeStorages(storageFilterNames ?? Enumerable.Empty<string>());
        var filterPart = list.Count == 0 ? "__ALL__" : string.Join("|", list);
        return $"{ELIGIBLE_PREFIX}{clusterId}:{controllerId}:{filterPart}";
    }

    private static List<string> NormalizeStorages(IEnumerable<string> storages)
        => (storages ?? Enumerable.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static long EstimateSize(Dictionary<string, List<ProxmoxVM>> data)
    {
        if (data is null || data.Count == 0) return 1;
        long vmCount = 0;
        foreach (var kv in data) vmCount += kv.Value?.Count ?? 0;
        return Math.Max(1, vmCount + data.Count);
    }

    private static void TrackKey(int clusterId, string key)
    {
        var bag = _clusterKeys.GetOrAdd(clusterId, _ => new ConcurrentDictionary<string, byte>());
        bag[key] = 1;
    }

    private static void TrackEligibleKey(int clusterId, int controllerId, string key)
    {
        var bag = _controllerEligibleKeys.GetOrAdd((clusterId, controllerId), _ => new ConcurrentDictionary<string, byte>());
        bag[key] = 1;
    }

    private sealed class CacheEntry
    {
        public required Dictionary<string, List<ProxmoxVM>> Data { get; init; }
        public required DateTimeOffset LastUpdated { get; init; }
    }
}
