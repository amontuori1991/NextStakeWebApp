using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;

var builder = WebApplication.CreateBuilder(args);

// --- LETTURA CONNECTION STRING (ENV -> appsettings -> errore chiaro) ---
var conn =
    Environment.GetEnvironmentVariable("CONNECTION_STRING") ??
    builder.Configuration.GetConnectionString("DefaultConnection");

if (string.IsNullOrWhiteSpace(conn))
    throw new InvalidOperationException(
        "No DB connection string found. Set ENV 'CONNECTION_STRING' or ConnectionStrings:DefaultConnection in appsettings / user-secrets."
    );

// (facoltativo ma utile con Postgres)
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

// --- DbContext ---
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(conn));

builder.Services.AddDbContext<ReadDbContext>(options =>
    options.UseNpgsql(conn));

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

// --- Seed ruoli/utenti di servizio (usa DI già pronto) ---
using (var scope = app.Services.CreateScope())
{
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
        // Se vuoi anche la creazione automatica dell'utente se non esiste, dimmelo.
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
