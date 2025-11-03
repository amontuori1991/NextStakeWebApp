namespace NextStakeWebApp.Services
{
    public class TelegramOptions
    {
        public string BotToken { get; set; } = "";
        public string ChatId { get; set; } = "";
        public int? DefaultTopicId { get; set; }
        public Dictionary<string, int> Topics { get; set; } = new();
    }
}
