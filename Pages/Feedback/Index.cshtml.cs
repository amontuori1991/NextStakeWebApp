using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using NextStakeWebApp.Services;

namespace NextStakeWebApp.Pages.Feedback
{
    [Authorize]
    public class IndexModel : PageModel
    {
        private readonly ITelegramService _telegram;
        private readonly TelegramOptions _tgOpts;

        public IndexModel(
            ITelegramService telegram,
            IOptions<TelegramOptions> tgOpts)
        {
            _telegram = telegram;
            _tgOpts = tgOpts.Value ?? new TelegramOptions();
        }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        public class InputModel
        {
            [Required, Display(Name = "Tipologia")]
            public string Category { get; set; } = "bug"; // bug | integrazione | altro

            [Required, StringLength(4000, MinimumLength = 5), Display(Name = "Messaggio")]
            public string Message { get; set; } = "";
        }

        public void OnGet() { }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!ModelState.IsValid) return Page();

            if (_tgOpts.Topics == null || !_tgOpts.Topics.TryGetValue("idee", out var topicId))
            {
                ModelState.AddModelError(string.Empty, "Configurazione Telegram mancante: topic 'idee' non trovato.");
                return Page();
            }

            // Dati utente dalle claim
            var userId = User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
            var userName = User?.Identity?.Name ?? "unknown";
            var email = User?.FindFirstValue(ClaimTypes.Email) ?? "-";
            var when = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            var text =
$@"<b>Nuovo feedback</b>
<b>Utente:</b> {System.Net.WebUtility.HtmlEncode(userName)} (ID: {System.Net.WebUtility.HtmlEncode(userId)})
<b>Email:</b> {System.Net.WebUtility.HtmlEncode(email)}
<b>Quando:</b> {when}
<b>Tipologia:</b> {System.Net.WebUtility.HtmlEncode(Input.Category)}

<pre>{System.Net.WebUtility.HtmlEncode(Input.Message)}</pre>";

            await _telegram.SendMessageAsync(topicId, text);

            TempData["FeedbackOk"] = "Grazie! Il tuo feedback è stato inviato.";
            return RedirectToPage(); // PRG
        }
    }
}
