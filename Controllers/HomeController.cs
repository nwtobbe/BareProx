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
using BareProx.Services.Proxmox;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Text.RegularExpressions;

namespace BareProx.Controllers
{
    public class HomeController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ProxmoxService _proxmoxService;
        private readonly IDbFactory _dbf;          // main DB (ApplicationDbContext)
        private readonly IQueryDbFactory _qdbf;    // query/inventory DB (QueryDbContext)

        public HomeController(ApplicationDbContext context, ProxmoxService proxmoxService, IDbFactory dbf, IQueryDbFactory qdbf)
        {
            _context = context;
            _proxmoxService = proxmoxService;
            _dbf = dbf;
            _qdbf = qdbf;
        }

        public async Task<IActionResult> Index()
        {
            var ct = HttpContext.RequestAborted;
            var since = DateTime.UtcNow.AddHours(-24);

            // ---------- MAIN DB (recent jobs) ----------
            await using var db = await _dbf.CreateAsync(ct);
            ViewBag.RecentJobs = await db.Jobs
                .AsNoTracking()
                .Where(j => j.StartedAt > since && (j.Status == "Failed" || j.Status == "Cancelled"))
                .OrderByDescending(j => j.StartedAt)
                .Select(j => new
                {
                    j.StartedAt,
                    j.Type,
                    j.Status,
                    j.RelatedVm,
                    j.ErrorMessage
                })
                .ToListAsync(ct);

            // Helper to strip trailing ", storage ... active" (or any "storage ..." tail)
            static string TrimStorageTail(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return string.Empty;
                return Regex.Replace(s, @"\s*,?\s*storage\s+.*$", "", RegexOptions.IgnoreCase).Trim();
            }

            // ---------- QUERY DB (cluster + host status) ----------
            await using var qdb = await _qdbf.CreateAsync(ct);

            var clusters = await qdb.InventoryClusterStatuses
                .AsNoTracking()
                .ToListAsync(ct);

            var clusterIds = clusters.Select(c => c.ClusterId).ToList();

            var hostStatuses = await qdb.InventoryHostStatuses
                .AsNoTracking()
                .Where(h => clusterIds.Contains(h.ClusterId))
                .ToListAsync(ct);

            // group hosts by cluster and project to dynamic
            var hostsByCluster = hostStatuses
                .GroupBy(h => h.ClusterId)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(h => string.IsNullOrWhiteSpace(h.Hostname) ? h.HostAddress : h.Hostname)
                          .Select(h => new
                          {
                              Name = string.IsNullOrWhiteSpace(h.Hostname) ? h.HostAddress : h.Hostname,
                              Status = h.IsOnline ? "Running" : "Offline",
                              LastMessage = TrimStorageTail(h.LastStatusMessage)
                          })
                          .Cast<dynamic>()      // inner list dynamic
                          .ToList()
                );

            // outer projection to dynamic for the view
            var proxmoxClusters = clusters
                .OrderBy(c => c.ClusterName ?? $"Cluster {c.ClusterId}")
                .Select(c => new
                {
                    Name = c.ClusterName ?? $"Cluster {c.ClusterId}",
                    Status = c.LastStatus ?? "Unknown",
                    Hosts = hostsByCluster.TryGetValue(c.ClusterId, out var list) ? list : new List<dynamic>()
                })
                .Cast<dynamic>()              // outer list dynamic
                .ToList();

            ViewBag.ProxmoxClusters = proxmoxClusters;

            return View();
        }



        public IActionResult About()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
            var licensePath = Path.Combine(Directory.GetCurrentDirectory(), "LICENSE");
            var licenseText = System.IO.File.Exists(licensePath)
                ? System.IO.File.ReadAllText(licensePath)
                : "License file not found.";

            ViewData["Version"] = version;
            ViewData["LicenseText"] = licenseText;
            return View();
        }

            [HttpGet]
            [AllowAnonymous]
            public IActionResult Privacy()
            {
                // If you want to pass dynamic info (version, contact), do it here
                ViewBag.ProductName = "BareProx";
                ViewBag.ContactEmail = "nwtobbe@gmail.com"; 
                return View();
            }

        public IActionResult Help() => View();
    }
}
