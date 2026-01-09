using System.ComponentModel.DataAnnotations;

namespace NextStakeWebApp.Models
{
    public class BetSelection
    {
        [Key]
        public long Id { get; set; }

        [Required]
        public long BetSlipId { get; set; }

        public BetSlip? BetSlip { get; set; }

        [Required]
        public long MatchId { get; set; }

        [Required]
        [MaxLength(120)]
        public string Pick { get; set; } = ""; // es: "Over 2.5", "GG", "1X", ecc.

        public decimal? Odd { get; set; } // opzionale

        [MaxLength(250)]
        public string? Note { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
