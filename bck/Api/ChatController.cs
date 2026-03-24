using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;
using System.Text;
using System.Text.Json;

namespace NextStakeWebApp.bck.Api
{
    [ApiController]
    [Route("api/match")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class ChatController : ControllerBase
    {
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _http;
        private readonly ReadDbContext _read;

        public ChatController(IConfiguration config, IHttpClientFactory http, ReadDbContext read)
        {
            _config = config;
            _http = http;
            _read = read;
        }

        // ════════════════════════════════════════════════════════════
        // POST /api/match/{matchId}/chat
        // Body: { "messages": [ { "role": "user"|"assistant", "content": "..." } ] }
        // ════════════════════════════════════════════════════════════
        [HttpPost("{matchId}/chat")]
        public async Task<IActionResult> Chat(long matchId, [FromBody] ChatRequest request)
        {
            if (request?.Messages == null || request.Messages.Count == 0)
                return BadRequest(new { error = "Nessun messaggio fornito." });

            // ── 1. DATI BASE PARTITA ─────────────────────────────
            var match = await (
                from m in _read.Matches
                join lg in _read.Leagues on m.LeagueId equals lg.Id
                join th in _read.Teams on m.HomeId equals th.Id
                join ta in _read.Teams on m.AwayId equals ta.Id
                where m.Id == matchId
                select new
                {
                    homeId = m.HomeId,
                    awayId = m.AwayId,
                    homeName = th.Name,
                    awayName = ta.Name,
                    leagueId = m.LeagueId,
                    leagueName = lg.Name,
                    countryName = lg.CountryName,
                    season = m.Season,
                    status = m.StatusShort,
                    date = m.Date,
                    homeGoal = m.HomeGoal,
                    awayGoal = m.AwayGoal
                }
            ).AsNoTracking().FirstOrDefaultAsync();

            if (match == null)
                return Ok(new { reply = "Partita non trovata nel database." });

            var homeName = match.homeName ?? "Casa";
            var awayName = match.awayName ?? "Ospite";

            // ── 2. PRONOSTICO ────────────────────────────────────
            var predCtx = new StringBuilder();
            try
            {
                var pred = await _read.Set<PredictionDbRow>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.MatchId == matchId);

                if (pred != null)
                {
                    predCtx.AppendLine("PRONOSTICO DEL MODELLO:");
                    predCtx.AppendLine($"  Esito previsto: {pred.Esito}");
                    predCtx.AppendLine($"  Over/Under: {pred.OverUnderRange}");
                    predCtx.AppendLine($"  GG/NG: {pred.GG_NG}");
                    predCtx.AppendLine($"  Goal simulati: {homeName} {pred.GoalSimulatiCasa:0.0} - {pred.GoalSimulatiOspite:0.0} {awayName}");
                    predCtx.AppendLine($"  Over 1.5: {pred.Over1_5:0}% | Over 2.5: {pred.Over2_5:0}% | Over 3.5: {pred.Over3_5:0}%");
                    predCtx.AppendLine($"  Combo consigliata: {pred.ComboFinale}");
                }
            }
            catch { predCtx.AppendLine("Pronostico: non disponibile."); }

            // ── 3. QUOTE ─────────────────────────────────────────
            var oddsCtx = new StringBuilder();
            try
            {
                var q1x2 = await _read.Odds
                    .Where(o => o.Id == matchId && o.Betid == 1)
                    .AsNoTracking()
                    .ToListAsync();

                if (q1x2.Any())
                {
                    var oddHome = q1x2.FirstOrDefault(o => o.Value == "Home")?.Odd;
                    var oddDraw = q1x2.FirstOrDefault(o => o.Value == "Draw")?.Odd;
                    var oddAway = q1x2.FirstOrDefault(o => o.Value == "Away")?.Odd;
                    oddsCtx.AppendLine($"QUOTE 1X2 (media bookmaker): 1={oddHome:0.00}  X={oddDraw:0.00}  2={oddAway:0.00}");
                }
            }
            catch { oddsCtx.AppendLine("Quote: non disponibili."); }

            // ── 4. FORMA HOME ────────────────────────────────────
            var homeFormCtx = new StringBuilder();
            try
            {
                var form = await GetFormAsync(match.homeId, matchId);
                homeFormCtx.AppendLine($"ULTIMI 5 RISULTATI {homeName.ToUpper()}:");
                foreach (var f in form)
                    homeFormCtx.AppendLine($"  {f.date:dd/MM} vs {f.opponent} ({(f.isHome ? "Casa" : "Trasferta")}) {f.score} → {f.result}");
            }
            catch { homeFormCtx.AppendLine($"Forma {homeName}: non disponibile."); }

            // ── 5. FORMA AWAY ────────────────────────────────────
            var awayFormCtx = new StringBuilder();
            try
            {
                var form = await GetFormAsync(match.awayId, matchId);
                awayFormCtx.AppendLine($"ULTIMI 5 RISULTATI {awayName.ToUpper()}:");
                foreach (var f in form)
                    awayFormCtx.AppendLine($"  {f.date:dd/MM} vs {f.opponent} ({(f.isHome ? "Casa" : "Trasferta")}) {f.score} → {f.result}");
            }
            catch { awayFormCtx.AppendLine($"Forma {awayName}: non disponibile."); }

            // ── 6. STATO LIVE (se partita in corso) ─────────────
            var liveCtx = new StringBuilder();
            if (match.status is "1H" or "2H" or "HT" or "ET" or "P")
            {
                liveCtx.AppendLine($"STATO LIVE: {match.status}");
                liveCtx.AppendLine($"Risultato attuale: {homeName} {match.homeGoal ?? 0} - {match.awayGoal ?? 0} {awayName}");
            }
            else if (match.status is "FT" or "AET" or "PEN")
            {
                liveCtx.AppendLine($"PARTITA TERMINATA ({match.status})");
                liveCtx.AppendLine($"Risultato finale: {homeName} {match.homeGoal ?? 0} - {match.awayGoal ?? 0} {awayName}");
            }
            else
            {
                liveCtx.AppendLine($"PARTITA NON ANCORA INIZIATA");
                liveCtx.AppendLine($"Kickoff: {match.date:dd/MM/yyyy HH:mm}");
            }

            // ── 7. COSTRUZIONE SYSTEM MESSAGE ───────────────────
            var systemMessage = $"""
                Sei un esperto analista sportivo di calcio integrato nell'app NextStake.
                Rispondi SEMPRE in italiano, in modo diretto, preciso e motivato.
                Usa un tono professionale ma accessibile — come un consulente scommesse esperto.
                Non inventare dati non presenti nel contesto. Se non hai un'informazione, dillo chiaramente.
                Risposte concise: massimo 4-5 righe per risposta breve, 8-10 per analisi approfondita.
                Non usare elenchi numerati — scrivi in prosa fluida.

                ═══ CONTESTO PARTITA ═══
                Competizione: {match.countryName} — {match.leagueName}
                Partita: {homeName} vs {awayName}

                {liveCtx}

                {oddsCtx}

                {predCtx}

                {homeFormCtx}

                {awayFormCtx}

                ═══ REGOLA FONDAMENTALE ═══
                Usa ESCLUSIVAMENTE i dati forniti sopra come base del ragionamento.
                Puoi usare la tua conoscenza generale del calcio per contestualizzare,
                ma non inventare statistiche o risultati specifici non presenti.
                """;

            // ── 8. COSTRUZIONE MESSAGES PER OPENAI ──────────────
            // System message + history completa dall'utente
            var openAiMessages = new List<object>
            {
                new { role = "system", content = systemMessage }
            };

            // Aggiungi la history (max ultimi 20 messaggi per non sforare il context)
            var history = request.Messages.TakeLast(20).ToList();
            foreach (var msg in history)
            {
                if (msg.Role is "user" or "assistant")
                    openAiMessages.Add(new { role = msg.Role, content = msg.Content });
            }

            // ── 9. CHIAMATA OPENAI ───────────────────────────────
            var openAiKey = _config["OpenAI:ApiKey"]
                ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            var openAiModel = _config["OpenAI:Model"] ?? "gpt-4o-mini";

            var openAiClient = _http.CreateClient("OpenAI");
            openAiClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {openAiKey}");

            var requestBody = JsonSerializer.Serialize(new
            {
                model = openAiModel,
                messages = openAiMessages,
                max_tokens = 400,
                temperature = 0.6
            });

            var aiResponse = await openAiClient.PostAsync(
                "chat/completions",
                new StringContent(requestBody, Encoding.UTF8, "application/json"));

            if (!aiResponse.IsSuccessStatusCode)
                return Ok(new { reply = "Servizio AI temporaneamente non disponibile." });

            var responseJson = await aiResponse.Content.ReadAsStringAsync();
            using var responseDoc = JsonDocument.Parse(responseJson);

            var reply = responseDoc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return Ok(new { reply });
        }

        // ════════════════════════════════════════════════════════════
        // Helper forma squadra
        // ════════════════════════════════════════════════════════════
        private async Task<List<(DateTime date, string opponent, bool isHome, string score, string result)>>
            GetFormAsync(int teamId, long excludeMatchId)
        {
            var recent = await (
                from m in _read.Matches
                join th in _read.Teams on m.HomeId equals th.Id
                join ta in _read.Teams on m.AwayId equals ta.Id
                where (m.HomeId == teamId || m.AwayId == teamId)
                   && m.Id != excludeMatchId
                   && m.HomeGoal != null
                   && m.AwayGoal != null
                   && m.StatusShort == "FT"
                orderby m.Date descending
                select new
                {
                    m.Date,
                    IsHome = m.HomeId == teamId,
                    Opponent = m.HomeId == teamId ? ta.Name : th.Name,
                    m.HomeGoal,
                    m.AwayGoal
                }
            ).AsNoTracking().Take(5).ToListAsync();

            return recent.Select(m =>
            {
                var scored = (m.IsHome ? m.HomeGoal : m.AwayGoal) ?? 0;
                var conceded = (m.IsHome ? m.AwayGoal : m.HomeGoal) ?? 0;
                var result = scored > conceded ? "V" : scored < conceded ? "P" : "N";
                return (m.Date, m.Opponent ?? "", m.IsHome, $"{m.HomeGoal}-{m.AwayGoal}", result);
            }).ToList();
        }
    }

    // ── DTO Request ──────────────────────────────────────────────────
    public class ChatRequest
    {
        public List<ChatMessage> Messages { get; set; } = new();
    }

    public class ChatMessage
    {
        public string Role { get; set; } = "user";
        public string Content { get; set; } = "";
    }
}