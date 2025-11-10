using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;
using NextStakeWebApp.Services;
using Npgsql;

// NOTE: niente Microsoft.Extensions.Options qui: non lo usiamo

var builder = WebApplication.CreateBuilder(args);

// --- Helper: legge da IConfiguration (UserSecrets/appsettings/ENV) con fallback a ConnectionStrings ---
static string GetConn(IConfiguration cfg, string key1, string key2, string? appsettingsKey = null)
{
    var c =
        cfg[key1] ??
        cfg[key2] ??
        Environment.GetEnvironmentVariable(key1) ??
        Environment.GetEnvironmentVariable(key2) ??
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
builder.Services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, AppClaimsPrincipalFactory>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Plan1", policy =>
        policy.RequireClaim("plan", "1"));
});
builder.Services.AddRazorPages()
    .AddRazorPagesOptions(o =>
    {
        o.Conventions.AddPageRoute("/Events/Index", ""); // mappa "/"
    });

// DbContext: pooling + retry
builder.Services.AddDbContextPool<ApplicationDbContext>(opt =>
    opt.UseNpgsql(writeConn, o =>
    {
        o.CommandTimeout(60);
        o.EnableRetryOnFailure(5, TimeSpan.FromSeconds(5), null);
    }));

// Live scores: cache + HTTP client per API-FOOTBALL
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("ApiSports", c =>
{
    c.BaseAddress = new Uri("https://v3.football.api-sports.io/");
});

builder.Services.AddDbContextPool<ReadDbContext>(opt =>
    opt.UseNpgsql(readConn, o =>
    {
        o.CommandTimeout(60);
        o.EnableRetryOnFailure(5, TimeSpan.FromSeconds(5), null);
    }));

// === OpenAI ===
// --- OpenAI ---
builder.Services.AddHttpClient("OpenAI", c =>
{
    c.BaseAddress = new Uri("https://api.openai.com/v1/");
});

builder.Services.AddSingleton<NextStakeWebApp.Services.OpenAIOptions>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    return new NextStakeWebApp.Services.OpenAIOptions
    {
        ApiKey = cfg["OpenAI:ApiKey"]
                 ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                 ?? Environment.GetEnvironmentVariable("OpenAI__ApiKey"),
        Model = cfg["OpenAI:Model"] ?? "gpt-4o-mini"
    };
});

builder.Services.AddScoped<NextStakeWebApp.Services.IOpenAIService, NextStakeWebApp.Services.OpenAIService>();
builder.Services.AddScoped<NextStakeWebApp.Services.IAiService, NextStakeWebApp.Services.AiService>();


// Telegram
builder.Services.AddHttpClient();
builder.Services.AddScoped<IMatchBannerService, MatchBannerService>();
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection("Telegram"));
builder.Services.AddSingleton<ITelegramService, TelegramService>();

// Identity
// Identity
builder.Services
    .AddIdentity<ApplicationUser, IdentityRole>(o =>
    {
        o.SignIn.RequireConfirmedAccount = true;   // ✅ obbligo conferma email
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 4;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireLowercase = false;
        o.Password.RequireDigit = false;
        o.Tokens.EmailConfirmationTokenProvider = TokenOptions.DefaultEmailProvider;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders()
    .AddDefaultUI();

builder.Services.AddTransient<IEmailSender, GmailSmtpSender>();

builder.Services.Configure<DataProtectionTokenProviderOptions>(o =>
{
    o.TokenLifespan = TimeSpan.FromHours(24); // token conferma valido 24h
});

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
    o.Conventions.AllowAnonymousToAreaPage("Identity", "/Account/RegisterConfirmation");
    o.Conventions.AllowAnonymousToAreaPage("Identity", "/Account/ConfirmEmail");
});


builder.Services.AddControllersWithViews();

var app = builder.Build();

// Migrazioni/seed SOLO sul DB WRITE
using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;
    var appDb = sp.GetRequiredService<ApplicationDbContext>();

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

    var email = config["SuperAdmin:Email"];
    var userName = config["SuperAdmin:UserName"] ?? email;
    var password = config["SuperAdmin:Password"] ?? "Admin#12345";

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
        if (user != null)
        {
            // Abilita le funzioni plan-based: 1 = PRO (o come si chiama nel tuo enum)
            if ((int)user.Plan != 1)
            {
                user.Plan = (SubscriptionPlan)1; // oppure SubscriptionPlan.PRO se esiste
                user.PlanExpiresAtUtc = null;    // opzionale: senza scadenza per il seed
                await userManager.UpdateAsync(user);
            }
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

// --- Proxy minimal per Live Scores (API-FOOTBALL) ---
app.MapGet("/api/livescores", async (
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    IMemoryCache cache,
    string ids // "123,456,789"
) =>
{
    if (string.IsNullOrWhiteSpace(ids)) return Results.BadRequest("ids required");

    var key = cfg["ApiSports:Key"];
    if (string.IsNullOrWhiteSpace(key)) return Results.Problem("Missing ApiSports:Key");

    // cache 5s per batch ids
    var cacheKey = $"livescores:{ids}";
    if (cache.TryGetValue(cacheKey, out object? cached) && cached is not null)
        return Results.Json(cached);

    var http = httpFactory.CreateClient("ApiSports");
    var req = new HttpRequestMessage(HttpMethod.Get, $"fixtures?ids={Uri.EscapeDataString(ids)}");
    req.Headers.Add("x-apisports-key", key);

    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    if (!resp.IsSuccessStatusCode) return Results.StatusCode((int)resp.StatusCode);

    var json = await resp.Content.ReadFromJsonAsync<ApiFootballFixturesResponse>();

    var payload = json?.response?.Select(x => new
    {
        id = x.fixture?.id,
        status = x.fixture?.status?.Short,
        elapsed = x.fixture?.status?.Elapsed,
        home = x.goals?.home,
        away = x.goals?.away
    }).Where(x => x.id.HasValue).ToList() ?? new();

    cache.Set(cacheKey, payload, TimeSpan.FromSeconds(5));
    return Results.Json(payload);
})
.WithName("LiveScores");

// Endpoint diagnostico minimale (opzionale)
app.MapGet("/_debug/db", () => Results.Json(new
{
    write = new { wcsb.Host, wcsb.Database, wcsb.Username },
    read = new { rcsb.Host, rcsb.Database, rcsb.Username }
}));

app.Run();

// --- Records per deserializzazione API-FOOTBALL ---
public record ApiFootballFixturesResponse(System.Collections.Generic.List<ApiFixtureItem> response);
public record ApiFixtureItem(ApiFixture fixture, ApiGoals goals);
public record ApiFixture(int id, ApiStatus status);
public record ApiStatus(
    [property: JsonPropertyName("short")] string? Short,
    [property: JsonPropertyName("elapsed")] int? Elapsed
);
public record ApiGoals(int? home, int? away);
