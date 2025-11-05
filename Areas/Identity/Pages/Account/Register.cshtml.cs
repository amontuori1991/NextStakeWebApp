using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NextStakeWebApp.Models;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.WebUtilities;
using System.Text;
using System.Text.Encodings.Web;


namespace NextStakeWebApp.Areas.Identity.Pages.Account
{
    [AllowAnonymous]
    public class RegisterModel : PageModel
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly IEmailSender _emailSender;
        public RegisterModel(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager, IEmailSender emailSender)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _emailSender = emailSender;
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
                UserName = normalizedUser,
                Email = normalizedEmail,
                Plan = SubscriptionPlan.TRL,
                PlanExpiresAtUtc = DateTime.UtcNow.AddDays(15),
                IsApproved = true,         // lasci come da tua logica
                EmailConfirmed = false     // deve confermare email
            };

            var result = await _userManager.CreateAsync(user, Input.Password);
            if (result.Succeeded)
            {
                // Genera token e link di conferma
                var code = await _userManager.GenerateEmailConfirmationTokenAsync(user);
                var codeEncoded = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));

                var callbackUrl = Url.Page(
                    "/Account/ConfirmEmail",
                    pageHandler: null,
                    values: new { area = "Identity", userId = user.Id, code = codeEncoded, returnUrl = ReturnUrl },
                    protocol: Request.Scheme);

                // Email HTML semplice con logo inline (cid:logo-nextstake)
                var html = $@"
<!doctype html>
<html lang=""it"">
  <body style=""font-family:Arial,Helvetica,sans-serif;color:#111;background:#f7f7f9;padding:24px;"">
    <table width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""max-width:560px;margin:0 auto;background:#fff;border-radius:12px;overflow:hidden;"">
      <tr>
        <td style=""padding:20px 24px;background:#0d6efd;color:#fff;"">
          <div style=""display:flex;align-items:center;gap:12px;"">
            <img src=""cid:logo-nextstake"" alt=""NextStake"" style=""height:36px;"">
            <span style=""font-size:18px;font-weight:700;"">NextStake</span>
          </div>
        </td>
      </tr>
      <tr>
        <td style=""padding:24px;"">
          <h1 style=""font-size:20px;margin:0 0 12px;"">Conferma il tuo indirizzo email</h1>
          <p style=""margin:0 0 16px;"">Ciao {System.Net.WebUtility.HtmlEncode(normalizedUser)},</p>
          <p style=""margin:0 0 16px;"">Grazie per esserti registrato su <strong>NextStake</strong>. Per completare la registrazione, conferma il tuo indirizzo email cliccando sul pulsante qui sotto.</p>
          <p style=""margin:24px 0;"">
            <a href=""{HtmlEncoder.Default.Encode(callbackUrl!)}"" style=""background:#0d6efd;color:#fff;text-decoration:none;padding:12px 18px;border-radius:8px;display:inline-block;"">Conferma email</a>
          </p>
          <p style=""font-size:12px;color:#555;"">Se il pulsante non funziona, copia e incolla questo link nel browser:<br>
            <span style=""word-break:break-all;"">{HtmlEncoder.Default.Encode(callbackUrl!)}</span>
          </p>
          <hr style=""border:none;border-top:1px solid #eee;margin:24px 0;"">
          <p style=""font-size:12px;color:#777;"">Se non hai creato tu questo account, puoi ignorare questa email.</p>
        </td>
      </tr>
      <tr>
        <td style=""padding:16px 24px;background:#fafafa;color:#666;font-size:12px;"">© NextStake — Tutti i diritti riservati.</td>
      </tr>
    </table>
  </body>
</html>";

                await _emailSender.SendEmailAsync(normalizedEmail, "Conferma la tua email — NextStake", html);

                // ❌ Niente sign-in automatico: serve conferma
                return RedirectToPage("RegisterConfirmation", new { email = normalizedEmail, returnUrl = ReturnUrl });
            }

            foreach (var error in result.Errors)
                ModelState.AddModelError(string.Empty, error.Description);

            return Page();
        }

    }
}
