// Services/Contracts.cs
using System.Threading;

namespace NextStakeWebApp.Services
{
    public class OpenAIOptions
    {
        public string? ApiKey { get; set; }
        public string Model { get; set; } = "gpt-4o-mini";
    }

    public interface IOpenAIService
    {
        Task<string?> AskAsync(string prompt, CancellationToken ct = default);
    }

    public interface IAiService
    {
        Task<string> BuildPredictionAsync(
            long matchId, string home, string away, string league, string? extraContext,
            CancellationToken ct = default);
    }
}
