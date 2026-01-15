using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;
using static NextStakeWebApp.Models.BetSlip;

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

        public Dictionary<long, MatchInfo> MatchMap { get; set; } = new();
        public Dictionary<long, int> LikeCounts { get; set; } = new();
        public HashSet<long> LikedByMe { get; set; } = new();

        public HashSet<long> SavedByMe { get; set; } = new();

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
            public int? HomeHalfTimeGoal { get; set; }
            public int? AwayHalfTimeGoal { get; set; }

        }

        public Dictionary<string, string> AuthorMap { get; set; } = new();
        public HashSet<string> FollowedUserIds { get; set; } = new();

        public async Task OnGetAsync()
        {
            AuthorOptions = new List<SelectListItem>
            {
                new SelectListItem { Value = "", Text = "Tutti gli utenti" }
            };

            var me = _userManager.GetUserId(User);

            // Feed pubblico
            var q = _db.BetSlips
                .AsNoTracking()
                .Where(x => x.IsPublic);

            if (!string.IsNullOrWhiteSpace(UserFilter))
            {
                q = q.Where(x => x.UserId == UserFilter);
            }

            PublicSlips = await q
                .Include(x => x.Selections)
                .Include(x => x.Comments)
                .OrderByDescending(x => x.UpdatedAtUtc)
                .Take(50)
                .ToListAsync();

            var slipIds = PublicSlips.Select(x => x.Id).ToList();

            if (slipIds.Count > 0)
            {
                LikeCounts = await _db.BetSlipLikes
                    .AsNoTracking()
                    .Where(l => slipIds.Contains(l.BetSlipId))
                    .GroupBy(l => l.BetSlipId)
                    .Select(g => new { BetSlipId = g.Key, Cnt = g.Count() })
                    .ToDictionaryAsync(x => x.BetSlipId, x => x.Cnt);

                if (!string.IsNullOrWhiteSpace(me))
                {
                    LikedByMe = (await _db.BetSlipLikes
                            .AsNoTracking()
                            .Where(l => slipIds.Contains(l.BetSlipId) && l.UserId == me)
                            .Select(l => l.BetSlipId)
                            .ToListAsync())
                        .ToHashSet();

                    SavedByMe = (await _db.BetSlipSaves
                            .AsNoTracking()
                            .Where(s => slipIds.Contains(s.SourceBetSlipId) && s.SavedByUserId == me)
                            .Join(_db.BetSlips.AsNoTracking(),
                                  s => s.CopiedBetSlipId,
                                  b => b.Id,
                                  (s, b) => new { s.SourceBetSlipId, b.Id })
                            .Select(x => x.SourceBetSlipId)
                            .ToListAsync())
                        .ToHashSet();

                }
            }


            // ✅ auto-archiviazione (serve DB tracking separato)
            await AutoArchiveExpiredPublicAsync(PublicSlips);

            // --- MATCH MAP ---
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
                        AwayGoal = m.AwayGoal,
                        HomeHalfTimeGoal = m.HomeHalftimeGoal,
                        AwayHalfTimeGoal = m.AwayHalftimeGoal

                    }
                ).ToListAsync();

                MatchMap = rows.ToDictionary(x => x.MatchId, x => x);
            }

            // --- AUTHOR MAP ---
            var userIds = PublicSlips
                .Select(s => s.UserId)
                .Concat(PublicSlips.SelectMany(s => s.Comments).Select(c => c.UserId))
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
                    .Select(u => new { u.Id, u.DisplayName, u.Email, u.UserName })
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

        // ✅ auto-archiviazione per feed pubblico (PublicSlips sono NoTracking, quindi aggiorno via query su DB)
        private async Task AutoArchiveExpiredPublicAsync(List<BetSlip> slips)
        {
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

            // matchIds (long) -> Matches.Id (int) nel DB read
            var matchIdsInt = matchIds.Select(x => (int)x).Distinct().ToList();

            var dates = await _read.Matches.AsNoTracking()
                .Where(m => matchIdsInt.Contains(m.Id))
                .Select(m => new { Id = (long)m.Id, m.Date }) // Id long per combaciare con sel.MatchId
                .ToListAsync();

            // Se m.Date è DateTime (non nullable) NON usare .Value
            var matchDateMap = dates.ToDictionary(x => x.Id, x => x.Date);


            var now = DateTime.UtcNow;
            var toUpdate = new List<long>();

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
                    // aggiorno anche in memoria così vedi subito il badge
                    s.ArchivedAtUtc = now;
                    s.AutoArchived = true;
                    toUpdate.Add(s.Id);
                }
            }

            if (toUpdate.Count == 0) return;

            var tracked = await _db.BetSlips
                .Where(x => toUpdate.Contains(x.Id))
                .ToListAsync();

            foreach (var t in tracked)
            {
                t.ArchivedAtUtc = now;
                t.AutoArchived = true;
                t.UpdatedAtUtc = now;
            }

            await _db.SaveChangesAsync();
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
            return RedirectToPage(new { UserFilter });
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

        public async Task<IActionResult> OnPostToggleLikeAsync(long slipId, string? userFilter)
        {
            if (User?.Identity?.IsAuthenticated != true) return Unauthorized();

            var me = _userManager.GetUserId(User);
            if (string.IsNullOrWhiteSpace(me)) return Unauthorized();

            var slip = await _db.BetSlips.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == slipId && x.IsPublic);
            if (slip == null) return NotFound();

            var existing = await _db.BetSlipLikes
                .FirstOrDefaultAsync(x => x.BetSlipId == slipId && x.UserId == me);

            if (existing == null)
            {
                _db.BetSlipLikes.Add(new BetSlipLike
                {
                    BetSlipId = slipId,
                    UserId = me,
                    CreatedAtUtc = DateTime.UtcNow
                });
                await _db.SaveChangesAsync();
                StatusMessage = "👍 Like aggiunto.";
            }
            else
            {
                _db.BetSlipLikes.Remove(existing);
                await _db.SaveChangesAsync();
                StatusMessage = "👎 Like rimosso.";
            }

            return RedirectToPage(new { UserFilter = userFilter });
        }
        public async Task<IActionResult> OnPostSaveToMySlipsAsync(long slipId, string? userFilter)
        {
            if (User?.Identity?.IsAuthenticated != true) return Unauthorized();

            var me = _userManager.GetUserId(User);
            if (string.IsNullOrWhiteSpace(me)) return Unauthorized();

            var source = await _db.BetSlips
                .AsNoTracking()
                .Include(x => x.Selections)
                .FirstOrDefaultAsync(x => x.Id == slipId && x.IsPublic);

            if (source == null) return NotFound();

            // già salvata?
            var already = await _db.BetSlipSaves
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.SourceBetSlipId == slipId && x.SavedByUserId == me);

            if (already != null)
            {
                StatusMessage = "📌 Schedina già salvata tra le tue.";
                return RedirectToPage(new { UserFilter = userFilter });
            }

            // COPIA tra le mie: privata, draft, BLOCCATA (ImportedFromCommunity = true)
            var copy = new BetSlip
            {
                UserId = me,
                Title = string.IsNullOrWhiteSpace(source.Title) ? $"Schedina di @{source.UserId}" : source.Title,
                Type = "Saved",
                IsPublic = false,                 // NON pubblicabile
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                Result = BetSlipResult.None,
                ArchivedAtUtc = null,
                AutoArchived = false,

                ImportedFromCommunity = true,
                SourceBetSlipId = source.Id,
                SourceUserId = source.UserId,

                Selections = source.Selections.Select(s => new BetSelection
                {
                    MatchId = s.MatchId,
                    Market = s.Market,
                    Pick = s.Pick,
                    Odd = s.Odd,
                    Note = s.Note

                }).ToList()
            };

            _db.BetSlips.Add(copy);
            await _db.SaveChangesAsync();

            _db.BetSlipSaves.Add(new BetSlipSave
            {
                SourceBetSlipId = source.Id,
                SavedByUserId = me,
                CopiedBetSlipId = copy.Id,
                CreatedAtUtc = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();

            StatusMessage = "✅ Schedina salvata tra le tue (non ripubblicabile).";
            return RedirectToPage(new { UserFilter = userFilter });
        }
        public async Task<IActionResult> OnPostUnsaveAsync(long slipId, string? userFilter)
        {
            if (User?.Identity?.IsAuthenticated != true) return Unauthorized();

            var me = _userManager.GetUserId(User);
            if (string.IsNullOrWhiteSpace(me)) return Unauthorized();

            // Prendo la riga di salvataggio (serve per sapere anche CopiedBetSlipId)
            var save = await _db.BetSlipSaves
                .FirstOrDefaultAsync(x => x.SourceBetSlipId == slipId && x.SavedByUserId == me);

            if (save == null)
            {
                StatusMessage = "ℹ️ Nessuna schedina salvata da rimuovere.";
                return RedirectToPage(new { UserFilter = userFilter });
            }

            // Se esiste una copia nel mio profilo, la elimino (con selezioni)
            if (save.CopiedBetSlipId.HasValue)
            {
                var copied = await _db.BetSlips
                    .Include(x => x.Selections)
                    .FirstOrDefaultAsync(x => x.Id == save.CopiedBetSlipId.Value && x.UserId == me);

                if (copied != null)
                {
                    _db.BetSlips.Remove(copied);
                }
            }

            // Rimuovo il link di salvataggio
            _db.BetSlipSaves.Remove(save);

            await _db.SaveChangesAsync();

            StatusMessage = "🗑 Schedina rimossa dalle tue.";
            return RedirectToPage(new { UserFilter = userFilter });
        }

        public enum SelectionOutcome
        {
            Pending,   // match non finito
            Won,
            Lost,

            Push,      // rimborso (quota 1.00)
            HalfWon,   // metà vinta + metà push
            HalfLost,  // metà persa + metà push

            Unknown    // pick non riconosciuto / dati incompleti
        }
        public SelectionOutcome EvaluateSelectionOutcome(BetSelection sel, MatchInfo? mi)
        {
            if (sel == null || mi == null) return SelectionOutcome.Unknown;

            // ✅ Override manuale dell’utente
            if (sel.ManualOutcome == 1) return SelectionOutcome.Won;
            if (sel.ManualOutcome == 2) return SelectionOutcome.Lost;

            var status = (mi.StatusShort ?? "").Trim().ToUpperInvariant();
            var finished = status is "FT" or "AET" or "PEN";

            // Se non è finita -> in corso
            if (!finished) return SelectionOutcome.Pending;

            // Se finita ma senza goal -> non valutabile
            if (!mi.HomeGoal.HasValue || !mi.AwayGoal.HasValue) return SelectionOutcome.Unknown;

            // ✅ Score FT di default
            int hFT = mi.HomeGoal.Value;
            int aFT = mi.AwayGoal.Value;

            // ✅ Score HT disponibile
            int? hHT = mi.HomeHalfTimeGoal;
            int? aHT = mi.AwayHalfTimeGoal;

            // useremo FT o HT in base al mercato
            (int h, int a) = GetScoreForMarket(sel?.Market, hFT, aFT, hHT, aHT);
            int tot = h + a;

            // ✅ VIA INGLESE: passiamo anche lo score "corrente" (h,a) già scelto da GetScoreForMarket
            var marketOutcome = TryEvaluateOddsApiMarket(sel, hFT, aFT, hHT, aHT, h, a);

            if (marketOutcome != null)
                return marketOutcome.Value;


            // Normalizzo pick
            string raw = (sel.Pick ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw)) return SelectionOutcome.Unknown;

            string pick = raw.ToUpperInvariant();
            pick = pick.Replace("GOAL", "GG");
            pick = pick.Replace("NO GOAL", "NG");
            pick = pick.Replace("NOGOAL", "NG");

            // Rimuovo spazi
            pick = Regex.Replace(pick, @"\s+", "");

            // Split combo: 1+O2.5, 1X+UNDER2.5, 12+OV15 ecc.
            var parts = Regex.Split(pick, @"[+\|&;,]+")
                             .Where(x => !string.IsNullOrWhiteSpace(x))
                             .Select(x => x.Trim())
                             .ToList();

            // Se non c'erano separatori, parts = [pick]
            if (parts.Count == 0) parts.Add(pick);

            // Valuto ogni "gamba" della combo
            var legOutcomes = new List<SelectionOutcome>();

            foreach (var leg in parts)
            {
                legOutcomes.Add(EvaluateLeg(leg, h, a, tot));
            }

            // Regola combinazione:
            // - se una è persa => persa
            // - altrimenti se una è unknown => unknown
            // - altrimenti vinta (a match finito)
            if (legOutcomes.Any(x => x == SelectionOutcome.Lost)) return SelectionOutcome.Lost;
            if (legOutcomes.Any(x => x == SelectionOutcome.HalfLost)) return SelectionOutcome.HalfLost;
            if (legOutcomes.Any(x => x == SelectionOutcome.Unknown)) return SelectionOutcome.Unknown;
            if (legOutcomes.Any(x => x == SelectionOutcome.Pending)) return SelectionOutcome.Pending;

            if (legOutcomes.Any(x => x == SelectionOutcome.HalfWon)) return SelectionOutcome.HalfWon;

            // se tutte Push -> Push, altrimenti Won
            return legOutcomes.All(x => x == SelectionOutcome.Push)
                ? SelectionOutcome.Push
                : SelectionOutcome.Won;

        }
        private static (int h, int a) GetScoreForMarket(string? market, int hFT, int aFT, int? hHT, int? aHT)
        {
            var m = (market ?? "").Trim().ToUpperInvariant();

            // Se il mercato è 1st half / first half -> usa HT
            bool isFirstHalf =
                m.Contains("1ST HALF") ||
                m.Contains("FIRST HALF") ||
                m.Contains("1H") ||
                m.Contains("HT") ||
                m.Contains("HALF TIME") ||
    m.Contains("HALFTIME"); ;

            if (isFirstHalf && hHT.HasValue && aHT.HasValue)
                return (hHT.Value, aHT.Value);

            return (hFT, aFT);
        }
        private static bool LooksLegacyPick(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return false;
            var x = v.Trim().ToUpperInvariant().Replace(" ", "");

            // tipici token legacy
            if (x is "1" or "2" or "X" or "1X" or "X2" or "12" or "GG" or "NG") return true;

            // Over/Under legacy: O2.5, U2.5, OV25, UN15 ecc.
            if (Regex.IsMatch(x, @"^(O|U|OV|UN|OVER|UNDER)\d")) return true;

            // Multigol legacy: MG2-4, MGCASA1-2 ecc.
            if (x.StartsWith("MG")) return true;

            // Correct score legacy: "2-1" senza prefissi e senza contesto inglese
            // (se vuoi tenerlo legacy, altrimenti puoi farlo gestire anche all'inglese)
            if (Regex.IsMatch(x, @"^\d+\-\d+$")) return true;

            return false;
        }
        private static bool LooksEnglishPick(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return false;
            var x = v.Trim().ToUpperInvariant();

            // classici inglesi
            if (x is "HOME" or "AWAY" or "DRAW" or "YES" or "NO" or "Y" or "N") return true;

            // combo tipo AWAY/OVER2.5 ecc.
            if (x.Contains("/")) return true;

            // "Over 2.5" / "Under 1.5"
            if (x.StartsWith("OVER") || x.StartsWith("UNDER")) return true;

            // "Home -0.25" / "Away +0.75"
            if ((x.Contains("HOME") || x.Contains("AWAY")) && Regex.IsMatch(x, @"[+\-]\d")) return true;

            // "1:0" / "0:0" tipico correct score API
            if (Regex.IsMatch(x, @"\d+\s*[:\-]\s*\d+")) return true;

            return false;
        }
        private static string NormalizeEnglishPick(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            return s.Trim().ToUpperInvariant().Replace(" ", "");
        }

        private static string NormalizeEnglishMarket(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = Regex.Replace(s.Trim(), @"\s*\(\d+\)\s*$", "");
            return s.ToUpperInvariant();
        }

        // esito 1X2 in inglese: HOME/AWAY/DRAW
        private static string GetResultHAD(int h, int a)
        {
            if (h > a) return "HOME";
            if (h < a) return "AWAY";
            return "DRAW";
        }
        private SelectionOutcome? TryEvaluateOddsApiMarket(
           BetSelection sel,
           int hFT, int aFT,
           int? hHT, int? aHT,
           int hScoped, int aScoped
       )
        {
            if (sel == null) return null;

            var marketRaw = (sel.Market ?? "").Trim();
            var valueRaw = (sel.Pick ?? "").Trim();

            if (string.IsNullOrWhiteSpace(marketRaw) || string.IsNullOrWhiteSpace(valueRaw))
                return null;

            // Se sembra una pick "legacy" (1/X/2, GG/NG, O2.5, MG2-4, ecc) la gestisce EvaluateLeg
            if (LooksLegacyPick(valueRaw))
                return null;

            // Normalizzo
            var m = NormalizeEnglishMarket(marketRaw);
            var v = valueRaw.Trim();

            // score già scelto a monte (FT o HT) tramite GetScoreForMarket
            int h = hScoped;
            int a = aScoped;
            int tot = h + a;

            // Supporto valutazione 2nd half se serve
            bool has2H = TryGetSecondHalfGoals(hFT, aFT, hHT, aHT, out int h2, out int a2);
            int tot2 = h2 + a2;

            // Helper: ritorna RESULT in inglese (HOME/AWAY/DRAW) sullo score "scoped"
            string had = GetResultHAD(h, a);

            // ---------------------------
            // 1) MATCH WINNER / 1X2 (HOME/DRAW/AWAY)
            // ---------------------------
            if (m.Contains("MATCH WINNER") || m == "1X2" || m.Contains("FULL TIME RESULT") || m.Contains("RESULT"))
            {
                var vn = NormalizeEnglishPick(v);
                if (vn is "HOME" or "AWAY" or "DRAW")
                    return (had == vn) ? SelectionOutcome.Won : SelectionOutcome.Lost;

                // Se value è "1" "X" "2" qui potresti gestirlo, ma dovrebbe essere legacy -> quindi null
                return null;
            }

            // ---------------------------
            // 2) DOUBLE CHANCE (HOME/DRAW, AWAY/DRAW, HOME/AWAY)
            // ---------------------------
            if (m.Contains("DOUBLE CHANCE"))
            {
                var vn = NormalizeEnglishPick(v);

                // formati tipici: "HOME/DRAW", "AWAY/DRAW", "HOME/AWAY"
                if (vn.Contains("/"))
                {
                    var parts = vn.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                    {
                        return (had == parts[0] || had == parts[1])
                            ? SelectionOutcome.Won
                            : SelectionOutcome.Lost;
                    }
                }

                // fallback: "HOME OR DRAW" ecc
                vn = vn.Replace("OR", "/");
                if (vn.Contains("/"))
                {
                    var parts = vn.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        return parts.Contains(had) ? SelectionOutcome.Won : SelectionOutcome.Lost;
                    }
                }

                return null;
            }

            // ---------------------------
            // 3) BOTH TEAMS TO SCORE (YES/NO)
            // ---------------------------
            if (m.Contains("BOTH TEAMS TO SCORE") || m.Contains("BTTS"))
            {
                var vn = NormalizeEnglishPick(v);
                if (vn is "YES" or "NO")
                {
                    bool gg = (h > 0 && a > 0);
                    return (vn == "YES" ? gg : !gg) ? SelectionOutcome.Won : SelectionOutcome.Lost;
                }
                return null;
            }

            // ---------------------------
            // 4) TOTAL GOALS (OVER/UNDER X)  - "Goal Line" / "Total Goals"
            // ---------------------------
            if (m.Contains("GOAL LINE") || m.Contains("TOTAL GOALS") || m.Contains("TOTAL"))
            {
                var ou = ParseOverUnderFromValue(v);
                if (ou != null)
                {
                    var (isOver, line) = ou.Value;

                    // linee .25/.75 -> asian total con HalfWon/HalfLost/Push
                    if (m.Contains("ASIAN") || m.Contains("GOAL LINE"))
                        return EvaluateAsianTotal(isOver, line, tot);

                    // classico: Over vince se tot > line, Under vince se tot < line
                    if (tot > line) return isOver ? SelectionOutcome.Won : SelectionOutcome.Lost;
                    if (tot < line) return isOver ? SelectionOutcome.Lost : SelectionOutcome.Won;
                    return SelectionOutcome.Push;
                }

                // Odd/Even goals (se ti arriva dentro TOTAL)
                var oe = ParseOddEvenValue(v);
                if (oe != null)
                {
                    bool isOddWanted = oe.Value;
                    bool isOdd = (tot % 2) == 1;
                    return (isOdd == isOddWanted) ? SelectionOutcome.Won : SelectionOutcome.Lost;
                }

                return null;
            }

            // ---------------------------
            // 5) ASIAN HANDICAP (Home -0.25 / Away +0.75)
            // ---------------------------
            if (m.Contains("ASIAN HANDICAP") || m.Contains("HANDICAP"))
            {
                var ah = ParseAsianHandicapValue(marketRaw, v);
                if (ah != null)
                {
                    var (side, line) = ah.Value;
                    return EvaluateAsianHandicap(side, line, h, a);
                }
                return null;
            }

            // ---------------------------
            // 6) DRAW NO BET (HOME/AWAY) -> DRAW = PUSH
            // ---------------------------
            if (m.Contains("DRAW NO BET") || m.Contains("DNB"))
            {
                var vn = NormalizeEnglishPick(v);
                if (vn is "HOME" or "AWAY")
                {
                    if (had == "DRAW") return SelectionOutcome.Push;
                    return (had == vn) ? SelectionOutcome.Won : SelectionOutcome.Lost;
                }
                return null;
            }

            // ---------------------------
            // 7) CORRECT SCORE (1:0 / 2-1 ecc.)
            // ---------------------------
            if (m.Contains("CORRECT SCORE") || m.Contains("EXACT SCORE"))
            {
                var vn = v.Trim();
                var ms = Regex.Match(vn, @"^\s*(\d+)\s*[:\-]\s*(\d+)\s*$");
                if (ms.Success)
                {
                    int eh = int.Parse(ms.Groups[1].Value);
                    int ea = int.Parse(ms.Groups[2].Value);
                    return (h == eh && a == ea) ? SelectionOutcome.Won : SelectionOutcome.Lost;
                }
                return null;
            }

            // ---------------------------
            // 8) RESULT / TOTAL GOALS (HOME/OVER 2.5, DRAW/UNDER 3.5 ecc)
            // ---------------------------
            if (m.Contains("RESULT/TOTAL") || m.Contains("RESULT / TOTAL") || m.Contains("RESULT/TOTAL GOALS"))
            {
                var vn = v.Trim().ToUpperInvariant();

                // "HOME/OVER 2.5"
                if (vn.Contains("/"))
                {
                    var parts = vn.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                    {
                        var wantRes = parts[0].Trim();
                        var wantOU = parts[1].Trim();

                        // risultato
                        if (wantRes is not ("HOME" or "AWAY" or "DRAW"))
                            return null;

                        // OU
                        var ou = ParseOverUnderFromValue(wantOU);
                        if (ou == null) return null;
                        var (isOver, line) = ou.Value;

                        bool okRes = (had == wantRes);
                        bool okOU =
                            (tot > line) ? isOver :
                            (tot < line) ? !isOver :
                            false; // se tot == line, non è né over né under (salvo asian)

                        return (okRes && okOU) ? SelectionOutcome.Won : SelectionOutcome.Lost;
                    }
                }
                return null;
            }

            // ---------------------------
            // 9) RESULT / BOTH TEAMS TO SCORE (HOME/YES ecc)
            // ---------------------------
            if (m.Contains("RESULT/BOTH TEAMS") || m.Contains("RESULT / BOTH TEAMS") || m.Contains("RESULT/BTTS"))
            {
                var vn = v.Trim().ToUpperInvariant();
                if (vn.Contains("/"))
                {
                    var parts = vn.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                    {
                        var wantRes = parts[0].Trim();
                        var wantGG = parts[1].Trim();

                        if (wantRes is not ("HOME" or "AWAY" or "DRAW")) return null;
                        if (wantGG is not ("YES" or "NO")) return null;

                        bool gg = (h > 0 && a > 0);
                        bool okRes = (had == wantRes);
                        bool okGG = (wantGG == "YES") ? gg : !gg;

                        return (okRes && okGG) ? SelectionOutcome.Won : SelectionOutcome.Lost;
                    }
                }
                return null;
            }

            // ---------------------------
            // 10) TOTAL GOALS / BOTH TEAMS TO SCORE (OVER 2.5/YES ecc)
            // ---------------------------
            if (m.Contains("TOTAL GOALS/BOTH TEAMS") || m.Contains("TOTAL GOALS / BOTH TEAMS"))
            {
                var vn = v.Trim().ToUpperInvariant();
                if (vn.Contains("/"))
                {
                    var parts = vn.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                    {
                        var partOU = parts[0].Trim();
                        var partGG = parts[1].Trim();

                        var ou = ParseOverUnderFromValue(partOU);
                        if (ou == null) return null;
                        var (isOver, line) = ou.Value;

                        if (partGG is not ("YES" or "NO")) return null;

                        bool gg = (h > 0 && a > 0);
                        bool okGG = (partGG == "YES") ? gg : !gg;

                        bool okOU =
                            (tot > line) ? isOver :
                            (tot < line) ? !isOver :
                            false;

                        return (okOU && okGG) ? SelectionOutcome.Won : SelectionOutcome.Lost;
                    }
                }
                return null;
            }

            // ---------------------------
            // 11) TEAM TO SCORE (HOME YES/NO, AWAY YES/NO) - molto comune
            // ---------------------------
            if (m.Contains("TEAM TO SCORE") || m.Contains("TO SCORE"))
            {
                var vn = v.Trim().ToUpperInvariant().Replace(" ", "");
                // esempi: "HOMEYES", "AWAYNO", "HOME/YES"
                vn = vn.Replace("/", "");

                bool? wantYes = null;
                if (vn.EndsWith("YES")) { wantYes = true; vn = vn.Substring(0, vn.Length - 3); }
                else if (vn.EndsWith("NO")) { wantYes = false; vn = vn.Substring(0, vn.Length - 2); }

                if (wantYes == null) return null;

                // lato
                string side = vn.Contains("AWAY") ? "AWAY" : (vn.Contains("HOME") ? "HOME" : "");
                if (string.IsNullOrEmpty(side)) return null;

                bool scored = side == "HOME" ? (h > 0) : (a > 0);
                return (wantYes.Value ? scored : !scored) ? SelectionOutcome.Won : SelectionOutcome.Lost;
            }

            // ---------------------------
            // 12) ODD/EVEN (Total Goals Odd/Even) se mercato dedicato
            // ---------------------------
            if (m.Contains("ODD") || m.Contains("EVEN"))
            {
                var oe = ParseOddEvenValue(v);
                if (oe != null)
                {
                    bool isOddWanted = oe.Value;
                    bool isOdd = (tot % 2) == 1;
                    return (isOdd == isOddWanted) ? SelectionOutcome.Won : SelectionOutcome.Lost;
                }
                return null;
            }

            // ---------------------------
            // 13) EXACT GOALS (Total Goals = 2, 3, 4+ ecc)
            // ---------------------------
            if (m.Contains("EXACT GOALS") || m.Contains("TOTAL GOALS - EXACT") || m.Contains("EXACT TOTAL GOALS"))
            {
                var ex = ParseExactGoalsValue(v);
                if (ex != null)
                {
                    var (mode, n) = ex.Value;
                    if (n == null) return null;

                    bool ok = mode switch
                    {
                        "PLUS" => tot >= n.Value,
                        _ => tot == n.Value
                    };
                    return ok ? SelectionOutcome.Won : SelectionOutcome.Lost;
                }
                return null;
            }

            // ---------------------------
            // 14) SECOND HALF markets (se pick/market te li porta)
            // ---------------------------
            if (m.Contains("2ND HALF") || m.Contains("SECOND HALF"))
            {
                // se non ho HT non posso calcolare 2H
                if (!has2H) return SelectionOutcome.Unknown;

                // esempi: Match Winner 2H -> HOME/DRAW/AWAY
                var vn = NormalizeEnglishPick(v);

                if (m.Contains("MATCH WINNER") || m.Contains("RESULT"))
                {
                    string had2 = GetResultHAD(h2, a2);
                    if (vn is "HOME" or "AWAY" or "DRAW")
                        return (had2 == vn) ? SelectionOutcome.Won : SelectionOutcome.Lost;

                    return null;
                }

                // Total Goals 2H -> Over/Under
                if (m.Contains("GOAL LINE") || m.Contains("TOTAL GOALS") || m.Contains("TOTAL"))
                {
                    var ou2 = ParseOverUnderFromValue(v);
                    if (ou2 != null)
                    {
                        var (isOver, line) = ou2.Value;
                        // uso asian total se goal line/asian
                        if (m.Contains("ASIAN") || m.Contains("GOAL LINE"))
                            return EvaluateAsianTotal(isOver, line, tot2);

                        if (tot2 > line) return isOver ? SelectionOutcome.Won : SelectionOutcome.Lost;
                        if (tot2 < line) return isOver ? SelectionOutcome.Lost : SelectionOutcome.Won;
                        return SelectionOutcome.Push;
                    }
                }

                return null;
            }

            // Nessun match sul mercato inglese -> lascia valutare legacy (o Unknown)
            return null;
        }

        private static bool TryGetSecondHalfGoals(int hFT, int aFT, int? hHT, int? aHT, out int h2, out int a2)
        {
            h2 = a2 = 0;
            if (!hHT.HasValue || !aHT.HasValue) return false;

            h2 = hFT - hHT.Value;
            a2 = aFT - aHT.Value;

            // safety: se dati incoerenti, non valutare
            if (h2 < 0 || a2 < 0) return false;
            return true;
        }
        private static (string part, int? n)? ParseExactGoalsValue(string value)
        {
            // Supporta:
            // "0" "1" "2" "3" ...
            // "4+" "5+" (se ti capita)
            // "OVER 2.5" ecc NON qui
            if (string.IsNullOrWhiteSpace(value)) return null;

            var v = value.Trim().ToUpperInvariant().Replace(" ", "");
            if (v.EndsWith("+"))
            {
                var num = v.TrimEnd('+');
                if (int.TryParse(num, out var nPlus))
                    return ("PLUS", nPlus);
                return null;
            }

            if (int.TryParse(v, out var n))
                return ("EQ", n);

            return null;
        }
        private static bool? ParseOddEvenValue(string value)
        {
            // supporta: "ODD" / "EVEN" / "YES(ODD)" ecc se arrivano strani
            if (string.IsNullOrWhiteSpace(value)) return null;
            var v = value.Trim().ToUpperInvariant().Replace(" ", "");

            if (v.Contains("ODD")) return true;
            if (v.Contains("EVEN")) return false;

            // alcune API usano "YES"=ODD "NO"=EVEN (raro) -> non assumo
            return null;
        }
        private (bool isOver, double line)? ParseOverUnderFromValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var x = value.Trim().ToUpperInvariant();
            x = x.Replace(",", "."); // sicurezza

            bool? isOver = null;
            if (x.StartsWith("OVER")) { isOver = true; x = x.Substring(4).Trim(); }
            else if (x.StartsWith("UNDER")) { isOver = false; x = x.Substring(5).Trim(); }
            else if (x.StartsWith("OV")) { isOver = true; x = x.Substring(2).Trim(); }
            else if (x.StartsWith("UN")) { isOver = false; x = x.Substring(2).Trim(); }

            if (isOver == null) return null;

            // cerco la prima cifra tipo 2.5
            var m = Regex.Match(x, @"(\d+(\.\d+)?)");
            if (!m.Success) return null;

            if (!double.TryParse(m.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var line))
                return null;

            return (isOver.Value, line);
        }
        private (string side, double line)? ParseAsianHandicapValue(string market, string value)
        {
            // Side: prova dal value ("Home -0.25")
            var v = (value ?? "").Trim().ToUpperInvariant();
            v = v.Replace(",", ".");

            string? side = null;

            if (v.Contains("HOME")) side = "HOME";
            else if (v.Contains("AWAY")) side = "AWAY";

            // se side non è nel value, provo dal market
            if (side == null)
            {
                var m = (market ?? "").ToUpperInvariant();
                if (m.Contains("HOME")) side = "HOME";
                else if (m.Contains("AWAY")) side = "AWAY";
            }

            if (side == null) return null;

            // estraggo numero con segno
            var num = Regex.Match(v, @"([+\-]?\d+(\.\d+)?)");
            if (!num.Success) return null;

            if (!double.TryParse(num.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var line))
                return null;

            return (side, line);
        }
        private SelectionOutcome EvaluateAsianHandicap(string side, double line, int h, int a)
        {
            // gestisco linee .25 e .75 spezzandole
            bool isQuarter = Math.Abs(line * 4 - Math.Round(line * 4)) < 1e-9 && (Math.Abs(line % 0.5) > 1e-9);
            // sopra: valori tipo x.25 / x.75

            if (!isQuarter)
                return EvaluateAsianHandicapSingle(side, line, h, a);

            // split:
            // +0.25 => (0.0, +0.5) ; -0.25 => (0.0, -0.5)
            // +0.75 => (+0.5, +1.0); -0.75 => (-0.5, -1.0)
            double lower = Math.Floor(line * 2) / 2.0;     // arrotondo a step 0.5 verso -inf
            double upper = lower + 0.5;

            // Per i negativi funziona correttamente perché floor va verso -inf:
            // -0.25 => floor(-0.5)=-0.5, upper=0.0 -> invertiamo per ottenere (0.0, -0.5)?
            // meglio costruire esplicitamente:
            if (line > 0)
            {
                lower = Math.Floor(line * 2) / 2.0;  // es 0.25 -> 0.0; 0.75 -> 0.5
                upper = lower + 0.5;
            }
            else
            {
                upper = Math.Ceiling(line * 2) / 2.0; // es -0.25 -> 0.0; -0.75 -> -0.5
                lower = upper - 0.5;                  // -> -0.5 / -1.0
            }

            var r1 = EvaluateAsianHandicapSingle(side, lower, h, a);
            var r2 = EvaluateAsianHandicapSingle(side, upper, h, a);

            return CombineTwoAsianResults(r1, r2);
        }

        private SelectionOutcome EvaluateAsianHandicapSingle(string side, double line, int h, int a)
        {
            // confronto handicap-adjusted
            double left = (side == "HOME") ? (h + line) : (a + line);
            double right = (side == "HOME") ? a : h;

            if (left > right) return SelectionOutcome.Won;
            if (left < right) return SelectionOutcome.Lost;
            return SelectionOutcome.Push;
        }
        private SelectionOutcome EvaluateAsianTotal(bool isOver, double line, int tot)
        {
            // linee .25 e .75 => split in due (come AH)
            bool isQuarter = Math.Abs(line * 4 - Math.Round(line * 4)) < 1e-9 && (Math.Abs(line % 0.5) > 1e-9);
            if (!isQuarter)
                return EvaluateAsianTotalSingle(isOver, line, tot);

            double lower, upper;

            if (line > 0)
            {
                lower = Math.Floor(line * 2) / 2.0;
                upper = lower + 0.5;
            }
            else
            {
                // non dovrebbe arrivare mai per goal line, ma per sicurezza:
                upper = Math.Ceiling(line * 2) / 2.0;
                lower = upper - 0.5;
            }

            var r1 = EvaluateAsianTotalSingle(isOver, lower, tot);
            var r2 = EvaluateAsianTotalSingle(isOver, upper, tot);
            return CombineTwoAsianResults(r1, r2); // ✅ già ce l’hai
        }

        private SelectionOutcome EvaluateAsianTotalSingle(bool isOver, double line, int tot)
        {
            // Totale vs linea: Over vince se tot > line, Push se tot == line, Lost se tot < line
            if (tot > line) return isOver ? SelectionOutcome.Won : SelectionOutcome.Lost;
            if (tot < line) return isOver ? SelectionOutcome.Lost : SelectionOutcome.Won;
            return SelectionOutcome.Push;
        }
        private SelectionOutcome CombineTwoAsianResults(SelectionOutcome r1, SelectionOutcome r2)
        {
            // due gambe: Won/Push/Lost
            if (r1 == SelectionOutcome.Won && r2 == SelectionOutcome.Won) return SelectionOutcome.Won;
            if (r1 == SelectionOutcome.Lost && r2 == SelectionOutcome.Lost) return SelectionOutcome.Lost;

            if ((r1 == SelectionOutcome.Won && r2 == SelectionOutcome.Push) ||
                (r2 == SelectionOutcome.Won && r1 == SelectionOutcome.Push))
                return SelectionOutcome.HalfWon;

            if ((r1 == SelectionOutcome.Lost && r2 == SelectionOutcome.Push) ||
                (r2 == SelectionOutcome.Lost && r1 == SelectionOutcome.Push))
                return SelectionOutcome.HalfLost;

            // Push+Push
            if (r1 == SelectionOutcome.Push && r2 == SelectionOutcome.Push) return SelectionOutcome.Push;

            // casi strani (Won+Lost non dovrebbe succedere con split corretto)
            return SelectionOutcome.Unknown;
        }
        private (string wanted, double line)? ParseHandicapResultValue(string value)
        {
            var x = (value ?? "").Trim().ToUpperInvariant().Replace(",", ".");
            // esempio: "AWAY +1" / "DRAW -1" / "HOME -2"
            string? wanted = null;
            if (x.StartsWith("HOME")) wanted = "HOME";
            else if (x.StartsWith("AWAY")) wanted = "AWAY";
            else if (x.StartsWith("DRAW")) wanted = "DRAW";
            if (wanted == null) return null;

            var num = Regex.Match(x, @"([+\-]?\d+(\.\d+)?)");
            if (!num.Success) return null;

            if (!double.TryParse(num.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var line))
                return null;

            // qui ci aspettiamo interi (±1, ±2)
            return (wanted, line);
        }
        private (string target, int handicap, string outcomeWanted)? ParseHandicapResultValueWithTarget(string value)
        {
            var x = (value ?? "").Trim().ToUpperInvariant().Replace(",", ".");
            // "AWAY -1" / "DRAW +1" / "HOME +2"
            string? outcomeWanted = null;
            if (x.StartsWith("HOME")) outcomeWanted = "HOME";
            else if (x.StartsWith("AWAY")) outcomeWanted = "AWAY";
            else if (x.StartsWith("DRAW")) outcomeWanted = "DRAW";
            if (outcomeWanted == null) return null;

            var num = Regex.Match(x, @"([+\-]?\d+)");
            if (!num.Success) return null;
            int handicap = int.Parse(num.Groups[1].Value);

            // target handicap: se outcomeWanted è HOME o AWAY => target coincide
            // se è DRAW, target non è esplicito nel value: in pratica è un 3-way handicap del match.
            // per gestirlo senza impazzire: assumo target = HOME (standard mercato), così "DRAW -1" = pareggio dopo HOME-1 (cioè HOME vince di 1).
            string target = (outcomeWanted == "AWAY") ? "AWAY" : "HOME";

            return (target, handicap, outcomeWanted);
        }
        private (bool isOver, double line)? ParseOverUnderLeg(string leg)
        {
            // normalizza
            var x = leg.ToUpperInvariant();

            // riconosco prefissi
            // OVER: O / OV / OVER
            // UNDER: U / UN / UNDER
            bool? isOver = null;

            if (x.StartsWith("OVER")) { isOver = true; x = x.Substring(4); }
            else if (x.StartsWith("OV")) { isOver = true; x = x.Substring(2); }
            else if (x.StartsWith("O")) { isOver = true; x = x.Substring(1); }
            else if (x.StartsWith("UNDER")) { isOver = false; x = x.Substring(5); }
            else if (x.StartsWith("UN")) { isOver = false; x = x.Substring(2); }
            else if (x.StartsWith("U")) { isOver = false; x = x.Substring(1); }

            if (isOver == null) return null;

            // x ora dovrebbe contenere la linea tipo 2.5 / 25 / 15 / 3,5 ecc.
            if (string.IsNullOrWhiteSpace(x)) return null;

            // sostituisco eventuale virgola
            x = x.Replace(",", ".");

            // se è tipo "25" -> 2.5 (supporto esplicito 15/25/35/45)
            if (Regex.IsMatch(x, @"^\d{2}$"))
            {
                if (x is "15" or "25" or "35" or "45")
                    x = x[0] + ".5";
            }

            // se è tipo "1.5" / "2.5" ...
            if (!double.TryParse(x, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var line))
                return null;

            return (isOver.Value, line);
        }
        private (string scope, int min, int max)? ParseMultiGoalLeg(string leg)
        {
            if (string.IsNullOrWhiteSpace(leg)) return null;

            var x = leg.ToUpperInvariant();

            // accettiamo prefissi:
            // MG (default = TOTAL)
            // MGTOT, MGTOTALE
            // MGCASA, MGHOME
            // MGOSPITE, MGAWAY
            if (!x.StartsWith("MG")) return null;

            // Determino ambito (TOTAL / HOME / AWAY)
            string scope = "TOTAL"; // default

            if (x.StartsWith("MGTOT")) { scope = "TOTAL"; x = x.Substring(5); }       // "MGTOT"
            else if (x.StartsWith("MGTOTALE")) { scope = "TOTAL"; x = x.Substring(8); } // "MGTOTALE"
            else if (x.StartsWith("MGCASA")) { scope = "HOME"; x = x.Substring(6); }  // "MGCASA"
            else if (x.StartsWith("MGHOME")) { scope = "HOME"; x = x.Substring(6); } // "MGHOME"
            else if (x.StartsWith("MGOSPITE")) { scope = "AWAY"; x = x.Substring(8); } // "MGOSPITE"
            else if (x.StartsWith("MGAWAY")) { scope = "AWAY"; x = x.Substring(6); }  // "MGAWAY"
            else
            {
                // solo "MG" => totale
                x = x.Substring(2);
            }

            // Ora mi aspetto "min-max" tipo "2-4" oppure "1-1"
            var m = Regex.Match(x, @"^(\d+)\-(\d+)$");
            if (!m.Success) return null;

            int min = int.Parse(m.Groups[1].Value); 
            int max = int.Parse(m.Groups[2].Value);
            if (min > max) (min, max) = (max, min);

            return (scope, min, max);
        }
        private SelectionOutcome EvaluateLeg(string leg, int h, int a, int tot)
        {
            if (string.IsNullOrWhiteSpace(leg)) return SelectionOutcome.Unknown;

            // ===== RISULTATO ESATTO =====
            // accetto: "2-1" / "CS2-1" / "ESATTO2-1" / "RISULTATOESATTO2-1"
            var legForExact = leg;
            bool explicitExact =
                legForExact.Contains("ESATTO") ||
                legForExact.Contains("RISULTATOESATTO") ||
                legForExact.StartsWith("CS");

            var mExact = Regex.Match(legForExact, @"(\d+)\-(\d+)");
            if (mExact.Success && (explicitExact || Regex.IsMatch(legForExact, @"^\d+\-\d+$") || legForExact.StartsWith("CS")))
            {
                int eh = int.Parse(mExact.Groups[1].Value);
                int ea = int.Parse(mExact.Groups[2].Value);
                return (h == eh && a == ea) ? SelectionOutcome.Won : SelectionOutcome.Lost;
            }

            // ===== 1X2 =====
            if (leg is "1" or "X" or "2")
            {
                var esito = (h > a) ? "1" : (h < a) ? "2" : "X";
                return (esito == leg) ? SelectionOutcome.Won : SelectionOutcome.Lost;
            }

            // ===== DOPPIA CHANCE =====
            if (leg is "1X" or "X2" or "12")
            {
                var esito = (h > a) ? "1" : (h < a) ? "2" : "X";
                return leg.Contains(esito) ? SelectionOutcome.Won : SelectionOutcome.Lost;
            }

            // ===== GG / NG =====
            if (leg is "GG" or "NG")
            {
                bool gg = (h > 0 && a > 0);
                return (leg == "GG" ? gg : !gg) ? SelectionOutcome.Won : SelectionOutcome.Lost;
            }

            // ===== OVER / UNDER (molte abbreviazioni) =====
            // accetto: OVER2.5, OV2.5, O2.5, O25, OV25, UNDER1.5, UN15, U35 ecc.
            // linee supportate: 1.5 / 2.5 / 3.5 / 4.5 (ma se arriva 5.5 la gestisce uguale)
            var ou = ParseOverUnderLeg(leg);
            if (ou != null)
            {
                var (isOver, line) = ou.Value;
                bool ok = isOver ? (tot > line) : (tot < line); // Under 2.5 => 0,1,2 quindi tot < 2.5
                return ok ? SelectionOutcome.Won : SelectionOutcome.Lost;
            }

            // ===== MULTIGOL (se vuoi: MULTIGOL1-3) =====
            var mMG = Regex.Match(leg, @"^MULTIGOL(\d+)\-(\d+)$");
            if (mMG.Success)
            {
                int min = int.Parse(mMG.Groups[1].Value);
                int max = int.Parse(mMG.Groups[2].Value);
                bool ok = tot >= min && tot <= max;
                return ok ? SelectionOutcome.Won : SelectionOutcome.Lost;
            }
            // ===== MG (MULTIGOL) - Totale / Casa / Ospite =====
            // Supporto esempi (spazi già rimossi a monte):
            // "MG2-4" (totale)
            // "MGTOT2-4" / "MGTOTALE2-4"
            // "MGCASA1-2" / "MGHOME1-2"
            // "MGOSPITE0-1" / "MGAWAY0-1"
            var mg = ParseMultiGoalLeg(leg);
            if (mg != null)
            {
                var (scope, min, max) = mg.Value;

                int value = scope switch
                {
                    "HOME" => h,
                    "AWAY" => a,
                    _ => tot // TOTAL
                };

                bool ok = value >= min && value <= max;
                return ok ? SelectionOutcome.Won : SelectionOutcome.Lost;
            }

            return SelectionOutcome.Unknown;
        }

    }
}
