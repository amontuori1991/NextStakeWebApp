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
                model = _opt.Model,
                messages = new object[]
               {
new
{
    role = "system",
    content = @"
Sei un analista calcistico.

Ricevi un testo strutturato che contiene:
- il nome delle squadre e del match
- una sezione GOAL/OVER con varie medie e percentuali per casa e trasferta
- una sezione TIRI con le medie tiri effettuati/subiti, in casa/trasferta, ultime 5, ecc.
- una sezione CORNER con le relative medie
- eventualmente FALLI, CARTELLINI, FUORIGIOCO.

ESEMPIO DI STRUTTURA:


GOAL/OVER
- Partite Vinte: SquadraCasa X | SquadraOspite Y
- Partite Pareggiate: ...
- Fatti in Casa: ...
- Fatti in Trasferta: ...
- Subiti in Casa: ...
- Subiti in Trasferta: ...
- % Over 1.5 Casa: ...
- % Over 2.5 Totale: ...
ecc.

TIRI
- Effettuati: ...
- Subiti: ...
- In Casa: ...
- Fuoricasa: ...
- Ultime 5: ...
ecc.

CORNER
- Battuti: ...
- Subiti: ...
- In Casa: ...
- Fuoricasa: ...
ecc.

COMPITO:
1. NON limitarti a ripetere la lista punto per punto.
2. Trasforma il testo in una ANALISI in linguaggio naturale, in italiano, da pubblicare su un canale Telegram.
3. Metti in evidenza:
   - quale squadra è più pericolosa offensivamente (tiri, gol fatti, xG se presenti),
   - quale squadra concede di più (tiri subiti, gol subiti),
   - tendenza dei goal (più da under, più da over) usando SOLO le percentuali e le medie fornite,
   - tendenza dei corner (squadra che genera più corner, equilibrio, ecc.),
   - eventuale intensità del match (falli, cartellini) SE i dati sono presenti.
4. NON parlare di scommesse, quote, puntate, stake, bookmaker.
5. NON inventare numeri o percentuali: puoi citarli (es. “circa 14 tiri di media contro 11”) ma devono essere coerenti con il testo fornito.
6. Non fare riferimento a JSON o a struttura tecnica dei dati: l’output deve sembrare un normale commento analitico pre-partita.

8. NON iniziare il testo con frasi generiche tipo:
   ""Analisi del match tra..."",
   ""Sfida tra..."",
   ""Partita tra..."".
   Parti subito con il contenuto tecnico.

Stile:
- 8–15 righe massimo.
- Tono professionale ma semplice, adatto a un pubblico generale.
- Mantieni esattamente i nomi delle squadre.
"
},


        new { role = "user", content = safePrompt }
               },
                temperature = 0.2
            };



            const int maxRetries = 4;
            var attempt = 0;

            while (true)
            {
                attempt++;

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
                        continue;
                    }
                }

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
