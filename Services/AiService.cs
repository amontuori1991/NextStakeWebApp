using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

public class AiService : IAiService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly OpenAIOptions _options;
    private readonly ILogger<AiService> _logger;

    public AiService(IHttpClientFactory httpFactory, OpenAIOptions options, ILogger<AiService> logger)
    {
        _httpFactory = httpFactory;
        _options = options;
        _logger = logger;
    }

    public async Task<string> BuildPredictionAsync(long matchId, string home, string away, string league, string? extraContext, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("OpenAI API key mancante. Imposta OPENAI_API_KEY su Render.");

        var client = _httpFactory.CreateClient("OpenAI");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        // Prompt minimale – adatta liberamente
        var prompt = $@"Sei un analista calcistico. Fornisci un pronostico sintetico e motivato per:
MatchId: {matchId}
Lega: {league}
Home: {home}
Away: {away}
Contesto aggiuntivo: {extraContext ?? "(nessuno)"}.
Formato: 3 righe massime (combo consigliata + due bullet di motivazione).";

        var payload = new
        {
            // usa un modello economico e veloce
            model = "gpt-4o-mini",
            messages = new object[]
            {
                new { role = "system", content = "Sei un assistente che scrive pronostici calcistici sintetici, chiari, coerenti coi dati." },
                new { role = "user", content = prompt }
            },
            temperature = 0.7
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        using var res = await client.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            _logger.LogError("OpenAI error {Status}: {Body}", (int)res.StatusCode, body);
            // prova a estrarre messaggio API
            try
            {
                using var doc = JsonDocument.Parse(body);
                var msg = doc.RootElement.GetProperty("error").GetProperty("message").GetString();
                throw new ApplicationException($"OpenAI error: {msg}");
            }
            catch
            {
                throw new ApplicationException($"OpenAI HTTP {(int)res.StatusCode}. Vedi log per dettagli.");
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return text ?? "Nessuna risposta generata.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Parsing risposta OpenAI fallito. Body: {Body}", body);
            throw new ApplicationException("Risposta AI non valida. Vedi log.");
        }
    }
}
