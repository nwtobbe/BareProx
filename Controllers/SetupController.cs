using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using BareProx.Models;
using BareProx.Services;

// Alias to disambiguate if needed
using DbConfigModel = BareProx.Models.DatabaseConfig;

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
        public IActionResult Config(DbConfigModel model)
        {
            // Redirect if already configured
            if (IsConfigured())
                return RedirectToAction("Index", "Home");

            if (!ModelState.IsValid)
                return View("~/Views/Setup/Config.cshtml", model);

            var root = System.IO.File.Exists(_configFile)
                ? JObject.Parse(System.IO.File.ReadAllText(_configFile))
                : new JObject();

            // Encrypt before saving
            model.DbPassword = _encryptionService.Encrypt(model.DbPassword);

            root["DatabaseConfig"] = JObject.FromObject(model);
            System.IO.File.WriteAllText(
                _configFile,
                root.ToString(Newtonsoft.Json.Formatting.Indented)
            );

            TempData["SuccessMessage"] = "Configuration saved. Please restart the app to apply.";
            return RedirectToAction(nameof(Config));
        }
    }
}
