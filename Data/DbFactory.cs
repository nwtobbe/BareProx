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

using BareProx.Data;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Data
{
    public interface IDbFactory
    {
        Task<ApplicationDbContext> CreateAsync(CancellationToken ct = default);
    }

    public sealed class DbFactory : IDbFactory
    {
        private readonly IDbContextFactory<ApplicationDbContext> _inner;
        public DbFactory(IDbContextFactory<ApplicationDbContext> inner) => _inner = inner;

        public Task<ApplicationDbContext> CreateAsync(CancellationToken ct = default)
            => _inner.CreateDbContextAsync(ct);
    }
}
