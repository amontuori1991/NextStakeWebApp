namespace NextStakeWebApp.Models
{
    public class BetSlipLike
    {
        public long Id { get; set; }
        public long BetSlipId { get; set; }
        public string UserId { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }

        public BetSlip? BetSlip { get; set; }
    }
}
