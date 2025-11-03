using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;
using Npgsql;
using System.Data;
using System.Text.RegularExpressions;
using NextStakeWebApp.Services;

namespace NextStakeWebApp.Pages.Match
{
    [Authorize]
    public class DetailsModel : PageModel
    {
        private readonly ReadDbContext _read;
        private readonly ApplicationDbContext _write;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ITelegramService _telegram;

        public DetailsModel(
            ReadDbContext read,
            ApplicationDbContext write,
            UserManager<ApplicationUser> userManager,
            ITelegramService telegram)
        {
            _read = read;
            _write = write;
            _userManager = userManager;
            _telegram = telegram;
        }

        public VM Data { get; private set; } = new();

        public class VM
        {
            public long MatchId { get; set; }
            public int LeagueId { get; set; }
            public int Season { get; set; }

            public string LeagueName { get; set; } = "";
            public string? LeagueLogo { get; set; }
            public string? CountryName { get; set; }

            public long HomeId { get; set; }
            public long AwayId { get; set; }
            public string Home { get; set; } = "";
            public string Away { get; set; } = "";
            public string? HomeLogo { get; set; }
            public string? AwayLogo { get; set; }

            public DateTime KickoffUtc { get; set; }
            public bool IsFavorite { get; set; }
            public int RemainingMatches { get; set; }

            public List<FormRow> HomeForm { get; set; } = new();
            public List<FormRow> AwayForm { get; set; } = new();

            public List<StandRow> Standings { get; set; } = new();

            public List<H2HRow> HeadToHead { get; set; } = new();

            public PredictionRow? Prediction { get; set; }
            public ExchangePredictionRow? Exchange { get; set; }

            // Analisi varie
            public GoalsAnalysisRow? Goals { get; set; }          // NextMatchGoals_Analyses
            public ShotsAnalysisRow? Shots { get; set; }          // NextMatchShots_Analyses
            public CornersAnalysisRow? Corners { get; set; }      // NextMatchCorners_Analyses
            public CardsAnalysisRow? Cards { get; set; }          // NextMatchCards_Analyses
            public FoulsAnalysisRow? Fouls { get; set; }          // NextMatchFouls_Analyses
            public OffsidesAnalysisRow? Offsides { get; set; }    // NextMatchOffsides_Analyses

            public int? HomeGoal { get; set; }
            public int? AwayGoal { get; set; }
            public string? StatusShort { get; set; }
        }

        public class FormRow
        {
            public int MatchId { get; set; }
            public DateTime DateUtc { get; set; }
            public string Opponent { get; set; } = "";
            public bool IsHome { get; set; }
            public string Score { get; set; } = "";
            public string Result { get; set; } = "";
        }

        public class H2HRow
        {
            public long MatchId { get; set; }
            public DateTime DateUtc { get; set; }
            public string LeagueName { get; set; } = "";
            public string HomeName { get; set; } = "";
            public string AwayName { get; set; } = "";
            public string Score { get; set; } = "";
        }

        public class StandRow
        {
            public int Rank { get; set; }
            public long TeamId { get; set; }
            public string TeamName { get; set; } = "";
            public int Points { get; set; }
            public int Played { get; set; }
            public int Win { get; set; }
            public int Draw { get; set; }
            public int Lose { get; set; }
            public int GF { get; set; }
            public int GA { get; set; }
            public int Diff { get; set; }
        }

        // Contenitori generici per ogni vista Analyses (chiave/valore)
        public class GoalsAnalysisRow { public Dictionary<string, string> Metrics { get; set; } = new(); }
        public class ShotsAnalysisRow { public Dictionary<string, string> Metrics { get; set; } = new(); }
        public class CornersAnalysisRow { public Dictionary<string, string> Metrics { get; set; } = new(); }
        public class CardsAnalysisRow { public Dictionary<string, string> Metrics { get; set; } = new(); }
        public class FoulsAnalysisRow { public Dictionary<string, string> Metrics { get; set; } = new(); }
        public class OffsidesAnalysisRow { public Dictionary<string, string> Metrics { get; set; } = new(); }

        public async Task<IActionResult> OnGetAsync(long id)
        {
            var dto = await (
                from mm in _read.Matches
                join lg in _read.Leagues on mm.LeagueId equals lg.Id
                join th in _read.Teams on mm.HomeId equals th.Id
                join ta in _read.Teams on mm.AwayId equals ta.Id
                where mm.Id == id
                select new
                {
                    MatchId = mm.Id,
                    LeagueId = mm.LeagueId,
                    Season = mm.Season,
                    LeagueName = lg.Name,
                    LeagueLogo = lg.Logo,
                    CountryName = lg.CountryName,
                    Home = th.Name ?? "",
                    Away = ta.Name ?? "",
                    HomeLogo = th.Logo,
                    AwayLogo = ta.Logo,
                    HomeTeamId = th.Id,
                    AwayTeamId = ta.Id,
                    KickoffUtc = mm.Date,
                    HomeGoal = mm.HomeGoal,
                    AwayGoal = mm.AwayGoal,
                    StatusShort = mm.StatusShort
                }
            ).AsNoTracking().FirstOrDefaultAsync();

            if (dto == null) return NotFound();

            var userId = _userManager.GetUserId(User)!;
            var isFav = await _write.FavoriteMatches.AnyAsync(f => f.UserId == userId && f.MatchId == id);

            var homeForm = await GetLastFiveAsync((int)dto.HomeTeamId, dto.LeagueId, dto.Season);
            var awayForm = await GetLastFiveAsync((int)dto.AwayTeamId, dto.LeagueId, dto.Season);

            var standings = await (
                from s in _read.Standings
                join t in _read.Teams on s.TeamId equals t.Id
                where s.LeagueId == dto.LeagueId && s.Season == dto.Season
                orderby s.Rank
                select new StandRow
                {
                    Rank = s.Rank ?? 0,
                    TeamId = t.Id,
                    TeamName = t.Name ?? "",
                    Points = s.Points ?? 0,
                    Played = s.AllPlayed ?? 0,
                    Win = s.AllWin ?? 0,
                    Draw = s.AllDraw ?? 0,
                    Lose = s.AllLose ?? 0,
                    GF = s.AllGoalFor ?? 0,
                    GA = s.AllGoalAgainst ?? 0,
                    Diff = s.GoalsDiff ?? 0
                }
            ).AsNoTracking().ToListAsync();

            // --- Prediction ---
            PredictionRow? prediction = null;
            var script = await _read.Analyses
                .Where(a => a.ViewName == "NextMatch_Prediction_New")
                .Select(a => a.ViewValue)
                .AsNoTracking()
                .FirstOrDefaultAsync();

            if (!string.IsNullOrWhiteSpace(script))
            {
                var cs = _read.Database.GetConnectionString();
                await using var conn = new NpgsqlConnection(cs);
                await conn.OpenAsync();

                await using var cmd = new NpgsqlCommand(script, conn);
                cmd.Parameters.AddWithValue("@MatchId", (int)id);

                await using var rd = await cmd.ExecuteReaderAsync();
                if (await rd.ReadAsync())
                {
                    prediction = new PredictionRow
                    {
                        Id = GetField<int>(rd, "Id"),
                        GoalSimulatoCasa = GetField<int>(rd, "Goal Simulati Casa"),
                        GoalSimulatoOspite = GetField<int>(rd, "Goal Simulati Ospite"),
                        TotaleGoalSimulati = GetField<int>(rd, "Totale Goal Simulati"),
                        Esito = GetField<string>(rd, "Esito"),
                        OverUnderRange = GetField<string>(rd, "OverUnderRange"),
                        Over1_5 = GetField<decimal?>(rd, "Over1_5"),
                        Over2_5 = GetField<decimal?>(rd, "Over2_5"),
                        Over3_5 = GetField<decimal?>(rd, "Over3_5"),
                        GG_NG = GetField<string>(rd, "GG_NG"),
                        MultigoalCasa = GetField<string>(rd, "MultigoalCasa"),
                        MultigoalOspite = GetField<string>(rd, "MultigoalOspite"),
                        ComboFinale = GetField<string>(rd, "ComboFinale")
                    };
                }
            }

            // --- Exchange ---
            ExchangePredictionRow? exchange = null;
            var exchangeScript = await _read.Analyses
                .Where(a => a.ViewName == "NextMatch_Prediction_Exchange")
                .Select(a => a.ViewValue)
                .AsNoTracking()
                .FirstOrDefaultAsync();

            if (!string.IsNullOrWhiteSpace(exchangeScript))
            {
                var cs = _read.Database.GetConnectionString();
                await using var conn2 = new NpgsqlConnection(cs);
                await conn2.OpenAsync();

                await using var cmd2 = new NpgsqlCommand(exchangeScript, conn2);
                cmd2.Parameters.AddWithValue("@MatchId", (int)id);

                await using var rd2 = await cmd2.ExecuteReaderAsync();
                if (await rd2.ReadAsync())
                {
                    exchange = new ExchangePredictionRow
                    {
                        MatchId = GetField<long>(rd2, "MatchId"),
                        Banca1Affidabilita = GetField<int?>(rd2, "Banca 1 - Affidabilità %"),
                        BancaXAffidabilita = GetField<int?>(rd2, "Banca X - Affidabilità %"),
                        Banca2Affidabilita = GetField<int?>(rd2, "Banca 2 - Affidabilità %"),
                        BancataConsigliata = GetField<string>(rd2, "Bancata consigliata"),
                        BancaRisultato1 = GetField<string>(rd2, "Banca Risultato 1"),
                        BancaRisultato2 = GetField<string>(rd2, "Banca Risultato 2"),
                        BancaRisultato3 = GetField<string>(rd2, "Banca Risultato 3"),
                    };
                }
            }

            // --- Analyses vari ---
            GoalsAnalysisRow? goals = await RunGenericAnalysisAsync<GoalsAnalysisRow>(
                "NextMatchGoals_Analyses", dto.LeagueId, dto.Season, (int)id);

            ShotsAnalysisRow? shots = await RunGenericAnalysisAsync<ShotsAnalysisRow>(
                "NextMatchShots_Analyses", dto.LeagueId, dto.Season, (int)id);

            CornersAnalysisRow? corners = await RunGenericAnalysisAsync<CornersAnalysisRow>(
                "NextMatchCorners_Analyses", dto.LeagueId, dto.Season, (int)id);

            CardsAnalysisRow? cards = await RunGenericAnalysisAsync<CardsAnalysisRow>(
                "NextMatchCards_Analyses", dto.LeagueId, dto.Season, (int)id);

            FoulsAnalysisRow? fouls = await RunGenericAnalysisAsync<FoulsAnalysisRow>(
                "NextMatchFouls_Analyses", dto.LeagueId, dto.Season, (int)id);

            OffsidesAnalysisRow? offsides = await RunGenericAnalysisAsync<OffsidesAnalysisRow>(
                "NextMatchOffsides_Analyses", dto.LeagueId, dto.Season, (int)id);

            // --- H2H (tutte le stagioni, solo partite terminate) ---
            var finished = new[] { "FT", "AET", "PEN" };
            var h2h = await (
                from m in _read.Matches
                join th in _read.Teams on m.HomeId equals th.Id
                join ta in _read.Teams on m.AwayId equals ta.Id
                join lg in _read.Leagues on m.LeagueId equals lg.Id
                where finished.Contains(m.StatusShort!)
                      && ((m.HomeId == dto.HomeTeamId && m.AwayId == dto.AwayTeamId)
                       || (m.HomeId == dto.AwayTeamId && m.AwayId == dto.HomeTeamId))
                      && m.Date <= DateTime.UtcNow
                orderby m.Date descending
                select new H2HRow
                {
                    MatchId = m.Id,
                    DateUtc = m.Date,
                    LeagueName = lg.Name ?? "",
                    HomeName = th.Name ?? "",
                    AwayName = ta.Name ?? "",
                    Score = $"{(m.HomeGoal ?? 0)}-{(m.AwayGoal ?? 0)}"
                }
            ).AsNoTracking().ToListAsync();

            // --- Giornate mancanti (lega+stagione, non terminate) ---
            int remainingMatches = await _read.Matches
               .Where(m => m.LeagueId == dto.LeagueId
                        && m.Season == dto.Season
                        && (m.StatusShort == null || m.StatusShort != "FT"))
               .Select(m => m.MatchRound)
               .Distinct()
               .CountAsync();

            Data = new VM
            {
                MatchId = dto.MatchId,
                LeagueId = dto.LeagueId,
                Season = dto.Season,
                LeagueName = dto.LeagueName ?? "League",
                LeagueLogo = dto.LeagueLogo,
                CountryName = dto.CountryName,
                Home = dto.Home,
                Away = dto.Away,
                HomeLogo = dto.HomeLogo,
                AwayLogo = dto.AwayLogo,
                HomeId = dto.HomeTeamId,
                AwayId = dto.AwayTeamId,
                KickoffUtc = dto.KickoffUtc,
                HomeGoal = dto.HomeGoal,
                AwayGoal = dto.AwayGoal,
                StatusShort = dto.StatusShort,
                IsFavorite = isFav,
                HomeForm = homeForm,
                AwayForm = awayForm,
                Standings = standings,
                Prediction = prediction,
                Exchange = exchange,
                Goals = goals,
                Shots = shots,
                Corners = corners,
                Cards = cards,
                Fouls = fouls,
                Offsides = offsides,
                HeadToHead = h2h,
                RemainingMatches = remainingMatches
            };

            return Page();
        }

        public async Task<IActionResult> OnPostToggleFavoriteAsync(long id)
        {
            var userId = _userManager.GetUserId(User)!;
            var existing = await _write.FavoriteMatches.FirstOrDefaultAsync(f => f.UserId == userId && f.MatchId == id);
            if (existing == null)
                _write.FavoriteMatches.Add(new FavoriteMatch { UserId = userId, MatchId = id });
            else
                _write.FavoriteMatches.Remove(existing);

            await _write.SaveChangesAsync();
            return RedirectToPage(new { id });
        }

        public async Task<IActionResult> OnPostSendPredictionAsync(long id)
        {
            // Dati minimi per il messaggio + CountryCode
            var dto = await (
               from mm in _read.Matches
               join lg in _read.Leagues on mm.LeagueId equals lg.Id
               join th in _read.Teams on mm.HomeId equals th.Id
               join ta in _read.Teams on mm.AwayId equals ta.Id
               where mm.Id == id
               select new
               {
                   MatchId = mm.Id,
                   LeagueName = lg.Name,
                   LeagueLogo = lg.Logo,
                   CountryName = lg.CountryName,
                   CountryCode = lg.CountryCode,   // <-- serve per la bandiera
                   KickoffUtc = mm.Date,
                   Home = th.Name ?? "",
                   Away = ta.Name ?? "",
                   LeagueId = mm.LeagueId,
                   Season = mm.Season
               }
           ).AsNoTracking().FirstOrDefaultAsync();

            if (dto is null) return NotFound();

            // Pronostico
            var script = await _read.Analyses
                .Where(a => a.ViewName == "NextMatch_Prediction_New")
                .Select(a => a.ViewValue)
                .AsNoTracking()
                .FirstOrDefaultAsync();

            PredictionRow? p = null;
            if (!string.IsNullOrWhiteSpace(script))
            {
                var cs = _read.Database.GetConnectionString();
                await using var conn = new NpgsqlConnection(cs);
                await conn.OpenAsync();

                await using var cmd = new NpgsqlCommand(script, conn);
                cmd.Parameters.AddWithValue("@MatchId", (int)id);

                await using var rd = await cmd.ExecuteReaderAsync();
                if (await rd.ReadAsync())
                {
                    p = new PredictionRow
                    {
                        Id = GetField<int>(rd, "Id"),
                        GoalSimulatoCasa = GetField<int>(rd, "Goal Simulati Casa"),
                        GoalSimulatoOspite = GetField<int>(rd, "Goal Simulati Ospite"),
                        TotaleGoalSimulati = GetField<int>(rd, "Totale Goal Simulati"),
                        Esito = GetField<string>(rd, "Esito"),
                        OverUnderRange = GetField<string>(rd, "OverUnderRange"),
                        Over1_5 = GetField<decimal?>(rd, "Over1_5"),
                        Over2_5 = GetField<decimal?>(rd, "Over2_5"),
                        Over3_5 = GetField<decimal?>(rd, "Over3_5"),
                        GG_NG = GetField<string>(rd, "GG_NG"),
                        MultigoalCasa = GetField<string>(rd, "MultigoalCasa"),
                        MultigoalOspite = GetField<string>(rd, "MultigoalOspite"),
                        ComboFinale = GetField<string>(rd, "ComboFinale")
                    };
                }
            }

            // BANDIERA SOLO DAVANTI AL NOME LEGA
            string flag = FlagFromCountryCode(dto.CountryCode);

            string header =
                $"{flag} <b>{dto.LeagueName}</b>  🕒 {dto.KickoffUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n" +
                $"⚽️ {dto.Home} - {dto.Away}";

            string body = p is null
                ? "\nNessun pronostico disponibile."
                : $"\n🧠 <b>Pronostici:</b>\n" +
                  $"  Esito: {p.Esito}\n" +
                  $"  U/O: {p.OverUnderRange}\n" +
                  $"  GG/NG: {p.GG_NG}\n" +
                  $"  MG Casa: {p.MultigoalCasa}\n" +
                  $"  MG Ospite: {p.MultigoalOspite}\n\n" +
                  $"<b>Pronostico Consigliato:</b>\n{p.ComboFinale}";


            string caption = header + "\n" + body;

            // Topic: Pronostici da pubblicare (appsettings)
            var topicIdStr = HttpContext.RequestServices
                .GetRequiredService<IConfiguration>()["Telegram:Topics:PronosticiDaPubblicare"];
            long.TryParse(topicIdStr, out long topicId);

            await _telegram.SendMessageAsync(topicId, caption);

            TempData["StatusMessage"] = "Pronostico inviato su Telegram.";
            return RedirectToPage(new { id });
        }

        private static string FlagFromCountryCode(string? countryCode)
        {
            if (string.IsNullOrWhiteSpace(countryCode)) return "🌍";

            var cc = countryCode.Trim().ToUpperInvariant();

            // Sotto-codici UK più usati dalle leghe
            switch (cc)
            {
                case "GB-ENG": return "🏴"; // Inghilterra
                case "GB-SCT": return "🏴"; // Scozia
                case "GB-WLS": return "🏴"; // Galles
                case "GB-NIR": return "🇬🇧"; // NI: fallback Union Jack
            }

            // Se c'è un trattino (es. GB-ENG) prendiamo la parte principale (GB)
            var main = cc.Contains('-') ? cc.Split('-')[0] : cc;

            // Alcuni provider usano 3 lettere (USA, KOS...). Mapping rapido.
            switch (main)
            {
                case "USA": return "🇺🇸";
                case "KOS": return "🇽🇰";
                case "UEFA": return "🇪🇺";
                case "ENG": return "🏴";
                case "SCO": return "🏴";
                case "WAL": return "🏴";
                case "NIR": return "🇬🇧";
            }

            // ISO-2 standard => Regional Indicator Symbols
            if (main.Length == 2 && main.All(char.IsLetter))
            {
                int baseCode = 0x1F1E6; // 'A'
                var r1 = char.ConvertFromUtf32(baseCode + (main[0] - 'A'));
                var r2 = char.ConvertFromUtf32(baseCode + (main[1] - 'A'));
                return r1 + r2;
            }

            return "🌍";
        }

        private async Task<List<FormRow>> GetLastFiveAsync(int teamId, int leagueId, int season)
        {
            var finished = new[] { "FT", "AET", "PEN" };

            var rows = await (
                from m in _read.Matches
                where m.LeagueId == leagueId && m.Season == season
                      && finished.Contains(m.StatusShort!)
                      && (m.HomeId == teamId || m.AwayId == teamId)
                orderby m.Date descending
                select new { m.Id, m.Date, m.HomeId, m.AwayId, m.HomeGoal, m.AwayGoal }
            ).AsNoTracking().Take(5).ToListAsync();

            var result = new List<FormRow>(rows.Count);
            foreach (var r in rows)
            {
                bool wasHome = r.HomeId == teamId;
                int opponentId = wasHome ? r.AwayId : r.HomeId;

                var oppName = await _read.Teams.Where(t => t.Id == opponentId).Select(t => t.Name).FirstOrDefaultAsync() ?? "—";

                int gf = wasHome ? (r.HomeGoal ?? 0) : (r.AwayGoal ?? 0);
                int ga = wasHome ? (r.AwayGoal ?? 0) : (r.HomeGoal ?? 0);
                string res = gf > ga ? "W" : (gf == ga ? "D" : "L");

                result.Add(new FormRow
                {
                    MatchId = r.Id,
                    DateUtc = r.Date,
                    Opponent = oppName,
                    IsHome = wasHome,
                    Score = $"{r.HomeGoal ?? 0}-{r.AwayGoal ?? 0}",
                    Result = res
                });
            }
            return result;
        }

        private static T? GetField<T>(IDataRecord r, string name)
        {
            int ord = r.GetOrdinal(name);
            if (r.IsDBNull(ord)) return default;
            object val = r.GetValue(ord);
            var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

            try
            {
                if (targetType == typeof(long))
                {
                    if (val is long l) return (T)(object)l;
                    if (val is int i) return (T)(object)(long)i;
                }
                else if (targetType == typeof(int))
                {
                    if (val is int i) return (T)(object)i;
                    if (val is long l) return (T)(object)(int)l;
                }
                return (T)Convert.ChangeType(val, targetType);
            }
            catch { return (T)val; }
        }

        // Esegue uno script in Analyses e ritorna un dizionario chiave/valore delle colonne
        private async Task<T?> RunGenericAnalysisAsync<T>(string viewName, int leagueId, int season, int matchId)
            where T : class, new()
        {
            var script = await _read.Analyses
                .Where(a => a.ViewName == viewName)
                .Select(a => a.ViewValue)
                .AsNoTracking()
                .FirstOrDefaultAsync();

            if (string.IsNullOrWhiteSpace(script)) return null;

            var cs = _read.Database.GetConnectionString();
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(script, conn);
            cmd.Parameters.AddWithValue("@MatchId", matchId);
            cmd.Parameters.AddWithValue("@Season", season);
            cmd.Parameters.AddWithValue("@LeagueId", leagueId);

            await using var rd = await cmd.ExecuteReaderAsync();
            if (!await rd.ReadAsync()) return null;

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rd.FieldCount; i++)
            {
                var colName = rd.GetName(i);

                if (string.Equals(colName, "Id", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(colName, "MatchId", StringComparison.OrdinalIgnoreCase))
                    continue;

                string displayName = PrettyLabel(colName);
                string value = rd.IsDBNull(i) ? "—" : Convert.ToString(rd.GetValue(i)) ?? "—";
                dict[displayName] = value;
            }

            var result = new T();
            // tutte le nostre *Row hanno la proprietà Metrics
            var prop = typeof(T).GetProperty("Metrics");
            prop?.SetValue(result, dict);
            return result;
        }

        private static string CountryNameToFlag(string? country)
        {
            if (string.IsNullOrWhiteSpace(country)) return "🌍";
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Italy"] = "🇮🇹",
                ["Italia"] = "🇮🇹",
                ["England"] = "🇬🇧",
                ["United Kingdom"] = "🇬🇧",
                ["Spain"] = "🇪🇸",
                ["France"] = "🇫🇷",
                ["Germany"] = "🇩🇪",
                ["Portugal"] = "🇵🇹",
                ["Netherlands"] = "🇳🇱",
                ["Turkey"] = "🇹🇷",
                ["Belgium"] = "🇧🇪"
            };
            return map.TryGetValue(country.Trim(), out var f) ? f : "🌍";
        }

        private static string HtmlEscape(string? s)
            => string.IsNullOrEmpty(s) ? "" : System.Net.WebUtility.HtmlEncode(s);

        // Normalizza le etichette
        private static string PrettyLabel(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw ?? "";

            var s = raw.Replace('_', ' ');
            s = Regex.Replace(s, "([a-z])([A-Z])", "$1 $2");
            s = Regex.Replace(s, @"\s+", " ").Trim();

            // Mappature comuni (IT)
            s = s.Replace("Home", "Casa", StringComparison.OrdinalIgnoreCase)
                 .Replace("Away", "Ospite", StringComparison.OrdinalIgnoreCase)
                 .Replace("Expected Goals", "Goal attesi", StringComparison.OrdinalIgnoreCase)
                 .Replace("Expected Goal", "Goal attesi", StringComparison.OrdinalIgnoreCase)
                 .Replace("Avg", "Media", StringComparison.OrdinalIgnoreCase)
                 .Replace("Average", "Media", StringComparison.OrdinalIgnoreCase)
                 .Replace("Total", "Totale", StringComparison.OrdinalIgnoreCase)
                 .Replace("Per Match", "per partita", StringComparison.OrdinalIgnoreCase)
                 .Replace("Per Game", "per partita", StringComparison.OrdinalIgnoreCase)
                 .Replace("Shots On Goal", "Tiri in porta", StringComparison.OrdinalIgnoreCase)
                 .Replace("Shots Off Goal", "Tiri fuori", StringComparison.OrdinalIgnoreCase)
                 .Replace("Total Shots", "Tiri totali", StringComparison.OrdinalIgnoreCase)
                 .Replace("Shots", "Tiri", StringComparison.OrdinalIgnoreCase)
                 .Replace("Corners", "Corner", StringComparison.OrdinalIgnoreCase)
                 .Replace("Cards", "Cartellini", StringComparison.OrdinalIgnoreCase)
                 .Replace("Yellow Cards", "Ammonizioni", StringComparison.OrdinalIgnoreCase)
                 .Replace("Red Cards", "Espulsioni", StringComparison.OrdinalIgnoreCase)
                 .Replace("Fouls", "Falli", StringComparison.OrdinalIgnoreCase)
                 .Replace("Offsides", "Fuorigioco", StringComparison.OrdinalIgnoreCase);

            if (s.Length > 1) s = char.ToUpperInvariant(s[0]) + s[1..];
            return s;
        }
    }
}
