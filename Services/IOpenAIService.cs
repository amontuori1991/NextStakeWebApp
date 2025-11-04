using System.Threading.Tasks;

namespace NextStakeWebApp.Services
{
    public interface IOpenAIService
    {
        Task<string> AskAsync(string prompt);
    }
}
