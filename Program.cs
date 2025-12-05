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
builder.Services.AddHostedService<NextStakeWebApp.Services.LiveNotifyWorker>();


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
// --- Proxy minimal per Live Scores (API-FOOTBALL) ---
app.MapGet("/api/livescores", async (
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    IMemoryCache cache,
    ApplicationDbContext writeDb,           // 👈 AGGIUNTO
    [FromQuery] string? ids // "123,456,789" (opzionale, usato solo per caching)
) =>

{
    var key = cfg["ApiSports:Key"];
    if (string.IsNullOrWhiteSpace(key))
        return Results.Problem("Missing ApiSports:Key");

    // chiave di cache (anche se ids è null va bene uguale)
    // chiave di cache (anche se ids è null va bene uguale)
    var cacheKey = $"livescores:{ids ?? "live-all"}";
    if (cache.TryGetValue(cacheKey, out object? cached) && cached is not null)
        return Results.Json(cached);

    var http = httpFactory.CreateClient("ApiSports");

    // Per i live usiamo l'endpoint ufficiale "live=all"
    var url = "fixtures?live=all&timezone=Europe/Rome";

    var req = new HttpRequestMessage(HttpMethod.Get, url);
    req.Headers.Add("x-apisports-key", key);

    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    // 👇 LOG CALLCOUNTER per Origin = 'WidgetWebApp'
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
        // Non blocchiamo l'API se il log fallisce
        Console.WriteLine($"[CALLCOUNTER][WidgetWebApp] ERROR: {ex.Message}");
    }

    if (!resp.IsSuccessStatusCode)
        return Results.StatusCode((int)resp.StatusCode);

    // NIENTE Console.WriteLine qui, solo deserializzazione
    var raw = await resp.Content.ReadAsStringAsync();
    var json = System.Text.Json.JsonSerializer.Deserialize<ApiFootballFixturesResponse>(raw);

    var payload = json?.Response?
        .Select(x => (object)new
        {
            id = x.Fixture.Id,
            status = x.Fixture.Status.Short,    // es. "NS", "1H", "HT", "FT"
            elapsed = x.Fixture.Status.Elapsed,  // minutaggio
            home = x.Goals.Home,
            away = x.Goals.Away
        })
        .ToList()
        ?? new List<object>();

    // cache molto corta: 1 secondo
    cache.Set(cacheKey, payload, TimeSpan.FromSeconds(1));

    return Results.Json(payload);

})
.WithName("LiveScores");

// === Push Notifications: salva / disattiva subscription ===
app.MapPost("/api/push/subscribe", async (
    [FromBody] PushSubscribeDto body,
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    HttpContext httpContext
) =>
{
    // Deve essere loggato
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

    // Cerca subscription per endpoint (univoco)
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
            MatchNotificationsEnabled = true  // di default ON
        };
        db.PushSubscriptions.Add(existing);
    }

    else
    {
        // Aggiorna dati e ri-attiva
        existing.UserId = userId;
        existing.P256Dh = body.P256Dh;
        existing.Auth = body.Auth;
        existing.IsActive = true;
        // NON tocchiamo MatchNotificationsEnabled: magari l’utente l’ha messa su OFF di proposito
    }

    await db.SaveChangesAsync();

    return Results.Ok(new { ok = true });
})
.RequireAuthorization(); // richiede login
// --- Test invio push ---
// Manda una notifica di test a tutte le subscription dell'utente loggato
app.MapPost("/api/push/test", async (
    HttpContext httpContext,
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    IConfiguration cfg) =>
{
    // Utente loggato
    var user = await userManager.GetUserAsync(httpContext.User);
    if (user == null)
    {
        return Results.Unauthorized();
    }

    // Recupera tutte le subscription attive per questo utente
    var subs = await db.PushSubscriptions
        .Where(s => s.UserId == user.Id && s.IsActive && s.MatchNotificationsEnabled)
        .ToListAsync();

    if (subs.Count == 0)
    {
        return Results.NotFound(new { message = "Nessuna subscription attiva per questo utente." });
    }

    // Legge le chiavi VAPID da config / env
    var publicKey =
        cfg["VAPID:PublicKey"] ??
        cfg["VAPID_PUBLIC_KEY"] ??
        Environment.GetEnvironmentVariable("VAPID_PUBLIC_KEY");

    var privateKey =
        cfg["VAPID:PrivateKey"] ??
        cfg["VAPID_PRIVATE_KEY"] ??
        Environment.GetEnvironmentVariable("VAPID_PRIVATE_KEY");

    var subject =
        cfg["VAPID:Subject"] ??
        cfg["VAPID_SUBJECT"] ??
        Environment.GetEnvironmentVariable("VAPID_SUBJECT") ??
        "mailto:nextstakeai@gmail.com";

    Console.WriteLine($"[PUSH][VAPID] pub len={publicKey?.Length ?? 0}, priv len={privateKey?.Length ?? 0}");

    if (string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(privateKey))
    {
        return Results.Problem("VAPID_PUBLIC_KEY o VAPID_PRIVATE_KEY non impostate.");
    }

    var vapidDetails = new VapidDetails(subject, publicKey, privateKey);
    var client = new WebPushClient();

    // Payload della notifica
    var payloadObj = new
    {
        title = "Test notifica NextStake",
        body = "Le notifiche push sono attive 👍",
        url = "/Events" // dove portarti al click
    };
    var payloadJson = System.Text.Json.JsonSerializer.Serialize(payloadObj);

    int success = 0;
    int failed = 0;

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
            // Se l'endpoint è scaduto o non valido, segna la subscription come inattiva
            if (wex.StatusCode == System.Net.HttpStatusCode.Gone ||
                wex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                s.IsActive = false;
            }

            failed++;
            Console.WriteLine($"[PUSH][ERROR] {wex.StatusCode} {wex.Message}");
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine($"[PUSH][ERROR] {ex.Message}");
        }
    }

    await db.SaveChangesAsync();

    return Results.Ok(new { success, failed });
})
.RequireAuthorization(); // richiede utente loggato

app.MapPost("/api/push/match-event", async (
    [FromBody] MatchEventPushDto body,
    ApplicationDbContext writeDb,
    ReadDbContext readDb,
    IConfiguration cfg
) =>
{
    if (body.MatchId <= 0 || string.IsNullOrWhiteSpace(body.Kind))
        return Results.BadRequest(new { error = "Dati evento non validi" });

    // Match + nomi squadre dal DB READ
    var match = await readDb.Matches.FirstOrDefaultAsync(m => m.Id == body.MatchId);
    if (match == null)
        return Results.NotFound(new { error = "Match non trovato" });

    var homeName = await readDb.Teams
        .Where(t => t.Id == match.HomeId)
        .Select(t => t.Name)
        .FirstOrDefaultAsync() ?? "Home";

    var awayName = await readDb.Teams
        .Where(t => t.Id == match.AwayId)
        .Select(t => t.Name)
        .FirstOrDefaultAsync() ?? "Away";

    // Utenti che hanno questo match tra i preferiti
    var userIds = await writeDb.FavoriteMatches
        .Where(f => f.MatchId == body.MatchId)
        .Select(f => f.UserId)
        .Distinct()
        .ToListAsync();

    if (userIds.Count == 0)
        return Results.Ok(new { success = 0, failed = 0, message = "Nessun utente con questo match tra i preferiti." });

    // Subscription push per quegli utenti
    var subs = await writeDb.PushSubscriptions
        .Where(s => userIds.Contains(s.UserId) && s.IsActive && s.MatchNotificationsEnabled)
        .ToListAsync();

    if (subs.Count == 0)
        return Results.Ok(new { success = 0, failed = 0, message = "Nessuna subscription attiva per questo match." });

    // Chiavi VAPID da config/env (stesso schema di /api/push/test)
    var publicKey =
        cfg["VAPID_PUBLIC_KEY"] ??
        Environment.GetEnvironmentVariable("VAPID_PUBLIC_KEY");
    var privateKey =
        cfg["VAPID_PRIVATE_KEY"] ??
        Environment.GetEnvironmentVariable("VAPID_PRIVATE_KEY");
    var subject =
        cfg["VAPID_SUBJECT"] ??
        Environment.GetEnvironmentVariable("VAPID_SUBJECT") ??
        "mailto:nextstakeai@gmail.com";

    if (string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(privateKey))
        return Results.Problem("VAPID_PUBLIC_KEY o VAPID_PRIVATE_KEY non impostate.");

    var vapidDetails = new VapidDetails(subject, publicKey, privateKey);
    var client = new WebPushClient();

    // Costruisci testo notifica in base al "kind"
    var scoreStr = (body.Home.HasValue && body.Away.HasValue)
        ? $"{body.Home}-{body.Away}"
        : "";

    string title;
    string bodyText;

    switch (body.Kind)
    {
        case "start":
            title = "Inizio partita";
            bodyText = $"{homeName} - {awayName} è iniziata.";
            break;
        case "halftime":
            title = "Fine primo tempo";
            bodyText = $"{homeName} - {awayName} | HT {scoreStr}";
            break;
        case "second":
            title = "Inizio secondo tempo";
            bodyText = $"{homeName} - {awayName} | Ripresa in corso.";
            break;
        case "end":
            title = "Partita terminata";
            bodyText = $"{homeName} - {awayName} | Finale {scoreStr}";
            break;
        case "goal":
            title = "GOAL!";
            var minutePart = body.Minute.HasValue ? $" al {body.Minute}'" : "";
            bodyText = $"{homeName} - {awayName} | {scoreStr}{minutePart}";
            break;
        default:
            title = "Aggiornamento match";
            bodyText = $"{homeName} - {awayName}";
            break;
    }

    var payloadObj = new
    {
        title = title,
        body = bodyText,
        url = $"/Match/Details?id={body.MatchId}"
    };
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
            if (wex.StatusCode == System.Net.HttpStatusCode.Gone ||
                wex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                s.IsActive = false;
            }
            failed++;
            Console.WriteLine($"[PUSH MATCH][ERROR] {wex.StatusCode} {wex.Message}");
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine($"[PUSH MATCH][ERROR] {ex.Message}");
        }
    }

    await writeDb.SaveChangesAsync();

    return Results.Ok(new { success, failed });
})
.RequireAuthorization();
// === Job live notify: rileva eventi sui match e invia Web Push agli utenti che li hanno nei preferiti ===
// === Job live notify: rileva eventi sui match e invia Web Push agli utenti che li hanno nei preferiti ===
app.MapPost("/internal/jobs/live-notify", async (
    [FromQuery] string key,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ReadDbContext readDb,
    ApplicationDbContext writeDb
) =>
{
    // --- 1) Sicurezza job ----------------------------------------------------
    var expectedKey =
        cfg["JOBS_LIVE_NOTIFY_KEY"] ??
        Environment.GetEnvironmentVariable("JOBS_LIVE_NOTIFY_KEY");

    if (string.IsNullOrWhiteSpace(expectedKey) || key != expectedKey)
    {
        return Results.Unauthorized();
    }

    // --- 2) Chiavi API-FOOTBALL ---------------------------------------------
    var apiKey =
        cfg["ApiSports:Key"] ??
        Environment.GetEnvironmentVariable("ApiSports__Key");

    if (string.IsNullOrWhiteSpace(apiKey))
    {
        return Results.Problem("ApiSports:Key non impostata.");
    }

    // --- 3) Chiavi VAPID -----------------------------------------------------
    var publicKey =
        cfg["VAPID_PUBLIC_KEY"] ??
        Environment.GetEnvironmentVariable("VAPID_PUBLIC_KEY");
    var privateKey =
        cfg["VAPID_PRIVATE_KEY"] ??
        Environment.GetEnvironmentVariable("VAPID_PRIVATE_KEY");
    var subject =
        cfg["VAPID_SUBJECT"] ??
        Environment.GetEnvironmentVariable("VAPID_SUBJECT") ??
        "mailto:nextstakeai@gmail.com";

    if (string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(privateKey))
    {
        return Results.Problem("VAPID_PUBLIC_KEY o VAPID_PRIVATE_KEY non impostate.");
    }

    var vapidDetails = new VapidDetails(subject, publicKey, privateKey);
    var client = new WebPushClient();

    // --- 4) Chiamata API-FOOTBALL per i live ---------------------------------
    var http = httpFactory.CreateClient("ApiSports");

    var req = new HttpRequestMessage(
        HttpMethod.Get,
        "fixtures?live=all&timezone=Europe/Rome"
    );
    req.Headers.Add("x-apisports-key", apiKey);

    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    if (!resp.IsSuccessStatusCode)
    {
        return Results.StatusCode((int)resp.StatusCode);
    }

    var raw = await resp.Content.ReadAsStringAsync();
    var fixtures = System.Text.Json.JsonSerializer.Deserialize<ApiFootballFixturesResponse>(raw);

    var liveList = fixtures?.Response ?? new List<ApiFixtureItem>();
    if (liveList.Count == 0)
    {
        // Nessuna partita live = niente da fare
        return Results.Ok(new { message = "Nessuna partita live al momento." });
    }

    var liveIds = liveList
        .Select(f => f.Fixture.Id)   // ✅ int
        .Distinct()
        .ToList();

    // --- 5) Dati aggiuntivi dal DB (nomi squadre, logo, lega) ----------------
    var meta = await (
        from m in readDb.Matches
        join lg in readDb.Leagues on m.LeagueId equals lg.Id
        join th in readDb.Teams on m.HomeId equals th.Id
        join ta in readDb.Teams on m.AwayId equals ta.Id
        where liveIds.Contains(m.Id)
        select new
        {
            MatchId = m.Id,
            LeagueName = lg.Name,
            LeagueLogo = lg.Logo,
            HomeName = th.Name,
            HomeLogo = th.Logo,
            AwayName = ta.Name,
            AwayLogo = ta.Logo
        }
    ).ToListAsync();

    var metaById = meta.ToDictionary(x => x.MatchId, x => x);

    // --- 6) Stato precedente dei match live ----------------------------------
    var existingStates = await writeDb.LiveMatchStates
        .Where(s => liveIds.Contains(s.MatchId))
        .ToListAsync();

    var stateById = existingStates.ToDictionary(s => s.MatchId, s => s);

    // --- 7) Preferiti + subscription per utente ------------------------------
    var favPerMatch = await writeDb.FavoriteMatches
        .Where(fm => liveIds.Contains((int)fm.MatchId))   // 👈 cast esplicito
        .GroupBy(fm => fm.MatchId)
        .Select(g => new
        {
            MatchId = g.Key,
            UserIds = g.Select(fm => fm.UserId).Distinct().ToList()
        })
        .ToListAsync();


    var favUsersByMatch = favPerMatch.ToDictionary(x => x.MatchId, x => x.UserIds);

    var activeSubs = await writeDb.PushSubscriptions
        .Where(s => s.IsActive && s.MatchNotificationsEnabled && s.UserId != null)
        .ToListAsync();

    var subsByUser = activeSubs
        .GroupBy(s => s.UserId!)
        .ToDictionary(g => g.Key, g => g.ToList());

    // --- 8) Rilevazione eventi + costruzione notifiche -----------------------
    string[] LIVE_STATUSES = { "1H", "2H", "ET", "P", "BT", "LIVE", "HT" };
    string[] FINISHED_STATUSES = { "FT", "AET", "PEN" }; // per ora non li vediamo da live=all, ma li consideriamo

    var nowUtc = DateTime.UtcNow;
    var toSend = new List<(DbPushSubscription sub, string title, string body, string url)>();

    foreach (var item in liveList)
    {
        var id = item.Fixture.Id; // ✅ int

        var status = (item.Fixture.Status.Short ?? "").ToUpperInvariant();
        var elapsed = item.Fixture.Status.Elapsed;
        var home = item.Goals.Home;
        var away = item.Goals.Away;

        metaById.TryGetValue(id, out var m);
        var leagueName = m?.LeagueName ?? "Match";
        var homeName = m?.HomeName ?? "Home";
        var awayName = m?.AwayName ?? "Away";

        stateById.TryGetValue(id, out var prev);

        var prevStatus = prev?.LastStatus;
        var prevHome = prev?.LastHome;
        var prevAway = prev?.LastAway;

        bool isLiveNow = LIVE_STATUSES.Contains(status);
        bool isLivePrev = prevStatus != null && LIVE_STATUSES.Contains(prevStatus);
        bool isFinishedNow = FINISHED_STATUSES.Contains(status);
        bool isFinishedPrev = prevStatus != null && FINISHED_STATUSES.Contains(prevStatus);

        int? currHome = home;
        int? currAway = away;
        int prevHomeVal = prevHome ?? 0;
        int prevAwayVal = prevAway ?? 0;

        bool scoreChanged = prev != null &&
                            (prevHome != currHome || prevAway != currAway);

        int prevTotal = prevHomeVal + prevAwayVal;
        int currTotal = (currHome ?? 0) + (currAway ?? 0);

        // Nessun favorito su questo match? Salta le notifiche, ma aggiorna lo stato
        if (!favUsersByMatch.TryGetValue(id, out var userIdsForMatch) || userIdsForMatch.Count == 0)
        {
            if (prev == null)
            {
                writeDb.LiveMatchStates.Add(new LiveMatchState
                {
                    MatchId = id,
                    LastStatus = status,
                    LastHome = currHome,
                    LastAway = currAway,
                    LastElapsed = elapsed,
                    LastUpdatedUtc = nowUtc
                });
            }
            else
            {
                prev.LastStatus = status;
                prev.LastHome = currHome;
                prev.LastAway = currAway;
                prev.LastElapsed = elapsed;
                prev.LastUpdatedUtc = nowUtc;
            }
            continue;
        }

        // Se non abbiamo stato precedente, inizializziamo solo e NON notifichiamo
        if (prev == null)
        {
            writeDb.LiveMatchStates.Add(new LiveMatchState
            {
                MatchId = id,
                LastStatus = status,
                LastHome = currHome,
                LastAway = currAway,
                LastElapsed = elapsed,
                LastUpdatedUtc = nowUtc
            });
            continue;
        }

        // Abbiamo un prev: possiamo rilevare eventi
        var events = new List<string>();

        // Inizio partita: da non-live a live
        if (!isLivePrev && isLiveNow)
        {
            events.Add("start");
        }

        // Fine primo tempo: 1H -> HT
        if (prevStatus == "1H" && status == "HT")
        {
            events.Add("halftime");
        }

        // Inizio secondo tempo: HT -> 2H
        if (prevStatus == "HT" && status == "2H")
        {
            events.Add("second");
        }

        // Fine partita
        if (!isFinishedPrev && isFinishedNow)
        {
            events.Add("end");
        }

        // Goal / Rettifica
        if (scoreChanged && !isFinishedNow)
        {
            if (currTotal > prevTotal)
            {
                events.Add("goal");
            }
            else if (currTotal < prevTotal)
            {
                events.Add("correction");
            }
        }

        if (events.Count > 0)
        {
            var scoreStr = (currHome.HasValue && currAway.HasValue)
                ? $"{currHome}-{currAway}"
                : "";

            var minuteStr = elapsed.HasValue ? $"{elapsed}'" : null;
            var matchLabel = $"{homeName} - {awayName}";
            var leagueLabel = leagueName;

            foreach (var ev in events)
            {
                string title;
                string body;

                switch (ev)
                {
                    case "start":
                        title = "Inizio partita";
                        body = $"{leagueLabel} | {matchLabel} è iniziata.";
                        break;
                    case "halftime":
                        title = "Fine primo tempo";
                        body = $"{leagueLabel} | {matchLabel} | HT {scoreStr}";
                        break;
                    case "second":
                        title = "Inizio secondo tempo";
                        body = $"{leagueLabel} | {matchLabel} | Ripresa in corso.";
                        break;
                    case "end":
                        title = "Partita terminata";
                        body = $"{leagueLabel} | {matchLabel} | Finale {scoreStr}";
                        break;
                    case "goal":
                        title = "GOAL!";
                        body = $"{leagueLabel} | {matchLabel} | {scoreStr}"
                             + (minuteStr != null ? $" al {minuteStr}" : "");
                        break;
                    case "correction":
                        title = "Rettifica punteggio";
                        body = $"{leagueLabel} | {matchLabel} | Nuovo punteggio {scoreStr}";
                        break;
                    default:
                        title = "Aggiornamento match";
                        body = $"{leagueLabel} | {matchLabel}";
                        break;
                }

                var url = $"/Match/Details?id={id}";

                foreach (var userId in userIdsForMatch)
                {
                    if (!subsByUser.TryGetValue(userId, out var userSubs)) continue;

                    foreach (var sub in userSubs)
                    {
                        toSend.Add((sub, title, body, url));
                    }
                }
            }
        }

        // Aggiorna stato in DB
        prev.LastStatus = status;
        prev.LastHome = currHome;
        prev.LastAway = currAway;
        prev.LastElapsed = elapsed;
        prev.LastUpdatedUtc = nowUtc;
    }

    // Salva stati aggiornati
    await writeDb.SaveChangesAsync();

    // --- 9) Invio Web Push ----------------------------------------------------
    int success = 0;
    int failed = 0;

    foreach (var item in toSend)
    {
        var sub = item.sub;

        var payloadObj = new
        {
            title = item.title,
            body = item.body,
            url = item.url
        };
        var payloadJson = System.Text.Json.JsonSerializer.Serialize(payloadObj);

        var pushSub = new WebPushSubscription(sub.Endpoint, sub.P256Dh, sub.Auth);

        try
        {
            await client.SendNotificationAsync(pushSub, payloadJson, vapidDetails);
            success++;
        }
        catch (WebPushException wex)
        {
            // Endpoint scaduto/non valido → disattivo
            if (wex.StatusCode == System.Net.HttpStatusCode.Gone ||
                wex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                sub.IsActive = false;
            }

            failed++;
            Console.WriteLine($"[LIVEPUSH][ERROR] {wex.StatusCode} {wex.Message}");
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine($"[LIVEPUSH][ERROR] {ex.Message}");
        }
    }

    if (failed > 0)
    {
        await writeDb.SaveChangesAsync();
    }

    return Results.Ok(new
    {
        liveCount = liveList.Count,
        sent = success,
        failed,
        events = toSend.Count
    });
});


app.MapPost("/api/push/unsubscribe", async (
    [FromBody] PushUnsubscribeDto body,
    ApplicationDbContext db,
    HttpContext httpContext
) =>
{
    if (string.IsNullOrWhiteSpace(body.Endpoint))
        return Results.BadRequest(new { error = "Endpoint mancante" });

    // opzionale: puoi richiedere login anche qui
    if (httpContext.User?.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var sub = await db.PushSubscriptions
        .FirstOrDefaultAsync(x => x.Endpoint == body.Endpoint);

    if (sub is null)
    {
        // idempotente: ok anche se non esiste
        return Results.Ok(new { ok = true });
    }

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
