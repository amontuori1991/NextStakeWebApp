using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace NextStakeWebApp.bck.Api
{
    [ApiController]
    [Route("api/events")]
    [Authorize(AuthenticationSchemes = "Bearer")]
    public class ScoresApiController : ControllerBase
    {
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _http;

        public ScoresApiController(IConfiguration config, IHttpClientFactory http)
        {
            _config = config;
            _http = http;
        }

        [HttpGet("scores")]
        public async Task<IActionResult> GetScores([FromQuery] string ids, [FromQuery] string? d = null)
        {
            if (string.IsNullOrWhiteSpace(ids))
                return Ok(new List<object>());

            var idList = ids.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrEmpty(x))
                .ToList();

            if (idList.Count == 0)
                return Ok(new List<object>());

            var apiKey = _config["ApiSports:Key"]
                         ?? Environment.GetEnvironmentVariable("ApiSports__Key");

            var client = _http.CreateClient("ApiSports");
            client.DefaultRequestHeaders.Remove("x-apisports-key");
            client.DefaultRequestHeaders.Add("x-apisports-key", apiKey);

            // Dividi in gruppi da 20 e chiama in parallelo
            var chunks = idList
                .Select((id, i) => new { id, i })
                .GroupBy(x => x.i / 20)
                .Select(g => g.Select(x => x.id).ToList())
                .ToList();

            var tasks = chunks.Select(async chunk =>
            {
                var idsParam = string.Join("-", chunk);
                var json = await client.GetStringAsync($"fixtures?ids={idsParam}");
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("response")
                    .EnumerateArray()
                    .Select(f =>
                    {
                        var fixture = f.GetProperty("fixture");
                        var goals = f.GetProperty("goals");
                        var status = fixture.GetProperty("status");

                        return new
                        {
                            matchId = fixture.GetProperty("id").GetInt64(),
                            statusShort = status.GetProperty("short").GetString(),
                            elapsed = status.TryGetProperty("elapsed", out var el) &&
                                          el.ValueKind != JsonValueKind.Null
                                          ? el.GetInt32() : (int?)null,
                            homeGoals = goals.GetProperty("home").ValueKind != JsonValueKind.Null
                                          ? goals.GetProperty("home").GetInt32() : (int?)null,
                            awayGoals = goals.GetProperty("away").ValueKind != JsonValueKind.Null
                                          ? goals.GetProperty("away").GetInt32() : (int?)null
                        };
                    })
                    .ToList();
            });

            var results = await Task.WhenAll(tasks);
            var flat = results.SelectMany(r => r).ToList();

            return Ok(flat);
        }
    }
}