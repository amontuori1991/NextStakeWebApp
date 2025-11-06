using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NextStakeWebApp.Models; // dove sta ApplicationUser + SubscriptionPlan

public class AppClaimsPrincipalFactory
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>
{
    public AppClaimsPrincipalFactory(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IOptions<IdentityOptions> optionsAccessor)
        : base(userManager, roleManager, optionsAccessor) { }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);

        // porta sia il valore numerico che il nome dell'enum (utile anche nelle viste)
        identity.AddClaim(new Claim("plan", ((int)user.Plan).ToString()));          // es. "0", "1"
        identity.AddClaim(new Claim("plan_name", user.Plan.ToString()));            // es. "TRL", "PRO"

        return identity;
    }
}
