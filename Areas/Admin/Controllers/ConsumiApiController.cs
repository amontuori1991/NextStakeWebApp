using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;

namespace NextStakeWebApp.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Route("admin/api/consumi")]
    public class ConsumiApiController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly IHttpClientFactory _httpFactory;
        private readonly IConfiguration _config;

        public ConsumiApiController(
            ApplicationDbContext db,
            IHttpClientFactory httpFactory,
            IConfiguration config)
        {
            _db = db;
            _httpFactory = httpFactory;
            _config = config;
        }

        /// <summary>
        /// API per la pagina /Admin/ConsumiApi (grafico + tabella).
        /// Risponde a: GET /admin/api/consumi
        /// </summary>
        [HttpGet("")]
        public async Task<IActionResult> GetConsumi()
        {
            // Leggi ultimi 60 record dalla CallCounter
            var records = await _db.CallCounter
                .OrderByDescending(c => c.Date)
                .Take(60)
                .ToListAsync();

            // Giorno più recente (se esiste) - Date è DateTime
            DateTime? lastDate = records.Any()
                ? records.Max(c => c.Date)
                : (DateTime?)null;

            int todayTotal = 0;
            object[] todayUsage = Array.Empty<object>();

            if (lastDate.HasValue)
            {
                // Se nel DB hai solo la data (senza ora) puoi anche usare ==,
                // con l'ora uso .Date per sicurezza
                var todayRecords = records
                    .Where(c => c.Date.Date == lastDate.Value.Date)
                    .ToList();

                todayTotal = todayRecords.Sum(c => c.Counter);

                todayUsage = todayRecords
                    .GroupBy(c => c.Origin)
                    .Select(g => new
                    {
                        origin = g.Key,
                        counter = g.Sum(x => x.Counter)
                    })
                    .ToArray();
            }

            // Tetto giornaliero (config o 0; lato JS usiamo DEFAULT_DAILY_CAP = 7500 se è 0)
            int dailyCap = 0;
            var capFromConfig = _config["ApiSports:DailyCap"];
            if (int.TryParse(capFromConfig, out var capParsed) && capParsed > 0)
            {
                dailyCap = capParsed;
            }

            var payload = new
            {
                dailyCap,
                todayTotal,
                todayUsage,
                records = records.Select(r => new
                {
                    // r.Date è già DateTime → ok per JS
                    date = r.Date,
                    r.Counter,
                    r.Origin
                })
            };

            return Json(payload);
        }

        /// <summary>
        /// Status reale del provider (API-FOOTBALL /status).
        /// Risponde a: GET /admin/api/consumi/provider-status
        /// </summary>
        [HttpGet("provider-status")]
        public async Task<IActionResult> GetProviderStatus()
        {
            var apiKey =
                _config["ApiSports:Key"] ??
                Environment.GetEnvironmentVariable("ApiSports__Key");

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return Json(new
                {
                    success = false,
                    message = "API key non configurata"
                });
            }

            var client = _httpFactory.CreateClient("ApiSports");

            // BaseAddress già = https://v3.football.api-sports.io/
            var req = new HttpRequestMessage(HttpMethod.Get, "status");
            req.Headers.Add("x-apisports-key", apiKey);

            using var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                return Json(new
                {
                    success = false,
                    message = $"Status provider non disponibile (HTTP {(int)resp.StatusCode})"
                });
            }

            var json = await resp.Content.ReadAsStringAsync();

            int? requestsCurrent = null;
            int? requestsLimitDay = null;
            string? plan = null;
            string? accountEmail = null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // ⚠️ response è un oggetto, NON un array
                if (root.TryGetProperty("response", out var respObj) &&
                    respObj.ValueKind == JsonValueKind.Object)
                {
                    // requests è a questo livello: response.requests
                    if (respObj.TryGetProperty("requests", out var reqObj) &&
                        reqObj.ValueKind == JsonValueKind.Object)
                    {
                        if (reqObj.TryGetProperty("current", out var curEl) &&
                            curEl.TryGetInt32(out var cur))
                        {
                            requestsCurrent = cur;
                        }

                        if (reqObj.TryGetProperty("limit_day", out var limEl) &&
                            limEl.TryGetInt32(out var lim))
                        {
                            requestsLimitDay = lim;
                        }
                    }

                    // subscription.plan
                    if (respObj.TryGetProperty("subscription", out var sub) &&
                        sub.ValueKind == JsonValueKind.Object)
                    {
                        if (sub.TryGetProperty("plan", out var planEl) &&
                            planEl.ValueKind == JsonValueKind.String)
                        {
                            plan = planEl.GetString();
                        }
                    }

                    // account.email
                    if (respObj.TryGetProperty("account", out var acc) &&
                        acc.ValueKind == JsonValueKind.Object)
                    {
                        if (acc.TryGetProperty("email", out var emailEl) &&
                            emailEl.ValueKind == JsonValueKind.String)
                        {
                            accountEmail = emailEl.GetString();
                        }
                    }
                }
            }
            catch
            {
                // se cambiano formato, non esplodiamo
            }

            return Json(new
            {
                success = true,
                requestsCurrent = requestsCurrent ?? 0,
                requestsLimitDay = requestsLimitDay ?? 0,
                plan,
                account = accountEmail   // se non vuoi la mail in UI puoi ometterlo qui
            });
        }

    }
}
