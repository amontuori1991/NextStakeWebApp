using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models; // << aggiungi questo using

var builder = WebApplication.CreateBuilder(args);

var connString = builder.Configuration.GetConnectionString("DefaultConnection");

// DbContext SOLO per Identity (migrazioni qui)
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connString));

// DbContext lettura tabelle esistenti
builder.Services.AddDbContext<ReadDbContext>(options =>
    options.UseNpgsql(connString));

// ✅ Identity con ApplicationUser
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
    .AddDefaultUI(); // << necessario per servire /Identity/Account/Login
// Imposta le route del cookie di autenticazione
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Identity/Account/Login";
    options.AccessDeniedPath = "/Identity/Account/AccessDenied";
});


builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToAreaPage("Identity", "/Account/Login");
    options.Conventions.AllowAnonymousToAreaPage("Identity", "/Account/Register");
});

builder.Services.AddRazorPages();

builder.Services.AddControllersWithViews();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;
    var roleManager = sp.GetRequiredService<RoleManager<IdentityRole>>();
    var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
    var config = sp.GetRequiredService<IConfiguration>();

    const string superRole = "SuperAdmin";
    if (!await roleManager.RoleExistsAsync(superRole))
    {
        await roleManager.CreateAsync(new IdentityRole(superRole));
    }

    var superEmail = config["SuperAdmin:Email"];
    if (!string.IsNullOrWhiteSpace(superEmail))
    {
        var user = await userManager.FindByEmailAsync(superEmail);
        if (user != null)
        {
            if (!await userManager.IsInRoleAsync(user, superRole))
            {
                await userManager.AddToRoleAsync(user, superRole);
            }
        }
        // Se vuoi creare automaticamente l'utente se non esiste, dimmelo e ti metto il codice (con password temp).
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
