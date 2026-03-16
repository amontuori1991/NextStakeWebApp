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
    [Route("api/events/live")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class LiveAiController : ControllerBase
    {
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _http;
        private readonly ReadDbContext _read;

        public LiveAiController(IConfiguration config, IHttpClientFactory http, ReadDbContext read)
        {
            _config = config;
            _http = http;
            _read = read;
        }

        [HttpGet("{matchId}/ai")]
        public async Task<IActionResult> GetAiAnalysis(long matchId, [FromQuery] string mode = "brief")
        {
            var isBrief = mode == "brief";

            // ── 1. DATI BASE PARTITA DAL DB ──────────────────────
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
                    season = m.Season,
                    status = m.StatusShort
                }
            ).AsNoTracking().FirstOrDefaultAsync();

            if (match == null)
                return Ok(new { analysis = "Partita non trovata nel database." });

            var homeName = match.homeName ?? "Casa";
            var awayName = match.awayName ?? "Ospite";

            // ── 2. PRONOSTICO DAL DB ─────────────────────────────
            var predText = new StringBuilder();
            try
            {
                var pred = await _read.Set<PredictionDbRow>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.MatchId == matchId);

                if (pred != null)
                {
                    predText.AppendLine($"\nPronostico del sistema (basato su modello predittivo):");
                    predText.AppendLine($"  Esito: {pred.Esito}");
                    predText.AppendLine($"  Over/Under: {pred.OverUnderRange}");
                    predText.AppendLine($"  GG/NG: {pred.GG_NG}");
                    predText.AppendLine($"  Goal simulati: {homeName} {pred.GoalSimulatiCasa} - {pred.GoalSimulatiOspite} {awayName}");
                    predText.AppendLine($"  Over 1.5: {pred.Over1_5:0}% | Over 2.5: {pred.Over2_5:0}% | Over 3.5: {pred.Over3_5:0}%");
                    predText.AppendLine($"  Combo finale: {pred.ComboFinale}");
                }
                else
                {
                    predText.AppendLine("\nPronostico del sistema: non disponibile.");
                }
            }
            catch { predText.AppendLine("\nPronostico del sistema: errore nel recupero."); }

            // ── 3. QUOTE DAL DB ──────────────────────────────────
            var oddsText = new StringBuilder();
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
                    oddsText.AppendLine($"\nQuote 1X2 (media bookmaker): 1={oddHome:0.00} X={oddDraw:0.00} 2={oddAway:0.00}");
                }
                else
                {
                    oddsText.AppendLine("\nQuote 1X2: non disponibili.");
                }
            }
            catch { oddsText.AppendLine("\nQuote: errore nel recupero."); }

            // ── 4. FORMA HOME DAL DB ─────────────────────────────
            var homeFormText = new StringBuilder();
            try
            {
                var homeForm = await GetFormAsync(match.homeId, matchId);
                if (homeForm.Any())
                {
                    homeFormText.AppendLine($"\nUltimi 5 risultati {homeName}:");
                    foreach (var f in homeForm)
                        homeFormText.AppendLine($"  {f.date:dd/MM} vs {f.opponent} ({(f.isHome ? "Casa" : "Trasferta")}) {f.score} → {f.result}");
                }
                else
                {
                    homeFormText.AppendLine($"\nForma {homeName}: dati non disponibili.");
                }
            }
            catch { homeFormText.AppendLine($"\nForma {homeName}: errore nel recupero."); }

            // ── 5. FORMA AWAY DAL DB ─────────────────────────────
            var awayFormText = new StringBuilder();
            try
            {
                var awayForm = await GetFormAsync(match.awayId, matchId);
                if (awayForm.Any())
                {
                    awayFormText.AppendLine($"\nUltimi 5 risultati {awayName}:");
                    foreach (var f in awayForm)
                        awayFormText.AppendLine($"  {f.date:dd/MM} vs {f.opponent} ({(f.isHome ? "Casa" : "Trasferta")}) {f.score} → {f.result}");
                }
                else
                {
                    awayFormText.AppendLine($"\nForma {awayName}: dati non disponibili.");
                }
            }
            catch { awayFormText.AppendLine($"\nForma {awayName}: errore nel recupero."); }

            // ── 6. DATI LIVE DA API-SPORT ────────────────────────
            var apiKey = _config["ApiSports:Key"]
                         ?? Environment.GetEnvironmentVariable("ApiSports__Key");

            var apiClient = _http.CreateClient("ApiSports");
            apiClient.DefaultRequestHeaders.Remove("x-apisports-key");
            apiClient.DefaultRequestHeaders.Add("x-apisports-key", apiKey);

            var fixtureTask = apiClient.GetStringAsync($"fixtures?ids={matchId}");
            var eventsTask = apiClient.GetStringAsync($"fixtures/events?fixture={matchId}");
            var statisticsTask = apiClient.GetStringAsync($"fixtures/statistics?fixture={matchId}");
            await Task.WhenAll(fixtureTask, eventsTask, statisticsTask);

            using var fixtureDoc = JsonDocument.Parse(fixtureTask.Result);
            using var eventsDoc = JsonDocument.Parse(eventsTask.Result);
            using var statisticsDoc = JsonDocument.Parse(statisticsTask.Result);

            int homeGoals = 0;
            int awayGoals = 0;
            int elapsed = 0;
            string statusShort = match.status ?? "NS";

            var fixtureArr = fixtureDoc.RootElement.GetProperty("response");
            if (fixtureArr.GetArrayLength() > 0)
            {
                var fix = fixtureArr[0];
                var goals = fix.GetProperty("goals");
                var status = fix.GetProperty("fixture").GetProperty("status");
                homeGoals = goals.GetProperty("home").ValueKind != JsonValueKind.Null ? goals.GetProperty("home").GetInt32() : 0;
                awayGoals = goals.GetProperty("away").ValueKind != JsonValueKind.Null ? goals.GetProperty("away").GetInt32() : 0;
                elapsed = status.TryGetProperty("elapsed", out var el) && el.ValueKind != JsonValueKind.Null ? el.GetInt32() : 0;
                statusShort = status.GetProperty("short").GetString() ?? statusShort;
            }

            // Statistiche live
            var statsText = new StringBuilder();
            var statsArr = statisticsDoc.RootElement.GetProperty("response");
            if (statsArr.GetArrayLength() > 0)
            {
                statsText.AppendLine("\nStatistiche live:");
                foreach (var teamStats in statsArr.EnumerateArray())
                {
                    var teamName = teamStats.GetProperty("team").GetProperty("name").GetString();
                    statsText.AppendLine($"\n  {teamName}:");
                    foreach (var stat in teamStats.GetProperty("statistics").EnumerateArray())
                    {
                        var type = stat.GetProperty("type").GetString();
                        var value = stat.GetProperty("value").ValueKind != JsonValueKind.Null
                            ? stat.GetProperty("value").ToString() : "0";
                        statsText.AppendLine($"    {type}: {value}");
                    }
                }
            }
            else
            {
                statsText.AppendLine("\nStatistiche live: non ancora disponibili.");
            }

            // Eventi live
            var eventsText = new StringBuilder();
            var eventsArr = eventsDoc.RootElement.GetProperty("response");
            if (eventsArr.GetArrayLength() > 0)
            {
                eventsText.AppendLine("\nEventi partita:");
                foreach (var ev in eventsArr.EnumerateArray())
                {
                    var time = ev.GetProperty("time").GetProperty("elapsed").GetInt32();
                    var team = ev.GetProperty("team").GetProperty("name").GetString();
                    var player = ev.GetProperty("player").GetProperty("name").GetString();
                    var type = ev.GetProperty("type").GetString();
                    var detail = ev.GetProperty("detail").GetString();
                    eventsText.AppendLine($"  {time}' [{team}] {player} - {type} ({detail})");
                }
            }
            else
            {
                eventsText.AppendLine("\nEventi: nessun evento registrato.");
            }

            // ── 7. PROMPT ────────────────────────────────────────
            const string dataInstruction = """
                REGOLA FONDAMENTALE: Usa ESCLUSIVAMENTE i dati forniti sopra.
                NON aggiungere informazioni, statistiche o notizie non presenti.
                Se una sezione manca di dati scrivi esplicitamente "Dati non disponibili".
                """;

            var prompt = isBrief
                ? $"""
                   Sei un analista sportivo. Basandoti SOLO sui dati forniti, dai un parere BREVISSIMO (max 3 righe).

                   Partita: {homeName} vs {awayName}
                   Minuto: {elapsed}' ({statusShort})
                   Risultato attuale: {homeGoals} - {awayGoals}
                   {oddsText}
                   {predText}
                   {statsText}
                   {eventsText}
                   {homeFormText}
                   {awayFormText}

                   {dataInstruction}
                   Rispondi in italiano, massimo 3 righe, diretto e conciso.
                   """
                : $"""
                   Sei un analista sportivo. Basandoti SOLO sui dati forniti, analizza questa partita.

                   Partita: {homeName} vs {awayName}
                   Minuto: {elapsed}' ({statusShort})
                   Risultato attuale: {homeGoals} - {awayGoals}
                   {oddsText}
                   {predText}
                   {statsText}
                   {eventsText}
                   {homeFormText}
                   {awayFormText}

                   {dataInstruction}

                   Fornisci in italiano:
                   1. Andamento partita (solo se ci sono statistiche live o eventi)
                   2. Pronostico finale (coerente con i dati del sistema)
                   3. Suggerimento scommessa live (basato su quote e pronostici forniti)
                   4. Livello di confidenza (basso/medio/alto) con motivazione basata sui dati

                   Non aggiungere informazioni esterne ai dati forniti.
                   """;

            // ── 8. CHIAMATA OPENAI ───────────────────────────────
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
                    new { role = "system", content = "Sei un analista sportivo professionale. Rispondi SOLO in italiano. Usa esclusivamente i dati forniti dall'utente, senza aggiungere informazioni esterne." },
                    new { role = "user", content = prompt }
                },
                max_tokens = isBrief ? 200 : 600,
                temperature = 0.3
            });

            var aiResponse = await openAiClient.PostAsync(
                "chat/completions",
                new StringContent(requestBody, Encoding.UTF8, "application/json"));

            if (!aiResponse.IsSuccessStatusCode)
                return Ok(new { analysis = "Servizio AI temporaneamente non disponibile." });

            var responseJson = await aiResponse.Content.ReadAsStringAsync();
            using var responseDoc = JsonDocument.Parse(responseJson);

            var analysis = responseDoc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return Ok(new { analysis, mode });
        }

        // ── Helper forma ─────────────────────────────────────────
        private async Task<List<(DateTime date, string opponent, bool isHome, string score, string result)>> GetFormAsync(int teamId, long excludeMatchId)
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
                    HomeGoal = m.HomeGoal,
                    AwayGoal = m.AwayGoal
                }
            ).AsNoTracking().Take(5).ToListAsync();

            return recent.Select(m =>
            {
                var scored = (m.IsHome ? m.HomeGoal : m.AwayGoal) ?? 0;
                var conceded = (m.IsHome ? m.AwayGoal : m.HomeGoal) ?? 0;
                var result = scored > conceded ? "W" : scored < conceded ? "L" : "D";
                return (m.Date, m.Opponent ?? "", m.IsHome, $"{m.HomeGoal}-{m.AwayGoal}", result);
            }).ToList();
        }
    }
}