using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.Extensions.Configuration;
using MimeKit;
using MimeKit.Utils;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace NextStakeWebApp.Services
{
    public class GmailSmtpSender : IEmailSender
    {
        private readonly IConfiguration _config;
        public GmailSmtpSender(IConfiguration config) => _config = config;

        public async Task SendEmailAsync(string email, string subject, string htmlMessage)
        {
            var fromEmail = _config["SMTP_FROM"] ?? "nextstakeai@gmail.com";
            var fromName = _config["SMTP_FROM_NAME"] ?? "NextStake AI";
            var host = _config["SMTP_HOST"] ?? "smtp.gmail.com";
            var port = int.TryParse(_config["SMTP_PORT"], out var p) ? p : 587;
            var user = _config["SMTP_USER"] ?? "nextstakeai@gmail.com";
            var pass = _config["SMTP_PASS"]; // ⚠️ App Password di Gmail

            var msg = new MimeMessage();
            msg.From.Add(new MailboxAddress(fromName, fromEmail));
            msg.To.Add(MailboxAddress.Parse(email));
            msg.Subject = subject;

            var body = new BodyBuilder { HtmlBody = htmlMessage };

            // Logo inline (CID) — usa il tuo SVG wwwroot/icons/favicon.svg
            var logoPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "icons", "favicon.svg");
            if (File.Exists(logoPath))
            {
                var logo = body.LinkedResources.Add(logoPath);
                logo.ContentId = MimeUtils.GenerateMessageId();
                // Sostituisci eventuale cid statico in html con quello reale
                body.HtmlBody = body.HtmlBody.Replace("cid:logo-nextstake", $"cid:{logo.ContentId}");
                // MimeKit riconosce svg; in caso, forzi ContentType:
                logo.ContentType.MediaType = "image";
                logo.ContentType.MediaSubtype = "svg+xml";
            }

            msg.Body = body.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(host, port, SecureSocketOptions.StartTls);
            if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pass))
                await smtp.AuthenticateAsync(user, pass);
            await smtp.SendAsync(msg);
            await smtp.DisconnectAsync(true);
        }
    }
}
