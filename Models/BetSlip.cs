using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NextStakeWebApp.Models
{
    public class BetSlip
    {
        [Key]
        public long Id { get; set; }

        [Required]
        public string UserId { get; set; } = "";

        [MaxLength(80)]
        public string? Title { get; set; }

        /// <summary>
        /// Draft = multipla in costruzione / Single = singola / Multiple = multipla chiusa
        /// </summary>
        [Required]
        [MaxLength(20)]
        public string Type { get; set; } = "Single"; // Single | Draft | Multiple

        public bool IsPublic { get; set; } = false;

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        public List<BetSelection> Selections { get; set; } = new();
        public List<BetComment> Comments { get; set; } = new();

        public enum BetSlipResult
        {
            None = 0,
            Win = 1,
            Loss = 2
        }

        public BetSlipResult Result { get; set; } = BetSlipResult.None;

        // Quando diventa archiviata (manuale o automatica)
        public DateTime? ArchivedAtUtc { get; set; }

        // True se archiviata automaticamente (solo info)
        public bool AutoArchived { get; set; } = false;

    }
}
