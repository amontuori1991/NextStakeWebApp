using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NextStakeWebApp.Services
{
    // Opzioni semplici usate in Program.cs (se l'hai già altrove, non duplicarle)
    public class OpenAIOptions
    {
        public string? ApiKey { get; set; }
        public string Model { get; set; } = "gpt-4o-mini";
    }

    // NON ridefinire IOpenAIService qui se esiste già in un altro file!
    // public interface IOpenAIService { Task<string?> AskAsync(string prompt); }

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

        // === Firma ESATTA richiesta dall'interfaccia ===
        public Task<string?> AskAsync(string prompt)
            => AskInternalAsync(prompt, CancellationToken.None);

        // Se ti serve il CT internamente, tieni una versione privata
        private async Task<string?> AskInternalAsync(string prompt, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_opt.ApiKey))
                throw new InvalidOperationException("OpenAI API key mancante (OPENAI_API_KEY).");

            var client = _httpFactory.CreateClient("OpenAI");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _opt.ApiKey);

            var payload = new
            {
                model = _opt.Model,
                messages = new object[]
                {
                    new { role = "system", content = "Riscrivi testi per un canale Telegram di analisi calcistiche. Sii chiaro e sintetico." },
                    new { role = "user",   content = prompt }
                },
                temperature = 0.6
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };

            using var res = await client.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
            {
                _log.LogError("OpenAI error {Status}: {Body}", (int)res.StatusCode, body);

                try
                {
                    using var doc = JsonDocument.Parse(body);
                    var msg = doc.RootElement.GetProperty("error").GetProperty("message").GetString();
                    throw new ApplicationException($"OpenAI: {msg}");
                }
                catch
                {
                    throw new ApplicationException($"OpenAI HTTP {(int)res.StatusCode}. Vedi log.");
                }
            }

            using var json = JsonDocument.Parse(body);
            var content = json.RootElement
                              .GetProperty("choices")[0]
                              .GetProperty("message")
                              .GetProperty("content")
                              .GetString();

            return content;
        }
    }
}
