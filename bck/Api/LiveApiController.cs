using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace NextStakeWebApp.bck.Api
{
    [ApiController]
    [Route("api/events")]
    [Authorize(AuthenticationSchemes = "Bearer")]
    public class LiveApiController : ControllerBase
    {
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _http;

        public LiveApiController(IConfiguration config, IHttpClientFactory http)
        {
            _config = config;
            _http = http;
        }

        [HttpGet("live/{matchId}")]
        public async Task<IActionResult> GetLive(long matchId)
        {
            var apiKey = _config["ApiSports:Key"]
                         ?? Environment.GetEnvironmentVariable("ApiSports__Key");

            var client = _http.CreateClient("ApiSports");
            client.DefaultRequestHeaders.Remove("x-apisports-key");
            client.DefaultRequestHeaders.Add("x-apisports-key", apiKey);

            // Chiamate parallele
            var fixtureTask = client.GetStringAsync($"fixtures?id={matchId}");
            var eventsTask = client.GetStringAsync($"fixtures/events?fixture={matchId}");
            var statisticsTask = client.GetStringAsync($"fixtures/statistics?fixture={matchId}");
            var lineupsTask = client.GetStringAsync($"fixtures/lineups?fixture={matchId}");

            await Task.WhenAll(fixtureTask, eventsTask, statisticsTask, lineupsTask);

            using var fixtureDoc = JsonDocument.Parse(fixtureTask.Result);
            using var eventsDoc = JsonDocument.Parse(eventsTask.Result);
            using var statisticsDoc = JsonDocument.Parse(statisticsTask.Result);
            using var lineupsDoc = JsonDocument.Parse(lineupsTask.Result);

            // Fixture
            var response = fixtureDoc.RootElement.GetProperty("response");
            if (response.GetArrayLength() == 0)
                return Ok(new { error = "Match non trovato" });

            var fixture = response[0];
            var fixtureInfo = fixture.GetProperty("fixture");
            var goals = fixture.GetProperty("goals");
            var teams = fixture.GetProperty("teams");

            var status = fixtureInfo.GetProperty("status");
            var statusShort = status.GetProperty("short").GetString();
            var elapsed = status.TryGetProperty("elapsed", out var el) && el.ValueKind != JsonValueKind.Null
                ? el.GetInt32() : (int?)null;

            int? homeGoals = goals.GetProperty("home").ValueKind != JsonValueKind.Null
                ? goals.GetProperty("home").GetInt32() : null;
            int? awayGoals = goals.GetProperty("away").ValueKind != JsonValueKind.Null
                ? goals.GetProperty("away").GetInt32() : null;

            var homeTeam = new
            {
                id = teams.GetProperty("home").GetProperty("id").GetInt32(),
                name = teams.GetProperty("home").GetProperty("name").GetString(),
                logo = teams.GetProperty("home").GetProperty("logo").GetString()
            };
            var awayTeam = new
            {
                id = teams.GetProperty("away").GetProperty("id").GetInt32(),
                name = teams.GetProperty("away").GetProperty("name").GetString(),
                logo = teams.GetProperty("away").GetProperty("logo").GetString()
            };

            // Events
            var events = eventsDoc.RootElement.GetProperty("response")
                .EnumerateArray()
                .Select(e => new
                {
                    time = e.GetProperty("time").GetProperty("elapsed").GetInt32(),
                    teamId = e.GetProperty("team").GetProperty("id").GetInt32(),
                    player = e.GetProperty("player").GetProperty("name").GetString(),
                    assist = e.TryGetProperty("assist", out var a) &&
                             a.GetProperty("name").ValueKind != JsonValueKind.Null
                             ? a.GetProperty("name").GetString() : null,
                    type = e.GetProperty("type").GetString(),
                    detail = e.GetProperty("detail").GetString()
                })
                .ToList();

            // Statistics
            var statistics = statisticsDoc.RootElement.GetProperty("response")
                .EnumerateArray()
                .Select(s => new
                {
                    teamId = s.GetProperty("team").GetProperty("id").GetInt32(),
                    stats = s.GetProperty("statistics").EnumerateArray()
                        .Select(st => new
                        {
                            type = st.GetProperty("type").GetString(),
                            value = st.GetProperty("value").ValueKind != JsonValueKind.Null
                                ? st.GetProperty("value").ToString() : null
                        }).ToList()
                })
                .ToList();

            // Lineups
            var lineups = lineupsDoc.RootElement.GetProperty("response")
                .EnumerateArray()
                .Select(l => new
                {
                    teamId = l.GetProperty("team").GetProperty("id").GetInt32(),
                    teamName = l.GetProperty("team").GetProperty("name").GetString(),
                    formation = l.TryGetProperty("formation", out var f) &&
                                f.ValueKind != JsonValueKind.Null ? f.GetString() : null,
                    startXI = l.GetProperty("startXI").EnumerateArray()
                        .Select(p => new
                        {
                            id = p.GetProperty("player").GetProperty("id").GetInt32(),
                            name = p.GetProperty("player").GetProperty("name").GetString(),
                            number = p.GetProperty("player").GetProperty("number").GetInt32(),
                            pos = p.GetProperty("player").GetProperty("pos").GetString()
                        }).ToList(),
                    substitutes = l.GetProperty("substitutes").EnumerateArray()
                        .Select(p => new
                        {
                            id = p.GetProperty("player").GetProperty("id").GetInt32(),
                            name = p.GetProperty("player").GetProperty("name").GetString(),
                            number = p.GetProperty("player").GetProperty("number").GetInt32(),
                            pos = p.GetProperty("player").GetProperty("pos").GetString()
                        }).ToList()
                })
                .ToList();

            return Ok(new
            {
                matchId,
                statusShort,
                elapsed,
                homeGoals,
                awayGoals,
                homeTeam,
                awayTeam,
                events,
                statistics,
                lineups
            });
        }
    }
}