using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;

namespace NextStakeWebApp.Models
{
    public class ApplicationUser : IdentityUser
    {
        public string? DisplayName { get; set; }
        public bool IsApproved { get; set; } = false;

        [Required]
        public SubscriptionPlan Plan { get; set; } = SubscriptionPlan.TRL;

        public DateTime? PlanExpiresAtUtc { get; set; } // null = senza scadenza (ADM)

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    }
}
