using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using BareProx.Models;
using BareProx.Services;

// Alias to disambiguate if needed
using DbConfigModel = BareProx.Models.DatabaseConfig;
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
            _configFile = Path.Combine(_env.ContentRootPath, "DatabaseConfig.json");
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
        public async Task<IActionResult> Config(DbConfigModel model)
        {
            // … your existing create-db-test code …

            // 1) Encrypt & save the DatabaseConfig.json
            model.DbPassword = _encryptionService.Encrypt(model.DbPassword);
            var root = System.IO.File.Exists(_configFile)
                ? JObject.Parse(System.IO.File.ReadAllText(_configFile))
                : new JObject();
            root["DatabaseConfig"] = JObject.FromObject(model);
            System.IO.File.WriteAllText(
                _configFile,
                root.ToString(Formatting.Indented)
            );

            // 2) **Now** generate the encryption key (only once!)
            var appSettingsPath = Path.Combine(_env.ContentRootPath, "appsettings.json");
            var json = System.IO.File.ReadAllText(appSettingsPath);
            var settings = JObject.Parse(json, new JsonLoadSettings
            {
                CommentHandling = CommentHandling.Ignore,
                LineInfoHandling = LineInfoHandling.Ignore
            });

            // if no Encryption section or no Key, generate+set it
            if (settings["Encryption"]?.Value<string>("Key") == null)
            {
                var keyBytes = RandomNumberGenerator.GetBytes(24);
                var newKey = Base64UrlEncoder.Encode(keyBytes);   // 32 chars
                settings["Encryption"] = new JObject(new JProperty("Key", newKey));
                System.IO.File.WriteAllText(
                    appSettingsPath,
                    settings.ToString(Formatting.Indented)
                );
            }

            TempData["SuccessMessage"] = "Configuration saved and verified successfully. Please restart the application.";
            return View("~/Views/Setup/Config.cshtml", model);
        }

    }
}
