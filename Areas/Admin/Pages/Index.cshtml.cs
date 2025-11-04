using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Configuration;
using NextStakeWebApp.Services;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace NextStakeWebApp.Areas.Admin.Pages
{
    [Authorize(Roles = "SuperAdmin")]
    public class IndexModel : PageModel
    {
        private readonly ITelegramService _telegram;
        private readonly IOpenAIService _openai;
        private readonly IConfiguration _cfg;

        public IndexModel(ITelegramService telegram, IOpenAIService openai, IConfiguration cfg)
        {
            _telegram = telegram;
            _openai = openai;
            _cfg = cfg;

            ToneOptions = new SelectList(new[]
            {
                new { Value = "Neutro",         Text = "Neutro" },
                new { Value = "Informale",      Text = "Informale" },
                new { Value = "Professionale",  Text = "Professionale" },
                new { Value = "Motivazionale",  Text = "Motivazionale" },
            }, "Value", "Text");

            LengthOptions = new SelectList(new[]
            {
                new { Value = "Breve",  Text = "Breve" },
                new { Value = "Media",  Text = "Media" },
                new { Value = "Lunga",  Text = "Lunga" },
            }, "Value", "Text");
        }

        [BindProperty]
        public FormInput Input { get; set; } = new();

        public string? Preview { get; private set; }
        public string? StatusMessage { get; private set; }

        public SelectList ToneOptions { get; }
        public SelectList LengthOptions { get; }

        public void OnGet() { }

        // ===== Anteprima (con o senza AI) =====
        public async Task<IActionResult> OnPostPreviewAsync()
        {
            ModelState.Remove("Input.Tone");
            ModelState.Remove("Input.Length");

            if (!ModelState.IsValid)
            {
                StatusMessage = "Compila correttamente il messaggio.";
                return Page();
            }

            var text = await BuildTextAsync(Input);
            Preview = text;
            StatusMessage = "✅ Anteprima generata.";
            return Page();
        }

        // ===== Invio a Telegram (topic 'Idee') =====
        public async Task<IActionResult> OnPostSendAsync()
        {
            ModelState.Remove("Input.Tone");
            ModelState.Remove("Input.Length");

            if (!ModelState.IsValid)
            {
                StatusMessage = "Compila correttamente il messaggio.";
                return Page();
            }

            // 1) testo finale (con o senza AI)
            var text = await BuildTextAsync(Input);

            // 2) topicId da config (Telegram__Topics__Idee)
            long.TryParse(_cfg["Telegram:Topics:Idee"], out var topicId);
            if (topicId <= 0)
            {
                StatusMessage = "❌ Topic 'Idee' non configurato (Telegram:Topics:Idee).";
                Preview = text;
                return Page();
            }
            Console.WriteLine($"[ADMIN] Send topic={topicId}, useAI={Input.UseAI}, tone={Input.Tone}, len={Input.Length}");

            // 3) invia
            await _telegram.SendMessageAsync(topicId, text);

            StatusMessage = "✅ Messaggio inviato al canale (topic Idee).";
            Preview = text;
            return Page();
        }

        // ----- Helpers -----
        private async Task<string> BuildTextAsync(FormInput input)
        {
            var baseText = (input.Message ?? "").Trim();

            if (!input.UseAI || string.IsNullOrWhiteSpace(baseText))
                return baseText;

            var tone = string.IsNullOrWhiteSpace(input.Tone) ? "Neutro" : input.Tone!;
            var length = string.IsNullOrWhiteSpace(input.Length) ? "Media" : input.Length!;

            var prompt =
                $"Riscrivi e migliora il seguente testo per un canale Telegram su analisi calcistiche. " +
                $"Tono: {tone}. Lunghezza: {length}. " +
                $"Mantieni l'italiano e NON aggiungere hashtag o emoji extra a meno che siano utili.\n\n" +
                $"Testo:\n\"\"\"\n{baseText}\n\"\"\"";

            try
            {
                var improved = await _openai.AskAsync(prompt);
                return string.IsNullOrWhiteSpace(improved) ? baseText : improved.Trim();
            }
            catch (Exception ex)
            {
                // Log opzionale se hai un ILogger<IndexModel>
                // _logger.LogError(ex, "Errore OpenAI");

                // Messaggio visibile in pagina (in alto)
                StatusMessage = $"⚠️ AI non disponibile: {ex.Message}";
                return baseText; // fallback: restituisco il testo originale
            }
        }


        public class FormInput
        {
            [Required, MinLength(3)]
            public string Message { get; set; } = "";

            // di default NO AI
            public bool UseAI { get; set; } = false;

            // opzionali: vengono normalizzati in BuildTextAsync
            public string? Tone { get; set; } = "Neutro";
            public string? Length { get; set; } = "Media";
        }
    }
}
