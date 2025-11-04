using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NextStakeWebApp.Services;

namespace NextStakeWebApp.Areas.Admin.Pages
{
    [Authorize(Roles = "SuperAdmin")]
    public class IndexModel : PageModel
    {
        private readonly ITelegramService _telegram;
        private readonly IConfiguration _config;

        public IndexModel(ITelegramService telegram, IConfiguration config)
        {
            _telegram = telegram;
            _config = config;
        }

        // Mostro a video quale topicId sto usando
        public long TopicIdeeId { get; private set; }

        // Messaggi di esito
        [TempData] public string? StatusMessage { get; set; }
        [TempData] public string? ErrorMessage { get; set; }

        public void OnGet()
        {
            long.TryParse(_config["Telegram:Topics:Idee"], out var tId);
            TopicIdeeId = tId;
        }

        public async Task<IActionResult> OnPostSendIdeeAsync(string text)
        {
            // Ricarico TopicIdeeId anche in POST per visualizzazione eventuale
            long.TryParse(_config["Telegram:Topics:Idee"], out var topicId);
            TopicIdeeId = topicId;

            if (string.IsNullOrWhiteSpace(text))
            {
                ErrorMessage = "Il messaggio non può essere vuoto.";
                return RedirectToPage();
            }
            if (topicId <= 0)
            {
                ErrorMessage = "Configurazione mancante o non valida: Telegram:Topics:Idee.";
                return RedirectToPage();
            }

            try
            {
                // Invia nel forum (usa ChatId di default + message_thread_id = topic)
                await _telegram.SendMessageAsync(topicId, text.Trim());
                StatusMessage = "✅ Messaggio inviato correttamente al topic 'Idee'.";
            }
            catch (Exception ex)
            {
                ErrorMessage = "Errore durante l'invio: " + ex.Message;
            }

            return RedirectToPage();
        }
    }
}
