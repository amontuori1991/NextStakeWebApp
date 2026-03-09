using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;
using NextStakeWebApp.Services;


namespace NextStakeWebApp.Pages.Database
{
    [Authorize]
    public class IndexModel : PageModel
    {
        private readonly ReadDbContext _read;
        private readonly ApplicationDbContext _identityDb;
        private readonly UserManager<ApplicationUser> _userManager;

        public IndexModel(ReadDbContext read, ApplicationDbContext identityDb, UserManager<ApplicationUser> userManager)
        {
            _read = read;
            _identityDb = identityDb;
            _userManager = userManager;
        }

        public bool IsPlan1 { get; private set; }
        public List<CachedPredictionRow> TodayCache { get; private set; } = new();

        public DateTime? LastRun { get; private set; }
        public int TodayMatchesCount { get; private set; }
        public int UpsertedCount { get; private set; }
        public string? Error { get; private set; }

        public List<VerificaRow> Verifica { get; private set; } = new();
        public VerificaStats VerificaTotali { get; private set; } = new();

        public async Task OnGetAsync()
        {
            IsPlan1 = await CheckPlan1Async();
            if (IsPlan1)
            {
                TodayCache = await LoadTodayCacheAsync();
                (Verifica, VerificaTotali) = await LoadVerificaAsync();
            }
        }

        private async Task<List<CachedPredictionRow>> LoadTodayCacheAsync()
        {
            var (utcStart, utcEnd) = GetTodayUtcRangeEuropeRome();

            var cs = _identityDb.Database.GetConnectionString();
            var csb = new NpgsqlConnectionStringBuilder(cs)
            {
                KeepAlive = 30,
                CommandTimeout = 120,
                Timeout = 15,
                CancellationTimeout = 10
            };

            var list = new List<CachedPredictionRow>();

            await using var conn = new NpgsqlConnection(csb.ToString());
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT
  ""MatchId"",
  ""EventDate"",
  ""GoalSimulatiCasa"",
  ""GoalSimulatiOspite"",
  ""TotaleGoalSimulati"",
  ""Esito"",
  ""OverUnderRange"",
  ""Over1_5"",
  ""Over2_5"",
  ""Over3_5"",
  ""GG_NG"",
  ""MultigoalCasa"",
  ""MultigoalOspite"",
  ""ComboFinale""
FROM ""NextMatchPredictionsCache""
WHERE ""EventDate"" >= @utcStart AND ""EventDate"" < @utcEnd
ORDER BY ""EventDate"", ""MatchId"";
";
            cmd.Parameters.AddWithValue("utcStart", utcStart);
            cmd.Parameters.AddWithValue("utcEnd", utcEnd);

            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                list.Add(new CachedPredictionRow
                {
                    MatchId = rd.GetFieldValue<long>(rd.GetOrdinal("MatchId")),
                    EventDate = rd.IsDBNull(rd.GetOrdinal("EventDate")) ? null : rd.GetFieldValue<DateTime>(rd.GetOrdinal("EventDate")),
                    GoalSimulatiCasa = rd.IsDBNull(rd.GetOrdinal("GoalSimulatiCasa")) ? null : rd.GetFieldValue<int>(rd.GetOrdinal("GoalSimulatiCasa")),
                    GoalSimulatiOspite = rd.IsDBNull(rd.GetOrdinal("GoalSimulatiOspite")) ? null : rd.GetFieldValue<int>(rd.GetOrdinal("GoalSimulatiOspite")),
                    TotaleGoalSimulati = rd.IsDBNull(rd.GetOrdinal("TotaleGoalSimulati")) ? null : rd.GetFieldValue<int>(rd.GetOrdinal("TotaleGoalSimulati")),
                    Esito = rd.IsDBNull(rd.GetOrdinal("Esito")) ? null : rd.GetFieldValue<string>(rd.GetOrdinal("Esito")),
                    OverUnderRange = rd.IsDBNull(rd.GetOrdinal("OverUnderRange")) ? null : rd.GetFieldValue<string>(rd.GetOrdinal("OverUnderRange")),
                    Over1_5 = rd.IsDBNull(rd.GetOrdinal("Over1_5")) ? null : rd.GetFieldValue<decimal>(rd.GetOrdinal("Over1_5")),
                    Over2_5 = rd.IsDBNull(rd.GetOrdinal("Over2_5")) ? null : rd.GetFieldValue<decimal>(rd.GetOrdinal("Over2_5")),
                    Over3_5 = rd.IsDBNull(rd.GetOrdinal("Over3_5")) ? null : rd.GetFieldValue<decimal>(rd.GetOrdinal("Over3_5")),
                    GG_NG = rd.IsDBNull(rd.GetOrdinal("GG_NG")) ? null : rd.GetFieldValue<string>(rd.GetOrdinal("GG_NG")),
                    MultigoalCasa = rd.IsDBNull(rd.GetOrdinal("MultigoalCasa")) ? null : rd.GetFieldValue<string>(rd.GetOrdinal("MultigoalCasa")),
                    MultigoalOspite = rd.IsDBNull(rd.GetOrdinal("MultigoalOspite")) ? null : rd.GetFieldValue<string>(rd.GetOrdinal("MultigoalOspite")),
                    ComboFinale = rd.IsDBNull(rd.GetOrdinal("ComboFinale")) ? null : rd.GetFieldValue<string>(rd.GetOrdinal("ComboFinale")),
                });
            }

            return list;
        }

        public async Task<IActionResult> OnPostRefreshPredictionsAsync()
        {
            IsPlan1 = await CheckPlan1Async();
            if (!IsPlan1)
                return Forbid();

            LastRun = DateTime.UtcNow;

            try
            {
                var script = await _read.Analyses
                    .Where(a => a.ViewName == "NextMatch_Prediction_New")
                    .Select(a => a.ViewValue)
                    .AsNoTracking()
                    .FirstOrDefaultAsync();

                if (string.IsNullOrWhiteSpace(script))
                {
                    Error = "Script non trovato: Analyses.ViewName = 'NextMatch_Prediction_New' (Enabled=TRUE).";
                    return Page();
                }

                var (utcStart, utcEnd) = GetTodayUtcRangeEuropeRome();

                var todayMatchIds = await _read.Matches
                    .AsNoTracking()
                    .Where(m => m.Date >= utcStart && m.Date < utcEnd)
                    .Select(m => (long)m.Id)
                    .ToListAsync();

                TodayMatchesCount = todayMatchIds.Count;

                var cs = _identityDb.Database.GetConnectionString();
                var csb = new NpgsqlConnectionStringBuilder(cs)
                {
                    KeepAlive = 30,
                    CommandTimeout = 120,
                    Timeout = 15,
                    CancellationTimeout = 10
                };

                await using var conn = new NpgsqlConnection(csb.ToString());
                await conn.OpenAsync();

                // ✅ SVUOTA CACHE PRIMA DI INSERIRE I NUOVI RECORD (run giornaliero)
                await ClearPredictionsCacheAsync(conn);

                // ✅ POPOLA CACHE IN UN SOLO COLPO (NO LOOP)
                UpsertedCount = await PopulateCacheBulkAsync(conn, script, todayMatchIds);
            }
            catch (Exception ex)
            {
                Error = ex.ToString();
            }

            return Page();
        }

        // -------------------------
        // Helpers
        // -------------------------
        public async Task<IActionResult> OnPostRefreshPredictionsStreamAsync()
        {
            IsPlan1 = await CheckPlan1Async();
            if (!IsPlan1) return Forbid();

            LastRun = DateTime.UtcNow;
            Error = null;
            TodayMatchesCount = 0;
            UpsertedCount = 0;

            Response.ContentType = "text/plain; charset=utf-8";
            Response.Headers["Cache-Control"] = "no-store";
            Response.Headers["X-Accel-Buffering"] = "no";

            async Task Log(string s)
            {
                await Response.WriteAsync(s);
                await Response.Body.FlushAsync();
            }

            try
            {
                await Log("🔎 Carico script da Analyses (NextMatch_Prediction_New)...\n");

                var script = await _read.Analyses
                    .Where(a => a.ViewName == "NextMatch_Prediction_New")
                    .Select(a => a.ViewValue)
                    .AsNoTracking()
                    .FirstOrDefaultAsync();

                if (string.IsNullOrWhiteSpace(script))
                {
                    Error = "Script non trovato: Analyses.ViewName = 'NextMatch_Prediction_New'.";
                    await Log($"❌ {Error}\n");
                    return new EmptyResult();
                }

                var (utcStart, utcEnd) = GetTodayUtcRangeEuropeRome();
                await Log($"📅 Range oggi (Europe/Rome) in UTC: {utcStart:yyyy-MM-dd HH:mm} -> {utcEnd:yyyy-MM-dd HH:mm}\n");

                var todayMatches = await _read.Matches
                    .AsNoTracking()
                    .Where(m => m.Date >= utcStart && m.Date < utcEnd)
                    .Select(m => new { m.Id, m.Date })
                    .ToListAsync();

                TodayMatchesCount = todayMatches.Count;
                await Log($"⚽ Match trovati oggi: {TodayMatchesCount}\n\n");

                var cs = _identityDb.Database.GetConnectionString();
                var csb = new NpgsqlConnectionStringBuilder(cs)
                {
                    KeepAlive = 30,
                    CommandTimeout = 120,
                    Timeout = 15,
                    CancellationTimeout = 10
                };

                // ─────────────────────────────────────────────────────────────
                // STEP 1: salva verifica pronostici del giorno PRIMA del truncate
                // ─────────────────────────────────────────────────────────────
                await Log("📊 Salvo verifica pronostici in PredictionsVerifica...\n");
                try
                {
                    await using var connVerifica = new NpgsqlConnection(csb.ToString());
                    await connVerifica.OpenAsync();
                    var verificaSaved = await SaveDailyVerificaAsync(connVerifica);
                    await Log($"✅ Verifica salvata: {verificaSaved} righe.\n\n");
                }
                catch (Exception exV)
                {
                    // non blocchiamo il job se la verifica fallisce
                    await Log($"⚠️ Verifica non salvata (non bloccante): {exV.Message}\n\n");
                }

                // ─────────────────────────────────────────────────────────────
                // STEP 2: svuota cache
                // ─────────────────────────────────────────────────────────────
                await Log("🧹 Svuoto NextMatchPredictionsCache...\n");
                await using (var connClear = new NpgsqlConnection(csb.ToString()))
                {
                    await connClear.OpenAsync();
                    await ClearPredictionsCacheAsync(connClear);
                }
                await Log("✅ Cache svuotata.\n\n");

                // ─────────────────────────────────────────────────────────────
                // STEP 3: popola cache con i match di oggi
                // ─────────────────────────────────────────────────────────────
                await Log("🚀 Avvio popolamento cache in modalità BULK (un solo comando SQL)...\n");

                var ids = todayMatches.Select(x => (long)x.Id).ToList();

                const int batchSize = 1; // DEBUG: 1 match alla volta
                int totalInserted = 0;

                var batches = ids.Chunk(batchSize).ToList();
                int b = 0;

                foreach (var batch in batches)
                {
                    b++;

                    var matchList = batch.ToList();
                    var matchId = matchList.FirstOrDefault();

                    await Log($"📦 Batch {b}/{batches.Count} | matchId: {matchId}\n");

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    await Log($"   ⏳ START SQL matchId={matchId} {DateTime.UtcNow:HH:mm:ss}\n");

                    try
                    {
                        await using var connBatch = new NpgsqlConnection(csb.ToString());
                        await connBatch.OpenAsync();

                        await using (var st2 = new NpgsqlCommand("SET statement_timeout = 90000;", connBatch))
                            await st2.ExecuteNonQueryAsync();

                        var ins = await PopulateCacheBulkAsync(connBatch, script, matchList);

                        sw.Stop();
                        await Log($"   ✅ END SQL matchId={matchId} {DateTime.UtcNow:HH:mm:ss}\n");

                        totalInserted += ins;
                        await Log($"   ✅ OK | Inserite: {ins} | Totale: {totalInserted} | Tempo: {sw.Elapsed.TotalSeconds:n1}s\n\n");
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        await Log($"   ❌ FAIL | Tempo: {sw.Elapsed.TotalSeconds:n1}s\n");
                        await Log($"   ❌ EX:\n{ex}\n\n");
                        continue;
                    }
                }

                UpsertedCount = totalInserted;

                await Log($"✅ Inserite righe in cache: {UpsertedCount}\n");
                await Log("🎉 Fine.\n");
                await Log($"🎉 Fine. Match: {TodayMatchesCount} | Insert: {UpsertedCount}\n");
            }
            catch (Exception ex)
            {
                Error = ex.ToString();
                await Log($"\n❌ ERRORE:\n{Error}\n");
            }

            return new EmptyResult();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Salva i pronostici del giorno (solo FT) in PredictionsVerifica
        // ─────────────────────────────────────────────────────────────────────
        private async Task<int> SaveDailyVerificaAsync(NpgsqlConnection conn)
        {
            // Carica la query di verifica da Analyses
            var verificaScript = await _read.Analyses
                .Where(a => a.ViewName == "NextMatch_Verifica_Giornaliera")
                .Select(a => a.ViewValue)
                .AsNoTracking()
                .FirstOrDefaultAsync();

            if (string.IsNullOrWhiteSpace(verificaScript))
                return 0;

            // Rimuove eventuale ';' finale che romperebbe la subquery
            var cleaned = verificaScript.Trim();
            while (cleaned.EndsWith(";"))
                cleaned = cleaned.Substring(0, cleaned.Length - 1).Trim();

            var sql = $@"
INSERT INTO ""PredictionsVerifica"" (
    ""MatchId"", ""EventDate"", ""HomeName"", ""AwayName"", ""Lega"",
    ""GoalSimulatiCasa"", ""GoalSimulatiOspite"", ""TotaleGoalSimulati"",
    ""Esito"", ""OverUnderRange"", ""GG_NG"", ""ComboFinale"",
    ""GolRealiCasa"", ""GolRealiOspite"", ""TotaleGolReali"",
    ""EsitoReale"", ""EsitoOK"", ""OverUnderOK"", ""GG_NG_OK"", ""ComboOK"",
    ""Stato"", ""SavedAt""
)
SELECT
    v.""MatchId"",
    v.""EventDate"",
    v.""HomeName"",
    v.""AwayName"",
    v.""Lega"",
    v.""GoalSimulatiCasa"",
    v.""GoalSimulatiOspite"",
    v.""TotaleGoalSimulati"",
    v.""Esito"",
    v.""OverUnderRange"",
    v.""GG_NG"",
    v.""ComboFinale"",
    v.""GolRealiCasa"",
    v.""GolRealiOspite"",
    v.""TotaleGolReali"",
    v.""EsitoReale"",
    v.""EsitoOK"",
    v.""OverUnderOK"",
    v.""GG_NG_OK"",
    v.""ComboOK"",
    v.""Stato"",
    now()
FROM ({cleaned}) v
WHERE v.""Stato"" = 'FT'
ON CONFLICT (""MatchId"", ""EventDate"") DO UPDATE SET
    ""GolRealiCasa""    = EXCLUDED.""GolRealiCasa"",
    ""GolRealiOspite""  = EXCLUDED.""GolRealiOspite"",
    ""TotaleGolReali""  = EXCLUDED.""TotaleGolReali"",
    ""EsitoReale""      = EXCLUDED.""EsitoReale"",
    ""EsitoOK""         = EXCLUDED.""EsitoOK"",
    ""OverUnderOK""     = EXCLUDED.""OverUnderOK"",
    ""GG_NG_OK""        = EXCLUDED.""GG_NG_OK"",
    ""ComboOK""         = EXCLUDED.""ComboOK"",
    ""Stato""           = EXCLUDED.""Stato"",
    ""SavedAt""         = EXCLUDED.""SavedAt"";
";

            await using var cmd = new NpgsqlCommand(sql, conn)
            {
                CommandTimeout = 120
            };

            return await cmd.ExecuteNonQueryAsync();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Svuota completamente la cache prima del popolamento giornaliero
        // ─────────────────────────────────────────────────────────────────────
        private async Task ClearPredictionsCacheAsync(NpgsqlConnection conn)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"TRUNCATE TABLE ""NextMatchPredictionsCache"";";
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task<bool> CheckPlan1Async()
        {
            var user = await _userManager.GetUserAsync(User);
            return user != null && (int)user.Plan == 1;
        }

        private static (DateTime utcStart, DateTime utcEnd) GetTodayUtcRangeEuropeRome()
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");

            var nowUtc = DateTime.UtcNow;
            var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);

            var localStart = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, DateTimeKind.Unspecified);
            var localEnd = localStart.AddDays(1);

            var utcStart = TimeZoneInfo.ConvertTimeToUtc(localStart, tz);
            var utcEnd = TimeZoneInfo.ConvertTimeToUtc(localEnd, tz);

            return (utcStart, utcEnd);
        }

        public sealed class CachedPredictionRow
        {
            public long MatchId { get; set; }
            public DateTime? EventDate { get; set; }
            public int? GoalSimulatiCasa { get; set; }
            public int? GoalSimulatiOspite { get; set; }
            public int? TotaleGoalSimulati { get; set; }
            public string? Esito { get; set; }
            public string? OverUnderRange { get; set; }
            public decimal? Over1_5 { get; set; }
            public decimal? Over2_5 { get; set; }
            public decimal? Over3_5 { get; set; }
            public string? GG_NG { get; set; }
            public string? MultigoalCasa { get; set; }
            public string? MultigoalOspite { get; set; }
            public string? ComboFinale { get; set; }
        }

        private async Task<CachedPredictionRow?> ExecutePredictionAsync(NpgsqlConnection conn, string script, long matchId)
        {
            await using var cmd = new NpgsqlCommand(script, conn)
            {
                CommandTimeout = 180
            };

            cmd.Parameters.Add("@MatchId", NpgsqlDbType.Bigint).Value = matchId;

            await using var rd = await cmd.ExecuteReaderAsync();
            if (!await rd.ReadAsync())
                return null;

            var row = new CachedPredictionRow
            {
                MatchId = GetField<long>(rd, "Id"),
                GoalSimulatiCasa = GetField<int?>(rd, "Goal Simulati Casa"),
                GoalSimulatiOspite = GetField<int?>(rd, "Goal Simulati Ospite"),
                TotaleGoalSimulati = GetField<int?>(rd, "Totale Goal Simulati"),
                Esito = GetField<string>(rd, "Esito"),
                OverUnderRange = GetField<string>(rd, "OverUnderRange"),
                Over1_5 = GetField<decimal?>(rd, "Over1_5"),
                Over2_5 = GetField<decimal?>(rd, "Over2_5"),
                Over3_5 = GetField<decimal?>(rd, "Over3_5"),
                GG_NG = GetField<string>(rd, "GG_NG"),
                MultigoalCasa = GetField<string>(rd, "MultigoalCasa"),
                MultigoalOspite = GetField<string>(rd, "MultigoalOspite"),
                ComboFinale = GetField<string>(rd, "ComboFinale"),
            };

            await rd.CloseAsync();

            await using var cmdDate = conn.CreateCommand();
            cmdDate.CommandText = @"SELECT date FROM matches WHERE id = @id LIMIT 1;";
            cmdDate.Parameters.AddWithValue("id", matchId);
            var obj = await cmdDate.ExecuteScalarAsync();
            if (obj != null && obj != DBNull.Value)
                row.EventDate = (DateTime)obj;

            return row;
        }

        private async Task<int> PopulateCacheBulkAsync(
            NpgsqlConnection conn,
            string script,
            List<long> matchIds)
        {
            if (matchIds == null || matchIds.Count == 0)
                return 0;

            // 1) Pulizia script: spesso termina con ';' e dentro un CTE rompe la sintassi
            var cleaned = (script ?? string.Empty).Trim();
            while (cleaned.EndsWith(";"))
                cleaned = cleaned.Substring(0, cleaned.Length - 1).Trim();

            // 2) Lo script è fatto per @MatchId (singolo).
            //    Lo trasformiamo in "ids.matchid" così diventa set-based.
            var scriptSetBased = cleaned.Replace("@MatchId", "ids.matchid");

            // 3) Wrapper set-based + LIMIT 1 per evitare duplicati dal tuo script
            //    + ON CONFLICT per non esplodere se esiste già la riga (PK su MatchId)
            var sql = $@"
WITH ids AS (
    SELECT unnest(@MatchIds::bigint[]) AS matchid
),
pred_one AS (
    SELECT
        ids.matchid AS matchid,
        mx.""date"" AS eventdate,
        pred.*
    FROM ids
    JOIN public.matches mx ON mx.id = ids.matchid
    CROSS JOIN LATERAL (
        SELECT * FROM (
            {scriptSetBased}
        ) q
        LIMIT 1
    ) pred
)
INSERT INTO ""NextMatchPredictionsCache"" (
  ""MatchId"", ""EventDate"",
  ""GoalSimulatiCasa"", ""GoalSimulatiOspite"", ""TotaleGoalSimulati"",
  ""Esito"", ""OverUnderRange"", ""Over1_5"", ""Over2_5"", ""Over3_5"", ""GG_NG"",
  ""MultigoalCasa"", ""MultigoalOspite"", ""ComboFinale"",
  ""GeneratedAtUtc""
)
SELECT
  p.matchid                                   AS ""MatchId"",
  p.eventdate                                 AS ""EventDate"",
  p.""Goal Simulati Casa""                    AS ""GoalSimulatiCasa"",
  p.""Goal Simulati Ospite""                  AS ""GoalSimulatiOspite"",
  p.""Totale Goal Simulati""                  AS ""TotaleGoalSimulati"",
  p.""Esito""                                 AS ""Esito"",
  p.""OverUnderRange""                        AS ""OverUnderRange"",
  p.""Over1_5""                               AS ""Over1_5"",
  p.""Over2_5""                               AS ""Over2_5"",
  p.""Over3_5""                               AS ""Over3_5"",
  p.""GG_NG""                                 AS ""GG_NG"",
  p.""MultigoalCasa""                         AS ""MultigoalCasa"",
  p.""MultigoalOspite""                       AS ""MultigoalOspite"",
  p.""ComboFinale""                           AS ""ComboFinale"",
  now()                                       AS ""GeneratedAtUtc""
FROM pred_one p
ON CONFLICT (""MatchId"")
DO UPDATE SET
  ""EventDate""           = EXCLUDED.""EventDate"",
  ""GoalSimulatiCasa""    = EXCLUDED.""GoalSimulatiCasa"",
  ""GoalSimulatiOspite""  = EXCLUDED.""GoalSimulatiOspite"",
  ""TotaleGoalSimulati""  = EXCLUDED.""TotaleGoalSimulati"",
  ""Esito""               = EXCLUDED.""Esito"",
  ""OverUnderRange""      = EXCLUDED.""OverUnderRange"",
  ""Over1_5""             = EXCLUDED.""Over1_5"",
  ""Over2_5""             = EXCLUDED.""Over2_5"",
  ""Over3_5""             = EXCLUDED.""Over3_5"",
  ""GG_NG""               = EXCLUDED.""GG_NG"",
  ""MultigoalCasa""       = EXCLUDED.""MultigoalCasa"",
  ""MultigoalOspite""     = EXCLUDED.""MultigoalOspite"",
  ""ComboFinale""         = EXCLUDED.""ComboFinale"",
  ""GeneratedAtUtc""      = EXCLUDED.""GeneratedAtUtc"";
";

            await using var cmd = new NpgsqlCommand(sql, conn)
            {
                CommandTimeout = 120
            };

            cmd.Parameters.AddWithValue("MatchIds", NpgsqlDbType.Array | NpgsqlDbType.Bigint, matchIds.ToArray());

            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(95));
                var affected = await cmd.ExecuteNonQueryAsync(cts.Token);
                return affected;
            }
            catch (Npgsql.PostgresException pgex)
            {
                int pos = 0;
                bool hasPos = false;

                try
                {
                    pos = Convert.ToInt32(pgex.Position, CultureInfo.InvariantCulture);
                    hasPos = pos > 0 && pos <= sql.Length;
                }
                catch
                {
                    hasPos = false;
                }

                if (hasPos)
                {
                    var start = Math.Max(0, pos - 200);
                    var len = Math.Min(400, sql.Length - start);
                    var snippet = sql.Substring(start, len);

                    throw new Exception(
                        $"SQL ERROR {pgex.SqlState}: {pgex.MessageText}\n" +
                        $"POSITION: {pos}\n" +
                        $"--- SQL SNIPPET ---\n{snippet}\n--- END ---"
                    );
                }

                throw new Exception($"SQL ERROR {pgex.SqlState}: {pgex.MessageText}", pgex);
            }
        }

        // ✅ INSERT puro (tabella svuotata prima)
        private async Task<bool> InsertCacheAsync(NpgsqlConnection conn, CachedPredictionRow r)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO ""NextMatchPredictionsCache"" (
  ""MatchId"", ""EventDate"",
  ""GoalSimulatiCasa"", ""GoalSimulatiOspite"", ""TotaleGoalSimulati"",
  ""Esito"", ""OverUnderRange"", ""Over1_5"", ""Over2_5"", ""Over3_5"", ""GG_NG"",
  ""MultigoalCasa"", ""MultigoalOspite"", ""ComboFinale"",
  ""GeneratedAtUtc""
)
VALUES (
  @MatchId, @EventDate,
  @GSC, @GSO, @TGS,
  @Esito, @OUR, @O15, @O25, @O35, @GG,
  @MGC, @MGO, @CF,
  now()
);";

            cmd.Parameters.AddWithValue("MatchId", r.MatchId);
            cmd.Parameters.AddWithValue("EventDate", (object?)r.EventDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("GSC", (object?)r.GoalSimulatiCasa ?? DBNull.Value);
            cmd.Parameters.AddWithValue("GSO", (object?)r.GoalSimulatiOspite ?? DBNull.Value);
            cmd.Parameters.AddWithValue("TGS", (object?)r.TotaleGoalSimulati ?? DBNull.Value);
            cmd.Parameters.AddWithValue("Esito", (object?)r.Esito ?? DBNull.Value);
            cmd.Parameters.AddWithValue("OUR", (object?)r.OverUnderRange ?? DBNull.Value);
            cmd.Parameters.AddWithValue("O15", (object?)r.Over1_5 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("O25", (object?)r.Over2_5 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("O35", (object?)r.Over3_5 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("GG", (object?)r.GG_NG ?? DBNull.Value);
            cmd.Parameters.AddWithValue("MGC", (object?)r.MultigoalCasa ?? DBNull.Value);
            cmd.Parameters.AddWithValue("MGO", (object?)r.MultigoalOspite ?? DBNull.Value);
            cmd.Parameters.AddWithValue("CF", (object?)r.ComboFinale ?? DBNull.Value);

            var n = await cmd.ExecuteNonQueryAsync();
            return n > 0;
        }

        private static T? GetField<T>(IDataRecord r, string name)
        {
            var ord = r.GetOrdinal(name);
            if (r.IsDBNull(ord)) return default;
            object val = r.GetValue(ord);

            var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

            if (targetType == typeof(long) && val is int i) val = (long)i;
            if (targetType == typeof(int) && val is long l) val = (int)l;
            if (targetType == typeof(decimal) && val is double d) val = (decimal)d;

            return (T)Convert.ChangeType(val, targetType, CultureInfo.InvariantCulture);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Carica storico verifica da PredictionsVerifica (ultimi 30 giorni)
        // con join su matches → leagues per recuperare logo e flag
        // ─────────────────────────────────────────────────────────────────────
        private async Task<(List<VerificaRow>, VerificaStats)> LoadVerificaAsync()
        {
            var cs = _identityDb.Database.GetConnectionString();
            var csb = new NpgsqlConnectionStringBuilder(cs)
            {
                KeepAlive = 30,
                CommandTimeout = 60,
                Timeout = 15
            };

            var list = new List<VerificaRow>();

            await using var conn = new NpgsqlConnection(csb.ToString());
            await conn.OpenAsync();

            // Usa ReadDb connection string per il join su matches/leagues
            var readCs = _read.Database.GetConnectionString();
            await using var connRead = new NpgsqlConnection(readCs);
            await connRead.OpenAsync();

            // Step 1: carica dati verifica
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT
    ""MatchId"", ""EventDate"", ""HomeName"", ""AwayName"", ""Lega"",
    ""Esito"", ""OverUnderRange"", ""GG_NG"", ""ComboFinale"",
    ""GolRealiCasa"", ""GolRealiOspite"",
    ""EsitoReale"", ""EsitoOK"", ""OverUnderOK"", ""GG_NG_OK"", ""ComboOK""
FROM ""PredictionsVerifica""
WHERE ""EventDate"" >= now() - interval '30 days'
ORDER BY ""EventDate"" DESC, ""MatchId"";
";
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                list.Add(new VerificaRow
                {
                    MatchId = rd.GetFieldValue<long>(rd.GetOrdinal("MatchId")),
                    EventDate = rd.IsDBNull(rd.GetOrdinal("EventDate")) ? null : rd.GetFieldValue<DateTime>(rd.GetOrdinal("EventDate")),
                    HomeName = rd.IsDBNull(rd.GetOrdinal("HomeName")) ? null : rd.GetFieldValue<string>(rd.GetOrdinal("HomeName")),
                    AwayName = rd.IsDBNull(rd.GetOrdinal("AwayName")) ? null : rd.GetFieldValue<string>(rd.GetOrdinal("AwayName")),
                    Lega = rd.IsDBNull(rd.GetOrdinal("Lega")) ? null : rd.GetFieldValue<string>(rd.GetOrdinal("Lega")),
                    Esito = rd.IsDBNull(rd.GetOrdinal("Esito")) ? null : rd.GetFieldValue<string>(rd.GetOrdinal("Esito")),
                    OverUnderRange = rd.IsDBNull(rd.GetOrdinal("OverUnderRange")) ? null : rd.GetFieldValue<string>(rd.GetOrdinal("OverUnderRange")),
                    GG_NG = rd.IsDBNull(rd.GetOrdinal("GG_NG")) ? null : rd.GetFieldValue<string>(rd.GetOrdinal("GG_NG")),
                    ComboFinale = rd.IsDBNull(rd.GetOrdinal("ComboFinale")) ? null : rd.GetFieldValue<string>(rd.GetOrdinal("ComboFinale")),
                    GolRealiCasa = rd.IsDBNull(rd.GetOrdinal("GolRealiCasa")) ? null : rd.GetFieldValue<int>(rd.GetOrdinal("GolRealiCasa")),
                    GolRealiOspite = rd.IsDBNull(rd.GetOrdinal("GolRealiOspite")) ? null : rd.GetFieldValue<int>(rd.GetOrdinal("GolRealiOspite")),
                    EsitoReale = rd.IsDBNull(rd.GetOrdinal("EsitoReale")) ? null : rd.GetFieldValue<string>(rd.GetOrdinal("EsitoReale")),
                    EsitoOK = rd.IsDBNull(rd.GetOrdinal("EsitoOK")) ? null : rd.GetFieldValue<string>(rd.GetOrdinal("EsitoOK")),
                    OverUnderOK = rd.IsDBNull(rd.GetOrdinal("OverUnderOK")) ? null : rd.GetFieldValue<string>(rd.GetOrdinal("OverUnderOK")),
                    GG_NG_OK = rd.IsDBNull(rd.GetOrdinal("GG_NG_OK")) ? null : rd.GetFieldValue<string>(rd.GetOrdinal("GG_NG_OK")),
                    ComboOK = rd.IsDBNull(rd.GetOrdinal("ComboOK")) ? null : rd.GetFieldValue<string>(rd.GetOrdinal("ComboOK")),
                });
            }
            await rd.CloseAsync();

            // Step 2: recupera logo+flag per lega via join matches→leagues (ReadDb)
            if (list.Count > 0)
            {
                var matchIds = list.Select(x => x.MatchId).Distinct().ToList();

                // Costruisce array literal PostgreSQL
                await using var cmdLogos = connRead.CreateCommand();
                cmdLogos.CommandText = @"
SELECT m.id AS match_id, lg.logo, lg.flag
FROM matches m
JOIN leagues lg ON lg.id = m.leagueid
WHERE m.id = ANY(@ids);
";
                cmdLogos.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint, matchIds.ToArray());

                var logoMap = new Dictionary<long, (string? Logo, string? Flag)>();
                await using var rdLogos = await cmdLogos.ExecuteReaderAsync();
                while (await rdLogos.ReadAsync())
                {
                    var mid = rdLogos.GetFieldValue<long>(rdLogos.GetOrdinal("match_id"));
                    var logo = rdLogos.IsDBNull(rdLogos.GetOrdinal("logo")) ? null : rdLogos.GetFieldValue<string>(rdLogos.GetOrdinal("logo"));
                    var flag = rdLogos.IsDBNull(rdLogos.GetOrdinal("flag")) ? null : rdLogos.GetFieldValue<string>(rdLogos.GetOrdinal("flag"));
                    logoMap[mid] = (logo, flag);
                }

                // Applica logo/flag a ogni riga
                foreach (var row in list)
                {
                    if (logoMap.TryGetValue(row.MatchId, out var lf))
                    {
                        row.LeagueLogo = lf.Logo;
                        row.LeagueFlag = lf.Flag;
                    }
                }
            }

            // Step 3: statistiche aggregate globali
            var finished = list.Where(x => x.EsitoOK != null).ToList();
            var stats = new VerificaStats
            {
                Totale = finished.Count,
                PctEsito = finished.Count > 0 ? Math.Round(finished.Count(x => x.EsitoOK == "OK") * 100.0 / finished.Count, 1) : 0,
                PctOU = finished.Count > 0 ? Math.Round(finished.Count(x => x.OverUnderOK == "OK") * 100.0 / finished.Count, 1) : 0,
                PctGGNG = finished.Count > 0 ? Math.Round(finished.Count(x => x.GG_NG_OK == "OK") * 100.0 / finished.Count, 1) : 0,
                PctCombo = finished.Count > 0 ? Math.Round(finished.Count(x => x.ComboOK == "OK") * 100.0 / finished.Count, 1) : 0,
            };

            // Step 4: statistiche per lega
            stats.PerLega = finished
                .GroupBy(x => x.Lega ?? "–")
                .Select(g => new LeagueStatsRow
                {
                    Lega = g.Key,
                    LeagueLogo = g.FirstOrDefault(x => x.LeagueLogo != null)?.LeagueLogo,
                    LeagueFlag = g.FirstOrDefault(x => x.LeagueFlag != null)?.LeagueFlag,
                    Totale = g.Count(),
                    PctEsito = Math.Round(g.Count(x => x.EsitoOK == "OK") * 100.0 / g.Count(), 1),
                    PctOU = Math.Round(g.Count(x => x.OverUnderOK == "OK") * 100.0 / g.Count(), 1),
                    PctGGNG = Math.Round(g.Count(x => x.GG_NG_OK == "OK") * 100.0 / g.Count(), 1),
                })
                .OrderByDescending(x => x.Totale)
                .ToList();

            return (list, stats);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Model classes
        // ─────────────────────────────────────────────────────────────────────
        public sealed class VerificaRow
        {
            public long MatchId { get; set; }
            public DateTime? EventDate { get; set; }
            public string? HomeName { get; set; }
            public string? AwayName { get; set; }
            public string? Lega { get; set; }
            public string? LeagueLogo { get; set; }
            public string? LeagueFlag { get; set; }
            public string? Esito { get; set; }
            public string? OverUnderRange { get; set; }
            public string? GG_NG { get; set; }
            public string? ComboFinale { get; set; }
            public int? GolRealiCasa { get; set; }
            public int? GolRealiOspite { get; set; }
            public string? EsitoReale { get; set; }
            public string? EsitoOK { get; set; }
            public string? OverUnderOK { get; set; }
            public string? GG_NG_OK { get; set; }
            public string? ComboOK { get; set; }
        }

        public sealed class LeagueStatsRow
        {
            public string? Lega { get; set; }
            public string? LeagueLogo { get; set; }
            public string? LeagueFlag { get; set; }
            public int Totale { get; set; }
            public double PctEsito { get; set; }
            public double PctOU { get; set; }
            public double PctGGNG { get; set; }
        }

        public sealed class VerificaStats
        {
            public int Totale { get; set; }
            public double PctEsito { get; set; }
            public double PctOU { get; set; }
            public double PctGGNG { get; set; }
            public double PctCombo { get; set; }
            public List<LeagueStatsRow> PerLega { get; set; } = new();
        }
    }
}
