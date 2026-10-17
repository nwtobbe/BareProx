/*
 * BareProx - Backup and Restore Automation for Proxmox using NetApp
 *
 * Copyright (C) 2025-2026 Tobias Modig
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

using Microsoft.Extensions.Caching.Memory;
using System.Security.Cryptography;

namespace BareProx.Services.Migration;

/// <summary>One-use, short-lived proof of a successful guest authentication check.
/// It contains no Windows passwords and never persists beyond the process lifetime.</summary>
public static class VerifiedGuestProof
{
    public sealed record VerifiedCredentials(string Username, string EncryptedPassword);
    private sealed record Entry(string VmUuid, string Username, string UserIdentity, string EncryptedPassword);
    private static string Key(string token) => "BareProx:VerifiedGuest:" + token;
    private static string Normalize(string? v) => new string((v ?? "").Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();

    public static string Issue(IMemoryCache cache, string uuid, string username, string identity, string encryptedPassword)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        cache.Set(Key(token), new Entry(Normalize(uuid), username.Trim(), identity, encryptedPassword),
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15) });
        return token;
    }

    public static VerifiedCredentials? Consume(IMemoryCache cache, string? token, string? uuid, string? identity)
    {
        if (string.IsNullOrEmpty(token) || !cache.TryGetValue(Key(token), out Entry? entry) || entry == null)
            return null;
        if (entry.VmUuid.Length != 32 || entry.VmUuid != Normalize(uuid) || entry.UserIdentity != identity)
            return null;
        cache.Remove(Key(token));
        return new VerifiedCredentials(entry.Username, entry.EncryptedPassword);
    }
}
