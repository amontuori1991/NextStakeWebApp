using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NextStakeWebApp.Models
{
    [Table("PushSubscriptions")]
    public class PushSubscription
    {
        public int Id { get; set; }

        [MaxLength(450)]
        public string? UserId { get; set; }

        [Required]
        public string Endpoint { get; set; } = "";

        [Required]
        public string P256Dh { get; set; } = "";

        [Required]
        public string Auth { get; set; } = "";

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public bool IsActive { get; set; } = true;

        public bool MatchNotificationsEnabled { get; set; } = true;

        public ApplicationUser? User { get; set; }
    }
}
