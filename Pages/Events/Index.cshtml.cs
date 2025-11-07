using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;

namespace NextStakeWebApp.Pages.Events
{
    [Authorize]
    public class IndexModel : PageModel
    {
        private readonly ReadDbContext _read;
        private readonly ApplicationDbContext _write;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IConfiguration _cfg;
        private readonly TimeZoneInfo _tzRome;
        private readonly bool _datesLocalInDb; // true: orari salvati come Europe/Rome; false: salvati in UTC

        public IndexModel(ReadDbContext read, ApplicationDbContext write, UserManager<ApplicationUser> userManager, IConfiguration cfg)
        {
            _read = read;
            _write = write;
            _userManager = userManager;
            _cfg = cfg;

            var tzId = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "W. Europe Standard Time"
                : "Europe/Rome";
            _tzRome = TimeZoneInfo.FindSystemTimeZoneById(tzId);

            // Env/appsettings: DatesLocalInDb (o DATES_LOCAL_IN_DB)
            var raw = _cfg["DatesLocalInDb"] ?? _cfg["DATES_LOCAL_IN_DB"];
            _datesLocalInDb = raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }

        // Converte un DateTime letto dal DB in UTC coerente con la config
        private DateTime FromDbToUtc(DateTime dbDate)
        {
            if (_datesLocalInDb)
            {
                // DB contiene Europe/Rome senza offset
                var local = DateTime.SpecifyKind(dbDate, DateTimeKind.Unspecified);
                return TimeZoneInfo.ConvertTimeToUtc(local, _tzRome);
            }
            else
            {
                // DB contiene UTC (anche se Unspecified)
                return DateTime.SpecifyKind(dbDate, DateTimeKind.Utc);
            }
        }

        public DateOnly SelectedDate { get; private set; } = DateOnly.FromDateTime(DateTime.UtcNow);
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
            public DateTime KickoffUtc { get; set; } // SEMPRE UTC per la View
            public string? LeagueFlag { get; set; }
            public string? HomeLogo { get; set; }
            public string? AwayLogo { get; set; }
        }

        public class CountryOption
        {
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
        }

        // ===== DTO tipizzati per le SELECT EF (niente dynamic!) =====
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
            public DateTime Date { get; set; }
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

            // Preferiti dell'utente
            var userId = _userManager.GetUserId(User)!;
            FavoriteMatchIds = _write.FavoriteMatches
                                     .Where(fm => fm.UserId == userId)
                                     .Select(fm => fm.MatchId)
                                     .ToHashSet();

            var day = SelectedDate;

            // Finestra della giornata [00:00, 24:00) nella TZ di Roma
            DateTime dayStartLocal = day.ToDateTime(TimeOnly.MinValue);
            DateTime dayEndLocal = dayStartLocal.AddDays(1);

            // E anche in UTC (per il ramo UTC)
            DateTime dayStartUtc, dayEndUtc;
            if (_datesLocalInDb)
            {
                dayStartUtc = TimeZoneInfo.ConvertTimeToUtc(dayStartLocal, _tzRome);
                dayEndUtc = TimeZoneInfo.ConvertTimeToUtc(dayEndLocal, _tzRome);
            }
            else
            {
                dayStartUtc = DateTime.SpecifyKind(dayStartLocal, DateTimeKind.Utc);
                dayEndUtc = DateTime.SpecifyKind(dayEndLocal, DateTimeKind.Utc);
            }

            // =========================
            // Query base eventi del giorno (TIPIZZATA)
            // =========================
            IQueryable<RawRow> baseQuery;
            if (_datesLocalInDb)
            {
                var localStart = dayStartLocal;
                var localEnd = dayEndLocal;

                baseQuery =
                    from m in _read.Matches
                    join lg in _read.Leagues on m.LeagueId equals lg.Id
                    join th in _read.Teams on m.HomeId equals th.Id
                    join ta in _read.Teams on m.AwayId equals ta.Id
                    where m.Date >= localStart && m.Date < localEnd
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
                        Date = m.Date,
                        StatusShort = m.StatusShort
                    };
            }
            else
            {
                var utcStart = dayStartUtc;
                var utcEnd = dayEndUtc;

                baseQuery =
                    from m in _read.Matches
                    join lg in _read.Leagues on m.LeagueId equals lg.Id
                    join th in _read.Teams on m.HomeId equals th.Id
                    join ta in _read.Teams on m.AwayId equals ta.Id
                    where m.Date >= utcStart && m.Date < utcEnd
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
                        Date = m.Date,
                        StatusShort = m.StatusShort
                    };
            }

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
                // Normalizziamo SEMPRE a UTC per la vista
                KickoffUtc = FromDbToUtc(r.Date)
            }).ToList();

            // =========================
            // Nazioni disponibili per il giorno (stessa finestra del where) - TIPIZZATA
            // =========================
            IQueryable<CountryPair> countriesQuery;
            if (_datesLocalInDb)
            {
                countriesQuery =
                    from m in _read.Matches
                    join lg in _read.Leagues on m.LeagueId equals lg.Id
                    where m.Date >= dayStartLocal && m.Date < dayEndLocal
                    select new CountryPair { CountryCode = lg.CountryCode, CountryName = lg.CountryName };
            }
            else
            {
                countriesQuery =
                    from m in _read.Matches
                    join lg in _read.Leagues on m.LeagueId equals lg.Id
                    where m.Date >= dayStartUtc && m.Date < dayEndUtc
                    select new CountryPair { CountryCode = lg.CountryCode, CountryName = lg.CountryName };
            }

            AvailableCountries = await countriesQuery
                .GroupBy(x => new { x.CountryCode, x.CountryName })
                .Select(g => new CountryOption
                {
                    Code = g.Key.CountryCode ?? "",
                    Name = g.Key.CountryName ?? (g.Key.CountryCode ?? "")
                })
                .OrderBy(x => x.Name)
                .ToListAsync();

            // Carica "Migliori pronostici" se richiesto
            if (ShowBest)
            {
                await LoadBestPicksAsync();
            }

            ViewData["Title"] = "Eventi";
        }

        private async Task LoadBestPicksAsync()
        {
            // Legge definizione query dalla tabella analyses
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

                // ===== Enrichment: asset per i match nei Best =====
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

    // Model minimo per la tabella 'analyses' se non fosse già presente nel tuo progetto
    // RIMUOVILO se hai già la tua classe Analysis mappata nel contesto.
    public class Analysis
    {
        public int Id { get; set; }
        public string ViewName { get; set; } = "";
        public string Description { get; set; } = "";
        public string ViewValue { get; set; } = "";
    }
}
