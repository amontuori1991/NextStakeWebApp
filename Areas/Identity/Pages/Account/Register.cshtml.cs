using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NextStakeWebApp.Models;

namespace NextStakeWebApp.Areas.Identity.Pages.Account
{
    [AllowAnonymous]
    public class RegisterModel : PageModel
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;

        public RegisterModel(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager)
        {
            _userManager = userManager;
            _signInManager = signInManager;
        }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        public string? ReturnUrl { get; set; }

        public class InputModel
        {
            [Required]
            [Display(Name = "Nome utente")]
            public string UserName { get; set; } = string.Empty;

            [Required, EmailAddress]
            [Display(Name = "Email")]
            public string Email { get; set; } = string.Empty;

            [Required, DataType(DataType.Password)]
            [Display(Name = "Password")]
            public string Password { get; set; } = string.Empty;

            [Required, DataType(DataType.Password), Display(Name = "Conferma password"),
             Compare(nameof(Password), ErrorMessage = "Le password non coincidono.")]
            public string ConfirmPassword { get; set; } = string.Empty;
        }

        public void OnGet(string? returnUrl = null)
        {
            ReturnUrl = returnUrl;
        }

        public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
        {
            ReturnUrl = returnUrl ?? Url.Content("~/");

            if (!ModelState.IsValid)
                return Page();

            // Normalizziamo input
            var normalizedEmail = Input.Email.Trim();
            var normalizedUser = Input.UserName.Trim();

            // Controllo duplicati
            var existingEmail = await _userManager.FindByEmailAsync(normalizedEmail);
            if (existingEmail != null)
            {
                ModelState.AddModelError(string.Empty, "Questa email è già registrata.");
                return Page();
            }

            var existingUser = await _userManager.FindByNameAsync(normalizedUser);
            if (existingUser != null)
            {
                ModelState.AddModelError(string.Empty, "Questo nome utente è già in uso.");
                return Page();
            }

            // Creazione utente con piano TRL (15 giorni)
            var user = new ApplicationUser
            {
                UserName = normalizedUser,        // 👈 username scelto
                Email = normalizedEmail,
                Plan = SubscriptionPlan.TRL,
                PlanExpiresAtUtc = DateTime.UtcNow.AddDays(15),
                IsApproved = true // per ora consentiamo l’accesso; il superadmin potrà poi bloccare/approvare
            };

            var result = await _userManager.CreateAsync(user, Input.Password);
            if (result.Succeeded)
            {
                await _signInManager.SignInAsync(user, isPersistent: false);
                return LocalRedirect(ReturnUrl!);
            }

            foreach (var error in result.Errors)
                ModelState.AddModelError(string.Empty, error.Description);

            return Page();
        }
    }
}
