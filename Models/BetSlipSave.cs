namespace NextStakeWebApp.Models
{
    public class BetSlipSave
    {
        public long Id { get; set; }

        public long SourceBetSlipId { get; set; }
        public string SavedByUserId { get; set; } = "";

        public long? CopiedBetSlipId { get; set; }
        public DateTime CreatedAtUtc { get; set; }

        public BetSlip? SourceBetSlip { get; set; }
        public BetSlip? CopiedBetSlip { get; set; }
    }
}
