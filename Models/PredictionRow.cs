namespace NextStakeWebApp.Models
{
    public class PredictionRow
    {
        public int Id { get; set; }
        public int GoalSimulatoCasa { get; set; }
        public int GoalSimulatoOspite { get; set; }
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
