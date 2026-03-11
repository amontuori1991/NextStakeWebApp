using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;
using Npgsql;
using NpgsqlTypes;
using System.Globalization;

namespace NextStakeWebApp.Api
{
    [ApiController]
    [Route("api/match/{id:long}/analysis")]
    [Authorize]
    public class MatchAnalysisApiController : ControllerBase
    {
        private readonly ReadDbContext _read;

        public MatchAnalysisApiController(ReadDbContext read)
        {
            _read = read;
        }

        // ─────────────────────────────────────────────────────────────
        // GET /api/match/{id}/analysis
        // ─────────────────────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> GetAsync(long id)
        {
            // 1) Dati partita
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
                    HomeName = th.Name ?? "",
                    AwayName = ta.Name ?? ""
                }
            ).AsNoTracking().FirstOrDefaultAsync();

            if (dto is null)
                return NotFound();

            // 2) Prediction (Esito per calcolare pronostici derivati)
            var prediction = await LoadPredictionFromCacheAsync(id)
                          ?? await LoadPredictionFromViewAsync(id);

            // 3) Analyses: stessa chiamata usata da DetailsModel.RunAnalysisAsync
            var goals = await RunAnalysisAsync("NextMatchGoals_Analyses", dto.LeagueId, dto.Season, (int)id);
            var shots = await RunAnalysisAsync("NextMatchShots_Analyses", dto.LeagueId, dto.Season, (int)id);
            var corners = await RunAnalysisAsync("NextMatchCorners_Analyses", dto.LeagueId, dto.Season, (int)id);
            var cards = await RunAnalysisAsync("NextMatchCards_Analyses", dto.LeagueId, dto.Season, (int)id);
            var fouls = await RunAnalysisAsync("NextMatchFouls_Analyses", dto.LeagueId, dto.Season, (int)id);
            var offsides = await RunAnalysisAsync("NextMatchOffsides_Analyses", dto.LeagueId, dto.Season, (int)id);

            var esito = prediction?.Esito ?? "";

            // 4) Costruisce la response
            var response = new AnalysisResponse
            {
                Esito = esito,
                HomeName = dto.HomeName,
                AwayName = dto.AwayName,

                Goals = BuildCategoryDto("Goals", goals, CalcGoalsPrediction(esito, goals?.Metrics)),
                Shots = BuildCategoryDto("Shots", shots, CalcShotsPrediction(esito, shots?.Metrics)),
                Corners = BuildCategoryDto("Corners", corners, CalcCornersPrediction(esito, corners?.Metrics)),
                Cards = BuildCategoryDto("Cards", cards, CalcCardsPrediction(esito, cards?.Metrics)),
                Fouls = BuildCategoryDto("Fouls", fouls, CalcSimplePrediction(fouls?.Metrics)),
                Offsides = BuildCategoryDto("Offsides", offsides, CalcSimplePrediction(offsides?.Metrics))
            };

            return Ok(response);
        }

        // ─────────────────────────────────────────────────────────────
        // Helper: costruisce il DTO di una categoria
        // ─────────────────────────────────────────────────────────────
        private static CategoryDto BuildCategoryDto(
            string label,
            MetricGroup? mg,
            PredictionValues pred)
        {
            return new CategoryDto
            {
                Label = label,
                Prediction = pred,
                Metrics = mg?.Metrics ?? new Dictionary<string, string>()
            };
        }

        // ─────────────────────────────────────────────────────────────
        // GOAL ATTESI – replica logica DetailsModel.BuildAnalysisContextForAi
        // ─────────────────────────────────────────────────────────────
        private static PredictionValues CalcGoalsPrediction(
            string esito,
            IDictionary<string, string>? m)
        {
            if (m is null || string.IsNullOrWhiteSpace(esito))
                return PredictionValues.Empty;

            var homeWon = GetMetric(m, "Partite Vinte", "Home");
            var homeDraw = GetMetric(m, "Partite Pareggiate", "Home");
            var homeLost = GetMetric(m, "Partite Perse", "Home");
            var awayWon = GetMetric(m, "Partite Vinte", "Away");
            var awayDraw = GetMetric(m, "Partite Pareggiate", "Away");
            var awayLost = GetMetric(m, "Partite Perse", "Away");
            var homeScoredHome = GetMetric(m, "Fatti in Casa", "Home");
            var awayScoredAway = GetMetric(m, "Fatti in Trasferta", "Away");
            var homeConcedHome = GetMetric(m, "Subiti in Casa", "Home");
            var homeConcedAway = GetMetric(m, "Subiti in Trasferta", "Home");
            var awayConcedHome = GetMetric(m, "Subiti in Casa", "Away");
            var awayConcedAway = GetMetric(m, "Subiti in Trasferta", "Away");

            decimal? h = null, a = null;

            switch (esito.Trim().ToUpperInvariant())
            {
                case "1":
                    h = Avg3(homeWon, homeScoredHome, awayConcedHome);
                    a = Avg3(awayLost, awayScoredAway, homeConcedAway);
                    break;
                case "2":
                    h = Avg3(homeLost, homeScoredHome, awayConcedAway);
                    a = Avg3(awayWon, awayScoredAway, homeConcedHome);
                    break;
                case "1X":
                    h = Avg4(homeWon, homeDraw, homeScoredHome, awayConcedHome);
                    a = Avg4(awayLost, awayDraw, awayScoredAway, homeConcedAway);
                    break;
                case "X2":
                    h = Avg4(homeDraw, homeLost, homeScoredHome, awayConcedAway);
                    a = Avg4(awayDraw, awayWon, awayScoredAway, homeScoredHome);
                    break;
                case "X":
                    h = Avg3(homeDraw, homeScoredHome, awayConcedHome);
                    a = Avg3(awayDraw, awayScoredAway, homeConcedAway);
                    break;
            }

            return new PredictionValues(h, a, h.HasValue && a.HasValue ? h + a : null);
        }

        // ─────────────────────────────────────────────────────────────
        // TIRI
        // ─────────────────────────────────────────────────────────────
        private static PredictionValues CalcShotsPrediction(
            string esito,
            IDictionary<string, string>? m)
        {
            if (m is null || string.IsNullOrWhiteSpace(esito))
                return PredictionValues.Empty;

            var shEffHome = GetMetric(m, "Effettuati", "Home");
            var shHomeHome = GetMetric(m, "In Casa", "Home");
            var shDrawHome = GetMetric(m, "Partite Pareggiate", "Home");
            var shLostHome = GetMetric(m, "Partite Perse", "Home");
            var shEffAway = GetMetric(m, "Effettuati", "Away");
            var shAwayAway = GetMetric(m, "Fuoricasa", "Away");
            var shDrawAway = GetMetric(m, "Partite Pareggiate", "Away");
            var shWonAway = GetMetric(m, "Partite Vinte", "Away");

            decimal? h = null, a = null;

            switch (esito.Trim().ToUpperInvariant())
            {
                case "1":
                    h = Avg3(shEffHome, shHomeHome, shWonAway);
                    a = Avg3(shEffAway, shAwayAway, shLostHome);
                    break;
                case "2":
                    h = Avg3(shEffHome, shHomeHome, shLostHome);
                    a = Avg3(shEffAway, shAwayAway, shWonAway);
                    break;
                case "1X":
                    h = Avg3(shEffHome, shHomeHome, shDrawHome);
                    a = Avg3(shEffAway, shAwayAway, shLostHome);
                    break;
                case "X2":
                    h = Avg3(shEffHome, shHomeHome, shLostHome);
                    a = Avg3(shEffAway, shAwayAway, shDrawAway);
                    break;
                case "X":
                    h = Avg3(shEffHome, shHomeHome, shDrawHome);
                    a = Avg3(shEffAway, shAwayAway, shDrawAway);
                    break;
            }

            return new PredictionValues(h, a, h.HasValue && a.HasValue ? h + a : null);
        }

        // ─────────────────────────────────────────────────────────────
        // CORNER
        // ─────────────────────────────────────────────────────────────
        private static PredictionValues CalcCornersPrediction(
            string esito,
            IDictionary<string, string>? m)
        {
            if (m is null || string.IsNullOrWhiteSpace(esito))
                return PredictionValues.Empty;

            var homeWon = GetMetric(m, "Partite Vinte", "Home");
            var homeDraw = GetMetric(m, "Partite Pareggiate", "Home");
            var homeLost = GetMetric(m, "Partite Perse", "Home");
            var awayWon = GetMetric(m, "Partite Vinte", "Away");
            var awayDraw = GetMetric(m, "Partite Pareggiate", "Away");
            var awayLost = GetMetric(m, "Partite Perse", "Away");
            var homeHome = GetMetric(m, "In Casa", "Home");
            var awayAway = GetMetric(m, "Fuoricasa", "Away");

            decimal? h = null, a = null;

            switch (esito.Trim().ToUpperInvariant())
            {
                case "1":
                    h = Avg2(homeWon, homeHome);
                    a = Avg2(awayLost, awayAway);
                    break;
                case "2":
                    h = Avg2(homeLost, homeHome);
                    a = Avg2(awayWon, awayAway);
                    break;
                case "1X":
                    h = Avg3(homeWon, homeDraw, homeHome);
                    a = Avg3(awayLost, awayDraw, awayAway);
                    break;
                case "X2":
                    h = Avg3(homeLost, homeDraw, homeHome);
                    a = Avg3(awayWon, awayDraw, awayAway);
                    break;
                case "X":
                    h = Avg2(homeDraw, homeHome);
                    a = Avg2(awayDraw, awayAway);
                    break;
            }

            return new PredictionValues(h, a, h.HasValue && a.HasValue ? h + a : null);
        }

        // ─────────────────────────────────────────────────────────────
        // CARTELLINI
        // ─────────────────────────────────────────────────────────────
        private static PredictionValues CalcCardsPrediction(
            string esito,
            IDictionary<string, string>? m)
        {
            if (m is null || string.IsNullOrWhiteSpace(esito))
                return PredictionValues.Empty;

            var homeWon = GetMetric(m, "Partite Vinte", "Home");
            var homeDraw = GetMetric(m, "Partite Pareggiate", "Home");
            var homeLost = GetMetric(m, "Partite Perse", "Home");
            var awayWon = GetMetric(m, "Partite Vinte", "Away");
            var awayDraw = GetMetric(m, "Partite Pareggiate", "Away");
            var awayLost = GetMetric(m, "Partite Perse", "Away");
            var homeInCasa = GetMetric(m, "In Casa", "Home");
            var awayFuori = GetMetric(m, "Fuoricasa", "Away");
            var homeFatti = GetMetric(m, "Fatti", "Home")
                          ?? GetMetric(m, "Effettuati", "Home");
            var awayFatti = GetMetric(m, "Fatti", "Away")
                          ?? GetMetric(m, "Effettuati", "Away");

            decimal? h = null, a = null;

            switch (esito.Trim().ToUpperInvariant())
            {
                case "1":
                    h = Avg3(homeInCasa, homeWon, homeFatti);
                    a = Avg3(awayFuori, awayLost, awayFatti);
                    break;
                case "2":
                    h = Avg3(homeInCasa, homeLost, homeFatti);
                    a = Avg3(awayFuori, awayWon, awayFatti);
                    break;
                case "1X":
                    h = Avg4(homeInCasa, homeWon, homeDraw, homeFatti);
                    a = Avg4(awayFuori, awayLost, awayDraw, awayFatti);
                    break;
                case "X2":
                    h = Avg4(homeInCasa, homeDraw, homeLost, homeFatti);
                    a = Avg4(awayFuori, awayWon, awayDraw, awayFatti);
                    break;
                case "X":
                    h = Avg3(homeInCasa, homeDraw, homeFatti);
                    a = Avg3(awayFuori, awayDraw, awayFatti);
                    break;
            }

            return new PredictionValues(h, a, h.HasValue && a.HasValue ? h + a : null);
        }

        // ─────────────────────────────────────────────────────────────
        // FALLI / FUORIGIOCO – media semplice Fatti + Subiti
        // ─────────────────────────────────────────────────────────────
        private static PredictionValues CalcSimplePrediction(IDictionary<string, string>? m)
        {
            if (m is null) return PredictionValues.Empty;

            var hFatti = GetMetric(m, "Fatti", "Home") ?? GetMetric(m, "Effettuati", "Home");
            var hSubiti = GetMetric(m, "Subiti", "Home");
            var aFatti = GetMetric(m, "Fatti", "Away") ?? GetMetric(m, "Effettuati", "Away");
            var aSubiti = GetMetric(m, "Subiti", "Away");

            var h = Avg2(hFatti, hSubiti);
            var a = Avg2(aFatti, aSubiti);

            return new PredictionValues(h, a, h.HasValue && a.HasValue ? h + a : null);
        }

        // ─────────────────────────────────────────────────────────────
        // GetMetric – identica a DetailsModel: normalizza >50 / 100
        // ─────────────────────────────────────────────────────────────
        private static decimal? GetMetric(IDictionary<string, string> metrics, string baseName, string side)
        {
            var sideCandidates = side.Equals("Home", StringComparison.OrdinalIgnoreCase)
                ? new[] { "Home", "Casa" }
                : new[] { "Away", "Ospite" };

            foreach (var sc in sideCandidates)
            {
                var key1 = $"{baseName}-{sc}";
                var key2 = $"{baseName} - {sc}";

                string? raw = null;
                if (metrics.TryGetValue(key1, out var v1)) raw = v1;
                else if (metrics.TryGetValue(key2, out var v2)) raw = v2;

                if (raw is null) continue;

                if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
                {
                    if (val > 50m) val /= 100m;
                    return val;
                }
            }

            return null;
        }

        // ─────────────────────────────────────────────────────────────
        // Avg helpers
        // ─────────────────────────────────────────────────────────────
        private static decimal? Avg2(decimal? a, decimal? b)
        {
            if (!a.HasValue && !b.HasValue) return null;
            return ((a ?? 0m) + (b ?? 0m)) / 2m;
        }

        private static decimal? Avg3(decimal? a, decimal? b, decimal? c)
        {
            if (!a.HasValue && !b.HasValue && !c.HasValue) return null;
            return ((a ?? 0m) + (b ?? 0m) + (c ?? 0m)) / 3m;
        }

        private static decimal? Avg4(decimal? a, decimal? b, decimal? c, decimal? d)
        {
            if (!a.HasValue && !b.HasValue && !c.HasValue && !d.HasValue) return null;
            return ((a ?? 0m) + (b ?? 0m) + (c ?? 0m) + (d ?? 0m)) / 4m;
        }

        // ─────────────────────────────────────────────────────────────
        // RunAnalysisAsync – copia di DetailsModel.RunAnalysisAsync
        // ─────────────────────────────────────────────────────────────
        private async Task<MetricGroup?> RunAnalysisAsync(
            string viewName, int leagueId, int season, int matchId)
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

            await using var cmd = new NpgsqlCommand(script, conn) { CommandTimeout = 180 };
            cmd.Parameters.Add("@MatchId", NpgsqlDbType.Integer).Value = matchId;
            cmd.Parameters.Add("@Season", NpgsqlDbType.Integer).Value = season;
            cmd.Parameters.Add("@LeagueId", NpgsqlDbType.Integer).Value = leagueId;

            await using var rd = await cmd.ExecuteReaderAsync();
            if (!await rd.ReadAsync()) return null;

            var mg = new MetricGroup
            {
                Metrics = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };

            for (int i = 0; i < rd.FieldCount; i++)
            {
                var name = rd.GetName(i);
                if (name.Equals("Id", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("MatchId", StringComparison.OrdinalIgnoreCase))
                    continue;

                mg.Metrics[name] = rd.IsDBNull(i)
                    ? "—"
                    : Convert.ToString(rd.GetValue(i)) ?? "—";
            }

            return mg;
        }

        // ─────────────────────────────────────────────────────────────
        // Prediction loaders
        // ─────────────────────────────────────────────────────────────
        private async Task<PredictionRow?> LoadPredictionFromCacheAsync(long matchId)
        {
            var cs = _read.Database.GetConnectionString();
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT ""Esito"", ""GoalSimulatiCasa"", ""GoalSimulatiOspite"", ""TotaleGoalSimulati"",
       ""OverUnderRange"", ""Over1_5"", ""Over2_5"", ""Over3_5"",
       ""GG_NG"", ""MultigoalCasa"", ""MultigoalOspite"", ""ComboFinale""
FROM ""NextMatchPredictionsCache""
WHERE ""MatchId"" = @id
LIMIT 1;";
            cmd.Parameters.AddWithValue("id", matchId);

            await using var rd = await cmd.ExecuteReaderAsync();
            if (!await rd.ReadAsync()) return null;

            return new PredictionRow
            {
                Id = matchId > int.MaxValue ? 0 : (int)matchId,
                Esito = rd.IsDBNull(0) ? "" : rd.GetString(0),
                GoalSimulatoCasa = rd.IsDBNull(1) ? 0 : rd.GetInt32(1),
                GoalSimulatoOspite = rd.IsDBNull(2) ? 0 : rd.GetInt32(2),
                TotaleGoalSimulati = rd.IsDBNull(3) ? 0 : rd.GetInt32(3),
                OverUnderRange = rd.IsDBNull(4) ? "" : rd.GetString(4),
                Over1_5 = rd.IsDBNull(5) ? null : rd.GetDecimal(5),
                Over2_5 = rd.IsDBNull(6) ? null : rd.GetDecimal(6),
                Over3_5 = rd.IsDBNull(7) ? null : rd.GetDecimal(7),
                GG_NG = rd.IsDBNull(8) ? "" : rd.GetString(8),
                MultigoalCasa = rd.IsDBNull(9) ? "" : rd.GetString(9),
                MultigoalOspite = rd.IsDBNull(10) ? "" : rd.GetString(10),
                ComboFinale = rd.IsDBNull(11) ? "" : rd.GetString(11),
            };
        }

        private async Task<PredictionRow?> LoadPredictionFromViewAsync(long matchId)
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

            await using var cmd = new NpgsqlCommand(script, conn) { CommandTimeout = 180 };
            cmd.Parameters.Add("@MatchId", NpgsqlDbType.Bigint).Value = matchId;

            await using var rd = await cmd.ExecuteReaderAsync();
            if (!await rd.ReadAsync()) return null;

            static T? F<T>(System.Data.IDataRecord r, string name)
            {
                int ord = r.GetOrdinal(name);
                if (r.IsDBNull(ord)) return default;
                object val = r.GetValue(ord);
                var t = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
                if (t == typeof(long) && val is int i) val = (long)i;
                if (t == typeof(int) && val is long l) val = (int)l;
                return (T)Convert.ChangeType(val, t);
            }

            return new PredictionRow
            {
                Id = F<int>(rd, "Id"),
                GoalSimulatoCasa = F<int>(rd, "Goal Simulati Casa"),
                GoalSimulatoOspite = F<int>(rd, "Goal Simulati Ospite"),
                TotaleGoalSimulati = F<int>(rd, "Totale Goal Simulati"),
                Esito = F<string>(rd, "Esito"),
                OverUnderRange = F<string>(rd, "OverUnderRange"),
                Over1_5 = F<decimal?>(rd, "Over1_5"),
                Over2_5 = F<decimal?>(rd, "Over2_5"),
                Over3_5 = F<decimal?>(rd, "Over3_5"),
                GG_NG = F<string>(rd, "GG_NG"),
                MultigoalCasa = F<string>(rd, "MultigoalCasa"),
                MultigoalOspite = F<string>(rd, "MultigoalOspite"),
                ComboFinale = F<string>(rd, "ComboFinale"),
            };
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // DTO response
    // ─────────────────────────────────────────────────────────────────────

    public class AnalysisResponse
    {
        public string Esito { get; init; } = "";
        public string HomeName { get; init; } = "";
        public string AwayName { get; init; } = "";

        public CategoryDto Goals { get; init; } = new();
        public CategoryDto Shots { get; init; } = new();
        public CategoryDto Corners { get; init; } = new();
        public CategoryDto Cards { get; init; } = new();
        public CategoryDto Fouls { get; init; } = new();
        public CategoryDto Offsides { get; init; } = new();
    }

    public class CategoryDto
    {
        public string Label { get; init; } = "";
        public PredictionValues Prediction { get; init; } = PredictionValues.Empty;

        /// <summary>
        /// Metriche raw dal DB (es. "Fatti in Casa-Home" → "1.32").
        /// La mobile app le usa per mostrare la tabella statistica dettagliata.
        /// </summary>
        public Dictionary<string, string> Metrics { get; init; } = new();
    }

    public class PredictionValues
    {
        public decimal? Home { get; init; }
        public decimal? Away { get; init; }
        public decimal? Total { get; init; }

        public PredictionValues() { }

        public PredictionValues(decimal? home, decimal? away, decimal? total)
        {
            Home = home is null ? null : Math.Round(home.Value, 2);
            Away = away is null ? null : Math.Round(away.Value, 2);
            Total = total is null ? null : Math.Round(total.Value, 2);
        }

        public static PredictionValues Empty => new();
    }
}
