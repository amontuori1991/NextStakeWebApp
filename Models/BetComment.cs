using System.ComponentModel.DataAnnotations;

namespace NextStakeWebApp.Models
{
    public class BetComment
    {
        [Key]
        public long Id { get; set; }

        [Required]
        public long BetSlipId { get; set; }

        public BetSlip? BetSlip { get; set; }

        [Required]
        public string UserId { get; set; } = "";

        [Required]
        [MaxLength(600)]
        public string Text { get; set; } = "";

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
