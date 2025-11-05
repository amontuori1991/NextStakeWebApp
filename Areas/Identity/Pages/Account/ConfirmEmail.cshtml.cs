using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using NextStakeWebApp.Models;

namespace NextStakeWebApp.Areas.Identity.Pages.Account
{
    public class ConfirmEmailModel : PageModel
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IEmailSender _emailSender;
        private readonly IConfiguration _config;

        public ConfirmEmailModel(UserManager<ApplicationUser> userManager, IEmailSender emailSender, IConfiguration config)
        {
            _userManager = userManager;
            _emailSender = emailSender;
            _config = config;
        }

        public bool Success { get; set; }
        public string? Message { get; set; }

        public async Task<IActionResult> OnGetAsync(string userId, string code, string? returnUrl = null)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(code))
                return Redirect("~/");

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null) return NotFound("Utente non trovato.");

            var decoded = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
            var res = await _userManager.ConfirmEmailAsync(user, decoded);
            Success = res.Succeeded;

            if (Success)
            {
                Message = "Email confermata con successo! Ora puoi effettuare l'accesso.";

                // ✅ dedup: invia notifica admin solo se non già inviata
                var alreadyNotified = await _userManager.GetAuthenticationTokenAsync(
                    user, "NextStake", "AdminEmailConfirmedNotified");

                if (string.IsNullOrEmpty(alreadyNotified))
                {
                    var adminEmail = _config["SuperAdmin:Email"] ?? "nextstakeai@gmail.com";
                    var html = $@"
<!doctype html>
<html lang=""it"">
  <body style=""font-family:Arial,Helvetica,sans-serif;color:#111;background:#f7f7f9;padding:24px;"">
    <table width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""max-width:560px;margin:0 auto;background:#fff;border-radius:12px;overflow:hidden;"">
      <tr>
        <td style=""padding:20px 24px;background:#198754;color:#fff;"">
          <div style=""display:flex;align-items:center;gap:12px;"">
            <img src=""cid:logo-nextstake"" alt=""NextStake"" style=""height:36px;"">
            <span style=""font-size:18px;font-weight:700;"">Nuova email confermata</span>
          </div>
        </td>
      </tr>
      <tr>
        <td style=""padding:24px;"">
          <p style=""margin:0 0 16px;"">Un nuovo utente ha <strong>confermato</strong> l'indirizzo email.</p>
          <ul style=""margin:0 0 16px; padding-left:18px;"">
            <li><strong>Email:</strong> {System.Net.WebUtility.HtmlEncode(user.Email)}</li>
            <li><strong>Nome utente:</strong> {System.Net.WebUtility.HtmlEncode(user.UserName)}</li>
          </ul>
          <p style=""margin:0;"">Puoi gestirlo dal pannello admin.</p>
        </td>
      </tr>
      <tr>
        <td style=""padding:16px 24px;background:#fafafa;color:#666;font-size:12px;"">NextStake — Notifica automatica</td>
      </tr>
    </table>
  </body>
</html>";

                    // invio email admin
                    await _emailSender.SendEmailAsync(adminEmail, "Nuova registrazione confermata — NextStake", html);

                    // marca come già notificato (idempotenza)
                    await _userManager.SetAuthenticationTokenAsync(
                        user, "NextStake", "AdminEmailConfirmedNotified", DateTime.UtcNow.ToString("o"));
                }
            }
            else
            {
                Message = "Impossibile confermare l'email. Il link potrebbe essere scaduto o già utilizzato.";
            }


            return Page();
        }
    }
}
