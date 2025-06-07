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

using Microsoft.AspNetCore.Server.Kestrel.Https;
using System;
using System.IO;
using System.Security.Cryptography;
using BareProx.Data;
using BareProx.Models;
using BareProx.Repositories;
using BareProx.Services;
using BareProx.Services.Background;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Events;

// Alias for clarity
using DbConfigModel = BareProx.Models.DatabaseConfigModels;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

// --- Set paths for persistent and data storage -----------------------------
var persistentPath = Path.Combine("/config"); // json
var dataPath = Path.Combine("/data"); // db
var logFolder = Path.Combine(persistentPath, "Logs");


// Make sure paths exist
Directory.CreateDirectory(persistentPath);
Directory.CreateDirectory(dataPath);
Directory.CreateDirectory(logFolder);

// ─── 1) Configure Serilog ───────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .MinimumLevel.Override("Default", LogEventLevel.Debug)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        path: Path.Combine(logFolder, "log-.txt"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        shared: true,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}"
    )
    .CreateLogger();

builder.Host.UseSerilog();


//var connectionString = builder.Configuration.GetConnectionString("ApplicationDbContextConnection") ?? throw new InvalidOperationException("Connection string 'ApplicationDbContextConnection' not found.");;


// Ensure config files exist
var appSettingsPath = Path.Combine(persistentPath, "appsettings.json");
var dbConfigPath = Path.Combine(persistentPath, "DatabaseConfig.json");

if (!File.Exists(appSettingsPath))
{
    File.WriteAllText(appSettingsPath, """
    {
      "Logging": {
        "LogLevel": {
          "Default": "Information",
          "Microsoft.AspNetCore": "Warning"
        }
      },
          "ConfigSettings": {
      "TimeZoneWindows": "W. Europe Standard Time",
      "TimeZoneIana":    "Europe/Berlin"
    },
      "AllowedHosts": "*",
      "CertificateOptions": {
      "OutputFolder": "/config/Certs",
      "PfxFileName": "selfsigned.pfx",
      "PfxPassword": "changeit",
      "SubjectName": "CN=localhost",
      "ValidDays": 365
    }
    }
    """);
}

if (!File.Exists(dbConfigPath))
{
    File.WriteAllText(dbConfigPath, """
    {
    "DatabaseConfig": {
    "DbType": "",
    "DbServer": "",
    "DbName": "",
    "DbUser": "",
    "DbPassword": ""
      }
    }
    """);
}



// --- Load JSON + ENV configs ----------------------------------------------
builder.Configuration
    .AddJsonFile(Path.Combine(persistentPath, "appsettings.json"), optional: true, reloadOnChange: true)
    .AddJsonFile(Path.Combine(persistentPath, "DatabaseConfig.json"), optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

// --- Add any additional configuration sources here if needed
builder.Services.Configure<ConfigSettings>(
    builder.Configuration.GetSection("ConfigSettings")
);
// Bind the "CertificateOptions" section
builder.Services.Configure<CertificateOptions>(
    builder.Configuration.GetSection("CertificateOptions"));

// Register the service as a Singleton
builder.Services.AddSingleton<SelfSignedCertificateService>();


// --- Check if DB config exists --------------------------------------------
var rawDbCfg = builder.Configuration
    .GetSection("DatabaseConfig")
    .Get<DbConfigModel>() ?? new DbConfigModel();

//bool dbIsEmpty = string.IsNullOrWhiteSpace(rawDbCfg.DbServer)
//              && string.IsNullOrWhiteSpace(rawDbCfg.DbName)
//              && string.IsNullOrWhiteSpace(rawDbCfg.DbUser)
//              && string.IsNullOrWhiteSpace(rawDbCfg.DbPassword);

//bool isConfigured = !dbIsEmpty;
bool isConfigured =
    !string.IsNullOrWhiteSpace(rawDbCfg.DbType) &&
    !string.IsNullOrWhiteSpace(rawDbCfg.DbName) &&
    (rawDbCfg.DbType == "Sqlite" ||
     (!string.IsNullOrWhiteSpace(rawDbCfg.DbServer) &&
      !string.IsNullOrWhiteSpace(rawDbCfg.DbUser) &&
      !string.IsNullOrWhiteSpace(rawDbCfg.DbPassword)));

builder.Services.Configure<DbConfigModel>(
    builder.Configuration.GetSection("DatabaseConfig"));

// --- 1) Auto-generate Encryption Key if missing ----------------------------
var encSection = builder.Configuration.GetSection("Encryption");
var existingKey = encSection.GetValue<string>("Key");
if (string.IsNullOrWhiteSpace(existingKey))
{
    // 24 bytes → 32-char Base64Url key
    var keyBytes = RandomNumberGenerator.GetBytes(24);
    var newKey = Base64UrlEncoder.Encode(keyBytes);

    // update in-memory
    encSection["Key"] = newKey;
    Console.WriteLine($"[Init] Generated encryption key: {newKey}");

    // persist to appsettings.json
    var path = Path.Combine(persistentPath, "appsettings.json");
    var json = File.ReadAllText(path);
    var settings = JObject.Parse(json);

    if (settings["Encryption"]?.Type != JTokenType.Object)
        settings["Encryption"] = new JObject();

    settings["Encryption"]["Key"] = newKey;

    File.WriteAllText(path,
        settings.ToString(Formatting.Indented));
}

// --- 2) Register Services --------------------------------------------------
builder.Services.AddSingleton<IEncryptionService, EncryptionService>();

if (isConfigured)
{
    builder.Services.AddDbContext<ApplicationDbContext>((sp, opts) =>
    {
        var cfg = sp.GetRequiredService<IOptionsMonitor<DbConfigModel>>().CurrentValue;
        var encSvc = sp.GetRequiredService<IEncryptionService>();

        if (cfg.DbType?.Equals("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
        {
            var dbPath = Path.Combine(dataPath, $"{cfg.DbName}.db");
            var connStr = $"Data Source={dbPath}";
            opts.UseSqlite(connStr);
        }
        else
        {
            var pwd = encSvc.Decrypt(cfg.DbPassword);
            var csb = new SqlConnectionStringBuilder
            {
                DataSource = cfg.DbServer,
                InitialCatalog = cfg.DbName,
                UserID = cfg.DbUser,
                Password = pwd,
                MultipleActiveResultSets = true,
                TrustServerCertificate = true
            };
            opts.UseSqlServer(csb.ConnectionString);
        }
    });


    builder.Services.AddDatabaseDeveloperPageExceptionFilter();

    builder.Services.AddDefaultIdentity<IdentityUser>(opts =>
    {
        // users can sign in immediately after creation
        opts.SignIn.RequireConfirmedAccount = false;

        // optionally tweak other settings:
        // opts.User.RequireUniqueEmail = false;
        // opts.Password.RequireNonAlphanumeric = false;
    })
        .AddEntityFrameworkStores<ApplicationDbContext>();

    // --- Repositories & Domain Services ----------------------------------------
    builder.Services.AddScoped<IBackupRepository, BackupRepository>();
    builder.Services.AddScoped<IBackupService, BackupService>();
    builder.Services.AddScoped<INetappService, NetappService>();
    builder.Services.AddScoped<ProxmoxService>();
    builder.Services.AddScoped<IRestoreService, RestoreService>();


    // --- Remote API Client -----------------------------------------------------
    builder.Services.AddSingleton<IRemoteApiClient, RemoteApiClient>();

    // --- HTTP Clients ----------------------------------------------------------
    builder.Services.AddHttpClient("ProxmoxClient")
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        });

    builder.Services.AddHttpClient("NetappClient")
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        });

    // --- Background Processing -------------------------------------------------
    builder.Services.AddSingleton<IBackgroundServiceQueue, BackgroundServiceQueue>();
    builder.Services.AddHostedService<QueuedBackgroundService>();
    builder.Services.AddHostedService<ScheduledBackupService>();
    builder.Services.AddHostedService<JanitorService>();
    builder.Services.AddHostedService<SnapMirrorSyncService>();

}


// --- BIND & REGISTER the App‐TimeZone stuff ----------------------------
builder.Services.Configure<ConfigSettings>(
    builder.Configuration.GetSection("ConfigSettings")
);
builder.Services.AddSingleton<IAppTimeZoneService, AppTimeZoneService>();

// MVC & Pages
builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();


#if DEBUG
builder.Logging.SetMinimumLevel(LogLevel.Debug);
#else
//---Lock down-- - Fix _LoginPartial.cshtml to enable lockout and uncomment this section
if (isConfigured)
{

    builder.Services.AddAuthorization(options =>
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    });
}
builder.Logging.SetMinimumLevel(LogLevel.Warning);
#endif

// 4) Configure Kestrel to use the SelfSignedCertificateService for HTTPS
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(80);

    options.ListenAnyIP(443, listenOpts =>
    {
        var certService = builder.Services.BuildServiceProvider()
                                 .GetRequiredService<SelfSignedCertificateService>();

        var cert = certService.CurrentCertificate;
        if (cert == null)
        {
            throw new InvalidOperationException("Failed to load or generate the self‐signed certificate.");
        }

        listenOpts.UseHttps(cert);
    });
});


var app = builder.Build();

// --- 3) Ensure Database Exists & Migrate ----------------------------------
if (isConfigured)
{
    using var scope = app.Services.CreateScope();
    var cfg = scope.ServiceProvider.GetRequiredService<IOptions<DbConfigModel>>().Value;
    var encSvc = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Only try SQL Server CREATE DATABASE logic if it's not SQLite
        if (!string.Equals(cfg.DbType, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            var masterCsb = new SqlConnectionStringBuilder
            {
                DataSource = cfg.DbServer,
                InitialCatalog = "master",
                UserID = cfg.DbUser,
                Password = encSvc.Decrypt(cfg.DbPassword),
                TrustServerCertificate = true
            };
            using var masterConn = new SqlConnection(masterCsb.ConnectionString);
            masterConn.Open();

            var safeName = cfg.DbName.Replace("]", "]]");
            var sql = $@"
                IF DB_ID(N'{safeName}') IS NULL
                    CREATE DATABASE [{safeName}];
            ";
            using var cmd = new SqlCommand(sql, masterConn);
            cmd.ExecuteNonQuery();

            logger.LogInformation("Database '{DbName}' verified/created.", cfg.DbName);
        }
        else
        {
            // Ensure SQLite file exists before migrations (it'll be created by Migrate() if not)
            var dbPath = Path.Combine(dataPath, $"{cfg.DbName}.db");
            if (!File.Exists(dbPath))
            {
                File.Create(dbPath).Dispose(); // create and close the file immediately
                logger.LogInformation("SQLite database file '{File}' created.", dbPath);
            }
        }

        // ✅ Always apply EF Core migrations
        dbContext.Database.Migrate();
        logger.LogInformation("Migrations applied to '{DbName}'.", cfg.DbName);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Migration phase failed for '{DbName}'.", cfg.DbName);
    }

    // 3c) Seed user if DB is connectable
    try
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

        if (!dbContext.Database.CanConnect())
        {
            logger.LogWarning("DB unreachable; skipping default user seed.");
        }
        else
        {
            const string user = "BinLadmin", pass = "P@ssw0rd!";
            var exists = userMgr.FindByNameAsync(user).GetAwaiter().GetResult();
            if (exists == null)
            {
                var admin = new IdentityUser { UserName = user, Email = "admin@example.com", EmailConfirmed = true };
                var res = userMgr.CreateAsync(admin, pass).GetAwaiter().GetResult();
                if (res.Succeeded)
                    logger.LogInformation("Seeded default user '{User}'.", user);
                else
                    logger.LogError("Seeding user failed: {Errors}",
                        string.Join(";", res.Errors.Select(e => e.Description)));
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error during default user seed.");
    }
}


// --- 4) Redirect if not configured ----------------------------------------
if (!isConfigured)
{
    app.MapGet("/", ctx => { ctx.Response.Redirect("/Setup/Config"); return Task.CompletedTask; });
}


// --- 5) Middleware ---------------------------------------------------------
if (app.Environment.IsDevelopment())
    app.UseMigrationsEndPoint();
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
if (isConfigured)
{
    app.UseAuthentication();
    app.UseAuthorization();
}
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.MapRazorPages();

app.Run();
