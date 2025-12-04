using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;


namespace NextStakeWebApp.Pages.Events
{
    [Authorize]
    public class IndexModel : PageModel
    {
        private readonly ReadDbContext _read;
        private readonly ApplicationDbContext _write;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IConfiguration _config;


        private readonly SignInManager<ApplicationUser> _signInManager;

        public IndexModel(
            ReadDbContext read,
            ApplicationDbContext write,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            IConfiguration config)                 // 👈 NUOVO
        {
            _read = read;
            _write = write;
            _userManager = userManager;
            _signInManager = signInManager;
            _config = config;                      // 👈 NUOVO
        }



        // Giorno selezionato (giorno "Italia")
        public DateOnly SelectedDate { get; private set; } = DateOnly.FromDateTime(DateTime.Now);
        public string VapidPublicKey { get; private set; } = "";

        public bool OnlyFavorites { get; private set; }
        public string? SelectedCountryCode { get; private set; }
        public string? Query { get; private set; }

        public List<EventRow> Rows { get; private set; } = new();
        public HashSet<long> FavoriteMatchIds { get; private set; } = new();
        public List<CountryOption> AvailableCountries { get; private set; } = new();

        // === Migliori pronostici ===
        public bool ShowBest { get; private set; }
        public List<NextStakeWebApp.Models.BestPickRow> BestPicks { get; private set; } = new();
        public string? BestMessage { get; private set; }

        // === Asset per i Best, indicizzati per MatchId ===
        public Dictionary<long, MatchAssets> AssetsByMatchId { get; private set; } = new();

        public class MatchAssets
        {
            public string? LeagueLogo { get; set; }
            public string? LeagueFlag { get; set; }
            public string? HomeLogo { get; set; }
            public string? AwayLogo { get; set; }
            public string? CountryCode { get; set; }
        }

        public class EventRow
        {
            public long MatchId { get; set; }
            public int LeagueId { get; set; }
            public string LeagueName { get; set; } = "";
            public string? LeagueLogo { get; set; }
            public string? CountryName { get; set; }
            public string? CountryCode { get; set; }
            public string Home { get; set; } = "";
            public string Away { get; set; } = "";
            public string? StatusShort { get; set; }
            public int? HomeGoal { get; set; }
            public int? AwayGoal { get; set; }
            public DateTime KickoffLocal { get; set; } // *** ORA LOCALE ITALIA, come salvato in DB ***
            public string? LeagueFlag { get; set; }
            public string? HomeLogo { get; set; }
            public string? AwayLogo { get; set; }
        }

        public class CountryOption
        {
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
        }

        // DTO tipizzati per le select EF
        private sealed class RawRow
        {
            public long Id { get; set; }
            public int LeagueId { get; set; }
            public string? LeagueName { get; set; }
            public string? LeagueLogo { get; set; }
            public int? HomeGoal { get; set; }
            public int? AwayGoal { get; set; }
            public string? CountryName { get; set; }
            public string? CountryCode { get; set; }
            public string? Flag { get; set; }
            public string? Home { get; set; }
            public string? Away { get; set; }
            public string? HomeLogo { get; set; }
            public string? AwayLogo { get; set; }
            public DateTime Date { get; set; }          // *** IN DB: ORA ITALIA (senza offset) ***
            public string? StatusShort { get; set; }
        }

        private sealed class CountryPair
        {
            public string? CountryCode { get; set; }
            public string? CountryName { get; set; }
        }

        public async Task OnGetAsync(string? d = null, int? fav = null, string? country = null, string? q = null, int? best = null)
        {
            if (!string.IsNullOrWhiteSpace(d) && DateOnly.TryParse(d, out var parsed))
                SelectedDate = parsed;

            OnlyFavorites = fav == 1;
            SelectedCountryCode = string.IsNullOrWhiteSpace(country) ? null : country;
            Query = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
            ShowBest = best == 1;
            VapidPublicKey = _config["Vapid:PublicKey"] ?? "";
            // Preferiti dell'utente
            var userId = _userManager.GetUserId(User)!;
            FavoriteMatchIds = _write.FavoriteMatches
                                     .Where(fm => fm.UserId == userId)
                                     .Select(fm => fm.MatchId)
                                     .ToHashSet();

            // Finestra del giorno: *** in locale ITALIA, senza conversioni ***
            var dayStartLocal = SelectedDate.ToDateTime(TimeOnly.MinValue);      // 00:00
            var dayEndLocal = dayStartLocal.AddDays(1);                        // 24:00

            // Query base eventi del giorno (DB contiene orari già Italia)
            IQueryable<RawRow> baseQuery =
                from m in _read.Matches
                join lg in _read.Leagues on m.LeagueId equals lg.Id
                join th in _read.Teams on m.HomeId equals th.Id
                join ta in _read.Teams on m.AwayId equals ta.Id
                where m.Date >= dayStartLocal && m.Date < dayEndLocal
                select new RawRow
                {
                    Id = m.Id,
                    LeagueId = m.LeagueId,
                    LeagueName = lg.Name,
                    LeagueLogo = lg.Logo,
                    HomeGoal = m.HomeGoal,
                    AwayGoal = m.AwayGoal,
                    CountryName = lg.CountryName,
                    CountryCode = lg.CountryCode,
                    Flag = lg.Flag,
                    Home = th.Name,
                    Away = ta.Name,
                    HomeLogo = th.Logo,
                    AwayLogo = ta.Logo,
                    Date = m.Date,                  // *** già locale Italia ***
                    StatusShort = m.StatusShort
                };

            // Filtri opzionali
            if (!string.IsNullOrWhiteSpace(SelectedCountryCode))
                baseQuery = baseQuery.Where(r => r.CountryCode == SelectedCountryCode);

            if (!string.IsNullOrWhiteSpace(Query))
            {
                var qLower = Query.ToLower();
                baseQuery = baseQuery.Where(r =>
                    ((r.Home ?? "").ToLower().Contains(qLower)) ||
                    ((r.Away ?? "").ToLower().Contains(qLower)) ||
                    ((r.LeagueName ?? "").ToLower().Contains(qLower)));
            }

            var list = await baseQuery
                .AsNoTracking()
                .OrderBy(r => r.Date)
                .ToListAsync();

            if (OnlyFavorites)
                list = list.Where(r => FavoriteMatchIds.Contains(r.Id)).ToList();

            Rows = list.Select(r => new EventRow
            {
                MatchId = r.Id,
                LeagueId = r.LeagueId,
                LeagueName = r.LeagueName ?? $"League {r.LeagueId}",
                LeagueLogo = r.LeagueLogo,
                CountryName = r.CountryName,
                CountryCode = r.CountryCode,
                Home = r.Home ?? "",
                Away = r.Away ?? "",
                HomeLogo = r.HomeLogo,
                AwayLogo = r.AwayLogo,
                StatusShort = r.StatusShort,
                HomeGoal = r.HomeGoal,
                AwayGoal = r.AwayGoal,
                LeagueFlag = r.Flag,
                KickoffLocal = DateTime.SpecifyKind(r.Date, DateTimeKind.Unspecified) // manteniamo "locale" senza offset
            }).ToList();

            // Nazioni disponibili per il giorno (stessa finestra locale)
            var countriesQuery =
                from m in _read.Matches
                join lg in _read.Leagues on m.LeagueId equals lg.Id
                where m.Date >= dayStartLocal && m.Date < dayEndLocal
                select new CountryPair { CountryCode = lg.CountryCode, CountryName = lg.CountryName };

            AvailableCountries = await countriesQuery
                .GroupBy(x => new { x.CountryCode, x.CountryName })
                .Select(g => new CountryOption
                {
                    Code = g.Key.CountryCode ?? "",
                    Name = g.Key.CountryName ?? (g.Key.CountryCode ?? "")
                })
                .OrderBy(x => x.Name)
                .ToListAsync();

            if (ShowBest)
                await LoadBestPicksAsync();

            ViewData["Title"] = "Eventi";
        }
        public class ThemePayload { public string? Theme { get; set; } }

        public async Task<IActionResult> OnPostSetThemeAsync()
        {
            // Legge JSON { "theme": "light|dark|system" }
            using var reader = new StreamReader(Request.Body);
            var json = await reader.ReadToEndAsync();
            ThemePayload? payload = null;
            try { payload = JsonSerializer.Deserialize<ThemePayload>(json); } catch { /* noop */ }

            var theme = (payload?.Theme ?? "system").Trim().ToLowerInvariant();
            if (theme != "light" && theme != "dark" && theme != "system")
                theme = "system";

            // 1) Persisti su COOKIE (vale per tutti, anche anonimi)
            Response.Cookies.Append("theme", theme, new CookieOptions
            {
                Path = "/",
                HttpOnly = false,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromDays(365)
            });

            // 2) (Opzionale) Persisti su DB per utenti loggati se hai ApplicationUser.Theme
            if (User?.Identity?.IsAuthenticated == true)
            {
                var user = await _userManager.GetUserAsync(User);
                if (user != null)
                {
                    try
                    {
                        // Richiede proprietà pubblica string? Theme { get; set; } su ApplicationUser
                        user.Theme = theme;
                        await _userManager.UpdateAsync(user);
                        // aggiorna i claims/cookie di login se necessario
                        if (_signInManager != null) await _signInManager.RefreshSignInAsync(user);
                    }
                    catch
                    {
                        // Se non esiste la colonna o non vuoi salvare su DB, ignora l’errore
                    }
                }
            }

            return new JsonResult(new { ok = true, applied = theme });
        }

        private async Task LoadBestPicksAsync()
        {
            var analysis = await _read.Analyses
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.ViewName == ".." && a.Description == "Partite in Pronostico");

            if (analysis == null)
            {
                BestMessage = "Nessuna analisi trovata con viewname='..' e description='Partite in Pronostico'.";
                BestPicks = new();
                AssetsByMatchId = new();
                return;
            }

            var sql = (analysis.ViewValue ?? "").Trim();
            if (string.IsNullOrWhiteSpace(sql))
            {
                BestMessage = "La query (viewvalue) risulta vuota.";
                BestPicks = new();
                AssetsByMatchId = new();
                return;
            }

            try
            {
                BestPicks = await _read.Set<NextStakeWebApp.Models.BestPickRow>()
                    .FromSqlRaw(sql)
                    .AsNoTracking()
                    .ToListAsync();

                if (BestPicks.Count == 0)
                {
                    BestMessage = "Nessun pronostico disponibile al momento.";
                    AssetsByMatchId = new();
                    return;
                }

                var matchIds = BestPicks.Select(b => b.Id).Distinct().ToList();

                var assets = await (
                    from m in _read.Matches
                    join lg in _read.Leagues on m.LeagueId equals lg.Id
                    join th in _read.Teams on m.HomeId equals th.Id
                    join ta in _read.Teams on m.AwayId equals ta.Id
                    where matchIds.Contains(m.Id)
                    select new
                    {
                        m.Id,
                        LeagueLogo = lg.Logo,
                        LeagueFlag = lg.Flag,
                        CountryCode = lg.CountryCode,
                        HomeLogo = th.Logo,
                        AwayLogo = ta.Logo
                    }
                )
                .AsNoTracking()
                .ToListAsync();

                AssetsByMatchId = assets.ToDictionary(
                    k => (long)k.Id,
                    v => new MatchAssets
                    {
                        LeagueLogo = v.LeagueLogo,
                        LeagueFlag = v.LeagueFlag,
                        HomeLogo = v.HomeLogo,
                        AwayLogo = v.AwayLogo,
                        CountryCode = v.CountryCode
                    }
                );
            }
            catch (Exception ex)
            {
                BestMessage = $"Errore nell'esecuzione della query dei pronostici: {ex.Message}";
                BestPicks = new();
                AssetsByMatchId = new();
            }
        }

        public async Task<IActionResult> OnPostToggleFavoriteAsync(long matchId, string? d = null, int? fav = null, string? country = null, string? q = null)
        {
            var userId = _userManager.GetUserId(User)!;

            var existing = await _write.FavoriteMatches
                .FirstOrDefaultAsync(f => f.UserId == userId && f.MatchId == matchId);

            if (existing == null)
                _write.FavoriteMatches.Add(new FavoriteMatch { UserId = userId, MatchId = matchId });
            else
                _write.FavoriteMatches.Remove(existing);

            await _write.SaveChangesAsync();
            return RedirectToPage(new { d, fav, country, q });
        }
    }

    // Rimuovi se hai già la tua entity
    public class Analysis
    {
        public int Id { get; set; }
        public string ViewName { get; set; } = "";
        public string Description { get; set; } = "";
        public string ViewValue { get; set; } = "";
    }
}