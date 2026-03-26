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

                    // Scarta il pick se la quota reale è fuori range (l'AI potrebbe aver
                    // scelto un mercato con quota non disponibile o fuori soglia)
                    if (quota < 1.20 || quota > 1.45) continue;

                    quotaTotale *= quota;

                    // GG e NG hanno per definizione ~50% di probabilità:
                    // la confidenza non può essere ALTA indipendentemente da cosa dice l'AI
                    var confidenzaAi = pick.TryGetProperty("confidenza", out var cf)
                        ? cf.GetString() ?? "MEDIA" : "MEDIA";
                    var confidenzaFinale = (pickType == "GG" || pickType == "NG")
                        ? "MEDIA"
                        : confidenzaAi;

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
                        confidenza = confidenzaFinale,
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
        // GET /api/events/multipla?quotaTarget=5.00
        // ════════════════════════════════════════════════════════════
        [HttpGet("multipla")]
        public async Task<IActionResult> GetMultipla([FromQuery] double quotaTarget = 5.0)
        {
            if (quotaTarget < 1.5)
                return BadRequest(new { error = "quotaTarget deve essere almeno 1.50" });

            var matchesWithPreds = await LoadMatchesWithPredsAsync();
            if (matchesWithPreds.Count == 0)
                return Ok(new { picks = new List<object>(), summary = "Nessun match disponibile oggi." });

            var oddsByMatch = await LoadOddsAsync(matchesWithPreds);
            var matchesContext = BuildMatchContext(matchesWithPreds, oddsByMatch, minOdd: 1.20, maxOdd: 3.50);

            var prompt =
                "Sei un analista sportivo esperto di value betting.\n" +
                "Costruisci una MULTIPLA che raggiunga una quota totale il più vicino possibile a " +
                $"{quotaTarget:0.00} (tolleranza ±20%).\n\n" +
                matchesContext +
                "REGOLE FONDAMENTALI:\n" +
                "1. Seleziona da 2 a 8 partite\n" +
                "2. Le quote dei pick moltiplicati tra loro devono avvicinarsi a " + $"{quotaTarget:0.00}\n" +
                "3. Usa ESCLUSIVAMENTE quote dalla sezione 'QUOTE REALI' di ogni match\n" +
                "4. quotaStimata deve essere ESATTAMENTE il numero dalla lista QUOTE REALI\n" +
                "5. Se un mercato non è nella lista QUOTE REALI, non puoi usarlo\n" +
                "6. Escludi match con 'nessuna quota disponibile'\n" +
                "7. Privilegia pick coerenti con il pronostico del modello\n\n" +
                "Rispondi ESCLUSIVAMENTE con questo JSON:\n" +
                "{\"picks\":[{\"matchId\":123456,\"pick\":\"Over 2.5\",\"quotaStimata\":1.80,\"confidenza\":\"ALTA\",\"motivazione\":\"Breve motivazione.\"}],\"summary\":\"Riepilogo multipla.\"}";

            return await CallAiAndBuildResponse(prompt, matchesWithPreds, oddsByMatch, quotaTarget);
        }

        // ════════════════════════════════════════════════════════════
        // GET /api/events/singola
        // ════════════════════════════════════════════════════════════
        [HttpGet("singola")]
        public async Task<IActionResult> GetSingola()
        {
            var matchesWithPreds = await LoadMatchesWithPredsAsync();
            if (matchesWithPreds.Count == 0)
                return Ok(new { picks = new List<object>(), summary = "Nessun match disponibile oggi." });

            var oddsByMatch = await LoadOddsAsync(matchesWithPreds);
            // Per la singola vogliamo quote più alte: 1.50 - 3.50
            var matchesContext = BuildMatchContext(matchesWithPreds, oddsByMatch, minOdd: 1.50, maxOdd: 3.50);

            var prompt =
                "Sei un analista sportivo esperto di value betting.\n" +
                "Individua la MIGLIORE SINGOLA DI VALORE tra i match di oggi.\n\n" +
                matchesContext +
                "REGOLE FONDAMENTALI:\n" +
                "1. Scegli UNA SOLA partita e UN SOLO pick\n" +
                "2. La quota deve essere >= 1.50 e presente nella sezione 'QUOTE REALI'\n" +
                "3. Privilegia il pick con il miglior rapporto confidenza/quota: alta probabilità reale, quota bookmaker elevata (value)\n" +
                "4. quotaStimata deve essere ESATTAMENTE il numero dalla lista QUOTE REALI\n" +
                "5. Escludi match con 'nessuna quota disponibile'\n" +
                "6. Fornisci una motivazione tecnica solida basata su GS, Over% e pronostico modello\n\n" +
                "Rispondi ESCLUSIVAMENTE con questo JSON (un solo pick):\n" +
                "{\"picks\":[{\"matchId\":123456,\"pick\":\"Over 2.5\",\"quotaStimata\":1.80,\"confidenza\":\"ALTA\",\"motivazione\":\"Motivazione tecnica approfondita.\"}],\"summary\":\"Perché questa è la singola di valore del giorno.\"}";

            return await CallAiAndBuildResponse(prompt, matchesWithPreds, oddsByMatch, 0,
                quotaRangeMin: 1.50, quotaRangeMax: 3.50);
        }

        // ════════════════════════════════════════════════════════════
        // METODI COMUNI ESTRATTI
        // ════════════════════════════════════════════════════════════

        private async Task<List<MatchPredRow>> LoadMatchesWithPredsAsync()
        {
            var today = DateTime.Today;
            var tomorrow = today.AddDays(1);
            return await (
                from m in _read.Matches
                join lg in _read.Leagues on m.LeagueId equals lg.Id
                join th in _read.Teams on m.HomeId equals th.Id
                join ta in _read.Teams on m.AwayId equals ta.Id
                join c in _read.Set<PredictionDbRow>() on m.Id equals c.MatchId
                where m.Date >= today && m.Date < tomorrow
                   && (m.StatusShort == null || m.StatusShort == "NS")
                orderby m.Date
                select new MatchPredRow
                {
                    MatchId = m.Id,
                    Date = m.Date,
                    HomeName = th.Name ?? "",
                    AwayName = ta.Name ?? "",
                    HomeLogo = th.Logo,
                    AwayLogo = ta.Logo,
                    LeagueName = lg.Name ?? "",
                    LeagueLogo = lg.Logo,
                    LeagueFlag = lg.Flag,
                    CountryCode = lg.CountryCode,
                    Esito = c.Esito,
                    OverUnder = c.OverUnderRange,
                    GgNg = c.GG_NG,
                    Combo = c.ComboFinale,
                    GsCasa = c.GoalSimulatiCasa,
                    GsOspite = c.GoalSimulatiOspite,
                    Over15 = c.Over1_5,
                    Over25 = c.Over2_5,
                    Over35 = c.Over3_5
                }
            ).AsNoTracking().ToListAsync();
        }

        private async Task<Dictionary<long, Dictionary<int, Dictionary<string, double>>>> LoadOddsAsync(
            List<MatchPredRow> matches)
        {
            var idsArray = matches.Select(m => m.MatchId).Distinct().ToArray();
            var betIdsArray = RelevantBetIds;
            var allOdds = await _read.Odds
                .FromSql($"SELECT * FROM odds WHERE id = ANY({idsArray}::bigint[]) AND betid = ANY({betIdsArray}::int[])")
                .AsNoTracking()
                .ToListAsync();

            return allOdds
                .GroupBy(o => o.Id)
                .ToDictionary(
                    g => g.Key,
                    g => g.GroupBy(o => o.Betid)
                          .ToDictionary(
                              bg => bg.Key,
                              bg => bg.ToDictionary(o => o.Value ?? "", o => (double)o.Odd)
                          )
                );
        }

        private string BuildMatchContext(
            List<MatchPredRow> matches,
            Dictionary<long, Dictionary<int, Dictionary<string, double>>> oddsByMatch,
            double minOdd, double maxOdd)
        {
            var sb = new StringBuilder();
            sb.AppendLine("PARTITE DISPONIBILI OGGI CON PRONOSTICI E QUOTE REALI:");
            sb.AppendLine();

            foreach (var m in matches)
            {
                var betMap = oddsByMatch.TryGetValue(m.MatchId, out var bm) ? bm : new();

                betMap.TryGetValue(1, out var o1x2);
                betMap.TryGetValue(5, out var ou);
                betMap.TryGetValue(8, out var ggng);
                betMap.TryGetValue(12, out var dc);

                var available = new List<string>();
                AddIfInRange(available, "1", GetOdd(o1x2, "Home"), minOdd, maxOdd);
                AddIfInRange(available, "X", GetOdd(o1x2, "Draw"), minOdd, maxOdd);
                AddIfInRange(available, "2", GetOdd(o1x2, "Away"), minOdd, maxOdd);
                AddIfInRange(available, "Over 1.5", GetOdd(ou, "Over 1.5"), minOdd, maxOdd);
                AddIfInRange(available, "Over 2.5", GetOdd(ou, "Over 2.5"), minOdd, maxOdd);
                AddIfInRange(available, "Over 3.5", GetOdd(ou, "Over 3.5"), minOdd, maxOdd);
                AddIfInRange(available, "Under 2.5", GetOdd(ou, "Under 2.5"), minOdd, maxOdd);
                AddIfInRange(available, "Under 3.5", GetOdd(ou, "Under 3.5"), minOdd, maxOdd);
                AddIfInRange(available, "GG", GetOdd(ggng, "Yes"), minOdd, maxOdd);
                AddIfInRange(available, "NG", GetOdd(ggng, "No"), minOdd, maxOdd);
                AddIfInRange(available, "1X", GetOdd(dc, "Home/Draw"), minOdd, maxOdd);
                AddIfInRange(available, "X2", GetOdd(dc, "Draw/Away"), minOdd, maxOdd);
                AddIfInRange(available, "12", GetOdd(dc, "Home/Away"), minOdd, maxOdd);

                sb.AppendLine("---");
                sb.AppendLine($"ID={m.MatchId} | {m.Date:HH:mm} | {m.LeagueName}");
                sb.AppendLine($"  {m.HomeName} vs {m.AwayName}");
                sb.AppendLine($"  Pronostico modello: Esito={m.Esito} | {m.OverUnder} | {m.GgNg} | Combo={m.Combo}");
                sb.AppendLine($"  GS={m.GsCasa:0.0}-{m.GsOspite:0.0} | O1.5={(m.Over15 ?? 0):0}% O2.5={(m.Over25 ?? 0):0}% O3.5={(m.Over35 ?? 0):0}%");

                sb.AppendLine(available.Count > 0
                    ? $"  QUOTE REALI (usa SOLO queste): {string.Join(" | ", available)}"
                    : $"  QUOTE REALI: nessuna quota disponibile nel range — ESCLUDI questo match");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private async Task<IActionResult> CallAiAndBuildResponse(
            string prompt,
            List<MatchPredRow> matchesWithPreds,
            Dictionary<long, Dictionary<int, Dictionary<string, double>>> oddsByMatch,
            double quotaTargetForLog,
            double quotaRangeMin = 1.20,
            double quotaRangeMax = 1.45)
        {
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
                    new { role = "system", content = "Sei un analista di scommesse sportive esperto. Rispondi SEMPRE e SOLO con JSON valido, senza markdown, senza backtick, senza testo aggiuntivo prima o dopo il JSON." },
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

            try
            {
                var cleaned = rawContent.Trim().Replace("```json", "").Replace("```", "").Trim();
                using var aiDoc = JsonDocument.Parse(cleaned);
                var picksEl = aiDoc.RootElement.GetProperty("picks");
                var summary = aiDoc.RootElement.TryGetProperty("summary", out var s) ? s.GetString() : "";

                var enrichedPicks = new List<object>();
                double quotaTotale = 1.0;

                foreach (var pick in picksEl.EnumerateArray())
                {
                    var mId = pick.GetProperty("matchId").GetInt64();
                    var match = matchesWithPreds.FirstOrDefault(m => m.MatchId == mId);
                    if (match == null) continue;

                    var pickType = pick.TryGetProperty("pick", out var pk) ? pk.GetString() ?? "" : "";
                    var betMap = oddsByMatch.TryGetValue(mId, out var bm) ? bm : new();
                    var realQuota = ResolveRealQuota(pickType, betMap);
                    var quota = realQuota ?? (pick.TryGetProperty("quotaStimata", out var q) ? q.GetDouble() : 1.0);

                    // Per scalata filtra stretto; per multipla/singola usa il range passato
                    if (quotaRangeMin > 0 && quotaRangeMax > 0)
                        if (quota < quotaRangeMin || quota > quotaRangeMax) continue;

                    var confidenzaAi = pick.TryGetProperty("confidenza", out var cf) ? cf.GetString() ?? "MEDIA" : "MEDIA";
                    var confidenzaFinale = (pickType == "GG" || pickType == "NG") ? "MEDIA" : confidenzaAi;

                    quotaTotale *= quota;

                    enrichedPicks.Add(new
                    {
                        matchId = mId,
                        kickoff = match.Date,
                        homeName = match.HomeName,
                        awayName = match.AwayName,
                        homeLogo = match.HomeLogo,
                        awayLogo = match.AwayLogo,
                        leagueName = match.LeagueName,
                        leagueLogo = match.LeagueLogo,
                        leagueFlag = match.LeagueFlag,
                        countryCode = match.CountryCode,
                        pick = pickType,
                        quotaStimata = Math.Round(quota, 2),
                        confidenza = confidenzaFinale,
                        motivazione = pick.TryGetProperty("motivazione", out var mv) ? mv.GetString() : ""
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
                return Ok(new { picks = new List<object>(), summary = rawContent, generatedAt = DateTime.UtcNow });
            }
        }

        private class MatchPredRow
        {
            public long MatchId { get; set; }
            public DateTime Date { get; set; }
            public string HomeName { get; set; } = "";
            public string AwayName { get; set; } = "";
            public string? HomeLogo { get; set; }
            public string? AwayLogo { get; set; }
            public string LeagueName { get; set; } = "";
            public string? LeagueLogo { get; set; }
            public string? LeagueFlag { get; set; }
            public string? CountryCode { get; set; }
            public string? Esito { get; set; }
            public string? OverUnder { get; set; }
            public string? GgNg { get; set; }
            public string? Combo { get; set; }
            public double GsCasa { get; set; }
            public double GsOspite { get; set; }
            public decimal? Over15 { get; set; }
            public decimal? Over25 { get; set; }
            public decimal? Over35 { get; set; }
        }

        // ════════════════════════════════════════════════════════════
        // HELPERS
        // ════════════════════════════════════════════════════════════

        private static double? GetOdd(Dictionary<string, double>? map, string key)
            => map?.TryGetValue(key, out var v) == true ? v : null;

        private static void AddIfInRange(
            List<string> list, string label, double? odd,
            double min = 1.20, double max = 1.45)
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