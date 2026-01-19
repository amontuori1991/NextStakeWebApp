using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NextStakeWebApp.Models;

namespace NextStakeWebApp.Areas.Identity.Pages.Account
{
    [AllowAnonymous]
    public class LoginModel : PageModel
    {
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly UserManager<ApplicationUser> _userManager;

        public LoginModel(SignInManager<ApplicationUser> signInManager, UserManager<ApplicationUser> userManager)
        {
            _signInManager = signInManager;
            _userManager = userManager;
        }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        public string? ReturnUrl { get; set; }

        [TempData]
        public string? ErrorMessage { get; set; }

        public class InputModel
        {
            [Required]
            [Display(Name = "Email o Nome utente")]
            public string Login { get; set; } = string.Empty;

            [Required]
            [DataType(DataType.Password)]
            [Display(Name = "Password")]
            public string Password { get; set; } = string.Empty;

            [Display(Name = "Ricordami")]
            public bool RememberMe { get; set; }
        }

        public void OnGet(string? returnUrl = null)
        {
            ReturnUrl = returnUrl;
        }

        public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
        {
            // Destinazione di default: schermata Eventi
            var defaultAfterLogin = Url.Page("/Events/Index") ?? Url.Content("~/Events");

            // Prendi l’eventuale returnUrl passato dalla querystring o dal parametro
            var provided = returnUrl ?? Request.Query["returnUrl"].ToString();

            // Se il returnUrl è nullo o punta alla root "/", forza la destinazione agli Eventi
            if (string.IsNullOrEmpty(provided) || provided == "/" || provided == Url.Content("~/"))
                ReturnUrl = defaultAfterLogin;
            else
                ReturnUrl = provided;

            if (!ModelState.IsValid)
                return Page();


            // Rileva se l'input è email o username
            ApplicationUser? user = null;
            if (Input.Login.Contains("@"))
                user = await _userManager.FindByEmailAsync(Input.Login.Trim());
            else
                user = await _userManager.FindByNameAsync(Input.Login.Trim());

            if (user == null)
            {
                ModelState.AddModelError(string.Empty, "Credenziali non valide.");
                return Page();
            }

            if (!user.IsApproved)
            {
                ModelState.AddModelError(string.Empty, "Account non ancora approvato dall'amministratore.");
                return Page();
            }

            var result = await _signInManager.PasswordSignInAsync(
                user.UserName,
                Input.Password,
                Input.RememberMe,
                lockoutOnFailure: false
            );

            if (result.Succeeded)
            {
                // Salva ultimo login
                user.LastLoginAtUtc = DateTime.UtcNow;

                // Opzionale: IP + UserAgent
                user.LastLoginIp = HttpContext.Connection.RemoteIpAddress?.ToString();
                user.LastLoginUserAgent = Request.Headers["User-Agent"].ToString();

                await _userManager.UpdateAsync(user);

                return LocalRedirect(ReturnUrl);
            }


            ModelState.AddModelError(string.Empty, "Credenziali non valide.");
            return Page();
        }
    }
}
