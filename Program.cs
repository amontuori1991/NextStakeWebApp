using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// --- Helper: prendi le connection string da ENV con fallback a appsettings ---
static string GetConn(IConfiguration cfg, string envName1, string envName2, string? appsettingsKey = null)
{
    var c =
        Environment.GetEnvironmentVariable(envName1) ??
        Environment.GetEnvironmentVariable(envName2) ??
        (appsettingsKey != null ? cfg.GetConnectionString(appsettingsKey) : null);

    if (string.IsNullOrWhiteSpace(c))
        throw new InvalidOperationException(
            $"Missing connection string. Set ENV '{envName1}' (or '{envName2}') " +
            (appsettingsKey != null ? $"or ConnectionStrings:{appsettingsKey} in appsettings/user-secrets." : ".")
        );
    return c;
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
    await appDb.Database.MigrateAsync(); // aggiorna/tiene allineato lo schema Identity

    var roleManager = sp.GetRequiredService<RoleManager<IdentityRole>>();
    var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
    var config = sp.GetRequiredService<IConfiguration>();

    const string superRole = "SuperAdmin";
    if (!await roleManager.RoleExistsAsync(superRole))
        await roleManager.CreateAsync(new IdentityRole(superRole));

    var superEmail = config["SuperAdmin:Email"];
    if (!string.IsNullOrWhiteSpace(superEmail))
    {
        var user = await userManager.FindByEmailAsync(superEmail);
        if (user != null && !await userManager.IsInRoleAsync(user, superRole))
            await userManager.AddToRoleAsync(user, superRole);
        // (volendo: crea l'utente se non esiste)
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
