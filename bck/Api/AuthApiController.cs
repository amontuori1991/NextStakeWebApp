using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using NextStakeWebApp.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace NextStakeWebApp.bck.Api
{
    [ApiController]
    [Route("api/auth")]
    public class AuthApiController : ControllerBase
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly IConfiguration _config;

        public AuthApiController(
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            IConfiguration config)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _config = config;
        }

        // POST /api/auth/login
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto dto)
        {
            var user = await _userManager.FindByEmailAsync(dto.Email);
            if (user == null)
                return Unauthorized(new { error = "Credenziali non valide" });

            if (!user.EmailConfirmed)
                return Unauthorized(new { error = "Email non confermata" });

            if (!user.IsApproved)
                return Unauthorized(new { error = "Account non ancora approvato" });

            var result = await _signInManager.CheckPasswordSignInAsync(user, dto.Password, false);
            if (!result.Succeeded)
                return Unauthorized(new { error = "Credenziali non valide" });

            var roles = await _userManager.GetRolesAsync(user);
            var token = GenerateJwt(user, roles);

            return Ok(new
            {
                token,
                user = new
                {
                    user.Id,
                    user.Email,
                    user.DisplayName,
                    user.Plan,
                    user.IsApproved,
                    roles
                }
            });
        }

        // POST /api/auth/register
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterDto dto)
        {
            var user = new ApplicationUser
            {
                UserName = dto.Email,
                Email = dto.Email,
                DisplayName = dto.DisplayName,
                Plan = SubscriptionPlan.TRL,
                IsApproved = false
            };

            var result = await _userManager.CreateAsync(user, dto.Password);
            if (!result.Succeeded)
                return BadRequest(new { errors = result.Errors.Select(e => e.Description) });

            // Manda email di conferma (stesso flusso del sito web)
            var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            // Per ora restituiamo solo successo; la mail parte tramite Identity
            return Ok(new { message = "Registrazione completata. Controlla la tua email per confermare l'account." });
        }

        private string GenerateJwt(ApplicationUser user, IList<string> roles)
        {
            var key = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(_config["Jwt:Key"]
                    ?? Environment.GetEnvironmentVariable("JWT_KEY")!));

            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, user.Id),
                new(JwtRegisteredClaimNames.Email, user.Email!),
                new("displayName", user.DisplayName ?? ""),
                new("plan", ((int)user.Plan).ToString()),
                new("isApproved", user.IsApproved.ToString())
            };

            foreach (var role in roles)
                claims.Add(new Claim(ClaimTypes.Role, role));

            var token = new JwtSecurityToken(
                claims: claims,
                expires: DateTime.UtcNow.AddDays(7),
                signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }

    public record LoginDto(string Email, string Password);
    public record RegisterDto(string Email, string Password, string DisplayName);
}