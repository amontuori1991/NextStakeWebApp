using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;
using static NextStakeWebApp.Models.BetSlip;
using System.Text.RegularExpressions;
using System.Globalization;

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

        public Dictionary<string, string> SourceAuthorMap { get; set; } = new();

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
            var sourceUserIds = all
    .Where(s => s.ImportedFromCommunity && !string.IsNullOrWhiteSpace(s.SourceUserId))
    .Select(s => s.SourceUserId!)
    .Distinct()
    .ToList();

            if (sourceUserIds.Count > 0)
            {
                var users = await _db.Users
                    .AsNoTracking()
                    .Where(u => sourceUserIds.Contains(u.Id))
                    .Select(u => new { u.Id, u.UserName, u.DisplayName, u.Email })
                    .ToListAsync();

                SourceAuthorMap = users.ToDictionary(
                    x => x.Id,
                    x => !string.IsNullOrWhiteSpace(x.UserName)
                        ? x.UserName!
                        : (!string.IsNullOrWhiteSpace(x.DisplayName) ? x.DisplayName! : (x.Email ?? "Utente"))
                );
            }

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
            if (slip.ImportedFromCommunity)
            {
                StatusMessage = "⛔ Questa schedina è stata salvata dalla Community e non può essere ripubblicata.";
                return RedirectToPage();
            }

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
        public async Task<IActionResult> OnPostSetSelectionOutcomeAsync(long selectionId, string outcome)
        {
            if (User?.Identity?.IsAuthenticated != true) return Unauthorized();

            var me = _userManager.GetUserId(User);
            if (string.IsNullOrWhiteSpace(me)) return Unauthorized();

            var sel = await _db.BetSelections
                .Include(x => x.BetSlip)
                .FirstOrDefaultAsync(x => x.Id == selectionId);

            if (sel?.BetSlip == null) return NotFound();
            if (sel.BetSlip.UserId != me) return Forbid();

            // outcome: win / loss / auto
            if (outcome == "win") sel.ManualOutcome = 1;
            else if (outcome == "loss") sel.ManualOutcome = 2;
            else if (outcome == "auto") sel.ManualOutcome = null;
            else return BadRequest();

            sel.BetSlip.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            StatusMessage = "✅ Esito del singolo evento aggiornato.";
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
        public enum SelectionOutcome
        {
            Pending,   // match non finito
            Won,
            Lost,
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

            int h = mi.HomeGoal.Value;
            int a = mi.AwayGoal.Value;
            int tot = h + a;

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
            if (legOutcomes.Any(x => x == SelectionOutcome.Unknown)) return SelectionOutcome.Unknown;
            if (legOutcomes.Any(x => x == SelectionOutcome.Pending)) return SelectionOutcome.Pending; // safety
            return SelectionOutcome.Won;
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


    }
}
