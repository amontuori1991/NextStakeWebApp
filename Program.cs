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
using static NextStakeWebApp.Services.IMatchBannerService;
using NextStakeWebApp.ApiSports;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using WebPush;
using DbPushSubscription = NextStakeWebApp.Models.PushSubscription;
using WebPushSubscription = WebPush.PushSubscription;

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
    // Policy "storica", se la usi da qualche parte
    options.AddPolicy("Plan1", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("plan", "1");
    });

    // Policy usata sulla pagina [Authorize(Policy = "RequirePlan1")]
    options.AddPolicy("RequirePlan1", policy =>
    {
        policy.RequireAuthenticatedUser();

        policy.RequireAssertion(ctx =>
            // Utente con claim plan = 1
            ctx.User.HasClaim(c =>
                string.Equals(c.Type, "plan", StringComparison.OrdinalIgnoreCase) &&
                string.Equals((c.Value ?? "").Trim(), "1", StringComparison.Ordinal)
            )
            // oppure SuperAdmin (se vuoi che il superadmin veda sempre tutto)
            || ctx.User.IsInRole("SuperAdmin")
        );
    });
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
// builder.Services.AddHostedService<NextStakeWebApp.Services.LiveNotifyWorker>();


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
    ApplicationDbContext writeDb,
    [FromQuery] string? ids
) =>
{
    var key = cfg["ApiSports:Key"];
    if (string.IsNullOrWhiteSpace(key))
        return Results.Problem("Missing ApiSports:Key");

    var cacheKey = $"livescores:{ids ?? "live-all"}";
    if (cache.TryGetValue(cacheKey, out object? cached) && cached is not null)
        return Results.Json(cached);

    var http = httpFactory.CreateClient("ApiSports");
    var url = "fixtures?live=all&timezone=Europe/Rome";

    var req = new HttpRequestMessage(HttpMethod.Get, url);
    req.Headers.Add("x-apisports-key", key);

    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

    // 🛑 DISATTIVATO: Inserimento nel CallCounter (Risparmio Neon)
    // Questo blocco scriveva nel DB ad ogni singolo refresh della pagina.
    /*
    const string origin = "WidgetWebApp";
    try
    {
        await writeDb.Database.ExecuteSqlRawAsync(
            @"INSERT INTO callcounter(date, origin, counter)
              VALUES (CURRENT_DATE, {0}, 1)
              ON CONFLICT (date, origin)
              DO UPDATE SET counter = callcounter.counter + 1;",
            origin);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[CALLCOUNTER][WidgetWebApp] ERROR: {ex.Message}");
    }
    */

    if (!resp.IsSuccessStatusCode)
        return Results.StatusCode((int)resp.StatusCode);

    var raw = await resp.Content.ReadAsStringAsync();
    var json = System.Text.Json.JsonSerializer.Deserialize<ApiFootballFixturesResponse>(raw);

    // 🛑 DISATTIVATO: Aggiornamento LiveMatchStates (Risparmio Neon)
    // Questa era la parte più costosa: centinaia di UPDATE continui per ogni match live.
    // Disattivandola, risparmi CPU e scritture su Neon.
    /*
    if (json?.Response != null && json.Response.Count > 0)
    {
        var nowUtc = DateTime.UtcNow;

        var liveIds = json.Response
            .Select(f => f.Fixture.Id)
            .Distinct()
            .ToList();

        if (liveIds.Count > 0)
        {
            var existingStates = await writeDb.LiveMatchStates
                .Where(s => liveIds.Contains(s.MatchId))
                .ToListAsync();

            var stateById = existingStates.ToDictionary(s => s.MatchId, s => s);

            foreach (var item in json.Response)
            {
                var id = item.Fixture.Id;
                var status = (item.Fixture.Status.Short ?? "").Trim().ToUpperInvariant();
                var home = item.Goals.Home;
                var away = item.Goals.Away;
                var elapsed = item.Fixture.Status.Elapsed;

                if (!stateById.TryGetValue(id, out var st))
                {
                    st = new LiveMatchState
                    {
                        MatchId = id
                    };
                    writeDb.LiveMatchStates.Add(st);
                    stateById[id] = st;
                }

                st.LastStatus = status;
                st.LastHome = home;
                st.LastAway = away;
                st.LastElapsed = elapsed;
                st.LastUpdatedUtc = nowUtc;

            }

            await writeDb.SaveChangesAsync();
        }
    }
    */

    var payload = json?.Response?
        .Select(x => (object)new
        {
            id = x.Fixture.Id,
            status = x.Fixture.Status.Short,
            elapsed = x.Fixture.Status.Elapsed,
            home = x.Goals.Home,
            away = x.Goals.Away
        })
        .ToList()
        ?? new List<object>();

    // ✅ OTTIMIZZAZIONE: Cache aumentata a 30 secondi (era 1s)
    // Riduce drasticamente il numero di chiamate all'API esterna e l'elaborazione del server.
    cache.Set(cacheKey, payload, TimeSpan.FromSeconds(30));

    return Results.Json(payload);
})
.WithName("LiveScores");


// === Push Notifications: salva / disattiva subscription ===
// Nota: Questo endpoint può restare attivo, poiché viene chiamato solo una volta per utente.
app.MapPost("/api/push/subscribe", async (
    [FromBody] PushSubscribeDto body,
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    HttpContext httpContext
) =>
{
    if (httpContext.User?.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var userId = userManager.GetUserId(httpContext.User);
    if (string.IsNullOrWhiteSpace(userId))
        return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(body.Endpoint) ||
        string.IsNullOrWhiteSpace(body.P256Dh) ||
        string.IsNullOrWhiteSpace(body.Auth))
    {
        return Results.BadRequest(new { error = "Dati subscription non validi" });
    }

    var existing = await db.PushSubscriptions
        .FirstOrDefaultAsync(x => x.Endpoint == body.Endpoint);

    if (existing is null)
    {
        existing = new DbPushSubscription
        {
            UserId = userId,
            Endpoint = body.Endpoint,
            P256Dh = body.P256Dh,
            Auth = body.Auth,
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
            MatchNotificationsEnabled = true
        };
        db.PushSubscriptions.Add(existing);
    }
    else
    {
        existing.UserId = userId;
        existing.P256Dh = body.P256Dh;
        existing.Auth = body.Auth;
        existing.IsActive = true;
    }

    await db.SaveChangesAsync();

    return Results.Ok(new { ok = true });
})
.RequireAuthorization();

// --- Test invio push ---
app.MapPost("/api/push/test", async (
    HttpContext httpContext,
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    IConfiguration cfg) =>
{
    var user = await userManager.GetUserAsync(httpContext.User);
    if (user == null) return Results.Unauthorized();

    var subs = await db.PushSubscriptions
        .Where(s => s.UserId == user.Id && s.IsActive && s.MatchNotificationsEnabled)
        .ToListAsync();

    if (subs.Count == 0) return Results.NotFound(new { message = "Nessuna subscription attiva." });

    var publicKey = cfg["VAPID:PublicKey"] ?? Environment.GetEnvironmentVariable("VAPID_PUBLIC_KEY");
    var privateKey = cfg["VAPID:PrivateKey"] ?? Environment.GetEnvironmentVariable("VAPID_PRIVATE_KEY");
    var subject = cfg["VAPID:Subject"] ?? Environment.GetEnvironmentVariable("VAPID_SUBJECT") ?? "mailto:nextstakeai@gmail.com";

    if (string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(privateKey))
        return Results.Problem("VAPID Keys mancanti.");

    var vapidDetails = new VapidDetails(subject, publicKey, privateKey);
    var client = new WebPushClient();

    var payloadObj = new { title = "Test notifica NextStake", body = "Le notifiche push sono attive 👍", url = "/Events" };
    var payloadJson = System.Text.Json.JsonSerializer.Serialize(payloadObj);

    int success = 0, failed = 0;
    foreach (var s in subs)
    {
        var pushSub = new WebPushSubscription(s.Endpoint, s.P256Dh, s.Auth);
        try
        {
            await client.SendNotificationAsync(pushSub, payloadJson, vapidDetails);
            success++;
        }
        catch (WebPushException wex)
        {
            if (wex.StatusCode == System.Net.HttpStatusCode.Gone || wex.StatusCode == System.Net.HttpStatusCode.NotFound)
                s.IsActive = false;
            failed++;
        }
        catch (Exception) { failed++; }
    }

    await db.SaveChangesAsync();
    return Results.Ok(new { success, failed });
})
.RequireAuthorization();

// 🛑 DISATTIVAZIONE CONSIGLIATA: match-event
// Anche questo endpoint genera query e può essere disattivato se vuoi spegnere le notifiche live.
/*
app.MapPost("/api/push/match-event", async (...) => { ... });
*/

// 🛑 DISATTIVAZIONE CONSIGLIATA: Il job di notifica automatica
// Questo job è quello che probabilmente consuma di più se gira ogni minuto.
/*
app.MapPost("/internal/jobs/live-notify", async (...) => { ... });
*/

app.MapPost("/api/push/unsubscribe", async (
    [FromBody] PushUnsubscribeDto body,
    ApplicationDbContext db,
    HttpContext httpContext
) =>
{
    if (string.IsNullOrWhiteSpace(body.Endpoint))
        return Results.BadRequest(new { error = "Endpoint mancante" });

    if (httpContext.User?.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var sub = await db.PushSubscriptions
        .FirstOrDefaultAsync(x => x.Endpoint == body.Endpoint);

    if (sub is null) return Results.Ok(new { ok = true });

    sub.IsActive = false;
    await db.SaveChangesAsync();

    return Results.Ok(new { ok = true });
})
.RequireAuthorization();


// Endpoint diagnostico minimale (opzionale)
app.MapGet("/_debug/db", () => Results.Json(new
{
    write = new { wcsb.Host, wcsb.Database, wcsb.Username },
    read = new { rcsb.Host, rcsb.Database, rcsb.Username }
}));
// === Consumi API (JSON per pagina Admin/Consumi) ===
app.MapGet("/admin/api/consumi", async (
    ApplicationDbContext db,
    IConfiguration cfg,
    CancellationToken ct
) =>
{
    // Tetto giornaliero (stessa logica che usi nel PageModel)
    var dailyCapStr = cfg["ApiSports:DailyCap"];
    int dailyCap = 0;
    int.TryParse(dailyCapStr, out dailyCap);

    // Leggo un po' di righe di storico (es. ultime 60)
    var records = await db.CallCounter
        .OrderByDescending(c => c.Date)
        .Take(60)
        .ToListAsync(ct);

    if (!records.Any())
    {
        return Results.Json(new
        {
            dailyCap,
            todayTotal = 0,
            todayUsage = Array.Empty<object>(),
            records = Array.Empty<object>()
        });
    }

    // giorno più recente presente in tabella
    var latestDate = records.Max(r => r.Date);

    var todayUsage = records
        .Where(r => r.Date == latestDate)
        .GroupBy(r => r.Origin)
        .Select(g => new
        {
            origin = g.Key,
            counter = g.Sum(x => x.Counter)
        })
        .ToList();

    var todayTotal = todayUsage.Sum(x => x.counter);

    // DTO "piatto" per la tabella
    var recDtos = records
        .Select(r => new
        {
            date = r.Date,
            origin = r.Origin,
            counter = r.Counter
        })
        .ToList();

    return Results.Json(new
    {
        dailyCap,
        todayTotal,
        todayUsage,
        records = recDtos
    });
})
.RequireAuthorization(); // stessa protezione della pagina Admin


// === Status reale del provider (API-FOOTBALL /status) ===
app.MapGet("/admin/api/consumi/provider-status", async (
    IHttpClientFactory httpFactory,
    IConfiguration cfg
) =>
{
    var apiKey =
        cfg["ApiSports:Key"] ??
        Environment.GetEnvironmentVariable("ApiSports__Key");

    if (string.IsNullOrWhiteSpace(apiKey))
    {
        return Results.Json(new
        {
            success = false,
            message = "API key non configurata"
        });
    }

    var client = httpFactory.CreateClient("ApiSports");

    // BaseAddress = https://v3.football.api-sports.io/
    var req = new HttpRequestMessage(HttpMethod.Get, "status");
    req.Headers.Add("x-apisports-key", apiKey);

    using var resp = await client.SendAsync(req);
    if (!resp.IsSuccessStatusCode)
    {
        return Results.Json(new
        {
            success = false,
            message = $"Status provider non disponibile (HTTP {(int)resp.StatusCode})"
        });
    }

    var json = await resp.Content.ReadAsStringAsync();
    Console.WriteLine("[STATUS][RAW] " + json); // 👈 utile la prima volta, poi puoi toglierlo

    int? requestsCurrent = null;
    int? requestsLimitDay = null;
    string? plan = null;
    string? accountEmail = null;

    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        // ⚠️ nel tuo JSON response è UN OGGETTO, non un array
        if (root.TryGetProperty("response", out var respObj) &&
            respObj.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            // response.requests.current / limit_day
            if (respObj.TryGetProperty("requests", out var reqObj) &&
                reqObj.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (reqObj.TryGetProperty("current", out var curEl) &&
                    curEl.TryGetInt32(out var cur))
                {
                    requestsCurrent = cur;
                }

                if (reqObj.TryGetProperty("limit_day", out var limEl) &&
                    limEl.TryGetInt32(out var lim))
                {
                    requestsLimitDay = lim;
                }
            }

            // response.subscription.plan
            if (respObj.TryGetProperty("subscription", out var subObj) &&
                subObj.ValueKind == System.Text.Json.JsonValueKind.Object &&
                subObj.TryGetProperty("plan", out var planEl) &&
                planEl.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                plan = planEl.GetString();
            }

            // response.account.email
            if (respObj.TryGetProperty("account", out var accObj) &&
                accObj.ValueKind == System.Text.Json.JsonValueKind.Object &&
                accObj.TryGetProperty("email", out var emailEl) &&
                emailEl.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                accountEmail = emailEl.GetString();
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[STATUS][PARSE ERROR] {ex.Message}");
    }

    return Results.Json(new
    {
        success = true,
        requestsCurrent = requestsCurrent ?? 0,
        requestsLimitDay = requestsLimitDay ?? 0,
        plan,
        account = accountEmail
    });
})
.RequireAuthorization();


app.Run();

// --- Records per deserializzazione API-FOOTBALL ---
// --- Modelli per deserializzazione API-FOOTBALL fixtures ---
public class ApiFootballFixturesResponse
{
    [JsonPropertyName("response")]
    public List<ApiFixtureItem> Response { get; set; } = new();
}

public class ApiFixtureItem
{
    [JsonPropertyName("fixture")]
    public ApiFixture Fixture { get; set; } = new();

    [JsonPropertyName("goals")]
    public ApiGoals Goals { get; set; } = new();
}

public class ApiFixture
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("status")]
    public ApiStatus Status { get; set; } = new();
}

public class ApiStatus
{
    [JsonPropertyName("short")]
    public string? Short { get; set; }

    [JsonPropertyName("elapsed")]
    public int? Elapsed { get; set; }
}

public class ApiGoals
{
    [JsonPropertyName("home")]
    public int? Home { get; set; }

    [JsonPropertyName("away")]
    public int? Away { get; set; }
}

// === DTO per Push Notifications ===
public record PushSubscribeDto(
    string Endpoint,
    string P256Dh,
    string Auth
);

public record PushUnsubscribeDto(
    string Endpoint
);

public record MatchEventPushDto(
    long MatchId,
    string Kind,
    int? Home,
    int? Away,
    int? Minute
);
