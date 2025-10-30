namespace NextStakeWebApp.Models
{
    public class MatchItem
    {
        public long Id { get; set; }
        public string League { get; set; } = "";
        public string Home { get; set; } = "";
        public string Away { get; set; } = "";
        public DateTime Kickoff { get; set; }
        public string Country { get; set; } = "";
    }
}
