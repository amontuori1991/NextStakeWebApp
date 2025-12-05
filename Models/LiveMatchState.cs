using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NextStakeWebApp.Models
{
    [Table("LiveMatchStates")] // nome tabella come in SQL
    public class LiveMatchState
    {
        [Key]                   // 👈 Dichiariamo esplicitamente la PK
        public int MatchId { get; set; }

        [Required]
        [MaxLength(16)]
        public string LastStatus { get; set; } = "";

        public int? LastHome { get; set; }

        public int? LastAway { get; set; }

        public int? LastElapsed { get; set; }

        public DateTime LastUpdatedUtc { get; set; }
    }
}
