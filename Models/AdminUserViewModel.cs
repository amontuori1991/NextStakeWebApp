namespace NextStakeWebApp.Models
{
    public class AdminUserViewModel
    {
        public string Id { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Plan { get; set; } = string.Empty;
        public DateTime? PlanExpiresAtUtc { get; set; }
        public bool IsApproved { get; set; }
    }
}
