using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;
using static NextStakeWebApp.Models.BetSlip;

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

        // 👇 Liste separate
        public List<BetSlip> ActiveItems { get; set; } = new();
        public List<BetSlip> ArchivedItems { get; set; } = new();

        public Dictionary<long, MatchInfo> MatchMap { get; set; } = new();
        public class MatchInfo
        {
            public long MatchId { get; set; }

            public string HomeName { get; set; } = "";
            public string AwayName { get; set; } = "";

            public string? HomeLogo { get; set; }
            public string? AwayLogo { get; set; }

            public DateTime? DateUtc { get; set; }

            // ✅ RISULTATO MATCH
            public string? StatusShort { get; set; }
            public int? HomeGoal { get; set; }
            public int? AwayGoal { get; set; }
        }


        public async Task OnGetAsync()
        {
            var userId = _userManager.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId))
            {
                // Se non loggato, non mostra nulla
                ActiveItems = new();
                ArchivedItems = new();
                return;
            }

            // 1) carico tutte le schedine dell’utente con selezioni (tracking ON perché poi potrei archiviare)
            var all = await _db.BetSlips
                .Where(x => x.UserId == userId)
                .Include(x => x.Selections)
                .OrderByDescending(x => x.UpdatedAtUtc)
                .ToListAsync();

            // 2) auto-archiviazione (12h dal kickoff dell’ultima partita) per quelle senza esito
            await AutoArchiveExpiredAsync(all);

            // 3) split attive/archiviate
            ActiveItems = all.Where(x => x.ArchivedAtUtc == null).ToList();
            ArchivedItems = all.Where(x => x.ArchivedAtUtc != null).ToList();

            // 4) match map per loghi/nomi
            var matchIds = all
                .SelectMany(s => s.Selections)
                .Select(sel => sel.MatchId)
                .Distinct()
                .ToList();

            if (matchIds.Count > 0)
            {
                var matchIdsInt = matchIds.Select(x => (int)x).Distinct().ToList();

                var rows = await (
                    from m in _read.Matches.AsNoTracking()
                    join th in _read.Teams.AsNoTracking() on m.HomeId equals th.Id
                    join ta in _read.Teams.AsNoTracking() on m.AwayId equals ta.Id
                    where matchIdsInt.Contains(m.Id)
                    select new MatchInfo
                    {
                        MatchId = (long)m.Id,          // ✅ cast a long
                        HomeName = th.Name ?? "",
                        AwayName = ta.Name ?? "",
                        HomeLogo = th.Logo,
                        AwayLogo = ta.Logo,
                        DateUtc = m.Date,               // ok anche se DateTime
                        StatusShort = m.StatusShort,
                        HomeGoal = m.HomeGoal,
                        AwayGoal = m.AwayGoal
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
                UpdatedAtUtc = DateTime.UtcNow,
                Result = BetSlipResult.None,
                ArchivedAtUtc = null,
                AutoArchived = false
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

        // ✅ Set esito manuale + archivia immediata
        public async Task<IActionResult> OnPostSetResultAsync(long slipId, string result)
        {
            if (User?.Identity?.IsAuthenticated != true) return Unauthorized();

            var me = _userManager.GetUserId(User);
            if (string.IsNullOrWhiteSpace(me)) return Unauthorized();

            var slip = await _db.BetSlips.FirstOrDefaultAsync(x => x.Id == slipId);
            if (slip == null) return NotFound();

            if (slip.UserId != me) return Forbid();

            if (result == "win")
                slip.Result = BetSlipResult.Win;
            else if (result == "loss")
                slip.Result = BetSlipResult.Loss;
            else
                return BadRequest();

            slip.ArchivedAtUtc = DateTime.UtcNow;
            slip.AutoArchived = false;
            slip.UpdatedAtUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            StatusMessage = "✅ Esito salvato e schedina archiviata.";
            return RedirectToPage();
        }
        // 🔄 Riapre una schedina archiviata (manuale o automatica)
        public async Task<IActionResult> OnPostReopenAsync(long slipId)
        {
            if (User?.Identity?.IsAuthenticated != true)
                return Unauthorized();

            var me = _userManager.GetUserId(User);
            if (string.IsNullOrWhiteSpace(me))
                return Unauthorized();

            var slip = await _db.BetSlips
                .FirstOrDefaultAsync(x => x.Id == slipId && x.UserId == me);

            if (slip == null)
                return NotFound();

            // reset completo stato
            slip.Result = BetSlipResult.None;
            slip.ArchivedAtUtc = null;
            slip.AutoArchived = false;
            slip.UpdatedAtUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            StatusMessage = "🔄 Schedina riaperta correttamente.";
            return RedirectToPage();
        }

        // ✅ Auto-archiviazione: 12h dopo il kickoff dell’ultima partita della schedina
        private async Task AutoArchiveExpiredAsync(List<BetSlip> slips)
        {
            // prendo solo quelle candidabili
            var candidates = slips
                .Where(s => s.Result == BetSlipResult.None && s.ArchivedAtUtc == null)
                .ToList();

            if (candidates.Count == 0) return;

            var matchIds = candidates
                .SelectMany(s => s.Selections)
                .Select(x => x.MatchId)
                .Distinct()
                .ToList();

            if (matchIds.Count == 0) return;

            var matchIdsInt = matchIds.Select(x => (int)x).Distinct().ToList();

            var dates = await _read.Matches.AsNoTracking()
                .Where(m => matchIdsInt.Contains(m.Id))
                .Select(m => new { Id = (long)m.Id, m.Date })   // ✅ Id long
                .ToListAsync();

            var matchDateMap = dates.ToDictionary(x => x.Id, x => x.Date);




            var now = DateTime.UtcNow;
            var toUpdateIds = new List<long>();

            foreach (var s in candidates)
            {
                DateTime? lastKickoffUtc = null;

                foreach (var sel in s.Selections)
                {
                    if (matchDateMap.TryGetValue(sel.MatchId, out var dt))
                    {
                        if (lastKickoffUtc == null || dt > lastKickoffUtc.Value)
                            lastKickoffUtc = dt;
                    }
                }

                if (lastKickoffUtc == null) continue;

                if (now >= lastKickoffUtc.Value.AddHours(12))
                {
                    // aggiorno anche in memoria (così la pagina split funziona subito)
                    s.ArchivedAtUtc = now;
                    s.AutoArchived = true;
                    toUpdateIds.Add(s.Id);
                }
            }

            if (toUpdateIds.Count == 0) return;

            // update DB (tracking già attivo perché all viene da _db con tracking)
            var tracked = slips.Where(x => toUpdateIds.Contains(x.Id)).ToList();
            foreach (var t in tracked)
            {
                t.ArchivedAtUtc = now;
                t.AutoArchived = true;
                t.UpdatedAtUtc = now;
            }

            await _db.SaveChangesAsync();
        }
    }
}
