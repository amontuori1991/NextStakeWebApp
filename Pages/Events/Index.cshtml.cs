using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Authorization;
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

        public IndexModel(ReadDbContext read, ApplicationDbContext write, UserManager<ApplicationUser> userManager)
        {
            _read = read;
            _write = write;
            _userManager = userManager;
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
            public int? HomeGoal { get; set; }
            public int? AwayGoal { get; set; }
            public DateTime KickoffUtc { get; set; }
        }

        public class CountryOption
        {
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
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

            var day = SelectedDate; // DateOnly
            var dayDateTimeUtc = DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

            // Query base eventi del giorno
            var baseQuery =
                from m in _read.Matches
                join lg in _read.Leagues on m.LeagueId equals lg.Id
                join th in _read.Teams on m.HomeId equals th.Id
                join ta in _read.Teams on m.AwayId equals ta.Id
                where m.Date.Date == dayDateTimeUtc.Date
                select new
                {
                    m.Id,
                    m.LeagueId,
                    LeagueName = lg.Name,
                    LeagueLogo = lg.Logo,
                    HomeGoal = m.HomeGoal,
                    AwayGoal = m.AwayGoal,
                    CountryName = lg.CountryName,
                    CountryCode = lg.CountryCode,
                    Home = th.Name,
                    Away = ta.Name,
                    m.Date
                };

            // Filtri opzionali
            if (!string.IsNullOrWhiteSpace(SelectedCountryCode))
                baseQuery = baseQuery.Where(r => r.CountryCode == SelectedCountryCode);

            if (!string.IsNullOrWhiteSpace(Query))
            {
                var qLower = Query.ToLower();
                baseQuery = baseQuery.Where(r =>
                    (r.Home ?? "").ToLower().Contains(qLower) ||
                    (r.Away ?? "").ToLower().Contains(qLower) ||
                    (r.LeagueName ?? "").ToLower().Contains(qLower));
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
                HomeGoal = r.HomeGoal,
                AwayGoal = r.AwayGoal,
                KickoffUtc = r.Date
            }).ToList();

            // Nazioni disponibili per il giorno
            AvailableCountries = await (
                from m in _read.Matches
                join lg in _read.Leagues on m.LeagueId equals lg.Id
                where m.Date.Date == dayDateTimeUtc.Date
                select new { lg.CountryCode, lg.CountryName }
            )
            .Distinct()
            .OrderBy(x => x.CountryName)
            .Select(x => new CountryOption
            {
                Code = x.CountryCode ?? "",
                Name = x.CountryName ?? (x.CountryCode ?? "")
            })
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

                // ===== Enrichment: prendi asset (logo squadra home/away, logo lega, flag) per i match nei Best =====
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
                        CountryCode = lg.CountryCode,   // <-- AGGIUNTO
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

                // ================================================================================================

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
