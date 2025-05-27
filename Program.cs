using BareProx.Data;
using BareProx.Models;
using BareProx.Repositories;
using BareProx.Services;
using BareProx.Services.Background;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System;

// Alias to avoid ambiguity
using DbConfigModel = BareProx.Models.DatabaseConfig;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---------------------------------------------------------
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile("DatabaseConfig.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables();

// Bind the DatabaseConfig section for DI and runtime checks
builder.Services.Configure<DbConfigModel>(
    builder.Configuration.GetSection("DatabaseConfig")
);

// Retrieve raw config values to determine if setup is complete
var rawCfg = builder.Configuration
    .GetSection("DatabaseConfig")
    .Get<DbConfigModel>()
    ?? new DbConfigModel();

bool isConfigured =
    !string.IsNullOrWhiteSpace(rawCfg.DbServer) &&
    !string.IsNullOrWhiteSpace(rawCfg.DbName) &&
    !string.IsNullOrWhiteSpace(rawCfg.DbUser) &&
    !string.IsNullOrWhiteSpace(rawCfg.DbPassword);

// --- Core Services ---------------------------------------------------------
// Encryption must always be available for setup and DB startup
builder.Services.AddSingleton<IEncryptionService, EncryptionService>();

if (isConfigured)
{
    // --- DbContext with decrypted password ----------------------------------
    builder.Services.AddDbContext<ApplicationDbContext>((sp, options) =>
    {
        var cfg = sp.GetRequiredService<IOptionsMonitor<DbConfigModel>>().CurrentValue;
        var encSvc = sp.GetRequiredService<IEncryptionService>();
        // decrypt the stored password
        var pwd = encSvc.Decrypt(cfg.DbPassword);

        var connStr =
            $"Server={cfg.DbServer};" +
            $"Database={cfg.DbName};" +
            $"User Id={cfg.DbUser};" +
            $"Password={pwd};" +
            "MultipleActiveResultSets=True;TrustServerCertificate=True;";

        options.UseSqlServer(connStr);
    });

    // --- Identity -----------------------------------------------------------
    builder.Services.AddDatabaseDeveloperPageExceptionFilter();
    builder.Services.AddDefaultIdentity<IdentityUser>(options =>
        options.SignIn.RequireConfirmedAccount = true)
        .AddEntityFrameworkStores<ApplicationDbContext>();

    // --- Repositories & Domain Services --------------------------------------
    builder.Services.AddScoped<IBackupRepository, BackupRepository>();
    builder.Services.AddScoped<IBackupService, BackupService>();
    builder.Services.AddScoped<INetappService, NetappService>();
    builder.Services.AddScoped<ProxmoxService>();
    builder.Services.AddScoped<IRestoreService, RestoreService>();

    // --- Remote API Client --------------------------------------------------
    builder.Services.AddSingleton<IRemoteApiClient, RemoteApiClient>();

    // --- HTTP Clients --------------------------------------------------------
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

    // --- Background Processing ------------------------------------------------
    builder.Services.AddSingleton<IBackgroundServiceQueue, BackgroundServiceQueue>();
    builder.Services.AddHostedService<QueuedBackgroundService>();
    builder.Services.AddHostedService<ScheduledBackupService>();
    builder.Services.AddHostedService<JanitorService>();
}

// --- MVC & Razor Pages -----------------------------------------------------
builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

var app = builder.Build();

// --- Run Migrations if Configured ------------------------------------------
if (isConfigured)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    try
    {
        db.Database.Migrate();
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Database migration failed.");
    }
}
// Add default user
if (isConfigured)
{
    using var scope = app.Services.CreateScope();
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILogger<Program>>();
    var userManager = services.GetRequiredService<UserManager<IdentityUser>>();

    //— check if the user exists
    const string defaultUserName = "BinLadmin";
    const string defaultUserPassword = "P@ssw0rd!";

    var existing = userManager.FindByNameAsync(defaultUserName).GetAwaiter().GetResult();
    if (existing == null)
    {
        var admin = new IdentityUser
        {
            UserName = defaultUserName,
            Email = "admin@example.com",
            EmailConfirmed = true
        };
        var result = userManager.CreateAsync(admin, defaultUserPassword).GetAwaiter().GetResult();
        if (!result.Succeeded)
        {
            var errs = string.Join("; ", result.Errors.Select(e => e.Description));
            logger.LogError("Failed to create default user: {Errors}", errs);
        }
        else
        {
            logger.LogInformation("Seeded default user '{User}'", defaultUserName);
        }
    }
}
// --- Redirect to Setup if not Configured ----------------------------------
if (!isConfigured)
{
    app.MapGet("/", context =>
    {
        context.Response.Redirect("/Setup/Config");
        return Task.CompletedTask;
    });
}

// --- Middleware Pipeline ---------------------------------------------------
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
