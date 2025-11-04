using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace NextStakeWebApp.Services
{
    public class OpenAIService : IOpenAIService
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly string _apiKey;
        private readonly string _model;

        public OpenAIService(IHttpClientFactory httpFactory, IConfiguration cfg)
        {
            _httpFactory = httpFactory;
            _apiKey = cfg["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI API key missing");
            _model = cfg["OpenAI:Model"] ?? "gpt-4o-mini";
        }

        public async Task<string> AskAsync(string prompt)
        {
            var client = _httpFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

            var payload = new
            {
                model = _model,
                input = prompt,
            };

            var json = JsonSerializer.Serialize(payload);
            var res = await client.PostAsync(
                "https://api.openai.com/v1/responses",
                new StringContent(json, Encoding.UTF8, "application/json")
            );

            res.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString() ?? "";
        }
    }
}
