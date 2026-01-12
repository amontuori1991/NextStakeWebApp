using System.Globalization;
using System.Text.RegularExpressions;
using NextStakeWebApp.Models;

namespace NextStakeWebApp.Services
{
    public static class BetPickEvaluator
    {
        public enum SelectionOutcome
        {
            Pending,   // match non finito
            Won,
            Lost,
            Unknown    // pick non riconosciuto / dati incompleti
        }

        /// <summary>
        /// Valuta una selezione (supporta: 1X2, doppia chance, GG/NG, Over/Under con abbreviazioni,
        /// combo tipo 1+O2.5, X2+MG2-4, MG casa/ospite/tot, risultato esatto, ecc.)
        /// Usa ManualOutcome se presente (1=Won, 2=Lost).
        /// </summary>
        public static SelectionOutcome EvaluateSelectionOutcome(
            BetSelection sel,
            string? statusShort,
            int? homeGoal,
            int? awayGoal)
        {
            if (sel == null) return SelectionOutcome.Unknown;

            // ✅ Override manuale dell’utente (prima di tutto)
            if (sel.ManualOutcome == 1) return SelectionOutcome.Won;
            if (sel.ManualOutcome == 2) return SelectionOutcome.Lost;

            var status = (statusShort ?? "").Trim().ToUpperInvariant();
            var finished = status is "FT" or "AET" or "PEN";

            // Non finita -> in corso
            if (!finished) return SelectionOutcome.Pending;

            // Finita ma senza goal -> non valutabile
            if (!homeGoal.HasValue || !awayGoal.HasValue) return SelectionOutcome.Unknown;

            int h = homeGoal.Value;
            int a = awayGoal.Value;
            int tot = h + a;

            // Pick
            string raw = (sel.Pick ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw)) return SelectionOutcome.Unknown;

            string pick = raw.ToUpperInvariant();
            pick = pick.Replace("NO GOAL", "NG");
            pick = pick.Replace("NOGOAL", "NG");
            pick = pick.Replace("GOAL", "GG");

            // Rimuovo spazi
            pick = Regex.Replace(pick, @"\s+", "");

            // Split combo: 1+O2.5, 1X+UNDER2.5, 12+OV15 ecc.
            var parts = Regex.Split(pick, @"[+\|&;,]+")
                             .Where(x => !string.IsNullOrWhiteSpace(x))
                             .Select(x => x.Trim())
                             .ToList();

            if (parts.Count == 0) parts.Add(pick);

            var legOutcomes = new List<SelectionOutcome>();
            foreach (var leg in parts)
                legOutcomes.Add(EvaluateLeg(leg, h, a, tot));

            if (legOutcomes.Any(x => x == SelectionOutcome.Lost)) return SelectionOutcome.Lost;
            if (legOutcomes.Any(x => x == SelectionOutcome.Unknown)) return SelectionOutcome.Unknown;
            if (legOutcomes.Any(x => x == SelectionOutcome.Pending)) return SelectionOutcome.Pending; // safety

            return SelectionOutcome.Won;
        }

        private static SelectionOutcome EvaluateLeg(string leg, int h, int a, int tot)
        {
            if (string.IsNullOrWhiteSpace(leg)) return SelectionOutcome.Unknown;

            // ===== RISULTATO ESATTO =====
            // "2-1" / "CS2-1" / "ESATTO2-1" / "RISULTATOESATTO2-1"
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

            // ===== OVER / UNDER (abbreviazioni) =====
            // OVER2.5, OV2.5, O2.5, O25, OV25, UNDER1.5, UN15, U35, ecc.
            var ou = ParseOverUnderLeg(leg);
            if (ou != null)
            {
                var (isOver, line) = ou.Value;
                bool ok = isOver ? (tot > line) : (tot < line);
                return ok ? SelectionOutcome.Won : SelectionOutcome.Lost;
            }

            // ===== MULTIGOL (vecchio stile) =====
            var mMG = Regex.Match(leg, @"^MULTIGOL(\d+)\-(\d+)$");
            if (mMG.Success)
            {
                int min = int.Parse(mMG.Groups[1].Value);
                int max = int.Parse(mMG.Groups[2].Value);
                bool ok = tot >= min && tot <= max;
                return ok ? SelectionOutcome.Won : SelectionOutcome.Lost;
            }

            // ===== MG (totale/casa/ospite) =====
            // "MG2-4" (totale) / "MGCASA1-2" / "MGOSPITE0-1" / "MGTOT2-4"
            var mg = ParseMultiGoalLeg(leg);
            if (mg != null)
            {
                var (scope, min, max) = mg.Value;

                int value = scope switch
                {
                    "HOME" => h,
                    "AWAY" => a,
                    _ => tot
                };

                bool ok = value >= min && value <= max;
                return ok ? SelectionOutcome.Won : SelectionOutcome.Lost;
            }

            return SelectionOutcome.Unknown;
        }

        private static (bool isOver, double line)? ParseOverUnderLeg(string leg)
        {
            var x = leg.ToUpperInvariant();

            bool? isOver = null;

            if (x.StartsWith("OVER")) { isOver = true; x = x.Substring(4); }
            else if (x.StartsWith("OV")) { isOver = true; x = x.Substring(2); }
            else if (x.StartsWith("O")) { isOver = true; x = x.Substring(1); }
            else if (x.StartsWith("UNDER")) { isOver = false; x = x.Substring(5); }
            else if (x.StartsWith("UN")) { isOver = false; x = x.Substring(2); }
            else if (x.StartsWith("U")) { isOver = false; x = x.Substring(1); }

            if (isOver == null) return null;
            if (string.IsNullOrWhiteSpace(x)) return null;

            x = x.Replace(",", ".");

            // "25" -> "2.5" (supporto 15/25/35/45)
            if (Regex.IsMatch(x, @"^\d{2}$") && (x is "15" or "25" or "35" or "45"))
                x = x[0] + ".5";

            if (!double.TryParse(x, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var line))
                return null;

            return (isOver.Value, line);
        }

        private static (string scope, int min, int max)? ParseMultiGoalLeg(string leg)
        {
            if (string.IsNullOrWhiteSpace(leg)) return null;

            var x = leg.ToUpperInvariant();
            if (!x.StartsWith("MG")) return null;

            string scope = "TOTAL";

            if (x.StartsWith("MGTOT")) { scope = "TOTAL"; x = x.Substring(5); }
            else if (x.StartsWith("MGTOTALE")) { scope = "TOTAL"; x = x.Substring(8); }
            else if (x.StartsWith("MGCASA")) { scope = "HOME"; x = x.Substring(6); }
            else if (x.StartsWith("MGHOME")) { scope = "HOME"; x = x.Substring(6); }
            else if (x.StartsWith("MGOSPITE")) { scope = "AWAY"; x = x.Substring(8); }
            else if (x.StartsWith("MGAWAY")) { scope = "AWAY"; x = x.Substring(6); }
            else
            {
                x = x.Substring(2); // solo "MG"
            }

            var m = Regex.Match(x, @"^(\d+)\-(\d+)$");
            if (!m.Success) return null;

            int min = int.Parse(m.Groups[1].Value);
            int max = int.Parse(m.Groups[2].Value);
            if (min > max) (min, max) = (max, min);

            return (scope, min, max);
        }
    }
}
