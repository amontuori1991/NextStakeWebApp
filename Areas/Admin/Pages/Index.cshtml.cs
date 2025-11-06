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
    [Authorize(Policy = "Plan1")]

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

            ModeOptions = new SelectList(new[]
            {
                new { Value = "Migliora", Text = "Migliora la mia bozza" },
                new { Value = "Genera",   Text = "Genera da zero" }
            }, "Value", "Text");
        }

        [BindProperty]
        public FormInput Input { get; set; } = new();

        public string? Preview { get; private set; }
        public string? StatusMessage { get; private set; }

        public SelectList ToneOptions { get; }
        public SelectList LengthOptions { get; }
        public SelectList ModeOptions { get; }

        public void OnGet() { }

        // ===== Anteprima (con o senza AI) =====
        public async Task<IActionResult> OnPostPreviewAsync()
        {
            // i select possono essere disabilitati lato UI → rimuovo per validazione
            ModelState.Remove("Input.Tone");
            ModelState.Remove("Input.Length");
            ModelState.Remove("Input.Mode");
            ModelState.Remove("Input.Instructions");

            if (!ModelState.IsValid)
            {
                StatusMessage = "Compila correttamente i campi richiesti.";
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
            ModelState.Remove("Input.Mode");
            ModelState.Remove("Input.Instructions");

            if (!ModelState.IsValid)
            {
                StatusMessage = "Compila correttamente i campi richiesti.";
                return Page();
            }

            // 1) testo finale (con o senza AI)
            var text = await BuildTextAsync(Input);

            // 2) topicId da config (Telegram__Topics__Idee)
            // 1) prova con Idee
            long.TryParse(_cfg["Telegram:Topics:Idee"], out var topicId);

            // 2) fallback: PronosticiDaPubblicare
            if (topicId <= 0)
                long.TryParse(_cfg["Telegram:Topics:PronosticiDaPubblicare"], out topicId);

            // 3) se ancora non c'è, invia senza topic (se il tuo TelegramService lo consente)
            if (topicId <= 0)
            {
                Console.WriteLine("[ADMIN] Topic non configurato: invio senza message_thread_id.");
                await _telegram.SendMessageAsync(0, text); // NB: il tuo TelegramService deve interpretare 0 = senza topic
            }
            else
            {
                await _telegram.SendMessageAsync(topicId, text);
            }

            StatusMessage = "✅ Messaggio inviato al canale.";
            Preview = text;
            return Page();

        }

        // ----- Helpers -----
        private async Task<string> BuildTextAsync(FormInput input)
        {
            var baseText = (input.Message ?? "").Trim();

            // Se AI OFF → restituisco il testo così com'è
            if (!input.UseAI)
                return baseText;

            // Fallback sicuri se i select sono disabilitati
            var tone = string.IsNullOrWhiteSpace(input.Tone) ? "Neutro" : input.Tone!;
            var length = string.IsNullOrWhiteSpace(input.Length) ? "Media" : input.Length!;
            var mode = string.IsNullOrWhiteSpace(input.Mode) ? "Migliora" : input.Mode!;
            var extra = (input.Instructions ?? "").Trim();

            string prompt;

            if (mode.Equals("Genera", StringComparison.OrdinalIgnoreCase))
            {
                // GENERA DA ZERO partendo dal tema
                if (string.IsNullOrWhiteSpace(baseText))
                    return "";

                prompt =
                    "Sei un copywriter esperto di calcio e betting. Produce un post per un canale Telegram che pubblica analisi e consigli.\n" +
                    "Obiettivi: informare velocemente, dare valore, invogliare alla discussione senza mai promettere vincite certe.\n" +
                    $"Tono: {tone}. Lunghezza: {length}.\n" +
                    "Regole:\n" +
                    "- Scrivi in italiano naturale.\n" +
                    "- Evita claim assoluti; usa linguaggio probabilistico.\n" +
                    "- Usa emoji con moderazione, solo se utili alla leggibilità.\n" +
                    "- Niente hashtag superflui.\n" +
                    "- Se ha senso, chiudi con una micro call-to-action (es. \"Tu come la vedi?\").\n" +
                    "Tema del post:\n" +
                    baseText + "\n\n" +
                    "Istruzioni aggiuntive (se pertinenti):\n" +
                    (string.IsNullOrWhiteSpace(extra) ? "(nessuna)" : extra);
            }
            else
            {
                // MIGLIORA LA MIA BOZZA
                prompt =
                    "Migliora e riscrivi la seguente bozza per un post Telegram su analisi calcistiche.\n" +
                    "Mantieni il significato, migliora ritmo e chiarezza.\n" +
                    $"Tono: {tone}. Lunghezza: {length}.\n" +
                    "Regole:\n" +
                    "- Italiano naturale.\n" +
                    "- Niente hashtag superflui, emoji essenziali.\n" +
                    "- Evita promesse; preferisci probabilità e scenario.\n" +
                    "- Mantieni eventuali dati/statistiche presenti.\n" +
                    "Bozza:\n" + baseText + "\n\n" +
                    "Istruzioni aggiuntive (se utili):\n" +
                    (string.IsNullOrWhiteSpace(extra) ? "(nessuna)" : extra);
            }

            var improved = baseText; // fallback
            try
            {
                var ai = await _openai.AskAsync(prompt);
                if (!string.IsNullOrWhiteSpace(ai))
                    improved = ai.Trim();
            }
            catch (ApplicationException ex)
            {
                // Tipico per 429 o errori quota/limite
                StatusMessage = "⚠️ L'AI è momentaneamente occupata o hai raggiunto il limite. Riprova tra poco.";
                Console.WriteLine("[AI][WARN] " + ex.Message);
            }
            catch (Exception ex)
            {
                StatusMessage = "⚠️ Errore AI inatteso. Riprova.";
                Console.WriteLine("[AI][ERR] " + ex);
            }

            return improved;
        }


        public class FormInput
        {
            // Campo multifunzione:
            // - in modalità "Migliora": è la bozza da rifinire
            // - in modalità "Genera": è il "tema/argomento" a partire dal quale creare il post
            [Required, MinLength(3)]
            [Display(Name = "Messaggio / Tema")]
            public string Message { get; set; } = "";

            // ON/OFF AI
            public bool UseAI { get; set; } = true;

            // Modalità AI: Migliora o Genera
            public string? Mode { get; set; } = "Migliora";

            // Opzioni stile
            public string? Tone { get; set; } = "Neutro";
            public string? Length { get; set; } = "Media";

            // Istruzioni libere (come se stessi chattando): es. “usa bullet point”, “metti un gancio iniziale…”
            [Display(Name = "Istruzioni AI (opzionali)")]
            public string? Instructions { get; set; }
        }
    }
}
