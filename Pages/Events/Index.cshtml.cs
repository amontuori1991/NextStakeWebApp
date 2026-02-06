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
using System.Globalization;
using Npgsql;
using System.Text.RegularExpressions;

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

        public bool HasBestCandidatesToday { get; set; }
        // === Migliori Exchange di Oggi ===
        public bool ShowExchange { get; private set; }
        public List<ExchangeTodayRow> ExchangePicks { get; private set; } = new();

        public string? ExchangeMessage { get; private set; }
        public bool HasExchangeCandidatesToday { get; private set; }

        // DTO per la view exchange_exact_lay_candidates...
        public class ExchangePickRow
        {
            public long Match_Id { get; set; }
            public DateTime Match_Date { get; set; }
            public int LeagueId { get; set; }
            public int Season { get; set; }
            public int HomeId { get; set; }
            public string? Home_Name { get; set; }
            public int AwayId { get; set; }
            public string? Away_Name { get; set; }

            public float? Odd_Home { get; set; }
            public float? Odd_Draw { get; set; }
            public float? Odd_Away { get; set; }
            public float? Odd_Over_15 { get; set; }
            public float? Odd_Over_25 { get; set; }

            public int? Home_Rank { get; set; }
            public int? Away_Rank { get; set; }
            public int? Rank_Diff { get; set; }

            public float? Xg_Pred_Home { get; set; }
            public float? Xg_Pred_Away { get; set; }
            public float? Xg_Pred_Total { get; set; }

            public int Home_Hist_N { get; set; }
            public int Away_Hist_N { get; set; }

            public string? Favorite_Side { get; set; }
            public decimal? Favorite_Odd { get; set; }
            public string? Score_To_Lay { get; set; }
            public int Lay_Ok { get; set; }
            public int Rating { get; set; }
        }

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

        public async Task OnGetAsync(string? d = null, int? fav = null, string? country = null, string? q = null, int? best = null, int? ex = null)

        {
            if (!string.IsNullOrWhiteSpace(d) && DateOnly.TryParse(d, out var parsed))
                SelectedDate = parsed;

            OnlyFavorites = fav == 1;
            SelectedCountryCode = string.IsNullOrWhiteSpace(country) ? null : country;
            Query = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
            ShowBest = best == 1;
            ShowExchange = ex == 1;
            // 🔕 NOTIFICHE (PUSH) DISABILITATE TEMPORANEAMENTE
            // VapidPublicKey = _config["Vapid:PublicKey"] ?? "";
            VapidPublicKey = "";


            HasExchangeCandidatesToday = await CheckHasExchangeCandidatesTodayAsync();
          

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
            // =========================
            // 🔴 LIVE DISABILITATO TEMPORANEAMENTE
            // =========================
            // // 🔚 Sovrascrivo risultati/stato con quelli definitivi da LiveMatchStates (FT/AET/PEN)
            // var matchIdsInPage = Rows
            //     .Select(r => (int)r.MatchId)   // LiveMatchStates.MatchId è int
            //     .Distinct()
            //     .ToList();
            //
            // if (matchIdsInPage.Count > 0)
            // {
            //     // 1) Prendo TUTTI gli stati per i match che sto mostrando
            //     var allStates = await _write.LiveMatchStates
            //         .Where(s => matchIdsInPage.Contains(s.MatchId))
            //         .ToListAsync();
            //
            //     // 2) Normalizzo LastStatus (Trim + ToUpper) e tengo solo FT / AET / PEN
            //     var finalStates = allStates
            //         .Where(s =>
            //             !string.IsNullOrWhiteSpace(s.LastStatus) &&
            //             new[]
            //             {
            //                 "FT",
            //                 "AET",
            //                 "PEN"
            //             }.Contains(s.LastStatus.Trim().ToUpperInvariant())
            //         )
            //         // se per assurdo ci fossero più righe per lo stesso MatchId, ne prendo una (la prima)
            //         .GroupBy(s => s.MatchId)
            //         .ToDictionary(g => g.Key, g => g.First());
            //
            //     // 3) Applico i valori finali alla lista Rows che va in pagina
            //     foreach (var row in Rows)
            //     {
            //         var key = (int)row.MatchId;
            //         if (!finalStates.TryGetValue(key, out var st))
            //             continue;
            //
            //         // Stato finale (verrà usato anche da FormatScore e dalla logica live/non-live)
            //         row.StatusShort = st.LastStatus?.Trim().ToUpperInvariant() ?? row.StatusShort;
            //
            //         // Gol finali
            //         if (st.LastHome.HasValue)
            //             row.HomeGoal = st.LastHome;
            //
            //         if (st.LastAway.HasValue)
            //             row.AwayGoal = st.LastAway;
            //     }
            // }



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

            if (ShowExchange)
                await LoadExchangePicksAsync();


            ViewData["Title"] = "Eventi";
        }
        public class ThemePayload { public string? Theme { get; set; } }
        public async Task<IActionResult> OnGetMatchesOfDayAsync(
            string? d,
            int? fav = null,
            string? country = null,
            string? q = null,
            int? best = null)
        {
            // Richiamo la stessa logica della pagina Eventi,
            // usando ESATTAMENTE gli stessi parametri di filtro
            await OnGetAsync(d: d, fav: fav, country: country, q: q, best: best);

            List<long> orderedIds;

            if (ShowBest && BestPicks.Any())
            {
                // 🟢 MODALITÀ "PARTITE IN PRONOSTICO" (Best picks)

                // Id dei match in pronostico
                var ids = BestPicks
                    .Select(b => (long)b.Id)
                    .Distinct()
                    .ToList();

                // Finestra del giorno corrente (Italia), come fai per Rows
                var dayStartLocal = SelectedDate.ToDateTime(TimeOnly.MinValue);
                var dayEndLocal = dayStartLocal.AddDays(1);

                // Recupero meta-informazioni dal DB lettura
                var meta = await (
                    from m in _read.Matches
                    join lg in _read.Leagues on m.LeagueId equals lg.Id
                    where ids.Contains(m.Id)
                       && m.Date >= dayStartLocal
                       && m.Date < dayEndLocal
                    select new
                    {
                        m.Id,
                        m.Date,
                        CountryName = lg.CountryName,
                        LeagueName = lg.Name
                    }
                )
                .AsNoTracking()
                .ToListAsync();

                // Raggruppo per Nazione + Lega, poi ordino dentro per orario
                var groupedMeta = meta
                    .GroupBy(x => new { x.CountryName, x.LeagueName })
                    .OrderBy(g => g.Key.CountryName ?? string.Empty)
                    .ThenBy(g => g.Key.LeagueName ?? string.Empty);

                orderedIds = new List<long>();

                foreach (var g in groupedMeta)
                {
                    orderedIds.AddRange(
                        g.OrderBy(x => x.Date)
                         .ThenBy(x => x.Id)
                         .Select(x => (long)x.Id)
                    );
                }
            }
            else
            {
                // 🟡 MODALITÀ "TUTTI GLI EVENTI" -> usiamo Rows come prima

                var groupedRows = Rows
                    .GroupBy(r => new
                    {
                        r.CountryName,
                        r.CountryCode,
                        r.LeagueName,
                        r.LeagueId
                    })
                    .ToList(); // IMPORTANTISSIMO: niente OrderBy sui gruppi

                orderedIds = new List<long>();

                foreach (var g in groupedRows)
                {
                    orderedIds.AddRange(
                        g.OrderBy(r => r.KickoffLocal)
                         .ThenBy(r => r.MatchId)
                         .Select(r => r.MatchId)
                    );
                }
            }

            return new JsonResult(new
            {
                ok = true,
                matches = orderedIds
            });
        }


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
        private async Task<bool> CheckHasExchangeCandidatesTodayAsync()
        {
            // La view è "today": se non stai guardando "oggi", consideriamo già false
            var isSelectedToday = SelectedDate == DateOnly.FromDateTime(DateTime.Now);
            if (!isSelectedToday) return false;

            try
            {
                // 1) Prendo lo script dalla tabella Analyses
                var script = await _read.Analyses
                    .Where(a => a.ViewName == "NextMatch_ExchangeToday")
                    .Select(a => a.ViewValue)
                    .AsNoTracking()
                    .FirstOrDefaultAsync();

                if (string.IsNullOrWhiteSpace(script))
                    return false;

                // 2) Lo trasformo in "LIMIT 1" (check veloce)
                var trimmed = script.Trim().TrimEnd(';');

                string probeSql;
                if (Regex.IsMatch(trimmed, @"\bLIMIT\s+\d+\b", RegexOptions.IgnoreCase))
                {
                    // sostituisce LIMIT N con LIMIT 1
                    probeSql = Regex.Replace(trimmed, @"\bLIMIT\s+\d+\b", "LIMIT 1", RegexOptions.IgnoreCase);
                }
                else
                {
                    // fallback: wrappa e limita
                    probeSql = $"SELECT * FROM ({trimmed}) t LIMIT 1";
                }

                // 3) Eseguo e mi basta sapere se esiste una riga
                var cs = _read.Database.GetConnectionString();
                await using var conn = new NpgsqlConnection(cs);
                await conn.OpenAsync();

                await using var cmd = new NpgsqlCommand(probeSql, conn)
                {
                    CommandTimeout = 30
                };

                await using var rd = await cmd.ExecuteReaderAsync();
                return await rd.ReadAsync();
            }
            catch
            {
                // se per qualsiasi motivo fallisce, non blocchiamo la pagina: bottone disabilitato
                return false;
            }
        }

        private async Task LoadExchangePicksAsync()
        {
            // La view è "today": se non sei su oggi, non ha senso caricarla
            var isSelectedToday = SelectedDate == DateOnly.FromDateTime(DateTime.Now);
            if (!isSelectedToday)
            {
                ExchangePicks = new();
                ExchangeMessage = "La sezione Exchange è disponibile solo su Oggi.";
                return;
            }

            // Se abbiamo già calcolato prima che non ci sono candidati, evitiamo query pesanti
            if (!HasExchangeCandidatesToday)
            {
                ExchangePicks = new();
                ExchangeMessage = "Nessun candidato exchange disponibile al momento.";
                return;
            }

            var analysis = await _read.Analyses
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.ViewName == "NextMatch_ExchangeToday");


            if (analysis == null)
            {
                ExchangeMessage = "Nessuna analisi trovata con description='Migliori Exchange Di Oggi'.";
                ExchangePicks = new();
                return;
            }

            var sql = (analysis.ViewValue ?? "").Trim();
            if (string.IsNullOrWhiteSpace(sql))
            {
                ExchangeMessage = "La query (viewvalue) risulta vuota.";
                ExchangePicks = new();
                return;
            }

            try
            {
                ExchangePicks = await _read.Set<ExchangeTodayRow>()
                    .FromSqlRaw(sql)
                    .AsNoTracking()
                    .ToListAsync();

                if (ExchangePicks.Count == 0)
                {
                    // aggiornamento coerente della flag (così bottone si spegne alla prossima)
                    HasExchangeCandidatesToday = false;

                    ExchangeMessage = "Nessun candidato exchange disponibile al momento.";
                    return;
                }

                // Carico loghi/flag come fai per i Best
                var matchIds = ExchangePicks.Select(x => (int)x.Match_Id).Distinct().ToList();

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

                // Nota: NON sovrascrivo AssetsByMatchId globale se hai già caricato quelli dei Best.
                // Qui faccio merge.
                foreach (var a in assets)
                {
                    AssetsByMatchId[(long)a.Id] = new MatchAssets
                    {
                        LeagueLogo = a.LeagueLogo,
                        LeagueFlag = a.LeagueFlag,
                        HomeLogo = a.HomeLogo,
                        AwayLogo = a.AwayLogo,
                        CountryCode = a.CountryCode
                    };
                }
            }
            catch (Exception ex)
            {
                ExchangeMessage = $"Errore nell'esecuzione della query Exchange: {ex.Message}";
                ExchangePicks = new();
            }
        }

        public async Task<IActionResult> OnPostToggleFavoriteAsync(
            long matchId,
            string? d = null,
            int? fav = null,
            string? country = null,
            string? q = null)
        {
            var userId = _userManager.GetUserId(User)!;

            var existing = await _write.FavoriteMatches
                .FirstOrDefaultAsync(f => f.UserId == userId && f.MatchId == matchId);

            if (existing == null)
            {
                // ✅ 1) Aggiungo ai preferiti
                existing = new FavoriteMatch
                {
                    UserId = userId,
                    MatchId = matchId
                };
                _write.FavoriteMatches.Add(existing);

                // =========================
                // 🔴 LIVE DISABILITATO TEMPORANEAMENTE
                // =========================
                // // ✅ 2) Inizializzo lo stato live se NON esiste già
                // var existingState = await _write.LiveMatchStates
                //     .FirstOrDefaultAsync(s => s.MatchId == (int)matchId);
                //
                // if (existingState == null)
                // {
                //     // Prendo lo stato attuale dal DB di lettura
                //     var match = await _read.Matches
                //         .Where(m => m.Id == matchId)
                //         .Select(m => new
                //         {
                //             m.StatusShort,
                //             m.HomeGoal,
                //             m.AwayGoal
                //         })
                //         .FirstOrDefaultAsync();
                //
                //     var status = (match?.StatusShort ?? "NS").ToUpperInvariant();
                //     int? home = match?.HomeGoal;
                //     int? away = match?.AwayGoal;
                //
                //     // Elapsed non ce l’abbiamo in Matches, partiamo da 0
                //     var state = new LiveMatchState
                //     {
                //         MatchId = (int)matchId,   // LiveMatchStates.MatchId è int
                //         LastStatus = status,
                //         LastHome = home,
                //         LastAway = away,
                //         LastElapsed = 0,
                //         LastUpdatedUtc = DateTime.UtcNow
                //     };
                //
                //     _write.LiveMatchStates.Add(state);
                // }

            }
            else
            {
                // ❌ Rimozione dai preferiti
                _write.FavoriteMatches.Remove(existing);
                // NON tocchiamo LiveMatchStates: lo gestisce il job, ed è per match globale
            }

            await _write.SaveChangesAsync();

            // Torna alla stessa pagina con i filtri correnti
            return RedirectToPage(new { d, fav, country, q });
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
}