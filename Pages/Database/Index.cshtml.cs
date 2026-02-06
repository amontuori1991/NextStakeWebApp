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

        public DateTime? LastRun { get; private set; }
        public int TodayMatchesCount { get; private set; }
        public int UpsertedCount { get; private set; }
        public string? Error { get; private set; }

        public async Task OnGetAsync()
        {
            IsPlan1 = await CheckPlan1Async();
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
                    KeepAlive = 30,              // ping ogni 30s
                    CommandTimeout = 120,        // default command timeout
                    Timeout = 15,                // timeout connessione (open)
                    CancellationTimeout = 10     // secondi: attesa risposta al CANCEL
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
                Error = ex.ToString(); // log completo visibile nella card della pagina (Model.Error)
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

                // 1) SVUOTO CACHE con UNA connessione dedicata (così se muore non rovina il loop)
                await Log("🧹 Svuoto NextMatchPredictionsCache...\n");
                await using (var connClear = new NpgsqlConnection(csb.ToString()))
                {
                    await connClear.OpenAsync();
                    await ClearPredictionsCacheAsync(connClear);
                }
                await Log("✅ Cache svuotata.\n\n");

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
                        // 2) OGNI BATCH USA UNA CONNESSIONE NUOVA
                        await using var connBatch = new NpgsqlConnection(csb.ToString());
                        await connBatch.OpenAsync();

                        // ⛔ timeout DB per questa singola esecuzione (90 secondi)
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

        // ✅ NEW: svuota completamente la cache prima del popolamento giornaliero
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

        private sealed class CachedPredictionRow
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

            // 2) lo script è fatto per @MatchId (singolo).
            //    Lo trasformiamo in "ids.matchid" così diventa set-based.
            var scriptSetBased = cleaned.Replace("@MatchId", "ids.matchid");


            // 2) Query wrapper: unnest degli id + LATERAL sullo script + INSERT in cache
            //    Non facciamo più query extra per EventDate: la prendiamo direttamente da matches.
            var sql = $@"
WITH ids AS (
    SELECT unnest(@MatchIds::bigint[]) AS matchid
)
INSERT INTO ""NextMatchPredictionsCache"" (
  ""MatchId"", ""EventDate"",
  ""GoalSimulatiCasa"", ""GoalSimulatiOspite"", ""TotaleGoalSimulati"",
  ""Esito"", ""OverUnderRange"", ""Over1_5"", ""Over2_5"", ""Over3_5"", ""GG_NG"",
  ""MultigoalCasa"", ""MultigoalOspite"", ""ComboFinale"",
  ""GeneratedAtUtc""
)
SELECT
  ids.matchid                                   AS ""MatchId"",
  mx.""date""                                   AS ""EventDate"",
  pred.""Goal Simulati Casa""                    AS ""GoalSimulatiCasa"",
  pred.""Goal Simulati Ospite""                  AS ""GoalSimulatiOspite"",
  pred.""Totale Goal Simulati""                  AS ""TotaleGoalSimulati"",
  pred.""Esito""                                 AS ""Esito"",
  pred.""OverUnderRange""                        AS ""OverUnderRange"",
  pred.""Over1_5""                               AS ""Over1_5"",
  pred.""Over2_5""                               AS ""Over2_5"",
  pred.""Over3_5""                               AS ""Over3_5"",
  pred.""GG_NG""                                 AS ""GG_NG"",
  pred.""MultigoalCasa""                         AS ""MultigoalCasa"",
  pred.""MultigoalOspite""                       AS ""MultigoalOspite"",
  pred.""ComboFinale""                           AS ""ComboFinale"",
  now()                                          AS ""GeneratedAtUtc""
FROM ids
JOIN public.matches mx ON mx.id = ids.matchid
CROSS JOIN LATERAL (
    SELECT * FROM (
        {scriptSetBased}
    ) q
) pred;
";



            await using var cmd = new NpgsqlCommand(sql, conn)
            {
                CommandTimeout = 120 // 2 minuti lato client (il server taglia già a 90s)
            };



            // parametro array bigint[]
            cmd.Parameters.AddWithValue("MatchIds", NpgsqlDbType.Array | NpgsqlDbType.Bigint, matchIds.ToArray());

            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(95));
                var inserted = await cmd.ExecuteNonQueryAsync(cts.Token);

                return inserted;
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

        // ✅ RENAMED/CHANGED: ora è INSERT puro (tabella svuotata prima)
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
    }
}
