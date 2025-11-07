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
using BareProx.Services.Notifications;
using BareProx.Services.Proxmox;
using BareProx.Services.Proxmox.Authentication;
using BareProx.Services.Proxmox.Helpers;
using BareProx.Services.Proxmox.Ops;
using BareProx.Services.Proxmox.Restore;
using BareProx.Services.Proxmox.Snapshots;
using BareProx.Services.Restore;
using BareProx.Services.Updates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.File.Archive;
using System.IO.Compression;
using System.Security.Cryptography;
using DbConfigModel = BareProx.Models.DatabaseConfigModels;

var builder = WebApplication.CreateBuilder(args);

// ============================================================================
// Paths
// ============================================================================
var persistentPath = Path.Combine("/config"); // JSON configs
var dataPath = Path.Combine("/data");         // DB files
var logFolder = Path.Combine(persistentPath, "Logs");

Directory.CreateDirectory(persistentPath);
Directory.CreateDirectory(dataPath);
Directory.CreateDirectory(logFolder);

// ============================================================================
// Serilog + log compression
// ============================================================================
static void CompressStaleLogs(string folder)
{
    var today = DateTime.UtcNow.Date;

    foreach (var path in Directory.EnumerateFiles(folder, "log-*.txt", SearchOption.TopDirectoryOnly))
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            continue;

        var stem = Path.GetFileNameWithoutExtension(name);
        var datePart = stem.Replace("log-", "");

        if (!(DateTime.TryParse(datePart, out var parsed) ||
              DateTime.TryParseExact(datePart, "yyyyMMdd", null,
                  System.Globalization.DateTimeStyles.None, out parsed)))
            continue;

        if (parsed.Date >= today)
            continue;

        var gzPath = Path.Combine(folder, name + ".gz");
        if (File.Exists(gzPath))
        {
            try { File.Delete(path); } catch { }
            continue;
        }

        try
        {
            using var src = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var dst = File.Create(gzPath);
            using var gz = new GZipStream(dst, CompressionLevel.Optimal, leaveOpen: true);
            src.CopyTo(gz);
        }
        catch
        {
            continue;
        }

        try { File.Delete(path); } catch { }
    }
}

CompressStaleLogs(logFolder);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .MinimumLevel.Debug() // global floor
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
    .MinimumLevel.Override("BareProx", LogEventLevel.Debug) // your app namespace
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        path: Path.Combine(logFolder, "log-.txt"),
        rollingInterval: RollingInterval.Day,
        fileSizeLimitBytes: 50 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        retainedFileCountLimit: 60,
        retainedFileTimeLimit: TimeSpan.FromDays(30),
        shared: false,
        hooks: new ArchiveHooks(CompressionLevel.Optimal, logFolder),
        outputTemplate:
            "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}"
    )
    .CreateLogger();

builder.Host.UseSerilog();

// ============================================================================
// Ensure base config files exist
// ============================================================================
var appSettingsPath = Path.Combine(persistentPath, "appsettings.json");
var dbConfigPath = Path.Combine(persistentPath, "DatabaseConfig.json");

if (!File.Exists(appSettingsPath))
{
    File.WriteAllText(appSettingsPath,
    """
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
        "TimeZoneIana": "Europe/Berlin"
      },
      "AllowedHosts": "*",
      "CertificateOptions": {
        "OutputFolder": "/config/certs",
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
    File.WriteAllText(dbConfigPath,
    """
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

// ============================================================================
// Configuration sources
// ============================================================================
builder.Configuration
    .AddJsonFile(appSettingsPath, optional: true, reloadOnChange: true)
    .AddJsonFile(dbConfigPath, optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

builder.Services.Configure<ConfigSettings>(
    builder.Configuration.GetSection("ConfigSettings"));

builder.Services.Configure<CertificateOptions>(
    builder.Configuration.GetSection("CertificateOptions"));

builder.Services.AddSingleton<CertificateService>();

// ============================================================================
// DB Config & Encryption key bootstrap
// ============================================================================
var rawDbCfg = builder.Configuration
    .GetSection("DatabaseConfig")
    .Get<DbConfigModel>() ?? new DbConfigModel();

bool isConfigured =
    !string.IsNullOrWhiteSpace(rawDbCfg.DbType) &&
    !string.IsNullOrWhiteSpace(rawDbCfg.DbName) &&
    (rawDbCfg.DbType.Equals("Sqlite", StringComparison.OrdinalIgnoreCase) ||
     (!string.IsNullOrWhiteSpace(rawDbCfg.DbServer) &&
      !string.IsNullOrWhiteSpace(rawDbCfg.DbUser) &&
      !string.IsNullOrWhiteSpace(rawDbCfg.DbPassword)));

builder.Services.Configure<DbConfigModel>(
    builder.Configuration.GetSection("DatabaseConfig"));

var encSection = builder.Configuration.GetSection("Encryption");
var existingKey = encSection.GetValue<string>("Key");

if (string.IsNullOrWhiteSpace(existingKey))
{
    var keyBytes = RandomNumberGenerator.GetBytes(24);
    var newKey = Base64UrlEncoder.Encode(keyBytes);

    encSection["Key"] = newKey;

    var json = File.Exists(appSettingsPath)
        ? File.ReadAllText(appSettingsPath)
        : "{}";

    var settings = JObject.Parse(json);
    var encObj = (settings["Encryption"] as JObject)
                 ?? (JObject)(settings["Encryption"] = new JObject());

    encObj["Key"] = newKey;

    File.WriteAllText(appSettingsPath, settings.ToString(Newtonsoft.Json.Formatting.Indented));
}

// ============================================================================
// Services registration
// ============================================================================
builder.Services.AddSingleton<IEncryptionService, EncryptionService>();

if (isConfigured)
{
    // Shared interceptor for SQLite connections
    builder.Services.AddSingleton<WalOnOpenConnectionInterceptor>();

    // ---------------- Main ApplicationDbContext ----------------
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

    // ---------------- QueryDbContext -> /data/bareprox_query.db --------------
    var queryDbPath = Path.Combine(dataPath, "bareprox_query.db");
    var queryConnStr = $"Data Source={queryDbPath};Mode=ReadWriteCreate;Cache=Shared";

    builder.Services.AddDbContextPool<QueryDbContext>((sp, opts) =>
    {
        opts.UseSqlite(queryConnStr, sqliteOpts =>
        {
            sqliteOpts.MigrationsAssembly(typeof(QueryDbContext).Assembly.FullName);
        });
        opts.AddInterceptors(sp.GetRequiredService<WalOnOpenConnectionInterceptor>());
    });

    builder.Services.AddPooledDbContextFactory<QueryDbContext>((sp, opts) =>
    {
        opts.UseSqlite(queryConnStr, sqliteOpts =>
        {
            sqliteOpts.MigrationsAssembly(typeof(QueryDbContext).Assembly.FullName);
        });
        opts.AddInterceptors(sp.GetRequiredService<WalOnOpenConnectionInterceptor>());
    });

    // DbFactory convenience wrapper
    builder.Services.AddSingleton<IDbFactory, DbFactory>();
    builder.Services.AddSingleton<IQueryDbFactory, QueryDbFactory>();

    // Identity + EF
    builder.Services.AddDatabaseDeveloperPageExceptionFilter();

    builder.Services.AddDefaultIdentity<IdentityUser>(opts =>
    {
        opts.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>();

    // --- Repositories & Domain Services ------------------------------------
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
    builder.Services.AddScoped<IProxmoxSnapChains, ProxmoxSnapChains>();
    builder.Services.AddScoped<ProxmoxService>();
    builder.Services.AddScoped<IProxmoxRestore, ProxmoxRestore>();
    builder.Services.AddScoped<IProxmoxAuthenticator, ProxmoxAuthenticator>();
    builder.Services.AddScoped<IProxmoxHelpersService, ProxmoxHelpersService>();
    builder.Services.AddScoped<IRestoreService, RestoreService>();
    builder.Services.AddScoped<IProxmoxFileScanner, ProxmoxFileScanner>();
    builder.Services.AddSingleton<IMigrationQueueRunner, MigrationQueueRunner>();
    builder.Services.AddScoped<IStorageSnapshotCoordinator, StorageSnapshotCoordinator>();
    builder.Services.AddScoped<IMigrationExecutor, ProxmoxMigrationExecutor>();
    builder.Services.AddScoped<BareProx.Services.Proxmox.Migration.IProxmoxMigration, BareProx.Services.Proxmox.Migration.ProxmoxMigration>();
    builder.Services.AddScoped<IProxmoxOpsService, ProxmoxOpsService>();
    builder.Services.AddScoped<IProxmoxSnapshotsService, ProxmoxSnapshotsService>();
    builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
    builder.Services.AddMemoryCache();
    builder.Services.AddHttpClient(nameof(UpdateChecker));
    builder.Services.AddSingleton<IUpdateChecker, UpdateChecker>();
    builder.Services.AddScoped<IProxmoxClusterDiscoveryService, ProxmoxClusterDiscoveryService>();

    // Remote-API client
    builder.Services.AddSingleton<IRemoteApiClient, RemoteApiClient>();

    // HTTP clients (Proxmox + NetApp) with relaxed TLS
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

    // Background processing
    builder.Services.AddSingleton<IBackgroundServiceQueue, BackgroundServiceQueue>();
    builder.Services.AddHostedService<QueuedBackgroundService>();
    builder.Services.AddHostedService<ScheduledBackupService>();
    builder.Services.AddHostedService<JanitorService>();
    builder.Services.AddSingleton<IProxmoxInventoryCache, ProxmoxInventoryCache>();

    builder.Services.AddSingleton<CollectionService>();
    builder.Services.AddSingleton<ICollectionService>(sp =>
        sp.GetRequiredService<CollectionService>());
    builder.Services.AddHostedService(sp =>
        sp.GetRequiredService<CollectionService>());
}

// ============================================================================
// Time zone + MVC/Razor
// ============================================================================
builder.Services.Configure<ConfigSettings>(
    builder.Configuration.GetSection("ConfigSettings"));
builder.Services.AddSingleton<IAppTimeZoneService, AppTimeZoneService>();

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

// ============================================================================
// Auth policy in Release
// ============================================================================
#if DEBUG
builder.Logging.SetMinimumLevel(LogLevel.Debug);
#else
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

// ============================================================================
//
// Kestrel HTTPS (self-signed via CertificateService)
//
// ============================================================================
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(80);

    options.ListenAnyIP(443, listenOpts =>
    {
        listenOpts.UseHttps(httpsOpts =>
        {
            var sp = options.ApplicationServices;
            var certService = sp.GetRequiredService<CertificateService>();
            var cert = certService.CurrentCertificate
                      ?? throw new InvalidOperationException("Failed to load or generate the self-signed certificate.");
            httpsOpts.ServerCertificate = cert;
        });
    });
});

var app = builder.Build();

// ============================================================================
// Ensure Databases & Migrate
// ============================================================================
if (isConfigured)
{
    using var scope = app.Services.CreateScope();
    var cfg = scope.ServiceProvider.GetRequiredService<IOptions<DbConfigModel>>().Value;
    var encSvc = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // --- Main DB (ApplicationDbContext)
    try
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

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
            var dbPath = Path.Combine(dataPath, $"{cfg.DbName}.db");
            if (!File.Exists(dbPath))
            {
                File.Create(dbPath).Dispose();
                logger.LogInformation("SQLite database file '{File}' created.", dbPath);
            }
        }

        dbContext.Database.Migrate();
        logger.LogInformation("Migrations applied to '{DbName}'.", cfg.DbName);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Migration phase failed for '{DbName}'.", cfg.DbName);
    }

    // --- Query DB (QueryDbContext -> bareprox_query.db)
    try
    {
        var queryDbPath = Path.Combine(dataPath, "bareprox_query.db");
        if (!File.Exists(queryDbPath))
        {
            File.Create(queryDbPath).Dispose();
            logger.LogInformation("Query SQLite database file '{File}' created.", queryDbPath);
        }

        var queryCtx = scope.ServiceProvider.GetRequiredService<QueryDbContext>();
        queryCtx.Database.Migrate();
        logger.LogInformation("Migrations applied to query database '{File}'.", queryDbPath);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Migration phase failed for QueryDbContext (bareprox_query.db).");
    }

    // --- Seed default user if possible
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
            const string user = "Overseer";
            const string pass = "P@ssw0rd!";

            var exists = userMgr.FindByNameAsync(user).GetAwaiter().GetResult();
            if (exists == null)
            {
                var admin = new IdentityUser
                {
                    UserName = user,
                    Email = "admin@example.com",
                    EmailConfirmed = true
                };

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

// ============================================================================
// Redirect if not configured
// ============================================================================
if (!isConfigured)
{
    app.MapGet("/", ctx =>
    {
        ctx.Response.Redirect("/Setup/Config");
        return Task.CompletedTask;
    });
}

// ============================================================================
// Middleware pipeline
// ============================================================================
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
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

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapRazorPages();

app.Run();
