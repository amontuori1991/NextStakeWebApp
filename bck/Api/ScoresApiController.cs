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
                .ToHashSet();

            if (idList.Count == 0)
                return Ok(new List<object>());

            var date = d ?? DateTime.Now.ToString("yyyy-MM-dd");

            var apiKey = _config["ApiSports:Key"]
                         ?? Environment.GetEnvironmentVariable("ApiSports__Key");

            var client = _http.CreateClient("ApiSports");
            client.DefaultRequestHeaders.Remove("x-apisports-key");
            client.DefaultRequestHeaders.Add("x-apisports-key", apiKey);

            var json = await client.GetStringAsync($"fixtures?date={date}");
            using var doc = JsonDocument.Parse(json);

            var response = doc.RootElement.GetProperty("response");

            var result = response.EnumerateArray()
                .Where(f =>
                {
                    var fixtureId = f.GetProperty("fixture").GetProperty("id").GetInt64().ToString();
                    return idList.Contains(fixtureId);
                })
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

            return Ok(result);
        }
    }
}