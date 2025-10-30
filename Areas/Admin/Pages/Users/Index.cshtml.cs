using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NextStakeWebApp.Models;

namespace NextStakeWebApp.Areas.Admin.Pages.Users
{
    [Authorize(Roles = "SuperAdmin")]
    public class IndexModel : PageModel
    {
        private readonly UserManager<ApplicationUser> _userManager;

        public IndexModel(UserManager<ApplicationUser> userManager)
        {
            _userManager = userManager;
        }

        public List<AdminUserViewModel> Users { get; set; } = new();

        public async Task OnGetAsync()
        {
            var allUsers = _userManager.Users.ToList();
            Users = allUsers.Select(u => new AdminUserViewModel
            {
                Id = u.Id,
                Email = u.Email ?? "",
                Plan = u.Plan.ToString(),
                PlanExpiresAtUtc = u.PlanExpiresAtUtc,
                IsApproved = u.IsApproved
            }).ToList();
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

    }
}
