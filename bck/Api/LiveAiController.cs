using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;

namespace NextStakeWebApp.bck.Api
{
    [ApiController]
    [Route("api/events/live")]
    [Authorize(AuthenticationSchemes = "Bearer")]
    public class LiveAiController : ControllerBase
    {
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _http;

        public LiveAiController(IConfiguration config, IHttpClientFactory http)
        {
            _config = config;
            _http = http;
        }

        [HttpGet("{matchId}/ai")]
        public async Task<IActionResult> GetAiAnalysis(long matchId, [FromQuery] string mode = "brief")
        {
            var apiKey = _config["ApiSports:Key"]
                         ?? Environment.GetEnvironmentVariable("ApiSports__Key");

            var apiClient = _http.CreateClient("ApiSports");
            apiClient.DefaultRequestHeaders.Remove("x-apisports-key");
            apiClient.DefaultRequestHeaders.Add("x-apisports-key", apiKey);

            // Dati live da API-Sport in parallelo
            var fixtureTask = apiClient.GetStringAsync($"fixtures?ids={matchId}");
            var eventsTask = apiClient.GetStringAsync($"fixtures/events?fixture={matchId}");
            var statisticsTask = apiClient.GetStringAsync($"fixtures/statistics?fixture={matchId}");

            await Task.WhenAll(fixtureTask, eventsTask, statisticsTask);

            using var fixtureDoc = JsonDocument.Parse(fixtureTask.Result);
            using var eventsDoc = JsonDocument.Parse(eventsTask.Result);
            using var statisticsDoc = JsonDocument.Parse(statisticsTask.Result);

            var fixtureArr = fixtureDoc.RootElement.GetProperty("response");
            if (fixtureArr.GetArrayLength() == 0)
                return Ok(new { analysis = "Dati partita non disponibili." });

            var fixture = fixtureArr[0];
            var teams = fixture.GetProperty("teams");
            var goals = fixture.GetProperty("goals");
            var status = fixture.GetProperty("fixture").GetProperty("status");

            var homeName = teams.GetProperty("home").GetProperty("name").GetString();
            var awayName = teams.GetProperty("away").GetProperty("name").GetString();
            var homeGoals = goals.GetProperty("home").ValueKind != JsonValueKind.Null ? goals.GetProperty("home").GetInt32() : 0;
            var awayGoals = goals.GetProperty("away").ValueKind != JsonValueKind.Null ? goals.GetProperty("away").GetInt32() : 0;
            var elapsed = status.TryGetProperty("elapsed", out var el) && el.ValueKind != JsonValueKind.Null ? el.GetInt32() : 0;
            var statusShort = status.GetProperty("short").GetString();

            // Statistiche
            var statsArr = statisticsDoc.RootElement.GetProperty("response");
            var statsText = new StringBuilder();
            foreach (var teamStats in statsArr.EnumerateArray())
            {
                var teamName = teamStats.GetProperty("team").GetProperty("name").GetString();
                statsText.AppendLine($"\n{teamName}:");
                foreach (var stat in teamStats.GetProperty("statistics").EnumerateArray())
                {
                    var type = stat.GetProperty("type").GetString();
                    var value = stat.GetProperty("value").ValueKind != JsonValueKind.Null
                        ? stat.GetProperty("value").ToString() : "0";
                    statsText.AppendLine($"  {type}: {value}");
                }
            }

            // Eventi (gol, cartellini)
            var eventsArr = eventsDoc.RootElement.GetProperty("response");
            var eventsText = new StringBuilder();
            foreach (var ev in eventsArr.EnumerateArray())
            {
                var time = ev.GetProperty("time").GetProperty("elapsed").GetInt32();
                var team = ev.GetProperty("team").GetProperty("name").GetString();
                var player = ev.GetProperty("player").GetProperty("name").GetString();
                var type = ev.GetProperty("type").GetString();
                var detail = ev.GetProperty("detail").GetString();
                eventsText.AppendLine($"  {time}' [{team}] {player} - {type} ({detail})");
            }

            // Prompt
            var isBrief = mode == "brief";
            var prompt = isBrief
                ? $"""
                   Sei un esperto di calcio. Analizza questa partita in corso e dai un parere BREVISSIMO (max 3 righe) su come potrebbe finire e se vale la pena scommettere.

                   Partita: {homeName} vs {awayName}
                   Minuto: {elapsed}' ({statusShort})
                   Risultato: {homeGoals} - {awayGoals}

                   Statistiche:
                   {statsText}

                   Eventi:
                   {eventsText}

                   Rispondi in italiano, massimo 3 righe, diretto e conciso.
                   """
                : $"""
                   Sei un esperto di calcio e scommesse. Analizza in dettaglio questa partita in corso.

                   Partita: {homeName} vs {awayName}
                   Minuto: {elapsed}' ({statusShort})
                   Risultato: {homeGoals} - {awayGoals}

                   Statistiche:
                   {statsText}

                   Eventi:
                   {eventsText}

                   Fornisci in italiano:
                   1. Analisi del gioco (chi domina, chi soffre)
                   2. Pronostico finale (risultato più probabile)
                   3. Suggerimento scommessa live (es. prossimo gol, over/under, ecc.)
                   4. Livello di confidenza (basso/medio/alto)

                   Sii dettagliato ma chiaro.
                   """;

            // Chiamata OpenAI
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
                    new { role = "user", content = prompt }
                },
                max_tokens = isBrief ? 150 : 500,
                temperature = 0.7
            });

            var response = await openAiClient.PostAsync(
                "chat/completions",
                new StringContent(requestBody, Encoding.UTF8, "application/json"));

            if (!response.IsSuccessStatusCode)
                return Ok(new { analysis = "Servizio AI temporaneamente non disponibile." });

            var responseJson = await response.Content.ReadAsStringAsync();
            using var responseDoc = JsonDocument.Parse(responseJson);

            var analysis = responseDoc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return Ok(new { analysis, mode });
        }
    }
}