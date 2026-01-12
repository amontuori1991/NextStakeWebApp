using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace NextStakeWebApp.Pages.Community
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

        [TempData] public string? StatusMessage { get; set; }
        [BindProperty(SupportsGet = true)]
        public string? UserFilter { get; set; }

        public List<SelectListItem> AuthorOptions { get; set; } = new();


        public List<BetSlip> PublicSlips { get; set; } = new();

        // MatchId -> info match (nomi + loghi + data)
        public Dictionary<long, MatchInfo> MatchMap { get; set; } = new();

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


        // UserId -> nome autore
        public Dictionary<string, string> AuthorMap { get; set; } = new();

        public HashSet<string> FollowedUserIds { get; set; } = new();


        public async Task OnGetAsync()
        {

            AuthorOptions = new List<SelectListItem>
{
    new SelectListItem { Value = "", Text = "Tutti gli utenti" }
};
            var me = _userManager.GetUserId(User);

            // Feed: schedine pubbliche, con selezioni + commenti
            var q = _db.BetSlips
                .AsNoTracking()
                .Where(x => x.IsPublic);

            if (!string.IsNullOrWhiteSpace(UserFilter))
            {
                // filtro per UserId
                q = q.Where(x => x.UserId == UserFilter);
            }

            PublicSlips = await q
                .Include(x => x.Selections)
                .Include(x => x.Comments)
                .OrderByDescending(x => x.UpdatedAtUtc)
                .Take(50)
                .ToListAsync();


            // --- MATCH MAP (nome/loghi) ---
            var matchIds = PublicSlips
                .SelectMany(s => s.Selections)
                .Select(sel => sel.MatchId)
                .Distinct()
                .ToList();

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

            // --- AUTHOR MAP (UserId -> DisplayName/email) ---
            var userIds = PublicSlips
                .Select(s => s.UserId)
                .Concat(
                    PublicSlips.SelectMany(s => s.Comments).Select(c => c.UserId)
                )
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToList();
            if (!string.IsNullOrWhiteSpace(me))
            {
                FollowedUserIds = (await _db.UserFollows
                        .AsNoTracking()
                        .Where(f => f.FollowerUserId == me)
                        .Select(f => f.FollowedUserId)
                        .ToListAsync())
                    .ToHashSet();

            }


            if (userIds.Count > 0)
            {
                var users = await _db.Users
                    .AsNoTracking()
                    .Where(u => userIds.Contains(u.Id))
                    .Select(u => new
                    {
                        u.Id,
                        u.DisplayName,
                        u.Email,
                        u.UserName
                    })
                    .ToListAsync();

                AuthorMap = users.ToDictionary(
                    x => x.Id,
                    x => !string.IsNullOrWhiteSpace(x.UserName)
                        ? x.UserName!
                        : (!string.IsNullOrWhiteSpace(x.DisplayName) ? x.DisplayName! : "Utente")
                );
                AuthorOptions.AddRange(
                    users
                        .OrderBy(u => u.UserName ?? u.DisplayName ?? u.Email)
                        .Select(u => new SelectListItem
                        {
                            Value = u.Id,
                            Text = !string.IsNullOrWhiteSpace(u.UserName)
                                ? u.UserName!
                                : (!string.IsNullOrWhiteSpace(u.DisplayName) ? u.DisplayName! : "Utente")
                        })
                );


            }
        }
        public async Task<IActionResult> OnPostFollowAsync(string targetUserId)
        {
            if (User?.Identity?.IsAuthenticated != true) return Unauthorized();

            var me = _userManager.GetUserId(User);
            if (string.IsNullOrWhiteSpace(me)) return Unauthorized();
            if (string.IsNullOrWhiteSpace(targetUserId)) return BadRequest();
            if (me == targetUserId) return BadRequest();

            var exists = await _db.UserFollows.AnyAsync(x => x.FollowerUserId == me && x.FollowedUserId == targetUserId);
            if (!exists)
            {
                _db.UserFollows.Add(new UserFollow
                {
                    FollowerUserId = me,
                    FollowedUserId = targetUserId,
                    CreatedAtUtc = DateTime.UtcNow
                });
                await _db.SaveChangesAsync();
            }

            StatusMessage = "✅ Utente seguito.";
            return RedirectToPage(new { UserFilter }); // mantiene filtro se presente
        }

        public async Task<IActionResult> OnPostUnfollowAsync(string targetUserId)
        {
            if (User?.Identity?.IsAuthenticated != true) return Unauthorized();

            var me = _userManager.GetUserId(User);
            if (string.IsNullOrWhiteSpace(me)) return Unauthorized();
            if (string.IsNullOrWhiteSpace(targetUserId)) return BadRequest();

            var row = await _db.UserFollows.FirstOrDefaultAsync(x => x.FollowerUserId == me && x.FollowedUserId == targetUserId);
            if (row != null)
            {
                _db.UserFollows.Remove(row);
                await _db.SaveChangesAsync();
            }

            StatusMessage = "✅ Utente rimosso dai seguiti.";
            return RedirectToPage(new { UserFilter });
        }

        public async Task<IActionResult> OnPostCommentAsync(long slipId, string? text)
        {
            if (User?.Identity?.IsAuthenticated != true)
                return Unauthorized();

            var userId = _userManager.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId))
                return Unauthorized();

            text = (text ?? "").Trim();
            if (text.Length < 1)
            {
                StatusMessage = "⚠️ Commento vuoto.";
                return RedirectToPage();
            }

            if (text.Length > 600)
            {
                StatusMessage = "⚠️ Commento troppo lungo (max 600).";
                return RedirectToPage();
            }

            var slip = await _db.BetSlips.FirstOrDefaultAsync(x => x.Id == slipId && x.IsPublic);
            if (slip == null) return NotFound();

            _db.BetComments.Add(new BetComment
            {
                BetSlipId = slipId,
                UserId = userId,
                Text = text,
                CreatedAtUtc = DateTime.UtcNow
            });

            slip.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            StatusMessage = "✅ Commento inviato.";
            return RedirectToPage();
        }
    }
}
