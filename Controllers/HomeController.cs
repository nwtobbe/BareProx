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
using BareProx.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;

    public class HomeController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly ProxmoxService _proxmoxService;

    public HomeController(
        ApplicationDbContext context,
        ProxmoxService proxmoxService)
    {
        _context = context;
        _proxmoxService = proxmoxService;
        _proxmoxService = proxmoxService;
    }
    public async Task<IActionResult> Index()
    {
        var since = DateTime.UtcNow.AddHours(-24);
        ViewBag.RecentJobs = _context.Jobs
            .Where(j => j.StartedAt > since && (j.Status == "Failed" || j.Status == "Cancelled"))
            .OrderByDescending(j => j.StartedAt)
            .Select(j => new {
                j.StartedAt,
                j.Type,
                j.Status,
                j.RelatedVm,
                j.ErrorMessage
            })
            .ToList();

        // **Proxmox real data**
        // 1) Load clusters + hosts in one go
        var clusters = await _context.ProxmoxClusters
            .Include(c => c.Hosts)
            .ToListAsync();

        // 2) Project into your dynamic list
        var proxmoxClusters = clusters.Select(cluster => new
        {
            Name = cluster.Name,
            Status = cluster.LastStatus ?? "Unknown",   // e.g. "Cluster healthy (all nodes online)" or error text
            Hosts = cluster.Hosts
                .OrderBy(h => (h.Hostname ?? h.HostAddress))
                .Select(h => new
                {
                    Name = h.Hostname ?? h.HostAddress,
                    Status = (h.IsOnline == true) ? "Running" : "Offline"
                })
                .ToList()
        })
        .ToList();

        ViewBag.ProxmoxClusters = proxmoxClusters;

        // your existing NetApp assignments...
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

