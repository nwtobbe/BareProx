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

using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using BareProx.Models;
using BareProx.Services;

// Alias to disambiguate if needed
using DbConfigModel = BareProx.Models.DatabaseConfigModels;
using Microsoft.Data.SqlClient;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;

namespace BareProx.Controllers
{
    [Route("Setup")]
    public class SetupController : Controller
    {
        private readonly IWebHostEnvironment _env;
        private readonly IEncryptionService _encryptionService;
        private readonly string _configFile;

        public SetupController(
            IWebHostEnvironment env,
            IEncryptionService encryptionService)
        {
            _env = env;
            _encryptionService = encryptionService;
            _configFile = Path.Combine("/config", "DatabaseConfig.json");
        }

        // Helper to check if configuration exists and is complete
        private bool IsConfigured()
        {
            if (!System.IO.File.Exists(_configFile))
                return false;
            try
            {
                var json = System.IO.File.ReadAllText(_configFile);
                var root = JObject.Parse(json)["DatabaseConfig"];
                if (root == null) return false;
                return new[] { "DbServer", "DbName", "DbUser", "DbPassword" }
                    .All(prop => !string.IsNullOrWhiteSpace((string)root[prop]));
            }
            catch
            {
                return false;
            }
        }

        // GET: /Setup/Config
        [HttpGet("Config")]
        public IActionResult Config()
        {
            // Redirect to home if already configured
            if (IsConfigured())
                return RedirectToAction("Index", "Home");

            DbConfigModel model;
            if (System.IO.File.Exists(_configFile))
            {
                var json = System.IO.File.ReadAllText(_configFile);
                var root = JObject.Parse(json);
                model = root["DatabaseConfig"]?.ToObject<DbConfigModel>() ?? new DbConfigModel();
                if (!string.IsNullOrWhiteSpace(model.DbPassword))
                {
                    try
                    {
                        model.DbPassword = _encryptionService.Decrypt(model.DbPassword);
                    }
                    catch
                    {
                        // ignore decryption errors, leave ciphertext
                    }
                }
            }
            else
            {
                model = new DbConfigModel();
            }

            return View("~/Views/Setup/Config.cshtml", model);
        }

        // POST: /Setup/Config
        [HttpPost("Config")]
        [ValidateAntiForgeryToken]
        public IActionResult Config(DbConfigModel model)
        {
            if (model.DbType == "Sqlite")
            {
                model.DbServer = "";
                model.DbUser = "";
                model.DbPassword = "";

                // ❗ Remove server-side validation errors for SQL fields
                ModelState.Remove(nameof(model.DbServer));
                ModelState.Remove(nameof(model.DbUser));
                ModelState.Remove(nameof(model.DbPassword));
            }

            if (!ModelState.IsValid)
                return View("~/Views/Setup/Config.cshtml", model);

            var root = System.IO.File.Exists(_configFile)
                ? JObject.Parse(System.IO.File.ReadAllText(_configFile))
                : new JObject();

            if (model.DbType == "SqlServer" && !string.IsNullOrWhiteSpace(model.DbPassword))
            {
                model.DbPassword = _encryptionService.Encrypt(model.DbPassword);
            }

            root["DatabaseConfig"] = JObject.FromObject(model);

            System.IO.File.WriteAllText(
                _configFile,
                root.ToString(Formatting.Indented)
            );

            TempData["SuccessMessage"] = "Configuration saved. Please restart the app to apply.";
            return RedirectToAction(nameof(Config));
        }




    }
}
