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
                    .Select(m => m.Id)
                    .ToListAsync();

                TodayMatchesCount = todayMatchIds.Count;

                var cs = _identityDb.Database.GetConnectionString();

                await using var conn = new NpgsqlConnection(cs);
                await conn.OpenAsync();

                // ✅ SVUOTA CACHE PRIMA DI INSERIRE I NUOVI RECORD (run giornaliero)
                await ClearPredictionsCacheAsync(conn);

                foreach (var matchId in todayMatchIds)
                {
                    var row = await ExecutePredictionAsync(conn, script, matchId);
                    if (row == null)
                        continue;

                    var ok = await InsertCacheAsync(conn, row);
                    if (ok) UpsertedCount++;
                }
            }
            catch (Exception ex)
            {
                Error = ex.Message;
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
                await using var conn = new NpgsqlConnection(cs);
                await conn.OpenAsync();

                // ✅ SVUOTA CACHE PRIMA DI INSERIRE I NUOVI RECORD (run giornaliero)
                await Log("🧹 Svuoto NextMatchPredictionsCache...\n");
                await ClearPredictionsCacheAsync(conn);
                await Log("✅ Cache svuotata.\n\n");

                int idx = 0;

                foreach (var m in todayMatches)
                {
                    idx++;
                    await Log($"➡️ [{idx}/{TodayMatchesCount}] MatchId {m.Id} | {m.Date:yyyy-MM-dd HH:mm}\n");

                    var row = await ExecutePredictionAsync(conn, script, m.Id);
                    if (row == null)
                    {
                        await Log("   ⚠️ Nessuna riga restituita dalla query.\n\n");
                        continue;
                    }

                    await Log($"   🧠 Esito={row.Esito ?? "-"} | GG/NG={row.GG_NG ?? "-"} | O2.5={row.Over2_5?.ToString() ?? "-"} | Combo={row.ComboFinale ?? "-"}\n");

                    var ok = await InsertCacheAsync(conn, row);
                    if (ok)
                    {
                        UpsertedCount++;
                        await Log($"   ✅ Cache insert OK (totale insert: {UpsertedCount})\n\n");
                    }
                    else
                    {
                        await Log("   ❌ Cache insert fallito.\n\n");
                    }
                }

                await Log($"🎉 Fine. Match: {TodayMatchesCount} | Insert: {UpsertedCount}\n");
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                await Log($"\n❌ ERRORE: {Error}\n");
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
