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

using System.Threading;
using System.Threading.Tasks;
using BareProx.Data;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Data
{
    public sealed class QueryDbFactory : IQueryDbFactory
    {
        private readonly IDbContextFactory<QueryDbContext> _factory;

        public QueryDbFactory(IDbContextFactory<QueryDbContext> factory)
        {
            _factory = factory;
        }

        public QueryDbContext Create()
        {
            return _factory.CreateDbContext();
        }

        public Task<QueryDbContext> CreateAsync(CancellationToken cancellationToken = default)
        {
            // For pooled factory this is effectively sync; keep the signature for symmetry.
            return Task.FromResult(_factory.CreateDbContext());
        }
    }
}
