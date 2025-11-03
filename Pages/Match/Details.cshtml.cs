using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;
using NextStakeWebApp.Services;
using Npgsql;
using System.Data;

namespace NextStakeWebApp.Pages.Match
{
    [Authorize]
    public class DetailsModel : PageModel
    {
        private readonly ReadDbContext _read;
        private readonly ApplicationDbContext _write;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ITelegramService _telegram;
        private readonly IConfiguration _config;

        public DetailsModel(
            ReadDbContext read,
            ApplicationDbContext write,
            UserManager<ApplicationUser> userManager,
            ITelegramService telegram,
            IConfiguration config)
        {
            _read = read;
            _write = write;
            _userManager = userManager;
            _telegram = telegram;
            _config = config;
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

            public PredictionRow? Prediction { get; set; }
            public ExchangePredictionRow? Exchange { get; set; }

            public bool IsFavorite { get; set; }
            public int RemainingMatches { get; set; }

            public MetricGroup? Goals { get; set; }
            public MetricGroup? Shots { get; set; }
            public MetricGroup? Fouls { get; set; }
            public MetricGroup? Cards { get; set; }
            public MetricGroup? Corners { get; set; }
            public MetricGroup? Offsides { get; set; }

            public List<FormRow> HomeForm { get; set; } = new();
            public List<FormRow> AwayForm { get; set; } = new();
            public List<TableStandingRow> Standings { get; set; } = new();

        }
        public class TableStandingRow
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

        // =======================
        // GET: carica dati + analyses da Neon
        // =======================
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
                    HomeId = th.Id,
                    AwayId = ta.Id,
                    Home = th.Name,
                    Away = ta.Name,
                    HomeLogo = th.Logo,
                    AwayLogo = ta.Logo,
                    KickoffUtc = mm.Date
                }
            ).AsNoTracking().FirstOrDefaultAsync();

            if (dto is null) return NotFound();

            var userId = _userManager.GetUserId(User)!;
            var isFav = await _write.FavoriteMatches.AnyAsync(f => f.UserId == userId && f.MatchId == id);

            // Carichi accessori
            var remaining = await GetRemainingMatchesAsync(dto.LeagueId, dto.Season);
            var homeForm = await GetLastFiveAsync(dto.HomeId, dto.LeagueId, dto.Season);
            var awayForm = await GetLastFiveAsync(dto.AwayId, dto.LeagueId, dto.Season);
            var standings = await GetStandingsAsync(dto.LeagueId, dto.Season);

            // Prediction & Exchange da Analyses
            var prediction = await LoadPredictionAsync(id);
            var exchange = await LoadExchangeAsync(id);

            // Analyses generiche (dizionari)
            var goals = await RunAnalysisAsync("NextMatchGoals_Analyses", dto.LeagueId, dto.Season, (int)id);
            var shots = await RunAnalysisAsync("NextMatchShots_Analyses", dto.LeagueId, dto.Season, (int)id);
            var corners = await RunAnalysisAsync("NextMatchCorners_Analyses", dto.LeagueId, dto.Season, (int)id);
            var cards = await RunAnalysisAsync("NextMatchCards_Analyses", dto.LeagueId, dto.Season, (int)id);
            var fouls = await RunAnalysisAsync("NextMatchFouls_Analyses", dto.LeagueId, dto.Season, (int)id);
            var offsides = await RunAnalysisAsync("NextMatchOffsides_Analyses", dto.LeagueId, dto.Season, (int)id);

            Data = new VM
            {
                MatchId = dto.MatchId,
                LeagueId = dto.LeagueId,
                Season = dto.Season,
                LeagueName = dto.LeagueName ?? "",
                LeagueLogo = dto.LeagueLogo,
                CountryName = dto.CountryName,
                HomeId = dto.HomeId,
                AwayId = dto.AwayId,
                Home = dto.Home ?? "",
                Away = dto.Away ?? "",
                HomeLogo = dto.HomeLogo,
                AwayLogo = dto.AwayLogo,
                KickoffUtc = dto.KickoffUtc,
                IsFavorite = isFav,
                RemainingMatches = remaining,

                Prediction = prediction,
                Exchange = exchange,

                Goals = goals,
                Shots = shots,
                Corners = corners,
                Cards = cards,
                Fouls = fouls,
                Offsides = offsides,

                HomeForm = homeForm,
                AwayForm = awayForm,
                Standings = standings
            };

            return Page();
        }

        // =======================
        // ⭐ Toggle preferito
        // =======================
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

        // =======================
        // INVIO PRONOSTICI (topicName -> id da config)
        // =======================
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> OnPostSendPredictionAsync(long id, string? topicName, string? customPick)
        {
            if (!User.IsInRole("Admin")) return Forbid();

            var key = string.IsNullOrWhiteSpace(topicName) ? "PronosticiDaPubblicare" : topicName!;
            long.TryParse(_config[$"Telegram:Topics:{key}"], out var topicId);

            var dto = await (
               from mm in _read.Matches
               join lg in _read.Leagues on mm.LeagueId equals lg.Id
               join th in _read.Teams on mm.HomeId equals th.Id
               join ta in _read.Teams on mm.AwayId equals ta.Id
               where mm.Id == id
               select new
               {
                   LeagueName = lg.Name,
                   CountryCode = lg.CountryCode,
                   KickoffUtc = mm.Date,
                   Home = th.Name ?? "",
                   Away = ta.Name ?? ""
               }
            ).AsNoTracking().FirstOrDefaultAsync();

            if (dto is null) return NotFound();

            var p = await LoadPredictionAsync(id);
            if (p is not null && !string.IsNullOrWhiteSpace(customPick))
                p.ComboFinale = customPick.Trim();

            var flag = EmojiHelper.FromCountryCode(dto.CountryCode);

            string message =
                $"{flag} <b>{dto.LeagueName}</b> 🕒 {dto.KickoffUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n" +
                $"⚽️ {dto.Home} - {dto.Away}\n\n" +
                $"🧠 <b>Pronostici:</b>\n" +
                $"  Esito: {p?.Esito}\n" +
                $"  U/O: {p?.OverUnderRange}\n" +
                $"  GG/NG: {p?.GG_NG}\n" +
                $"  MG Casa: {p?.MultigoalCasa}\n" +
                $"  MG Ospite: {p?.MultigoalOspite}\n\n" +
                $"<b>Pronostico Consigliato:</b>\n{p?.ComboFinale}";

            await _telegram.SendMessageAsync(topicId, message);
            TempData["StatusMessage"] = "✅ Pronostico inviato su Telegram.";
            return RedirectToPage(new { id });
        }

        // =======================
        // INVIO EXCHANGE (topicName -> id da config)
        // =======================
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> OnPostSendExchangeAsync(long id, string customLay, string riskLevel, string? topicName)
        {
            if (!User.IsInRole("Admin")) return Forbid();

            var key = string.IsNullOrWhiteSpace(topicName) ? "ExchangeDaPubblicare" : topicName!;
            long.TryParse(_config[$"Telegram:Topics:{key}"], out var topicId);

            var dto = await (
                from mm in _read.Matches
                join lg in _read.Leagues on mm.LeagueId equals lg.Id
                join th in _read.Teams on mm.HomeId equals th.Id
                join ta in _read.Teams on mm.AwayId equals ta.Id
                where mm.Id == id
                select new
                {
                    LeagueName = lg.Name,
                    CountryCode = lg.CountryCode,
                    KickoffUtc = mm.Date,
                    Home = th.Name ?? "",
                    Away = ta.Name ?? ""
                }
            ).AsNoTracking().FirstOrDefaultAsync();

            if (dto is null) return NotFound();

            string flag = EmojiHelper.FromCountryCode(dto.CountryCode);
            bool isScore = customLay.Contains('-');
            string tipoBancata = isScore ? "Banca Risultato Esatto" : "Banca 1X2";

            string message =
                $"{flag} <b>{dto.LeagueName}</b> 🕒 {dto.KickoffUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n" +
                $"⚽️ {dto.Home} - {dto.Away}\n\n" +
                $"🧠 <b>Bancate consigliate:</b>\n" +
                $"♦️ {tipoBancata}: {customLay} ⚠️ Rischio: {riskLevel}";

            await _telegram.SendMessageAsync(topicId, message);

            TempData["MessageSent"] = "✅ Messaggio Exchange inviato con successo!";
            return RedirectToPage(new { id });
        }

        // =======================
        // Loader Prediction
        // =======================
        private async Task<PredictionRow?> LoadPredictionAsync(long matchId)
        {
            var script = await _read.Analyses
                .Where(a => a.ViewName == "NextMatch_Prediction_New")
                .Select(a => a.ViewValue)
                .AsNoTracking()
                .FirstOrDefaultAsync();

            if (string.IsNullOrWhiteSpace(script)) return null;

            var cs = _read.Database.GetConnectionString();
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(script, conn);
            cmd.Parameters.AddWithValue("@MatchId", (int)matchId);

            await using var rd = await cmd.ExecuteReaderAsync();
            if (!await rd.ReadAsync()) return null;

            return new PredictionRow
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

        // =======================
        // Loader Exchange
        // =======================
        private async Task<ExchangePredictionRow?> LoadExchangeAsync(long matchId)
        {
            var script = await _read.Analyses
                .Where(a => a.ViewName == "NextMatch_Prediction_Exchange")
                .Select(a => a.ViewValue)
                .AsNoTracking()
                .FirstOrDefaultAsync();

            if (string.IsNullOrWhiteSpace(script)) return null;

            var cs = _read.Database.GetConnectionString();
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(script, conn);
            cmd.Parameters.AddWithValue("@MatchId", (int)matchId);

            await using var rd = await cmd.ExecuteReaderAsync();
            if (!await rd.ReadAsync()) return null;

            return new ExchangePredictionRow
            {
                MatchId = GetField<long>(rd, "MatchId"),
                Banca1Affidabilita = GetField<int?>(rd, "Banca 1 - Affidabilità %"),
                BancaXAffidabilita = GetField<int?>(rd, "Banca X - Affidabilità %"),
                Banca2Affidabilita = GetField<int?>(rd, "Banca 2 - Affidabilità %"),
                BancataConsigliata = GetField<string>(rd, "Bancata consigliata"),
                BancaRisultato1 = GetField<string>(rd, "Banca Risultato 1"),
                BancaRisultato2 = GetField<string>(rd, "Banca Risultato 2"),
                BancaRisultato3 = GetField<string>(rd, "Banca Risultato 3")
            };
        }

        // =======================
        // Analyses generiche (ritorna MetricGroup.Metrics)
        // =======================
        private async Task<MetricGroup?> RunAnalysisAsync(string viewName, int leagueId, int season, int matchId)
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

            var mg = new MetricGroup { Metrics = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) };
            for (int i = 0; i < rd.FieldCount; i++)
            {
                var name = rd.GetName(i);
                if (name.Equals("Id", StringComparison.OrdinalIgnoreCase) || name.Equals("MatchId", StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = rd.IsDBNull(i) ? "—" : Convert.ToString(rd.GetValue(i)) ?? "—";
                mg.Metrics[name] = value;
            }
            return mg;
        }

        private async Task<int> GetRemainingMatchesAsync(int leagueId, int season)
        {
            return await _read.Matches
               .Where(m => m.LeagueId == leagueId && m.Season == season && (m.StatusShort == null || m.StatusShort != "FT"))
               .Select(m => m.MatchRound)
               .Distinct()
               .CountAsync();
        }

        private async Task<List<FormRow>> GetLastFiveAsync(long teamId, int leagueId, int season)
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
                long opponentId = wasHome ? r.AwayId : r.HomeId;

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

        private async Task<List<TableStandingRow>> GetStandingsAsync(int leagueId, int season)
        {
            return await (
                from s in _read.Standings
                join t in _read.Teams on s.TeamId equals t.Id
                where s.LeagueId == leagueId && s.Season == season
                orderby s.Rank
                select new TableStandingRow
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
        }


        // Helper conversione field
        private static T? GetField<T>(IDataRecord r, string name)
        {
            int ord = r.GetOrdinal(name);
            if (r.IsDBNull(ord)) return default;
            object val = r.GetValue(ord);
            var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            // compatibilità int/long
            if (targetType == typeof(long) && val is int i) val = (long)i;
            if (targetType == typeof(int) && val is long l) val = (int)l;
            return (T)Convert.ChangeType(val, targetType);
        }
    }

    // =======================
    // EMOJI HELPER
    // =======================
    public static class EmojiHelper
    {
        public static string FromCountryCode(string? code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return "🌍";

            var cc = code.Length > 2 && code.Contains('-')
                ? code.Split('-')[0]
                : code;
            cc = cc.Trim().ToUpperInvariant();

            if (cc.Length != 2) return "🌍";
            int offset = 0x1F1E6;
            return string.Concat(
                char.ConvertFromUtf32(offset + (cc[0] - 'A')),
                char.ConvertFromUtf32(offset + (cc[1] - 'A'))
            );
        }
    }
}
