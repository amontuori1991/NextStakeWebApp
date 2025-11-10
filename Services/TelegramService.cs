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
            var client = _httpFactory.CreateClient();

            // Caso 1: URL pubblico -> mantieni la tua chiamata JSON
            if (Uri.TryCreate(filePath, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                var url = $"https://api.telegram.org/bot{_botToken}/sendPhoto";
                var payload = new
                {
                    chat_id = _chatId,
                    message_thread_id = topicId,
                    photo = filePath,
                    caption = caption,
                    parse_mode = "HTML"
                };
                var json = JsonSerializer.Serialize(payload);
                var res = await client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
                res.EnsureSuccessStatusCode();
                return;
            }

            // Caso 2: file locale -> upload multipart/form-data
            if (!System.IO.File.Exists(filePath))
                throw new FileNotFoundException("File immagine non trovato", filePath);

            var form = new MultipartFormDataContent();
            await using var fs = System.IO.File.OpenRead(filePath);
            var streamContent = new StreamContent(fs);
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");

            form.Add(new StringContent(_chatId), "chat_id");
            form.Add(new StringContent(topicId.ToString()), "message_thread_id");
            if (!string.IsNullOrWhiteSpace(caption))
            {
                form.Add(new StringContent(caption), "caption");
                form.Add(new StringContent("HTML"), "parse_mode");
            }
            form.Add(streamContent, "photo", Path.GetFileName(filePath));

            var apiUrl = $"https://api.telegram.org/bot{_botToken}/sendPhoto";
            var resp = await client.PostAsync(apiUrl, form);
            resp.EnsureSuccessStatusCode();
        }

    }
}
