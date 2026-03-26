using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;

namespace NextStakeWebApp.bck.Api
{
    [ApiController]
    [Route("api/events")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class ScalataController : ControllerBase
    {
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _http;
        private readonly ReadDbContext _read;

        // betId rilevanti per la scalata
        private static readonly int[] RelevantBetIds = { 1, 5, 8, 12 };

        public ScalataController(IConfiguration config, IHttpClientFactory http, ReadDbContext read)
        {
            _config = config;
            _http = http;
            _read = read;
        }

        // ════════════════════════════════════════════════════════════
        // GET /api/events/scalata
        // ════════════════════════════════════════════════════════════
        [HttpGet("scalata")]
        public async Task<IActionResult> GetScalata()
        {
            // ── 1. MATCH DEL GIORNO CON PRONOSTICI ──────────────
            var today = DateTime.Today;
            var tomorrow = today.AddDays(1);

            var matchesWithPreds = await (
                from m in _read.Matches
                join lg in _read.Leagues on m.LeagueId equals lg.Id
                join th in _read.Teams on m.HomeId equals th.Id
                join ta in _read.Teams on m.AwayId equals ta.Id
                join c in _read.Set<PredictionDbRow>() on m.Id equals c.MatchId
                where m.Date >= today && m.Date < tomorrow
                   && (m.StatusShort == null || m.StatusShort == "NS")
                orderby m.Date
                select new
                {
                    matchId = m.Id,
                    date = m.Date,
                    homeName = th.Name,
                    awayName = ta.Name,
                    homeLogo = th.Logo,
                    awayLogo = ta.Logo,
                    leagueName = lg.Name,
                    leagueLogo = lg.Logo,
                    leagueFlag = lg.Flag,
                    countryCode = lg.CountryCode,
                    countryName = lg.CountryName,
                    esito = c.Esito,
                    overUnder = c.OverUnderRange,
                    ggNg = c.GG_NG,
                    combo = c.ComboFinale,
                    gsCasa = c.GoalSimulatiCasa,
                    gsOspite = c.GoalSimulatiOspite,
                    over15 = c.Over1_5,
                    over25 = c.Over2_5,
                    over35 = c.Over3_5
                }
            ).AsNoTracking().ToListAsync();

            if (matchesWithPreds.Count == 0)
                return Ok(new
                {
                    picks = new List<object>(),
                    summary = "Nessun match con pronostici disponibili oggi."
                });

            // ── 2. QUOTE REALI PER TUTTI I MERCATI RILEVANTI ────
            var matchIdsLong = matchesWithPreds
                .Select(m => m.matchId)
                .Distinct()
                .ToList();

            var idsArray = matchIdsLong.ToArray();
            var betIdsArray = RelevantBetIds;

            var allOdds = await _read.Odds
                .FromSql($"SELECT * FROM odds WHERE id = ANY({idsArray}::bigint[]) AND betid = ANY({betIdsArray}::int[])")
                .AsNoTracking()
                .ToListAsync();

            // matchId -> betId -> value -> odd
            var oddsByMatch = allOdds
                .GroupBy(o => o.Id)
                .ToDictionary(
                    g => g.Key,
                    g => g.GroupBy(o => o.Betid)
                          .ToDictionary(
                              bg => bg.Key,
                              bg => bg.ToDictionary(o => o.Value ?? "", o => (double)o.Odd)
                          )
                );

            // ── 3. COSTRUZIONE CONTESTO PER L'AI ────────────────
            var matchesContext = new StringBuilder();
            matchesContext.AppendLine("PARTITE DISPONIBILI OGGI CON PRONOSTICI E QUOTE REALI:");
            matchesContext.AppendLine();

            foreach (var m in matchesWithPreds)
            {
                var betMap = oddsByMatch.TryGetValue(m.matchId, out var bm) ? bm : new();

                // betId=1: 1X2
                betMap.TryGetValue(1, out var o1x2);
                var q1 = GetOdd(o1x2, "Home");
                var qx = GetOdd(o1x2, "Draw");
                var q2 = GetOdd(o1x2, "Away");

                // betId=5: Over/Under
                betMap.TryGetValue(5, out var ou);
                var over15 = GetOdd(ou, "Over 1.5");
                var over25 = GetOdd(ou, "Over 2.5");
                var over35 = GetOdd(ou, "Over 3.5");
                var under25 = GetOdd(ou, "Under 2.5");
                var under35 = GetOdd(ou, "Under 3.5");

                // betId=8: GG/NG
                betMap.TryGetValue(8, out var ggng);
                var gg = GetOdd(ggng, "Yes");
                var ng = GetOdd(ggng, "No");

                // betId=12: Double Chance
                betMap.TryGetValue(12, out var dc);
                var dc1x = GetOdd(dc, "Home/Draw");
                var dcx2 = GetOdd(dc, "Draw/Away");
                var dc12 = GetOdd(dc, "Home/Away");

                // Lista quote disponibili nel range utile (1.10 - 2.50)
                var available = new List<string>();
                AddIfInRange(available, "1", q1);
                AddIfInRange(available, "X", qx);
                AddIfInRange(available, "2", q2);
                AddIfInRange(available, "Over 1.5", over15);
                AddIfInRange(available, "Over 2.5", over25);
                AddIfInRange(available, "Over 3.5", over35);
                AddIfInRange(available, "Under 2.5", under25);
                AddIfInRange(available, "Under 3.5", under35);
                AddIfInRange(available, "GG", gg);
                AddIfInRange(available, "NG", ng);
                AddIfInRange(available, "1X", dc1x);
                AddIfInRange(available, "X2", dcx2);
                AddIfInRange(available, "12", dc12);

                matchesContext.AppendLine("---");
                matchesContext.AppendLine($"ID={m.matchId} | {m.date:HH:mm} | {m.leagueName}");
                matchesContext.AppendLine($"  {m.homeName} vs {m.awayName}");
                matchesContext.AppendLine($"  Pronostico modello: Esito={m.esito} | {m.overUnder} | {m.ggNg} | Combo={m.combo}");
                matchesContext.AppendLine($"  GS={m.gsCasa:0.0}-{m.gsOspite:0.0} | O1.5={m.over15:0}% O2.5={m.over25:0}% O3.5={m.over35:0}%");

                if (available.Count > 0)
                    matchesContext.AppendLine($"  QUOTE REALI (usa SOLO queste): {string.Join(" | ", available)}");
                else
                    matchesContext.AppendLine($"  QUOTE REALI: nessuna quota disponibile nel range — ESCLUDI questo match");

                matchesContext.AppendLine();
            }

            // ── 4. PROMPT ────────────────────────────────────────
            var prompt =
                "Sei un analista sportivo esperto di value betting.\n" +
                "Analizza le partite del giorno e costruisci una SCALATA OTTIMALE.\n\n" +
                matchesContext.ToString() +
                "REGOLE FONDAMENTALI — rispettale TUTTE senza eccezioni:\n" +
                "1. Seleziona da 3 a 6 partite\n" +
                "2. Per ogni pick scegli UN'unica opzione dalla lista 'QUOTE REALI' del match\n" +
                "3. quotaStimata deve essere ESATTAMENTE il numero dalla lista QUOTE REALI, non un valore inventato\n" +
                "4. Non inventare mai una quota: se un mercato non compare nella lista QUOTE REALI, non puoi scommetterci\n" +
                "5. Escludi i match con 'nessuna quota disponibile'\n" +
                "6. Il campo 'pick' deve usare ESATTAMENTE la stessa stringa della lista (es. 'Over 2.5', 'GG', '1X', '1')\n" +
                "7. Privilegia pick coerenti con il pronostico del modello e con Over% / GS alti\n\n" +
                "Rispondi ESCLUSIVAMENTE con questo JSON (nessun testo prima o dopo):\n" +
                "{\"picks\":[{\"matchId\":123456,\"pick\":\"Over 2.5\",\"quotaStimata\":1.80,\"confidenza\":\"ALTA\",\"motivazione\":\"Breve motivazione tecnica.\"}],\"summary\":\"Riepilogo scalata in 1-2 righe.\"}";

            // ── 5. CHIAMATA OPENAI ───────────────────────────────
            var openAiKey = _config["OpenAI:ApiKey"]
                ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            var openAiModel = _config["OpenAI:Model"] ?? "gpt-4o-mini";

            var openAiClient = _http.CreateClient("OpenAI");
            openAiClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {openAiKey}");

            var requestBody = JsonSerializer.Serialize(new
            {
                model = openAiModel,
                messages = new[]
                {
                    new
                    {
                        role    = "system",
                        content = "Sei un analista di scommesse sportive esperto. Rispondi SEMPRE e SOLO con JSON valido, senza markdown, senza backtick, senza testo aggiuntivo prima o dopo il JSON."
                    },
                    new { role = "user", content = prompt }
                },
                max_tokens = 1500,
                temperature = 0.2
            });

            var aiResponse = await openAiClient.PostAsync(
                "chat/completions",
                new StringContent(requestBody, Encoding.UTF8, "application/json"));

            if (!aiResponse.IsSuccessStatusCode)
                return StatusCode(503, new { error = "Servizio AI temporaneamente non disponibile." });

            var responseJson = await aiResponse.Content.ReadAsStringAsync();
            using var responseDoc = JsonDocument.Parse(responseJson);

            var rawContent = responseDoc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "{}";

            // ── 6. PARSE + ARRICCHIMENTO ─────────────────────────
            try
            {
                var cleaned = rawContent
                    .Trim()
                    .Replace("```json", "")
                    .Replace("```", "")
                    .Trim();

                using var aiDoc = JsonDocument.Parse(cleaned);
                var picksEl = aiDoc.RootElement.GetProperty("picks");
                var summary = aiDoc.RootElement
                    .TryGetProperty("summary", out var s) ? s.GetString() : "";

                var enrichedPicks = new List<object>();
                double quotaTotale = 1.0;

                foreach (var pick in picksEl.EnumerateArray())
                {
                    var mId = pick.GetProperty("matchId").GetInt64();
                    var match = matchesWithPreds.FirstOrDefault(m => m.matchId == mId);
                    if (match == null) continue;

                    var pickType = pick.TryGetProperty("pick", out var pk)
                        ? pk.GetString() ?? "" : "";

                    // Usa sempre la quota reale dal DB, non quella stimata dall'AI
                    var betMap = oddsByMatch.TryGetValue(mId, out var bm) ? bm : new();
                    var realQuota = ResolveRealQuota(pickType, betMap);
                    var quota = realQuota
                        ?? (pick.TryGetProperty("quotaStimata", out var q) ? q.GetDouble() : 1.0);

                    quotaTotale *= quota;

                    enrichedPicks.Add(new
                    {
                        matchId = mId,
                        kickoff = match.date,
                        homeName = match.homeName,
                        awayName = match.awayName,
                        homeLogo = match.homeLogo,
                        awayLogo = match.awayLogo,
                        leagueName = match.leagueName,
                        leagueLogo = match.leagueLogo,
                        leagueFlag = match.leagueFlag,
                        countryCode = match.countryCode,
                        pick = pickType,
                        quotaStimata = Math.Round(quota, 2),
                        confidenza = pick.TryGetProperty("confidenza", out var cf)
                                           ? cf.GetString() : "MEDIA",
                        motivazione = pick.TryGetProperty("motivazione", out var mv)
                                           ? mv.GetString() : ""
                    });
                }

                return Ok(new
                {
                    picks = enrichedPicks,
                    quotaTotale = Math.Round(quotaTotale, 2),
                    summary,
                    generatedAt = DateTime.UtcNow
                });
            }
            catch
            {
                return Ok(new
                {
                    picks = new List<object>(),
                    summary = rawContent,
                    generatedAt = DateTime.UtcNow
                });
            }
        }

        // ════════════════════════════════════════════════════════════
        // HELPERS
        // ════════════════════════════════════════════════════════════

        private static double? GetOdd(Dictionary<string, double>? map, string key)
            => map?.TryGetValue(key, out var v) == true ? v : null;

        private static void AddIfInRange(
            List<string> list, string label, double? odd,
            double min = 1.10, double max = 2.50)
        {
            if (odd.HasValue && odd.Value >= min && odd.Value <= max)
                list.Add($"{label}={odd.Value:0.00}");
        }

        private static double? ResolveRealQuota(
            string pickType,
            Dictionary<int, Dictionary<string, double>> betMap)
        {
            if (string.IsNullOrEmpty(pickType)) return null;

            if (betMap.TryGetValue(1, out var o1x2))
            {
                if (pickType == "1" && o1x2.TryGetValue("Home", out var v1)) return v1;
                if (pickType == "X" && o1x2.TryGetValue("Draw", out var vx)) return vx;
                if (pickType == "2" && o1x2.TryGetValue("Away", out var v2)) return v2;
            }

            if (betMap.TryGetValue(12, out var dc))
            {
                if (pickType == "1X" && dc.TryGetValue("Home/Draw", out var v1x)) return v1x;
                if (pickType == "X2" && dc.TryGetValue("Draw/Away", out var vx2)) return vx2;
                if (pickType == "12" && dc.TryGetValue("Home/Away", out var v12)) return v12;
            }

            if (betMap.TryGetValue(5, out var ou))
            {
                // Normalizza spazi (l'AI a volte scrive "Over2.5" senza spazio)
                var normalized = System.Text.RegularExpressions.Regex
                    .Replace(pickType, @"(Over|Under)(\d)", "$1 $2");

                if (ou.TryGetValue(normalized, out var vou)) return vou;
                if (ou.TryGetValue(pickType, out var vouOr)) return vouOr;
            }

            if (betMap.TryGetValue(8, out var ggng))
            {
                if (pickType == "GG" && ggng.TryGetValue("Yes", out var vgg)) return vgg;
                if (pickType == "NG" && ggng.TryGetValue("No", out var vng)) return vng;
            }

            return null;
        }
    }
}