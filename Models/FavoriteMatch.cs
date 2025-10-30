using System.ComponentModel.DataAnnotations;

namespace NextStakeWebApp.Models
{
    public class FavoriteMatch
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public string UserId { get; set; } = string.Empty;

        [Required]
        public long MatchId { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
