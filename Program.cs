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
using BareProx.Models;
using BareProx.Repositories;
using BareProx.Services;
using BareProx.Services.Background;
using BareProx.Services.Backup;
using BareProx.Services.Interceptors;
using BareProx.Services.Jobs;
using BareProx.Services.Migration;
using BareProx.Services.Netapp;
using BareProx.Services.Proxmox.Authentication;
using BareProx.Services.Proxmox.Helpers;
using BareProx.Services.Proxmox.Ops;
using BareProx.Services.Proxmox.Snapshots;
using BareProx.Services.Updates;
using BareProx.Services.Restore;
using BareProx.Services.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.File.Archive;
using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
// Alias for clarity
using DbConfigModel = BareProx.Models.DatabaseConfigModels;

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

static void CompressStaleLogs(string folder)
{
    var today = DateTime.UtcNow.Date;
    foreach (var path in Directory.EnumerateFiles(folder, "log-*.txt", SearchOption.TopDirectoryOnly))
    {
        var name = Path.GetFileName(path);
        // Skip today's file and any file that already has a .gz alongside it
        if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) continue;

        // Parse date from name: log-YYYYMMDD.txt  OR  log-YYYY-MM-DD.txt
        var stem = Path.GetFileNameWithoutExtension(name); // e.g. "log-20251015"
        var datePart = stem.Replace("log-", "");
        if (!(DateTime.TryParse(datePart, out var parsed) || DateTime.TryParseExact(datePart, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out parsed)))
            continue;

        if (parsed.Date >= today) continue; // don't touch today's active file

        var gzPath = Path.Combine(folder, name + ".gz");
        if (File.Exists(gzPath)) { File.Delete(path); continue; } // already compressed earlier

        try
        {
            using var src = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var dst = File.Create(gzPath);
            using var gz = new GZipStream(dst, CompressionLevel.Optimal, leaveOpen: true);
            src.CopyTo(gz);
        }
        catch
        {
            // file locked or transient issue → skip; Serilog/next startup can try again
            continue;
        }

        try { File.Delete(path); } catch { /* ignore */ }
    }
}

// Compress any leftover plain .txt from previous days
CompressStaleLogs(logFolder);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .MinimumLevel.Override("Default", LogEventLevel.Debug)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
    path: Path.Combine(logFolder, "log-.txt"),
    rollingInterval: RollingInterval.Day,
    // Optional: also roll if a file gets big
    fileSizeLimitBytes: 50 * 1024 * 1024, // 50 MB
    rollOnFileSizeLimit: true,
    retainedFileCountLimit: 60,            // keep last 60 rolled files (compressed)
    retainedFileTimeLimit: TimeSpan.FromDays(30), // or time-based retention
    shared: false,
    hooks: new ArchiveHooks(CompressionLevel.Optimal, logFolder),      // <- auto-compress on roll
    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}"
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
          "Microsoft": "Information",
          "Microsoft.EntityFrameworkCore": "Warning",
          "Microsoft.AspNetCore": "Warning",
          "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
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
    // Interceptor used by both pooled registrations
    builder.Services.AddSingleton<WalOnOpenConnectionInterceptor>();

    // 1) Scoped, pooled DbContext (used by controllers/Identity/seeding)
    builder.Services.AddDbContextPool<ApplicationDbContext>((sp, opts) =>
    {
        var cfg = sp.GetRequiredService<IOptionsMonitor<DbConfigModel>>().CurrentValue;
        var encSvc = sp.GetRequiredService<IEncryptionService>();

        if (cfg.DbType?.Equals("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
        {
            var dbPath = Path.Combine(dataPath, $"{cfg.DbName}.db");
            var connStr = $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared";
            opts.UseSqlite(connStr, sqliteOpts =>
            {
                sqliteOpts.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName);
            });
            opts.AddInterceptors(sp.GetRequiredService<WalOnOpenConnectionInterceptor>());
        }
        else
        {
            var csb = new SqlConnectionStringBuilder
            {
                DataSource = cfg.DbServer,
                InitialCatalog = cfg.DbName,
                UserID = cfg.DbUser,
                Password = encSvc.Decrypt(cfg.DbPassword),
                MultipleActiveResultSets = true,
                TrustServerCertificate = true
            };
            opts.UseSqlServer(csb.ConnectionString);
        }
    });

    // 2) Pooled factory (used by background services / your DbFactory wrapper)
    builder.Services.AddPooledDbContextFactory<ApplicationDbContext>((sp, opts) =>
    {
        var cfg = sp.GetRequiredService<IOptionsMonitor<DbConfigModel>>().CurrentValue;
        var encSvc = sp.GetRequiredService<IEncryptionService>();

        if (cfg.DbType?.Equals("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
        {
            var dbPath = Path.Combine(dataPath, $"{cfg.DbName}.db");
            var connStr = $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared";
            opts.UseSqlite(connStr, sqliteOpts =>
            {
                sqliteOpts.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName);
            });
            opts.AddInterceptors(sp.GetRequiredService<WalOnOpenConnectionInterceptor>());
        }
        else
        {
            var csb = new SqlConnectionStringBuilder
            {
                DataSource = cfg.DbServer,
                InitialCatalog = cfg.DbName,
                UserID = cfg.DbUser,
                Password = encSvc.Decrypt(cfg.DbPassword),
                MultipleActiveResultSets = true,
                TrustServerCertificate = true
            };
            opts.UseSqlServer(csb.ConnectionString);
        }
    });

    // 3) Wrapper for convenience (requires: using BareProx.Services.Data;)
    builder.Services.AddSingleton<IDbFactory, DbFactory>();


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
    builder.Services.AddDataProtection();
    builder.Services.AddScoped<BareProx.Services.Features.IFeatureService, BareProx.Services.Features.FeatureService>();
    builder.Services.AddScoped<IBackupRepository, BackupRepository>();
    builder.Services.AddScoped<IBackupService, BackupService>();
    builder.Services.AddScoped<IJobService, JobService>();
    builder.Services.AddScoped<INetappAuthService, NetappAuthService>();
    builder.Services.AddScoped<INetappFlexCloneService, NetappFlexCloneService>();
    builder.Services.AddScoped<INetappExportNFSService, NetappExportNFSService>();
    builder.Services.AddScoped<INetappVolumeService, NetappVolumeService>();
    builder.Services.AddScoped<INetappSnapmirrorService, NetappSnapmirrorService>();
    builder.Services.AddScoped<INetappSnapshotService, NetappSnapshotService>();
    builder.Services.AddScoped<ProxmoxService>();
    builder.Services.AddScoped<IProxmoxAuthenticator, ProxmoxAuthenticator>();
    builder.Services.AddScoped<IProxmoxHelpersService, ProxmoxHelpersService>();
    builder.Services.AddScoped<IRestoreService, RestoreService>();
    builder.Services.AddScoped<IProxmoxFileScanner, ProxmoxFileScanner>();
    builder.Services.AddSingleton<IMigrationQueueRunner, MigrationQueueRunner>();
    builder.Services.AddScoped<IMigrationExecutor, ProxmoxMigrationExecutor>();
    builder.Services.AddScoped<IProxmoxOpsService, ProxmoxOpsService>();
    builder.Services.AddScoped<IProxmoxSnapshotsService, ProxmoxSnapshotsService>();
    builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
    builder.Services.AddMemoryCache();
    builder.Services.AddHttpClient(nameof(UpdateChecker));
    builder.Services.AddSingleton<IUpdateChecker, UpdateChecker>();

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
    builder.Services.AddHostedService<CollectionService>();
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton<IProxmoxInventoryCache, ProxmoxInventoryCache>();

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
        listenOpts.UseHttps(httpsOpts =>
        {
            var sp = options.ApplicationServices; // <-- the real app provider
            var certService = sp.GetRequiredService<SelfSignedCertificateService>();
            var cert = certService.CurrentCertificate
                      ?? throw new InvalidOperationException("Failed to load or generate the self-signed certificate.");
            httpsOpts.ServerCertificate = cert;
        });
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
            const string user = "Overseer", pass = "P@ssw0rd!";
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
