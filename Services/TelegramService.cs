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

        public async Task SendPhotoAsync(long topicId, string photoUrl, string caption)
        {
            var client = _httpFactory.CreateClient();
            var url = $"https://api.telegram.org/bot{_botToken}/sendPhoto";

            var payload = new
            {
                chat_id = _chatId,
                message_thread_id = topicId,
                photo = photoUrl,    // URL assoluto del logo lega
                caption = caption,   // stessa formattazione del testo che usavi
                parse_mode = "HTML"
            };

            var json = JsonSerializer.Serialize(payload);
            var res = await client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
            res.EnsureSuccessStatusCode();
        }
    }
}
