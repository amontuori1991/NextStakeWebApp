using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;

namespace NextStakeWebApp.Pages.Seguiti
{
    public class IndexModel : PageModel
    {
        private readonly ApplicationDbContext _db;
        private readonly ReadDbContext _read;
        private readonly UserManager<ApplicationUser> _userManager;

        public IndexModel(ApplicationDbContext db, ReadDbContext read, UserManager<ApplicationUser> userManager)
        {
            _db = db;
            _read = read;
            _userManager = userManager;
        }

        public List<BetSlip> Items { get; set; } = new();

        public class MatchInfo
        {
            public long MatchId { get; set; }
            public string HomeName { get; set; } = "";
            public string AwayName { get; set; } = "";
            public string? HomeLogo { get; set; }
            public string? AwayLogo { get; set; }
            public DateTime? DateUtc { get; set; }
            public string? StatusShort { get; set; }
            public int? HomeGoal { get; set; }
            public int? AwayGoal { get; set; }
        }

        public Dictionary<long, MatchInfo> MatchMap { get; set; } = new();
        public Dictionary<string, string> AuthorMap { get; set; } = new();

        public async Task OnGetAsync()
        {
            var me = _userManager.GetUserId(User) ?? "";

            var followedIds = await _db.UserFollows
                .AsNoTracking()
                .Where(f => f.FollowerUserId == me)
                .Select(f => f.FollowedUserId)
                .ToListAsync();

            if (followedIds.Count == 0)
            {
                Items = new();
                return;
            }

            Items = await _db.BetSlips
                .AsNoTracking()
                .Where(s => s.IsPublic && followedIds.Contains(s.UserId))
                .Include(s => s.Selections)
                .Include(s => s.Comments)
                .OrderByDescending(s => s.UpdatedAtUtc)
                .Take(50)
                .ToListAsync();

            // AuthorMap (username first)
            var users = await _db.Users.AsNoTracking()
                .Where(u => followedIds.Contains(u.Id))
                .Select(u => new { u.Id, u.UserName, u.DisplayName })
                .ToListAsync();

            AuthorMap = users.ToDictionary(
                x => x.Id,
                x => !string.IsNullOrWhiteSpace(x.UserName)
                    ? x.UserName!
                    : (!string.IsNullOrWhiteSpace(x.DisplayName) ? x.DisplayName! : "Utente")
            );

            // MatchMap
            var matchIds = Items.SelectMany(s => s.Selections).Select(x => x.MatchId).Distinct().ToList();
            if (matchIds.Count > 0)
            {
                var rows = await (
                    from m in _read.Matches.AsNoTracking()
                    join th in _read.Teams.AsNoTracking() on m.HomeId equals th.Id
                    join ta in _read.Teams.AsNoTracking() on m.AwayId equals ta.Id
                    where matchIds.Contains(m.Id)
                    select new MatchInfo
                    {
                        MatchId = m.Id,
                        HomeName = th.Name ?? "",
                        AwayName = ta.Name ?? "",
                        HomeLogo = th.Logo,
                        AwayLogo = ta.Logo,
                        DateUtc = m.Date,
                        StatusShort = m.StatusShort,
                        HomeGoal = m.HomeGoal,
                        AwayGoal = m.AwayGoal
                    }
                ).ToListAsync();

                MatchMap = rows.ToDictionary(x => x.MatchId, x => x);
            }
        }
    }
}
