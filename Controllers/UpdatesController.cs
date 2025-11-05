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


using BareProx.Services.Updates;
using Microsoft.AspNetCore.Mvc;
using System.Threading;
using System.Threading.Tasks;

namespace BareProx.Controllers
{
    [Route("api/updates")]
    public class UpdatesController : Controller
    {
        private readonly IUpdateChecker _checker;
        public UpdatesController(IUpdateChecker checker) => _checker = checker;

        [HttpGet("status")]
        public async Task<IActionResult> Status(CancellationToken ct)
        {
            var info = await _checker.CheckAsync(ct);
            return Json(info);
        }
    }
}
