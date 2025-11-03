using System;
using System.Linq;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// --- Helper: legge da IConfiguration (UserSecrets/appsettings/ENV) con fallback a ConnectionStrings ---
static string GetConn(IConfiguration cfg, string key1, string key2, string? appsettingsKey = null)
{
    // 1) chiavi dirette nella config (UserSecrets, appsettings, ENV provider)
    var c =
        cfg[key1] ??
        cfg[key2] ??
        // 2) ENV “puro”
        Environment.GetEnvironmentVariable(key1) ??
        Environment.GetEnvironmentVariable(key2) ??
        // 3) fallback ConnectionStrings
        (appsettingsKey != null ? cfg.GetConnectionString(appsettingsKey) : null);

    if (string.IsNullOrWhiteSpace(c))
        throw new InvalidOperationException(
            $"Missing connection string. Set '{key1}' (o '{key2}') " +
            (appsettingsKey != null ? $"oppure ConnectionStrings:{appsettingsKey} in appsettings/user-secrets." : ".")
        );

    return c!;
}

// WRITE = Identity + migrazioni/seed | READ = queries (Events, analyses, matches)
var writeConn = GetConn(builder.Configuration, "CONNECTION_STRING_WRITE", "CONNECTION_STRING");
var readConn = GetConn(builder.Configuration, "CONNECTION_STRING_READ", "CONNECTION_STRING", "DefaultConnection");

// utile con Postgres 14+
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

// Loggare a startup quali host stiamo usando
var wcsb = new NpgsqlConnectionStringBuilder(writeConn);
var rcsb = new NpgsqlConnectionStringBuilder(readConn);
Console.WriteLine($"[DB:WRITE] Host={wcsb.Host}; Port={wcsb.Port}; Db={wcsb.Database}; User={wcsb.Username}");
Console.WriteLine($"[DB:READ ] Host={rcsb.Host}; Port={rcsb.Port}; Db={rcsb.Database}; User={rcsb.Username}");

// DbContext: pooling + retry
builder.Services.AddDbContextPool<ApplicationDbContext>(opt =>
    opt.UseNpgsql(writeConn, o =>
    {
        o.CommandTimeout(60);
        o.EnableRetryOnFailure(5, TimeSpan.FromSeconds(5), null);
    }));

builder.Services.AddDbContextPool<ReadDbContext>(opt =>
    opt.UseNpgsql(readConn, o =>
    {
        o.CommandTimeout(60);
        o.EnableRetryOnFailure(5, TimeSpan.FromSeconds(5), null);
    }));

builder.Services.AddHttpClient();
builder.Services.Configure<NextStakeWebApp.Services.TelegramOptions>(
    builder.Configuration.GetSection("Telegram"));
builder.Services.AddSingleton<NextStakeWebApp.Services.ITelegramService, NextStakeWebApp.Services.TelegramService>();

// Identity
builder.Services
    .AddIdentity<ApplicationUser, IdentityRole>(o =>
    {
        o.SignIn.RequireConfirmedAccount = false;
        o.Password.RequiredLength = 4;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireLowercase = false;
        o.Password.RequireDigit = false;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders()
    .AddDefaultUI();

builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/Identity/Account/Login";
    o.AccessDeniedPath = "/Identity/Account/AccessDenied";
});

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/");
    o.Conventions.AllowAnonymousToAreaPage("Identity", "/Account/Login");
    o.Conventions.AllowAnonymousToAreaPage("Identity", "/Account/Register");
});
builder.Services.AddControllersWithViews();

var app = builder.Build();

// Migrazioni/seed SOLO sul DB WRITE (evita 42P01 in avvio su ambienti vuoti)
using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;
    var appDb = sp.GetRequiredService<ApplicationDbContext>();

    // Esegue le migrazioni in modo resiliente (retry su errori transient)
    var strategy = appDb.Database.CreateExecutionStrategy();
    await strategy.ExecuteAsync(async () =>
    {
        try
        {
            var pending = await appDb.Database.GetPendingMigrationsAsync();
            if (pending.Any())
            {
                Console.WriteLine($"[MIGRATE] Applying {pending.Count()} migration(s)...");
                await appDb.Database.MigrateAsync();
                Console.WriteLine("[MIGRATE] Done.");
            }
            else
            {
                Console.WriteLine("[MIGRATE] No pending migrations.");
            }
        }
        catch (Npgsql.NpgsqlException ex)
        {
            Console.WriteLine($"[MIGRATE][WARN] transient error: {ex.Message}");
            // throw; // se vuoi fallire duro in caso di errore, decommenta
        }
    });

    // --- SEED RUOLI + SUPERADMIN ---
    var roleManager = sp.GetRequiredService<RoleManager<IdentityRole>>();
    var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
    var config = sp.GetRequiredService<IConfiguration>();

    const string superRole = "SuperAdmin";
    const string adminRole = "Admin";

    if (!await roleManager.RoleExistsAsync(superRole))
        await roleManager.CreateAsync(new IdentityRole(superRole));
    if (!await roleManager.RoleExistsAsync(adminRole))
        await roleManager.CreateAsync(new IdentityRole(adminRole));

    var email = config["SuperAdmin:Email"];          // es. nextstakeai@gmail.com
    var userName = config["SuperAdmin:UserName"] ?? email;
    var password = config["SuperAdmin:Password"] ?? "Admin#12345"; // fallback se non impostata

    if (!string.IsNullOrWhiteSpace(email))
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user == null)
        {
            user = new ApplicationUser
            {
                Email = email,
                UserName = userName,
                EmailConfirmed = true
            };
            var createRes = await userManager.CreateAsync(user, password);
            if (!createRes.Succeeded)
            {
                Console.WriteLine("[SEED][ERROR] SuperAdmin creation failed: " +
                                  string.Join("; ", createRes.Errors.Select(e => $"{e.Code}:{e.Description}")));
            }
        }

        if (user != null)
        {
            if (!await userManager.IsInRoleAsync(user, superRole))
                await userManager.AddToRoleAsync(user, superRole);
            if (!await userManager.IsInRoleAsync(user, adminRole))
                await userManager.AddToRoleAsync(user, adminRole);
        }
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

// Endpoint diagnostico minimale (opzionale)
app.MapGet("/_debug/db", () => Results.Json(new
{
    write = new { wcsb.Host, wcsb.Database, wcsb.Username },
    read = new { rcsb.Host, rcsb.Database, rcsb.Username }
}));

app.Run();
