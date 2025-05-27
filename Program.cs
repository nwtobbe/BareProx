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

// Alias for clarity
using DbConfigModel = BareProx.Models.DatabaseConfig;

var builder = WebApplication.CreateBuilder(args);

// --- Load JSON + ENV configs ----------------------------------------------
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile("DatabaseConfig.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables();

// --- Check if DB config exists --------------------------------------------
var rawDbCfg = builder.Configuration
    .GetSection("DatabaseConfig")
    .Get<DbConfigModel>() ?? new DbConfigModel();

bool dbIsEmpty = string.IsNullOrWhiteSpace(rawDbCfg.DbServer)
              && string.IsNullOrWhiteSpace(rawDbCfg.DbName)
              && string.IsNullOrWhiteSpace(rawDbCfg.DbUser)
              && string.IsNullOrWhiteSpace(rawDbCfg.DbPassword);

bool isConfigured = !dbIsEmpty;

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
    var path = Path.Combine(builder.Environment.ContentRootPath, "appsettings.json");
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
    });

    builder.Services.AddDatabaseDeveloperPageExceptionFilter();

    builder.Services.AddDefaultIdentity<IdentityUser>(opts =>
        opts.SignIn.RequireConfirmedAccount = true)
        .AddEntityFrameworkStores<ApplicationDbContext>();
    // ... other scoped & hosted services ...
}

// MVC & Pages
builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

var app = builder.Build();

// --- 3) Ensure Database Exists & Migrate ----------------------------------
if (isConfigured)
{
    using var scope = app.Services.CreateScope();
    var cfg = scope.ServiceProvider.GetRequiredService<IOptions<DbConfigModel>>().Value;
    var encSvc = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // 3a) Create DB if missing via master
    try
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
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to verify/create database '{DbName}'", cfg.DbName);
    }

    // 3b) Apply EF Core migrations
    try
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        dbContext.Database.Migrate();
        logger.LogInformation("Migrations applied to '{DbName}'.", cfg.DbName);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Applying migrations to '{DbName}' failed.", cfg.DbName);
    }

    // 3c) Seed default user
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
        var logger2 = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger2.LogError(ex, "Error during default user seed.");
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
