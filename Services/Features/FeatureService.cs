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
using BareProx.Models;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Services.Features
{
    public sealed class FeatureService : IFeatureService
    {
        private readonly ApplicationDbContext _db;
        public FeatureService(ApplicationDbContext db) => _db = db;

        public async Task<bool> IsEnabledAsync(string key, CancellationToken ct = default)
        {
            var row = await _db.FeatureToggles.AsNoTracking()
                         .FirstOrDefaultAsync(x => x.Key == key, ct);
            return row?.Enabled == true;
        }

        public async Task SetAsync(string key, bool enabled, CancellationToken ct = default)
        {
            var row = await _db.FeatureToggles
                         .FirstOrDefaultAsync(x => x.Key == key, ct);
            if (row is null)
            {
                row = new FeatureToggle { Key = key, Enabled = enabled };
                _db.FeatureToggles.Add(row);
            }
            else
            {
                row.Enabled = enabled;
            }
            await _db.SaveChangesAsync(ct);
        }
    }
}
