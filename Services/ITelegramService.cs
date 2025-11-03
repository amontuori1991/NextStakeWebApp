using System.Threading.Tasks;

namespace NextStakeWebApp.Services
{
    public interface ITelegramService
    {
        Task SendMessageAsync(long topicId, string text);
        Task SendPhotoAsync(long topicId, string photoUrl, string caption);
    }
}
