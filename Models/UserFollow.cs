using System.ComponentModel.DataAnnotations;

namespace NextStakeWebApp.Models
{
    public class UserFollow
    {
        [Required]
        public string FollowerUserId { get; set; } = "";

        [Required]
        public string FollowedUserId { get; set; } = "";

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
