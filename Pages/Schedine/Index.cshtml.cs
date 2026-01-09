using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;
using NextStakeWebApp.Data;

namespace NextStakeWebApp.Pages.Schedine
{
    public class IndexModel : PageModel
    {
        private readonly ApplicationDbContext _db;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ReadDbContext _read;


        public IndexModel(ApplicationDbContext db, ReadDbContext read, UserManager<ApplicationUser> userManager)
        {
            _db = db;
            _read = read;
            _userManager = userManager;
        }

        [TempData] public string? StatusMessage { get; set; }

        public List<BetSlip> Items { get; set; } = new();
        public Dictionary<long, MatchInfo> MatchMap { get; set; } = new();

        public class MatchInfo
        {
            public long MatchId { get; set; }
            public string HomeName { get; set; } = "";
            public string AwayName { get; set; } = "";
            public string? HomeLogo { get; set; }
            public string? AwayLogo { get; set; }
            public DateTime? DateUtc { get; set; }
        }


        public async Task OnGetAsync()
        {
            var userId = _userManager.GetUserId(User) ?? "";

            Items = await _db.BetSlips
                .Where(x => x.UserId == userId)
                .Include(x => x.Selections.OrderByDescending(s => s.Id))
                .OrderByDescending(x => x.UpdatedAtUtc)
                .ToListAsync();

            // ✅ Recupero nomi + loghi squadre dal DB READ (Matches + Teams)
            var matchIds = Items
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
                        DateUtc = m.Date
                    }
                ).ToListAsync();

                MatchMap = rows.ToDictionary(x => x.MatchId, x => x);
            }

        }

        public async Task<IActionResult> OnPostCreateDraftAsync(string? title)
        {
            var userId = _userManager.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

            var slip = new BetSlip
            {
                UserId = userId,
                Title = string.IsNullOrWhiteSpace(title) ? "Multipla" : title.Trim(),
                Type = "Draft",
                IsPublic = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            _db.BetSlips.Add(slip);
            await _db.SaveChangesAsync();

            StatusMessage = "✅ Multipla (bozza) creata.";
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostTogglePublicAsync(long id)
        {
            var userId = _userManager.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

            var slip = await _db.BetSlips.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
            if (slip == null) return NotFound();

            slip.IsPublic = !slip.IsPublic;
            slip.UpdatedAtUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            StatusMessage = slip.IsPublic ? "✅ Schedina pubblicata in Community." : "✅ Schedina resa privata.";
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostDeleteAsync(long id)
        {
            var userId = _userManager.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

            var slip = await _db.BetSlips
                .Include(x => x.Selections)
                .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);

            if (slip == null) return NotFound();

            _db.BetSlips.Remove(slip);
            await _db.SaveChangesAsync();

            StatusMessage = "✅ Schedina eliminata.";
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostRemoveSelectionAsync(long selectionId)
        {
            var userId = _userManager.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

            var sel = await _db.BetSelections
                .Include(s => s.BetSlip)
                .FirstOrDefaultAsync(s => s.Id == selectionId);

            if (sel?.BetSlip == null) return NotFound();
            if (sel.BetSlip.UserId != userId) return Forbid();

            _db.BetSelections.Remove(sel);
            sel.BetSlip.UpdatedAtUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            StatusMessage = "✅ Selezione rimossa.";
            return RedirectToPage();
        }
    }
}
