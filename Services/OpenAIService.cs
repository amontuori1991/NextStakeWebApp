// Services/OpenAIService.cs
using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace NextStakeWebApp.Services
{
    public class OpenAIService : IOpenAIService
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly OpenAIOptions _opt;
        private readonly ILogger<OpenAIService> _log;

        public OpenAIService(
            IHttpClientFactory httpFactory,
            OpenAIOptions opt,
            ILogger<OpenAIService> log)
        {
            _httpFactory = httpFactory;
            _opt = opt;
            _log = log;
        }

        public Task<string?> AskAsync(string prompt, CancellationToken ct = default)
            => AskInternalAsync(prompt, ct);

        private async Task<string?> AskInternalAsync(string prompt, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_opt.ApiKey))
                throw new InvalidOperationException("OpenAI API key mancante (OPENAI_API_KEY / OpenAI__ApiKey).");

            // Limita prompt eccessivi (riduce il rischio di 429/token overflow)
            var safePrompt = prompt;
            const int maxChars = 4000;
            if (!string.IsNullOrEmpty(safePrompt) && safePrompt.Length > maxChars)
                safePrompt = safePrompt.Substring(0, maxChars) + " …";

            var client = _httpFactory.CreateClient("OpenAI");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _opt.ApiKey);

            var payload = new
            {
                model = _opt.Model, // es. "gpt-4o-mini"
                messages = new object[]
                {
                    new { role = "system", content = "Riscrivi testi per un canale Telegram di analisi calcistiche. Sii chiaro e sintetico." },
                    new { role = "user",   content = safePrompt }
                },
                temperature = 0.6
                // volendo: max_tokens = 300
            };

            const int maxRetries = 4;
            var attempt = 0;

            while (true)
            {
                attempt++;

                // CREA la request ad ogni tentativo (HttpRequestMessage è monouso)
                using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };

                using var res = await client.SendAsync(req, ct);
                var body = await res.Content.ReadAsStringAsync(ct);

                if (res.IsSuccessStatusCode)
                {
                    using var json = JsonDocument.Parse(body);
                    var content = json.RootElement
                                      .GetProperty("choices")[0]
                                      .GetProperty("message")
                                      .GetProperty("content")
                                      .GetString();
                    return content;
                }

                // 429/503 → retry con backoff
                if ((int)res.StatusCode == 429 || (int)res.StatusCode == 503)
                {
                    _log.LogWarning("OpenAI {Status} (tentativo {Attempt}/{Max}). Body: {Body}",
                        (int)res.StatusCode, attempt, maxRetries, body);

                    var waitSeconds = 2.5; // default
                    if (res.Headers.TryGetValues("retry-after", out var vals))
                    {
                        var v = vals.FirstOrDefault();
                        if (double.TryParse(v, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                            waitSeconds = Math.Max(1, parsed);
                    }

                    var delay = TimeSpan.FromSeconds(waitSeconds * Math.Pow(2, attempt - 1));

                    if (attempt < maxRetries)
                    {
                        await Task.Delay(delay, ct);
                        continue; // riprova creando una nuova request
                    }
                }

                // Altri errori o finiti i retry → log + eccezione leggibile
                _log.LogError("OpenAI error {Status}: {Body}", (int)res.StatusCode, body);

                try
                {
                    using var doc = JsonDocument.Parse(body);
                    var msg = doc.RootElement.TryGetProperty("error", out var e)
                        ? e.GetProperty("message").GetString()
                        : null;
                    throw new ApplicationException($"OpenAI: {msg ?? $"HTTP {(int)res.StatusCode}"}");
                }
                catch
                {
                    throw new ApplicationException($"OpenAI HTTP {(int)res.StatusCode}. Vedi log.");
                }
            }
        }
    }
}
