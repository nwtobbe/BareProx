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

using System.Reflection;
using System.Text.RegularExpressions;
using BareProx.Data;
using BareProx.Services.Proxmox;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BareProx.Controllers
{
    public class HomeController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ProxmoxService _proxmoxService;

        public HomeController(ApplicationDbContext context, ProxmoxService proxmoxService)
        {
            _context = context;
            _proxmoxService = proxmoxService;
        }

        public async Task<IActionResult> Index()
        {
            var since = DateTime.UtcNow.AddHours(-24);

            ViewBag.RecentJobs = await _context.Jobs
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
                .ToListAsync();

            // Helper to strip trailing ", storage ... active" (or any "storage ..." tail) from LastStatusMessage
            static string TrimStorageTail(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return string.Empty;
                return Regex.Replace(s, @"\s*,?\s*storage\s+.*$", "", RegexOptions.IgnoreCase).Trim();
            }

            // Load clusters and hosts
            var clusters = await _context.ProxmoxClusters
                .Include(c => c.Hosts)
                .ToListAsync();

            // Project for the view
            var proxmoxClusters = clusters.Select(cluster => new
            {
                Name = cluster.Name,
                Status = cluster.LastStatus ?? "Unknown",
                Hosts = cluster.Hosts
                    .OrderBy(h => (h.Hostname ?? h.HostAddress))
                    .Select(h => new
                    {
                        Name = h.Hostname ?? h.HostAddress,
                        Status = (h.IsOnline == true) ? "Running" : "Offline",
                        LastMessage = TrimStorageTail(h.LastStatusMessage)
                    })
                    .ToList()
            })
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

        public IActionResult Help() => View();
    }
}
