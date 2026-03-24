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

            // ── 2. QUOTE 1X2 PER OGNI MATCH ─────────────────────
            var matchIdsLong = matchesWithPreds
                .Select(m => m.matchId)
                .Distinct()
                .ToList();

            // Con entità HasNoKey() EF non supporta Contains() direttamente.
            // Usiamo FromSqlRaw con array PostgreSQL.
            var idsParam = string.Join(",", matchIdsLong);

            var idsArray = matchIdsLong.ToArray();
            var allOdds = await _read.Odds
                .FromSql($"SELECT * FROM odds WHERE id = ANY({idsArray}::bigint[]) AND betid = 1")
                .AsNoTracking()
                .ToListAsync();

            var oddsByMatch = allOdds
                .GroupBy(o => o.Id)
                .ToDictionary(g => g.Key, g => g.ToList());

            // ── 3. COSTRUZIONE CONTESTO PER L'AI ────────────────
            var matchesContext = new StringBuilder();
            matchesContext.AppendLine("PARTITE DISPONIBILI OGGI CON PRONOSTICI:");
            matchesContext.AppendLine("Formato: ID | Kickoff | Lega | Partita | Esito | Over/Under | GG/NG | Combo | GS Casa-Ospite | Over1.5% | Over2.5% | Over3.5% | Quote 1/X/2 | QuotaPick stimata");
            matchesContext.AppendLine();

            foreach (var m in matchesWithPreds)
            {
                var odds = oddsByMatch.TryGetValue(m.matchId, out var o) ? o : new();
                var q1 = odds.FirstOrDefault(x => x.Value == "Home")?.Odd;
                var qx = odds.FirstOrDefault(x => x.Value == "Draw")?.Odd;
                var q2 = odds.FirstOrDefault(x => x.Value == "Away")?.Odd;

                double? quotaPick = m.esito switch
                {
                    "1" => (double?)q1,
                    "2" => (double?)q2,
                    "X" => (double?)qx,
                    "1X" => q1.HasValue && qx.HasValue
                                ? (double?)Math.Min((double)q1, (double)qx) : null,
                    "X2" => qx.HasValue && q2.HasValue
                                ? (double?)Math.Min((double)qx, (double)q2) : null,
                    _ => null
                };

                matchesContext.AppendLine(
                    $"ID={m.matchId} | {m.date:HH:mm} | {m.leagueName} | " +
                    $"{m.homeName} vs {m.awayName} | " +
                    $"Esito={m.esito} | {m.overUnder} | {m.ggNg} | Combo={m.combo} | " +
                    $"GS={m.gsCasa:0.0}-{m.gsOspite:0.0} | " +
                    $"O1.5={m.over15:0}% O2.5={m.over25:0}% O3.5={m.over35:0}% | " +
                    $"Quote: 1={q1:0.00} X={qx:0.00} 2={q2:0.00} | " +
                    $"QuotaPick={quotaPick:0.00}"
                );
            }

            // ── 4. PROMPT ────────────────────────────────────────
            // NOTA: il formato JSON di esempio nel prompt usa {{}} per
            // fare escape delle parentesi graffe dentro l'interpolated string
            var prompt =
                "Sei un analista sportivo esperto di value betting.\n" +
                "Analizza le partite del giorno e costruisci una SCALATA OTTIMALE.\n\n" +
                matchesContext.ToString() + "\n\n" +
                "OBIETTIVO SCALATA:\n" +
                "- Seleziona da 3 a 6 partite (non di più)\n" +
                "- Ogni pick deve avere una quota bookmaker stimata tra 1.20 e 1.40\n" +
                "- Privilegia pick con alta confidenza del modello (Over% alti, differenza GS chiara)\n" +
                "- Puoi usare: esito 1X2, Over/Under, GG/NG — scegli il più sicuro per ogni match\n" +
                "- NON includere partite dove i dati sono contraddittori o la confidenza è bassa\n\n" +
                "Per ogni pick selezionato fornisci:\n" +
                "1. matchId (numerico esatto dalla lista)\n" +
                "2. pick: tipo di scommessa (es. \"1\", \"Over 2.5\", \"GG\")\n" +
                "3. quotaStimata: numero tra 1.20 e 1.40\n" +
                "4. confidenza: \"ALTA\" o \"MEDIA\"\n" +
                "5. motivazione: 1-2 righe concise e tecniche\n\n" +
                "Rispondi ESCLUSIVAMENTE con questo JSON (nessun testo prima o dopo):\n" +
                "{\"picks\":[{\"matchId\":123456,\"pick\":\"Over 2.5\",\"quotaStimata\":1.30,\"confidenza\":\"ALTA\",\"motivazione\":\"Motivazione qui.\"}],\"summary\":\"Riepilogo scalata.\"}";

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
                max_tokens = 1200,
                temperature = 0.3
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
                // Pulizia difensiva nel caso l'AI aggiunga backtick
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

                    var quota = pick.TryGetProperty("quotaStimata", out var q)
                        ? q.GetDouble() : 1.0;
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
                        pick = pick.TryGetProperty("pick", out var pk)
                                           ? pk.GetString() : "",
                        quotaStimata = quota,
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
    }
}