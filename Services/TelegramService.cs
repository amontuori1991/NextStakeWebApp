using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace NextStakeWebApp.Services
{
    public class TelegramService : ITelegramService
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly string _botToken;
        private readonly string _chatId;

        public TelegramService(IHttpClientFactory httpFactory, IConfiguration cfg)
        {
            _httpFactory = httpFactory;
            _botToken = cfg["Telegram:BotToken"]!;
            _chatId = cfg["Telegram:ChatId"]!;
        }

        public async Task SendMessageAsync(long topicId, string text)
        {
            var client = _httpFactory.CreateClient();
            var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";

            var payload = new
            {
                chat_id = _chatId,
                message_thread_id = topicId,
                text,
                parse_mode = "HTML",
                disable_web_page_preview = true
            };

            var json = JsonSerializer.Serialize(payload);
            var res = await client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
            res.EnsureSuccessStatusCode();
        }

        public async Task SendPhotoAsync(long topicId, string filePath, string? caption = null)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException("Foto da inviare non trovata", filePath);

            var client = _httpFactory.CreateClient();
            var url = $"https://api.telegram.org/bot{_botToken}/sendPhoto";

            using var form = new MultipartFormDataContent();

            // campi standard
            form.Add(new StringContent(_chatId), "chat_id");
            form.Add(new StringContent(topicId.ToString()), "message_thread_id");

            if (!string.IsNullOrWhiteSpace(caption))
            {
                form.Add(new StringContent(caption), "caption");
                form.Add(new StringContent("HTML"), "parse_mode");
            }

            // allega il file come stream
            var stream = File.OpenRead(filePath);
            var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            form.Add(fileContent, "photo", Path.GetFileName(filePath));

            var res = await client.PostAsync(url, form);
            res.EnsureSuccessStatusCode();
        }


    }
}