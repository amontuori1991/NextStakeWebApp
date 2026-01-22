using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NextStakeWebApp.Models
{
    [Table("NextMatchPredictionsCache")] // <-- la tua nuova tabella
    public class PredictionDbRow
    {
        [Key]

        public long MatchId { get; set; }


        public DateTime? EventDate { get; set; }

        public int GoalSimulatiCasa { get; set; }
        public int GoalSimulatiOspite { get; set; }
        public int TotaleGoalSimulati { get; set; }

        public string? Esito { get; set; }
        public string? OverUnderRange { get; set; }

        public decimal? Over1_5 { get; set; }
        public decimal? Over2_5 { get; set; }
        public decimal? Over3_5 { get; set; }

        public string? GG_NG { get; set; }

        public string? MultigoalCasa { get; set; }
        public string? MultigoalOspite { get; set; }
        public string? ComboFinale { get; set; }
    }
}
