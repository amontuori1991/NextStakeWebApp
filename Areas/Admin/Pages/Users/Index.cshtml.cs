using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Models;
using System.ComponentModel.DataAnnotations;

namespace NextStakeWebApp.Areas.Admin.Pages.Users
{
    [Authorize(Policy = "Plan1")]
    public class IndexModel : PageModel
    {
        private readonly UserManager<ApplicationUser> _userManager;

        // Fuso orario per la logica di scadenza (Italia)
        private static readonly TimeZoneInfo RomeTz =
            TimeZoneInfo.FindSystemTimeZoneById(
#if WINDOWS
                "W. Europe Standard Time"
#else
                "Europe/Rome"
#endif
            );

        public IndexModel(UserManager<ApplicationUser> userManager)
        {
            _userManager = userManager;
        }

        public List<AdminUserViewModel> Users { get; set; } = new();

        public async Task OnGetAsync()
        {
            var allUsers = await _userManager.Users.ToListAsync();

            var nowUtc = DateTime.UtcNow;
            var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, RomeTz).Date;

            Users = allUsers.Select(u =>
            {
                // Calcolo campi scadenza/localizzazione
                string? localYmd = null;
                string? display = null;
                bool infinite = u.PlanExpiresAtUtc == null;

                if (u.PlanExpiresAtUtc.HasValue)
                {
                    var local = TimeZoneInfo.ConvertTimeFromUtc(u.PlanExpiresAtUtc.Value, RomeTz);
                    localYmd = local.ToString("yyyy-MM-dd");   // per input type="date"
                    display = local.ToString("dd/MM/yyyy");   // per visualizzazione
                }

                // Stato: Attivo / In scadenza (entro 3 giorni) / Scaduto
                var status = GetStatus(nowLocal, u.PlanExpiresAtUtc);

                return new AdminUserViewModel
                {
                    Id = u.Id,
                    Email = u.Email ?? "",
                    Plan = u.Plan.ToString(),
                    PlanExpiresAtUtc = u.PlanExpiresAtUtc,
                    PlanExpiresAtLocalYmd = localYmd,
                    PlanExpiresAtDisplay = display,
                    IsApproved = u.IsApproved,
                    IsInfinite = infinite,
                    Status = status
                };
            })
            // Per comodità, ordino: in scadenza, scaduti, attivi (ma poi dividiamo in 3 sezioni)
            .OrderBy(u => u.Status == "In scadenza" ? 0 : u.Status == "Scaduto" ? 1 : 2)
            .ThenBy(u => u.Email)
            .ToList();
        }

        private static string GetStatus(DateTime todayLocalDate, DateTime? expiresAtUtc)
        {
            if (!expiresAtUtc.HasValue) return "Attivo"; // infinito

            var expiresLocalDate = expiresAtUtc.Value.ToUniversalTime();
            // Converte a locale e tronca a data
            var local = TimeZoneInfo.ConvertTimeFromUtc(expiresLocalDate, RomeTz).Date;

            if (local < todayLocalDate) return "Scaduto";

            var daysLeft = (local - todayLocalDate).TotalDays;
            if (daysLeft <= 3) return "In scadenza";

            return "Attivo";
        }

        public async Task<IActionResult> OnPostToggleApprovalAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return BadRequest();

            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            user.IsApproved = !user.IsApproved;
            await _userManager.UpdateAsync(user);

            TempData["Msg"] = $"Utente {user.Email}: {(user.IsApproved ? "APPROVATO" : "BLOCCATO")}.";
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostChangePlanAsync(string id, string plan)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(plan))
                return BadRequest();

            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            if (!Enum.TryParse<SubscriptionPlan>(plan, ignoreCase: true, out var newPlan))
                return BadRequest("Piano non valido");

            user.Plan = newPlan;
            user.PlanExpiresAtUtc = newPlan switch
            {
                SubscriptionPlan.TRL => DateTime.UtcNow.AddDays(15),
                SubscriptionPlan.ADM => (DateTime?)null,
                SubscriptionPlan.M1 => DateTime.UtcNow.AddMonths(1),
                SubscriptionPlan.M2 => DateTime.UtcNow.AddMonths(2),
                SubscriptionPlan.M3 => DateTime.UtcNow.AddMonths(3),
                SubscriptionPlan.M6 => DateTime.UtcNow.AddMonths(6),
                SubscriptionPlan.Y1 => DateTime.UtcNow.AddYears(1),
                _ => user.PlanExpiresAtUtc
            };

            await _userManager.UpdateAsync(user);

            TempData["Msg"] = $"Piano aggiornato a {user.Plan} per {user.Email}.";
            return RedirectToPage();
        }

        // NUOVO: modifica manuale scadenza (data o infinito)
        public async Task<IActionResult> OnPostChangeExpiryAsync(string id, string? expiryDate, bool? infinite)
        {
            if (string.IsNullOrWhiteSpace(id)) return BadRequest();

            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            if (infinite == true)
            {
                user.PlanExpiresAtUtc = null;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(expiryDate))
                    return BadRequest("Specificare una data oppure selezionare ∞.");

                // expiryDate arriva come "yyyy-MM-dd"
                if (!DateTime.TryParseExact(expiryDate, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var localDate))
                    return BadRequest("Formato data non valido.");

                // Imposto a fine giornata locale (23:59:59) e converto in UTC
                var localEndOfDay = new DateTime(localDate.Year, localDate.Month, localDate.Day, 23, 59, 59, DateTimeKind.Unspecified);
                var utc = TimeZoneInfo.ConvertTimeToUtc(localEndOfDay, RomeTz);

                user.PlanExpiresAtUtc = utc;
            }

            await _userManager.UpdateAsync(user);

            var msgTail = user.PlanExpiresAtUtc == null
                ? "∞ (infinito)"
                : TimeZoneInfo.ConvertTimeFromUtc(user.PlanExpiresAtUtc.Value, RomeTz).ToString("dd/MM/yyyy");

            TempData["Msg"] = $"Scadenza aggiornata per {user.Email}: {msgTail}.";
            return RedirectToPage();
        }

        // ViewModel usata dalla pagina e dalla partial
        public class AdminUserViewModel
        {
            public string Id { get; set; } = default!;
            public string Email { get; set; } = default!;
            public string Plan { get; set; } = default!;
            public DateTime? PlanExpiresAtUtc { get; set; }

            // Ausili per UI
            public string? PlanExpiresAtLocalYmd { get; set; }   // "yyyy-MM-dd" per input date
            public string? PlanExpiresAtDisplay { get; set; }    // "dd/MM/yyyy" per display
            public bool IsInfinite { get; set; }
            public bool IsApproved { get; set; }

            // Stato calcolato: "Attivo" | "In scadenza" | "Scaduto"
            public string Status { get; set; } = "Attivo";
        }
    }
}
