using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;
using Npgsql; // << AGGIUNTO per loggare i dettagli connessione

var builder = WebApplication.CreateBuilder(args);

// --- LETTURA CONNECTION STRING (ENV -> appsettings -> errore chiaro) ---
var conn =
    Environment.GetEnvironmentVariable("CONNECTION_STRING")
    ?? Environment.GetEnvironmentVariable("DATABASE_URL") // << fallback opzionale
    ?? builder.Configuration.GetConnectionString("DefaultConnection");

if (string.IsNullOrWhiteSpace(conn))
    throw new InvalidOperationException(
        "No DB connection string found. Set ENV 'CONNECTION_STRING' (or 'DATABASE_URL') or ConnectionStrings:DefaultConnection in appsettings / user-secrets."
    );

// (facoltativo ma utile con Postgres)
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

// --- LOG: stampa host/porta/db/utente realmente usati ---
var csb = new NpgsqlConnectionStringBuilder(conn);
Console.WriteLine($"[DB] Host={csb.Host}; Port={csb.Port}; Database={csb.Database}; Username={csb.Username}; Pooling={csb.Pooling}");

// --- DbContext con pool + retry + timeout ---
builder.Services.AddDbContextPool<ApplicationDbContext>(options =>
    options.UseNpgsql(conn, o =>
    {
        o.CommandTimeout(60);                    // 60s
        o.EnableRetryOnFailure(5,               // 5 tentativi
            TimeSpan.FromSeconds(5),            // backoff 5s
            null);                              // errori transient qualunque
    }));

builder.Services.AddDbContextPool<ReadDbContext>(options =>
    options.UseNpgsql(conn, o =>
    {
        o.CommandTimeout(60);
        o.EnableRetryOnFailure(5, TimeSpan.FromSeconds(5), null);
    }));

// --- Identity ---
builder.Services
    .AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.Password.RequiredLength = 4;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireDigit = false;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders()
    .AddDefaultUI();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Identity/Account/Login";
    options.AccessDeniedPath = "/Identity/Account/AccessDenied";
});

// Razor Pages / MVC
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToAreaPage("Identity", "/Account/Login");
    options.Conventions.AllowAnonymousToAreaPage("Identity", "/Account/Register");
});
builder.Services.AddControllersWithViews();

var app = builder.Build();

app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                                       .CreateLogger("Global");
        logger.LogError(ex, "Unhandled exception on {Path}", ctx.Request.Path);
        throw; // lascia gestire a UseExceptionHandler in produzione
    }
});
// --- (Opzionale) applica migrazioni Identity all’avvio: evita 42P01 se mancano ---
using (var scope = app.Services.CreateScope())
{
    // CREA/AGGIORNA lo schema Identity se mancante
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await db.Database.MigrateAsync();

    var sp = scope.ServiceProvider;
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

app.Run();
