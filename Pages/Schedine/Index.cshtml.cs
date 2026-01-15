using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;
using static NextStakeWebApp.Models.BetSlip;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Text;
using System.IO.Compression;
using SkiaSharp;
using Svg.Skia;


namespace NextStakeWebApp.Pages.Schedine
{
    public class IndexModel : PageModel
    {
        private readonly ApplicationDbContext _db;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ReadDbContext _read;
        // ================== IMMAGINI SCHEDINA (PAGINAZIONE) ==================

        // Numero massimo di selezioni per pagina immagine
        public const int SvgPageSize = 8;

        // Calcola quante pagine servono in base alle selezioni
        public static int CalcSvgPages(int selectionCount)
        {
            if (selectionCount <= 0) return 1;
            return (int)Math.Ceiling(selectionCount / (double)SvgPageSize);
        }

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

            public int? HomeHalfTimeGoal { get; set; }
            public int? AwayHalfTimeGoal { get; set; }

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
                        MatchId = (long)m.Id,
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

        public async Task<IActionResult> OnGetSlipImagePngAsync(long slipId, int page = 1)
        {
            // 🔐 solo plan=1
            if (!(User?.Identity?.IsAuthenticated ?? false) || !User.HasClaim("plan", "1"))
                return Forbid();

            var userId = _userManager.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId))
                return Forbid();

            var slip = await _db.BetSlips
                .Include(s => s.Selections)
                .FirstOrDefaultAsync(s => s.Id == slipId && s.UserId == userId);

            if (slip == null) return NotFound();

            var selections = slip.Selections.OrderBy(x => x.Id).ToList();
            var pages = CalcSvgPages(selections.Count);
            if (page < 1) page = 1;
            if (page > pages) page = pages;

            var pageSelections = selections
                .Skip((page - 1) * SvgPageSize)
                .Take(SvgPageSize)
                .ToList();

            var svg = await BuildSlipSvgAsync(slip, pageSelections, page, pages);
            var pngBytes = SvgToPng(svg, widthPx: 1080);

            // ✅ iOS: meglio "image/png" così compare “Salva immagine”
            Response.Headers["Content-Disposition"] = $@"attachment; filename=""schedina_{slip.Id}_p{page}.png""";
            return File(pngBytes, "image/png");
        }

        public async Task<IActionResult> OnGetSlipImageZipAsync(long slipId)
        {
            // 🔐 solo plan=1
            if (!(User?.Identity?.IsAuthenticated ?? false) || !User.HasClaim("plan", "1"))
                return Forbid();

            var userId = _userManager.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId))
                return Forbid();

            // carico schedina dell’utente
            var slip = await _db.BetSlips
                .Include(s => s.Selections)
                .FirstOrDefaultAsync(s => s.Id == slipId && s.UserId == userId);

            if (slip == null) return NotFound();

            var selections = slip.Selections.OrderBy(x => x.Id).ToList();

            // Calcola quante pagine servono
            var pages = CalcSvgPages(selections.Count);

            // genero PNG per ogni pagina e metto in ZIP
            using var zipStream = new MemoryStream();
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                for (int pageIndex = 0; pageIndex < pages; pageIndex++)
                {
                    var pageSelections = selections
                        .Skip(pageIndex * SvgPageSize)
                        .Take(SvgPageSize)
                        .ToList();

                    // genera SVG per questa pagina
                    var svg = await BuildSlipSvgAsync(slip, pageSelections, pageIndex + 1, pages);

                    // converte SVG->PNG
                    var pngBytes = SvgToPng(svg, widthPx: 1080);

                    var entryName = $"schedina_{slip.Id}_p{pageIndex + 1}.png";
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);

                    using var entryStream = entry.Open();
                    await entryStream.WriteAsync(pngBytes, 0, pngBytes.Length);
                }
            }

            zipStream.Position = 0;
            var zipBytes = zipStream.ToArray();

            Response.Headers["Content-Disposition"] = $@"attachment; filename=""schedina_{slip.Id}.zip""";
            return File(zipBytes, "application/zip");
        }
        private async Task<string> BuildSlipSvgAsync(BetSlip slip, List<BetSelection> pageSelections, int pageNo, int pages)
        {
            // match map locale (con loghi/nomi)
            var matchIds = pageSelections.Select(x => x.MatchId).Distinct().ToList();
            var matchIdsInt = matchIds.Select(x => (int)x).Distinct().ToList();

            var rows = await (
                from m in _read.Matches.AsNoTracking()
                join th in _read.Teams.AsNoTracking() on m.HomeId equals th.Id
                join ta in _read.Teams.AsNoTracking() on m.AwayId equals ta.Id
                where matchIdsInt.Contains(m.Id)
                select new MatchInfo
                {
                    MatchId = (long)m.Id,
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

            var map = rows.ToDictionary(x => x.MatchId, x => x);

            // quota totale schedina
            decimal? totalOdd = null;
            var all = slip.Selections.ToList();
            if (all.Count > 0 && all.All(x => x.Odd.HasValue))
            {
                decimal t = 1m;
                foreach (var s in all) t *= s.Odd!.Value;
                totalOdd = t;
            }

            string Esc(string? s) => System.Security.SecurityElement.Escape(s ?? "") ?? "";

            List<string> WrapByWords(string text, int maxCharsPerLine = 18, int maxLines = 2)
            {
                if (string.IsNullOrWhiteSpace(text)) return new List<string> { "" };

                text = text.Trim();
                var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

                // Se una singola "parola" è lunghissima, la spezzo in 2 (fallback)
                for (int i = 0; i < words.Count; i++)
                {
                    var w = words[i];
                    if (w.Length > maxCharsPerLine * 2)
                    {
                        words[i] = w.Substring(0, maxCharsPerLine);
                        words.Insert(i + 1, w.Substring(maxCharsPerLine));
                        i++;
                    }
                    else if (w.Length > maxCharsPerLine)
                    {
                        // prova a spezzare comunque in 2 parti
                        int cut = maxCharsPerLine;
                        words[i] = w.Substring(0, cut);
                        words.Insert(i + 1, w.Substring(cut));
                        i++;
                    }
                }

                var lines = new List<string>();
                var current = "";

                foreach (var w in words)
                {
                    if (current.Length == 0)
                    {
                        current = w;
                        continue;
                    }

                    if ((current.Length + 1 + w.Length) <= maxCharsPerLine)
                    {
                        current += " " + w;
                    }
                    else
                    {
                        lines.Add(current);
                        current = w;
                        if (lines.Count == maxLines - 1) break; // lasciamo l'ultima riga per il resto
                    }
                }

                if (current.Length > 0)
                {
                    if (lines.Count < maxLines)
                        lines.Add(current);
                    else
                        lines[lines.Count - 1] += " " + current; // non tronco: accumulo sull'ultima
                }

                // Se avanzano parole non inserite e ho maxLines, le appendo all'ultima riga
                if (words.Count > 0 && lines.Count == maxLines)
                {
                    // niente: già gestito sopra con append
                }

                return lines;
            }


            string CleanMarket(string? market)
            {
                if (string.IsNullOrWhiteSpace(market)) return "";
                return Regex.Replace(market.Trim(), @"\s*\(\d+\)\s*$", "");
            }

            string FormatOdd(decimal? o) => o.HasValue ? o.Value.ToString("0.00") : "—";

            // ====== LAYOUT FIX (anti-sovrapposizione footer) ======
            const int W = 1080;

            const int CardX = 40;
            const int CardY = 40;
            const int CardW = 1000;

            const int HeaderH = 310;   // più spazio per logo + pills sotto

            const int DividerH = 20;      // spazio sotto divider
            const int FooterH = 140;      // area footer dedicata
            const int BottomPad = 40;     // padding finale

            const int RowRectH = 190;     // altezza card riga
            const int RowGap = 20;        // spazio tra righe

            int rowsCount = pageSelections.Count;
            int rowsBlockH = rowsCount == 0 ? 0 : (rowsCount * RowRectH) + ((rowsCount - 1) * RowGap);

            // altezza totale: card top + header + divider + righe + footer + padding + card bottom
            int height = CardY + HeaderH + DividerH + rowsBlockH + FooterH + BottomPad;

            // coordinate righe
            int rowsTop = CardY + HeaderH + DividerH;                 // inizio area righe
            int rowsBottom = rowsTop + rowsBlockH;                    // fine area righe
            int footerTop = rowsBottom + 30;                          // inizio area footer (staccato)
            int footerLineY = footerTop;                              // linea separatrice footer
            int footerTextY = footerTop + 55;

            // badge stato
            var title = string.IsNullOrWhiteSpace(slip.Title) ? $"Schedina #{slip.Id}" : slip.Title;

            string slipBadge = slip.Result switch
            {
                BetSlip.BetSlipResult.Win => "VINCENTE",
                BetSlip.BetSlipResult.Loss => "PERDENTE",
                _ => (slip.ArchivedAtUtc != null ? "ARCHIVIATA" : "DA ASSEGNARE")
            };

            string slipBadgeColor = slip.Result switch
            {
                BetSlip.BetSlipResult.Win => "#16a34a",
                BetSlip.BetSlipResult.Loss => "#dc2626",
                _ => "#94a3b8"
            };

            // logo url -> data uri
            async Task<string?> ImgToDataUriAsync(string? url)
            {
                if (string.IsNullOrWhiteSpace(url)) return null;
                try
                {
                    using var http = new HttpClient();
                    var bytes = await http.GetByteArrayAsync(url);
                    var b64 = Convert.ToBase64String(bytes);
                    return $"data:image/png;base64,{b64}";
                }
                catch { return null; }
            }
            // logo locale (wwwroot) -> data uri (SVG)
            string? LocalSvgToDataUri(string relativePathFromWwwroot)
            {
                try
                {
                    // es: "icons/favicon.svg"
                    var env = HttpContext?.RequestServices?.GetService(typeof(IWebHostEnvironment)) as IWebHostEnvironment;
                    if (env == null) return null;

                    var fullPath = Path.Combine(
                        env.WebRootPath,
                        relativePathFromWwwroot.Replace("/", Path.DirectorySeparatorChar.ToString())
                    );

                    if (!System.IO.File.Exists(fullPath)) return null;

                    var svgText = System.IO.File.ReadAllText(fullPath);

                    // data-uri svg (URL encoded)
                    var encoded = Uri.EscapeDataString(svgText);
                    return $"data:image/svg+xml;utf8,{encoded}";
                }
                catch { return null; }
            }
            // logo locale (wwwroot) -> data uri (PNG/JPG)
            string? LocalImageToDataUri(string relativePathFromWwwroot)
            {
                try
                {
                    var env = HttpContext?.RequestServices?.GetService(typeof(IWebHostEnvironment)) as IWebHostEnvironment;
                    if (env == null) return null;

                    var fullPath = Path.Combine(
                        env.WebRootPath,
                        relativePathFromWwwroot.Replace("/", Path.DirectorySeparatorChar.ToString())
                    );

                    if (!System.IO.File.Exists(fullPath)) return null;

                    var bytes = System.IO.File.ReadAllBytes(fullPath);
                    var b64 = Convert.ToBase64String(bytes);

                    // deduco mime da estensione
                    var ext = Path.GetExtension(fullPath).ToLowerInvariant();
                    var mime = ext switch
                    {
                        ".png" => "image/png",
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".webp" => "image/webp",
                        _ => "image/png"
                    };

                    return $"data:{mime};base64,{b64}";
                }
                catch { return null; }
            }

            var sb = new StringBuilder();
            sb.AppendLine($@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""{W}"" height=""{height}"" viewBox=""0 0 {W} {height}"">");
            sb.AppendLine(@"
  <defs>
    <!-- ✅ Sfondo globale (più elegante del nero) -->
    <linearGradient id=""bg"" x1=""0"" y1=""0"" x2=""0"" y2=""1"">
      <stop offset=""0%"" stop-color=""#1e232b""/>
      <stop offset=""100%"" stop-color=""#151a21""/>
    </linearGradient>

    <!-- ✅ Ombra card -->
    <filter id=""shadow"" x=""-20%"" y=""-20%"" width=""140%"" height=""140%"">
      <feDropShadow dx=""0"" dy=""18"" stdDeviation=""22"" flood-color=""#000"" flood-opacity=""0.35""/>
    </filter>
  </defs>
");

            sb.AppendLine(@"
  <style>
    .font { font-family: ui-sans-serif, system-ui, -apple-system, Segoe UI, Roboto, Arial; }
    .muted { fill: #94a3b8; }
    .white { fill: #e5e7eb; }
    .title { font-size: 56px; font-weight: 900; }
    .sub { font-size: 26px; font-weight: 700; }
    .badgeText { font-size: 22px; font-weight: 900; fill: #0b1220; }
    .pillText { font-size: 22px; font-weight: 800; fill: #0b1220; }
    .team { font-size: 34px; font-weight: 900; fill: #e5e7eb; }
    .date { font-size: 24px; font-weight: 800; fill: #94a3b8; }

    /* ✅ VS sempre azzurro (forza priorità) */
    .vs { fill: #60a5fa !important; }
  </style>
");

            // background
            sb.AppendLine($@"  <rect x=""0"" y=""0"" width=""{W}"" height=""{height}"" fill=""url(#bg)""/>");

            // card container (altezza corretta)
            sb.AppendLine($@"  <rect x=""{CardX}"" y=""{CardY}"" width=""{CardW}"" height=""{height - (CardY * 2)}"" rx=""34"" fill=""#0b1020"" opacity=""0.96"" filter=""url(#shadow)""/>");
            sb.AppendLine($@"  <rect x=""{CardX}"" y=""{CardY}"" width=""{CardW}"" height=""{height - (CardY * 2)}"" rx=""34"" fill=""none"" stroke=""#1f2937"" stroke-width=""2""/>");

            // header
            // LOGO IN ALTO A SINISTRA (PNG - compatibile con Skia)
            var headerLogo = LocalImageToDataUri("icons/android-chrome-512x512.png");
            if (!string.IsNullOrWhiteSpace(headerLogo))
            {
                int logoW = 180;           // 🔥 più grande
                int logoH = 180;
                int logoX = (W / 2) - (logoW / 2);   // centro perfetto
                int logoY = 60;            // leggermente più su, così i box sotto respirano

                sb.AppendLine($@"  <image href=""{headerLogo}"" x=""{logoX}"" y=""{logoY}"" width=""{logoW}"" height=""{logoH}""/>");
            }



            var totalLabel = totalOdd.HasValue ? totalOdd.Value.ToString("0.00") : "N/D";

            // --- pills sotto logo ---
            int pillW = 320;         // 🔽 leggermente più strette
            int pillH = 50;          // 🔽 un filo più basse
            int pillRx = 20;

            int centerX = W / 2;
            int gap = 40;            // spazio tra le pill
            int pillsY = 250;        // ✅ sotto il logo

            int leftX = centerX - gap / 2 - pillW;
            int rightX = centerX + gap / 2;

            // pill sinistra (quota)
            sb.AppendLine($@"  <rect x=""{leftX}"" y=""{pillsY}"" width=""{pillW}"" height=""{pillH}"" rx=""{pillRx}"" fill=""#e5e7eb""/>");
            sb.AppendLine($@"  <text x=""{leftX + pillW / 2}"" y=""{pillsY + 33}"" text-anchor=""middle"" class=""font pillText"">QUOTA TOTALE: {Esc(totalLabel)}</text>");

            // pill destra (stato) - stessa grafica della quota
            sb.AppendLine($@"  <rect x=""{rightX}"" y=""{pillsY}"" width=""{pillW}"" height=""{pillH}"" rx=""{pillRx}"" fill=""#e5e7eb""/>");
            sb.AppendLine($@"  <text x=""{rightX + pillW / 2}"" y=""{pillsY + 33}"" text-anchor=""middle"" class=""font pillText"">{Esc(slipBadge)}</text>");



            // page indicator
            if (pages > 1)
                sb.AppendLine($@"  <text x=""1000"" y=""238"" text-anchor=""end"" class=""font sub muted"">Pag. {pageNo}/{pages}</text>");

            // divider
            sb.AppendLine($@"  <line x1=""80"" y1=""330"" x2=""1000"" y2=""330"" stroke=""#1f2937"" stroke-width=""2""/>");


            // rows
            int y = rowsTop;
            int idxStart = ((pageNo - 1) * SvgPageSize) + 1;
            int idx = idxStart;

            foreach (var sel in pageSelections)
            {
                map.TryGetValue(sel.MatchId, out var mi);

                var home = mi?.HomeName ?? "Casa";
                var away = mi?.AwayName ?? "Ospite";
                var date = mi?.DateUtc?.ToLocalTime().ToString("dd/MM HH:mm") ?? "";
                var market = CleanMarket(sel.Market);
                var pick = sel.Pick ?? "";
                var oddTxt = FormatOdd(sel.Odd);

                sb.AppendLine($@"  <rect x=""80"" y=""{y}"" width=""920"" height=""{RowRectH}"" rx=""26"" fill=""#0f172a"" stroke=""#1f2937"" stroke-width=""2""/>");

                var homeLogo = await ImgToDataUriAsync(mi?.HomeLogo);
                var awayLogo = await ImgToDataUriAsync(mi?.AwayLogo);

                int logoSize = 56;
                int logoYOffset = -10;          // 🔼 alza i loghi (prova -8 / -10 / -12)
                int logoY = y + 18 + logoYOffset;

                if (!string.IsNullOrWhiteSpace(homeLogo))
                    sb.AppendLine($@"  <image href=""{homeLogo}"" x=""120"" y=""{logoY}"" width=""{logoSize}"" height=""{logoSize}""/>");
                if (!string.IsNullOrWhiteSpace(awayLogo))
                    sb.AppendLine($@"  <image href=""{awayLogo}"" x=""904"" y=""{logoY}"" width=""{logoSize}"" height=""{logoSize}""/>");


                int teamYBase = y + 92;

                // wrap in 1–2 righe (senza troncare)
                var homeLines = WrapByWords(home, maxCharsPerLine: 18, maxLines: 2);
                var awayLines = WrapByWords(away, maxCharsPerLine: 18, maxLines: 2);

                int maxLinesUsed = Math.Max(homeLines.Count, awayLines.Count);
                int lineGap = 34; // distanza tra righe

                // calcolo Y di partenza per centrare verticalmente attorno a teamYBase
                int homeStartY = teamYBase - ((homeLines.Count - 1) * lineGap / 2);
                int awayStartY = teamYBase - ((awayLines.Count - 1) * lineGap / 2);

                // ancoraggi
                int centerXTeam = 540;
                int vsGap = 22;
                int vsHalf = 18;

                int leftEndX = centerXTeam - (vsHalf + vsGap);
                int rightStartX = centerXTeam + (vsHalf + vsGap);

                // HOME (multi-line, ancorato a destra)
                sb.AppendLine($@"  <text x=""{leftEndX}"" y=""{homeStartY}"" text-anchor=""end"" class=""font team"">");
                for (int i = 0; i < homeLines.Count; i++)
                {
                    var dy = (i == 0) ? 0 : lineGap;
                    sb.AppendLine($@"    <tspan x=""{leftEndX}"" dy=""{dy}"">{Esc(homeLines[i])}</tspan>");
                }
                sb.AppendLine(@"  </text>");

                // VS sempre al centro e azzurro
                sb.AppendLine($@"  <text x=""{centerXTeam}"" y=""{teamYBase}"" text-anchor=""middle"" class=""font team vs"">VS</text>");

                // AWAY (multi-line, ancorato a sinistra)
                sb.AppendLine($@"  <text x=""{rightStartX}"" y=""{awayStartY}"" text-anchor=""start"" class=""font team"">");
                for (int i = 0; i < awayLines.Count; i++)
                {
                    var dy = (i == 0) ? 0 : lineGap;
                    sb.AppendLine($@"    <tspan x=""{rightStartX}"" dy=""{dy}"">{Esc(awayLines[i])}</tspan>");
                }
                sb.AppendLine(@"  </text>");

                // Data: se ci sono 2 righe, la abbasso un po’
                // Se uso 2 righe per i nomi, devo spostare giù anche data + pills
                int extraDown = (maxLinesUsed > 1) ? 22 : 0;   // 🔧 regola: 18/20/24 se vuoi più/meno spazio

                int dateY = (y + 122) + extraDown;

                if (!string.IsNullOrWhiteSpace(date))
                    sb.AppendLine($@"  <text x=""540"" y=""{dateY}"" text-anchor=""middle"" class=""font date"">{Esc(date)}</text>");

                // Pills spostate giù insieme alla data
                int rowPillTop = (y + 136) + extraDown;
                int rowPillH = 46;


                if (!string.IsNullOrWhiteSpace(market))
                {
                    sb.AppendLine($@"  <rect x=""120"" y=""{rowPillTop}"" width=""360"" height=""{rowPillH}"" rx=""18"" fill=""#e5e7eb""/>");
                    sb.AppendLine($@"  <text x=""140"" y=""{rowPillTop + 31}"" class=""font pillText"">{Esc(market)}</text>");

                }

                sb.AppendLine($@"  <rect x=""500"" y=""{rowPillTop}"" width=""340"" height=""{rowPillH}"" rx=""18"" fill=""#e5e7eb""/>");
                sb.AppendLine($@"  <text x=""520"" y=""{rowPillTop + 31}"" class=""font pillText"">Pick: {Esc(pick)}</text>");


                sb.AppendLine($@"  <rect x=""860"" y=""{rowPillTop}"" width=""120"" height=""{rowPillH}"" rx=""18"" fill=""#e5e7eb""/>");
                sb.AppendLine($@"  <text x=""920"" y=""{rowPillTop + 31}"" text-anchor=""middle"" class=""font pillText"">x {Esc(oddTxt)}</text>");

                y += (RowRectH + RowGap);
                idx++;
            }

            // ✅ footer separato (non può più sovrapporsi)
            // ✅ footer separato (non può più sovrapporsi)
            sb.AppendLine($@"  <line x1=""80"" y1=""{footerLineY}"" x2=""1000"" y2=""{footerLineY}"" stroke=""#1f2937"" stroke-width=""2""/>");





            // Destra: timestamp
            // Titolo schedina in basso a sinistra
            var published = slip.UpdatedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

            // testo centrato
            sb.AppendLine($@"  <text x=""540"" y=""{footerTextY}"" text-anchor=""middle"" class=""font sub muted"">Pubblicata il: {Esc(published)}</text>");




            sb.AppendLine("</svg>");
            return sb.ToString();
        }


        private byte[] SvgToPng(string svg, int widthPx = 1080)
        {
            using var svgStream = new MemoryStream(Encoding.UTF8.GetBytes(svg));
            var skSvg = new SKSvg();
            skSvg.Load(svgStream);

            var pic = skSvg.Picture;
            if (pic == null) return Array.Empty<byte>();

            var bounds = pic.CullRect;
            if (bounds.Width <= 0 || bounds.Height <= 0) return Array.Empty<byte>();

            var scale = widthPx / bounds.Width;
            var heightPx = (int)Math.Ceiling(bounds.Height * scale);

            using var bitmap = new SKBitmap(widthPx, heightPx);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);

            canvas.Scale(scale);
            canvas.DrawPicture(pic);
            canvas.Flush();

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
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

            // ✅ Se è una copia dalla Community, elimino anche la riga di "save"
            if (slip.ImportedFromCommunity && slip.SourceBetSlipId.HasValue)
            {
                var saveRow = await _db.BetSlipSaves
                    .FirstOrDefaultAsync(x =>
                        x.SavedByUserId == userId &&
                        x.CopiedBetSlipId == slip.Id &&
                        x.SourceBetSlipId == slip.SourceBetSlipId.Value);

                if (saveRow != null)
                    _db.BetSlipSaves.Remove(saveRow);
            }

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
            else if (x.StartsWith("MGHOME")) { scope = "HOME"; x = x.Substring(6); } // "MGHOME"EvaluateLeg
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

            var market = (sel.Market ?? "").Trim();
            var value = (sel.Pick ?? "").Trim();

            if (string.IsNullOrWhiteSpace(market) || string.IsNullOrWhiteSpace(value))
                return null;

            if (LooksLegacyPick(value)) return null;

            if (!LooksEnglishPick(value) && !market.ToUpperInvariant().Contains("GOAL LINE")
                                         && !market.ToUpperInvariant().Contains("ASIAN")
                                         && !market.ToUpperInvariant().Contains("HANDICAP")
                                         && !market.ToUpperInvariant().Contains("MATCH WINNER")
                                         && !market.ToUpperInvariant().Contains("DOUBLE CHANCE")
                                         && !market.ToUpperInvariant().Contains("BOTH TEAMS")
                                         && !market.ToUpperInvariant().Contains("CORRECT SCORE")
                                         && !market.ToUpperInvariant().Contains("RESULT/TOTAL")
                                         && !market.ToUpperInvariant().Contains("TEAM TO SCORE")
                                         && !market.ToUpperInvariant().Contains("DRAW NO BET")
                                         && !market.ToUpperInvariant().Contains("TOTAL GOALS/BOTH TEAMS")
                                         && !market.ToUpperInvariant().Contains("RESULT/BOTH TEAMS"))
            {
                return null;
            }

            market = Regex.Replace(market, @"\s*\(\d+\)\s*$", "").Trim();
            var m = market.ToUpperInvariant();
            var v = value.Trim();

            // ✅ score di default = FT
            // ✅ score già scelto a monte (FT o HT) tramite GetScoreForMarket
            int h = hScoped;
            int a = aScoped;

            // mercato 1H? (serve solo per logiche specifiche, NON per cambiare h/a qui)
            bool isFirstHalfMarket =
                m.Contains("1ST HALF") ||
                m.Contains("FIRST HALF") ||
                m.Contains("1H") ||
                m.Contains("HT") ||
                m.Contains("HALF TIME") ||
                m.Contains("HALFTIME");





            // =========================
            // MATCH WINNER (1X2)
            // =========================
            if (m.Contains("MATCH WINNER"))
            {
                var vv = v.ToUpperInvariant();
                var esito = (h > a) ? "HOME" : (h < a) ? "AWAY" : "DRAW";

                if (vv is "HOME" or "1") return esito == "HOME" ? SelectionOutcome.Won : SelectionOutcome.Lost;
                if (vv is "AWAY" or "2") return esito == "AWAY" ? SelectionOutcome.Won : SelectionOutcome.Lost;
                if (vv is "DRAW" or "X") return esito == "DRAW" ? SelectionOutcome.Won : SelectionOutcome.Lost;

                return SelectionOutcome.Unknown;
            }

            // =========================
            // FIRST HALF WINNER
            // value: HOME / AWAY / DRAW
            // =========================
            if (m.Contains("FIRST HALF WINNER"))
            {
                if (!hHT.HasValue || !aHT.HasValue) return SelectionOutcome.Unknown;

                var vv = NormalizeEnglishPick(v);
                var esito = GetResultHAD(hHT.Value, aHT.Value);

                if (vv is "HOME" or "AWAY" or "DRAW")
                    return (esito == vv) ? SelectionOutcome.Won : SelectionOutcome.Lost;

                return SelectionOutcome.Unknown;
            }

            // =========================
            // DRAW NO BET (DNB)
            // value: "HOME" / "AWAY"
            // DRAW => Push
            // =========================
            if (m.Contains("DRAW NO BET"))
            {
                var vv = NormalizeEnglishPick(v);
                if (vv != "HOME" && vv != "AWAY") return SelectionOutcome.Unknown;

                var esito = GetResultHAD(h, a);

                if (esito == "DRAW") return SelectionOutcome.Push;

                return (esito == vv) ? SelectionOutcome.Won : SelectionOutcome.Lost;
            }

            // =========================
            // DOUBLE CHANCE (Home/Draw, Draw/Away, Home/Away)
            // =========================
            if (m.Contains("DOUBLE CHANCE"))
            {
                var vv = v.ToUpperInvariant().Replace(" ", "");
                // accetto varianti: "HOME/DRAW", "DRAW/AWAY", "HOME/AWAY"
                var esito = (h > a) ? "HOME" : (h < a) ? "AWAY" : "DRAW";

                bool ok =
                    (vv.Contains("HOME") && esito == "HOME") ||
                    (vv.Contains("AWAY") && esito == "AWAY") ||
                    (vv.Contains("DRAW") && esito == "DRAW");

                return ok ? SelectionOutcome.Won : SelectionOutcome.Lost;
            }

            // =========================
            // BOTH TEAMS SCORE (GG/NG)
            // =========================
            if (m.Contains("BOTH TEAMS") && m.Contains("SCORE"))
            {
                var vv = v.ToUpperInvariant().Trim();
                bool gg = (h > 0 && a > 0);

                if (vv is "YES" or "Y") return gg ? SelectionOutcome.Won : SelectionOutcome.Lost;
                if (vv is "NO" or "N") return !gg ? SelectionOutcome.Won : SelectionOutcome.Lost;

                return SelectionOutcome.Unknown;
            }

            // =========================
            // CORRECT SCORE / EXACT SCORE
            // value: "1:0" / "2-1" / "0:0"
            // =========================
            if (m.Contains("CORRECT SCORE") || m.Contains("EXACT SCORE"))
            {
                var vv = v.Trim();
                var mx = Regex.Match(vv, @"(\d+)\s*[:\-]\s*(\d+)");
                if (!mx.Success) return SelectionOutcome.Unknown;

                int eh = int.Parse(mx.Groups[1].Value);
                int ea = int.Parse(mx.Groups[2].Value);

                return (h == eh && a == ea) ? SelectionOutcome.Won : SelectionOutcome.Lost;
            }
            // =========================
            // EXACT GOALS NUMBER (Total/Home/Away)
            // value: "0" "1" "2" ... "4+"
            // =========================
            if (m.Contains("EXACT GOALS NUMBER"))
            {
                var parsed = ParseExactGoalsValue(v);
                if (parsed == null) return SelectionOutcome.Unknown;

                var (mode, n) = parsed.Value;
                int tot = h + a;


                if (mode == "PLUS" && n.HasValue)
                    return (tot >= n.Value) ? SelectionOutcome.Won : SelectionOutcome.Lost;

                if (mode == "EQ" && n.HasValue)
                    return (tot == n.Value) ? SelectionOutcome.Won : SelectionOutcome.Lost;

                return SelectionOutcome.Unknown;
            }
            // =========================
            // ODD/EVEN (Total + Home/Away)
            // =========================
            if (m.Equals("ODD/EVEN") || (m.Contains("ODD/EVEN") && !m.Contains("HOME") && !m.Contains("AWAY")))
            {
                var wantOdd = ParseOddEvenValue(v);
                if (wantOdd == null) return SelectionOutcome.Unknown;

                int tot = h + a;

                bool isOdd = (tot % 2) == 1;
                return (isOdd == wantOdd.Value) ? SelectionOutcome.Won : SelectionOutcome.Lost;
            }

            if (m.Contains("HOME ODD/EVEN"))
            {
                var wantOdd = ParseOddEvenValue(v);
                if (wantOdd == null) return SelectionOutcome.Unknown;

                bool isOdd = (h % 2) == 1;

                return (isOdd == wantOdd.Value) ? SelectionOutcome.Won : SelectionOutcome.Lost;
            }

            if (m.Contains("AWAY ODD/EVEN"))
            {
                var wantOdd = ParseOddEvenValue(v);
                if (wantOdd == null) return SelectionOutcome.Unknown;

                bool isOdd = (a % 2) == 1;

                return (isOdd == wantOdd.Value) ? SelectionOutcome.Won : SelectionOutcome.Lost;
            }

            if (m.Contains("HOME TEAM EXACT GOALS NUMBER"))
            {
                var parsed = ParseExactGoalsValue(v);
                if (parsed == null) return SelectionOutcome.Unknown;

                var (mode, n) = parsed.Value;
                int goals = hFT;

                if (mode == "PLUS" && n.HasValue)
                    return (goals >= n.Value) ? SelectionOutcome.Won : SelectionOutcome.Lost;

                if (mode == "EQ" && n.HasValue)
                    return (goals == n.Value) ? SelectionOutcome.Won : SelectionOutcome.Lost;

                return SelectionOutcome.Unknown;
            }

            if (m.Contains("AWAY TEAM EXACT GOALS NUMBER"))
            {
                var parsed = ParseExactGoalsValue(v);
                if (parsed == null) return SelectionOutcome.Unknown;

                var (mode, n) = parsed.Value;
                int goals = aFT;

                if (mode == "PLUS" && n.HasValue)
                    return (goals >= n.Value) ? SelectionOutcome.Won : SelectionOutcome.Lost;

                if (mode == "EQ" && n.HasValue)
                    return (goals == n.Value) ? SelectionOutcome.Won : SelectionOutcome.Lost;

                return SelectionOutcome.Unknown;
            }
            // =========================
            // SECOND HALF (derivato da FT - HT)
            // =========================
            bool isSecondHalfMarket =
                m.Contains("SECOND HALF") ||
                m.Contains("2ND HALF") ||
                m.Contains("HALF - SECOND");

            if (isSecondHalfMarket)
            {
                if (!TryGetSecondHalfGoals(hFT, aFT, hHT, aHT, out var h2, out var a2))
                    return SelectionOutcome.Unknown;

                int tot2 = h2 + a2;
                var vv = v.Trim().ToUpperInvariant().Replace(" ", "");

                // Second Half Winner
                if (m.Contains("SECOND HALF WINNER"))
                {
                    var pick = NormalizeEnglishPick(v);
                    var esito2 = GetResultHAD(h2, a2);
                    if (pick is "HOME" or "AWAY" or "DRAW")
                        return (esito2 == pick) ? SelectionOutcome.Won : SelectionOutcome.Lost;

                    return SelectionOutcome.Unknown;
                }

                // Goals O/U - Second Half
                if (m.Contains("GOALS OVER/UNDER") && m.Contains("SECOND HALF"))
                {
                    var ou = ParseOverUnderFromValue(v);
                    if (ou == null) return SelectionOutcome.Unknown;

                    var (isOver, line) = ou.Value;
                    bool ok = isOver ? (tot2 > line) : (tot2 < line);
                    return ok ? SelectionOutcome.Won : SelectionOutcome.Lost;
                }

                // BTS - Second Half
                if ((m.Contains("BOTH TEAMS") && m.Contains("SCORE")) && m.Contains("SECOND HALF"))
                {
                    bool gg2 = (h2 > 0 && a2 > 0);
                    if (vv is "YES" or "Y") return gg2 ? SelectionOutcome.Won : SelectionOutcome.Lost;
                    if (vv is "NO" or "N") return !gg2 ? SelectionOutcome.Won : SelectionOutcome.Lost;
                    return SelectionOutcome.Unknown;
                }

                // Second Half Exact Goals Number
                if (m.Contains("SECOND HALF EXACT GOALS NUMBER"))
                {
                    var parsed = ParseExactGoalsValue(v);
                    if (parsed == null) return SelectionOutcome.Unknown;

                    var (mode, n) = parsed.Value;

                    if (mode == "PLUS" && n.HasValue)
                        return (tot2 >= n.Value) ? SelectionOutcome.Won : SelectionOutcome.Lost;

                    if (mode == "EQ" && n.HasValue)
                        return (tot2 == n.Value) ? SelectionOutcome.Won : SelectionOutcome.Lost;

                    return SelectionOutcome.Unknown;
                }

                // Odd/Even - Second Half
                if (m.Contains("ODD/EVEN") && m.Contains("SECOND HALF"))
                {
                    var wantOdd = ParseOddEvenValue(v);
                    if (wantOdd == null) return SelectionOutcome.Unknown;

                    bool isOdd = (tot2 % 2) == 1;
                    return (isOdd == wantOdd.Value) ? SelectionOutcome.Won : SelectionOutcome.Lost;
                }

                // To Score In Both Halves By Teams (Home/Away)
                if (m.Contains("TO SCORE IN BOTH HALVES"))
                {
                    // value atteso: HOME / AWAY
                    var side = NormalizeEnglishPick(v);
                    if (side != "HOME" && side != "AWAY") return SelectionOutcome.Unknown;

                    if (!hHT.HasValue || !aHT.HasValue) return SelectionOutcome.Unknown;

                    bool homeScored1H = hHT.Value > 0;
                    bool awayScored1H = aHT.Value > 0;

                    bool homeScored2H = h2 > 0;
                    bool awayScored2H = a2 > 0;

                    bool ok = side == "HOME"
                        ? (homeScored1H && homeScored2H)
                        : (awayScored1H && awayScored2H);

                    return ok ? SelectionOutcome.Won : SelectionOutcome.Lost;
                }

                // Win Both Halves (Home/Away)
                if (m.Contains("WIN BOTH HALVES"))
                {
                    var side = NormalizeEnglishPick(v);
                    if (side != "HOME" && side != "AWAY") return SelectionOutcome.Unknown;
                    if (!hHT.HasValue || !aHT.HasValue) return SelectionOutcome.Unknown;

                    string firstHalfWinner = GetResultHAD(hHT.Value, aHT.Value);
                    string secondHalfWinner = GetResultHAD(h2, a2);

                    bool ok = (firstHalfWinner == side) && (secondHalfWinner == side);
                    return ok ? SelectionOutcome.Won : SelectionOutcome.Lost;
                }

                // To Win Either Half (Home/Away)
                if (m.Contains("TO WIN EITHER HALF"))
                {
                    var side = NormalizeEnglishPick(v);
                    if (side != "HOME" && side != "AWAY") return SelectionOutcome.Unknown;
                    if (!hHT.HasValue || !aHT.HasValue) return SelectionOutcome.Unknown;

                    string firstHalfWinner = GetResultHAD(hHT.Value, aHT.Value);
                    string secondHalfWinner = GetResultHAD(h2, a2);

                    bool ok = (firstHalfWinner == side) || (secondHalfWinner == side);
                    return ok ? SelectionOutcome.Won : SelectionOutcome.Lost;
                }

                // Highest Scoring Half (First/Second/Equal)
                if (m.Contains("HIGHEST SCORING HALF"))
                {
                    if (!hHT.HasValue || !aHT.HasValue) return SelectionOutcome.Unknown;

                    int tot1 = hHT.Value + aHT.Value;
                    int totSecond = tot2;

                    string result =
                        (tot1 > totSecond) ? "FIRST" :
                        (totSecond > tot1) ? "SECOND" : "EQUAL";

                    // value può essere: "FIRST HALF" / "SECOND HALF" / "EQUAL"
                    if (vv.Contains("FIRST")) return result == "FIRST" ? SelectionOutcome.Won : SelectionOutcome.Lost;
                    if (vv.Contains("SECOND")) return result == "SECOND" ? SelectionOutcome.Won : SelectionOutcome.Lost;
                    if (vv.Contains("EQUAL") || vv.Contains("DRAW")) return result == "EQUAL" ? SelectionOutcome.Won : SelectionOutcome.Lost;

                    return SelectionOutcome.Unknown;
                }
            }

            // =========================
            // OVER/UNDER TOTAL GOALS
            // value: "Over 2.5" / "Under 1.5" / "Over2.5"
            // =========================
            if (m.Contains("OVER/UNDER") || m.Contains("GOALS OVER/UNDER") || (m.Contains("TOTAL") && m.Contains("GOALS")))
            {
                var ou = ParseOverUnderFromValue(v);
                if (ou == null) return SelectionOutcome.Unknown;

                var (isOver, line) = ou.Value;
                var tot = h + a;

                bool ok = isOver ? (tot > line) : (tot < line);
                return ok ? SelectionOutcome.Won : SelectionOutcome.Lost;
            }
            // =========================
            // GOAL LINE (Asian Total Goals)  e GOAL LINE (1st Half)
            // value: "Over 2.25" / "Under 2.75" ecc.
            // =========================
            if (m.Contains("GOAL LINE"))
            {
                var ou = ParseOverUnderFromValue(v);
                if (ou == null) return SelectionOutcome.Unknown;

                var (isOver, line) = ou.Value;
                var tot = h + a;

                return EvaluateAsianTotal(isOver, line, tot);
            }


            // =========================
            // ASIAN HANDICAP (FT o 1st half già gestito da GetScoreForMarket)
            // value: "Home -0.25" / "Away +0.75" / "Home -1"
            // =========================
            if (m.Contains("ASIAN HANDICAP"))
            {
                var ah = ParseAsianHandicapValue(market, v);
                if (ah == null) return SelectionOutcome.Unknown;

                var (side, line) = ah.Value; // side: HOME/AWAY
                return EvaluateAsianHandicap(side, line, h, a);
            }

            // mercati non gestiti qui -> fallback legacy


            // =========================
            // HANDICAP RESULT (European handicap 3-way)
            // value: "Home -1" / "Draw -1" / "Away -1"  ecc.
            // Assumo handicap applicato al HOME (standard più comune)
            // =========================
            if (m.Contains("HANDICAP RESULT"))
            {
                var parsed = ParseHandicapResultValue(v);
                if (parsed == null) return SelectionOutcome.Unknown;

                var (wanted, line) = parsed.Value; // wanted=HOME/DRAW/AWAY, line = +1 / -1 / +2 / -2

                // Handicap applicato alla AWAY se wanted è AWAY, alla HOME se wanted è HOME, altrimenti (DRAW) va applicato comunque al match,
                // quindi prendiamo l'handicap dal value ma lo applichiamo a chi è indicato nel value.
                // Però nei tuoi esempi "Draw -1" esiste: qui l'handicap è del match (tipicamente HOME -1 / AWAY +1).
                // Regola più robusta: applica handicap alla HOME (come mercato standard) MA inverti il segno quando la pick è AWAY.
                // --> ancora meglio: PARSA anche il "target" dal value: "Away -1" dice target=AWAY, handicap=-1.

                var parsed2 = ParseHandicapResultValueWithTarget(v);
                if (parsed2 == null) return SelectionOutcome.Unknown;

                var (target, handicap, outcomeWanted) = parsed2.Value; // target=HOME/AWAY, handicap=-1/+1, outcomeWanted=HOME/DRAW/AWAY

                int hAdj = h;
                int aAdj = a;

                if (target == "HOME") hAdj = h + handicap;
                else aAdj = a + handicap;

                var esitoAdj = (hAdj > aAdj) ? "HOME" : (hAdj < aAdj) ? "AWAY" : "DRAW";
                return (esitoAdj == outcomeWanted) ? SelectionOutcome.Won : SelectionOutcome.Lost;
            }
            // =========================
            // RESULT/BOTH TEAMS TO SCORE
            // value: "HOME/YES" / "DRAW/NO" / "AWAY/YES"
            // =========================
            if (m.Contains("RESULT/BOTH TEAMS") || (m.Contains("RESULT") && m.Contains("BOTH TEAMS")))
            {
                var vv = v.ToUpperInvariant().Replace(" ", "");
                vv = vv.Replace("-", "/");

                var parts = vv.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2) return SelectionOutcome.Unknown;

                var resultPart = parts[0];  // HOME/DRAW/AWAY (a volte 1/X/2)
                var ynPart = parts[1];      // YES/NO

                // normalizzo eventuali 1/X/2
                resultPart = resultPart switch
                {
                    "1" => "HOME",
                    "2" => "AWAY",
                    "X" => "DRAW",
                    _ => resultPart
                };

                if (resultPart is not ("HOME" or "AWAY" or "DRAW")) return SelectionOutcome.Unknown;

                bool gg = (h > 0 && a > 0);
                bool wantYes = ynPart is "YES" or "Y";
                bool wantNo = ynPart is "NO" or "N";
                if (!wantYes && !wantNo) return SelectionOutcome.Unknown;

                // esito 1X2 reale
                var esito = (h > a) ? "HOME" : (h < a) ? "AWAY" : "DRAW";
                bool ok1 = (esito == resultPart);

                bool ok2 = wantYes ? gg : !gg;

                return (ok1 && ok2) ? SelectionOutcome.Won : SelectionOutcome.Lost;
            }

            if (m.Contains("RESULT/TOTAL GOALS"))
            {
                var vv = v.ToUpperInvariant().Replace(" ", "");
                // esempi: "AWAY/OVER2.5"
                var parts = vv.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2) return SelectionOutcome.Unknown;

                var resultPart = parts[0];   // HOME/DRAW/AWAY
                var ouPart = parts[1];       // OVER2.5 / UNDER2.5

                // 1X2
                var esito = (h > a) ? "HOME" : (h < a) ? "AWAY" : "DRAW";
                bool ok1 = esito == resultPart;

                // OU
                var ou = ParseOverUnderFromValue(ouPart);
                if (ou == null) return SelectionOutcome.Unknown;

                var (isOver, line) = ou.Value;
                var tot = h + a;
                bool ok2 = isOver ? (tot > line) : (tot < line);

                return (ok1 && ok2) ? SelectionOutcome.Won : SelectionOutcome.Lost;
            }

            // =========================
            // TOTAL GOALS / BOTH TEAMS TO SCORE
            // Market: "Total Goals/Both Teams To Score"
            // value: "OVER2.5/YES"  oppure "YES/OVER2.5"
            // =========================
            if (m.Contains("TOTAL GOALS") && m.Contains("BOTH TEAMS"))
            {
                var vv = v.ToUpperInvariant().Replace(" ", "");
                vv = vv.Replace("-", "/");

                var parts = vv.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2) return SelectionOutcome.Unknown;

                // accetta entrambe le direzioni
                string p1 = parts[0];
                string p2 = parts[1];

                // Normalizzo: identifico quale è OU e quale è BTS
                string? ouPart = null;
                string? btsPart = null;

                bool IsBts(string s) => s is "YES" or "Y" or "NO" or "N";
                bool IsOu(string s) => s.StartsWith("OVER") || s.StartsWith("UNDER") || s.StartsWith("OV") || s.StartsWith("UN");

                if (IsBts(p1) && IsOu(p2)) { btsPart = p1; ouPart = p2; }
                else if (IsOu(p1) && IsBts(p2)) { ouPart = p1; btsPart = p2; }
                else
                {
                    // fallback: prova a parsare OU con regex anche se non ha prefisso perfetto
                    var ouTry1 = ParseOverUnderFromValue(p1);
                    var ouTry2 = ParseOverUnderFromValue(p2);
                    if (ouTry1 != null && IsBts(p2)) { ouPart = p1; btsPart = p2; }
                    else if (ouTry2 != null && IsBts(p1)) { ouPart = p2; btsPart = p1; }
                    else return SelectionOutcome.Unknown;
                }

                // BTS
                bool gg = (h > 0 && a > 0);
                bool wantYes = btsPart is "YES" or "Y";
                bool wantNo = btsPart is "NO" or "N";
                if (!wantYes && !wantNo) return SelectionOutcome.Unknown;
                bool okBts = wantYes ? gg : !gg;

                // OU
                var ou = ParseOverUnderFromValue(ouPart);
                if (ou == null) return SelectionOutcome.Unknown;

                var (isOver, line) = ou.Value;
                var tot = h + a;
                bool okOu = isOver ? (tot > line) : (tot < line);

                return (okBts && okOu) ? SelectionOutcome.Won : SelectionOutcome.Lost;
            }

            // =========================
            // WIN TO NIL (Home/Away)
            // value: "HOME" / "AWAY"
            // =========================
            if (m.Contains("WIN TO NIL"))
            {
                var vv = NormalizeEnglishPick(v); // HOME/AWAY
                if (vv != "HOME" && vv != "AWAY") return SelectionOutcome.Unknown;

                bool homeWinToNil = (h > a) && (a == 0);
                bool awayWinToNil = (a > h) && (h == 0);

                bool ok = (vv == "HOME") ? homeWinToNil : awayWinToNil;
                return ok ? SelectionOutcome.Won : SelectionOutcome.Lost;
            }

            // =========================
            // CLEAN SHEET (Home/Away)
            // value: "HOME" / "AWAY"
            // (significa: squadra non subisce gol)
            // =========================
            if (m.Contains("CLEAN SHEET"))
            {
                var vv = NormalizeEnglishPick(v); // HOME/AWAY
                if (vv != "HOME" && vv != "AWAY") return SelectionOutcome.Unknown;

                bool homeClean = (a == 0);
                bool awayClean = (h == 0);

                bool ok = (vv == "HOME") ? homeClean : awayClean;
                return ok ? SelectionOutcome.Won : SelectionOutcome.Lost;
            }

            // =========================
            // TEAM TO SCORE (Home/Away)
            // value: "HOME YES" / "AWAY NO" / "HOME/YES"
            // =========================
            if (m.Contains("TEAM TO SCORE") || (m.Contains("TEAM") && m.Contains("TO SCORE")))
            {
                var vv = v.Trim().ToUpperInvariant().Replace(" ", "");
                vv = vv.Replace("-", "/");
                // normalizzo in forma HOME/YES o AWAY/NO
                if (!vv.Contains("/"))
                {
                    // tentativo: "HOMEYES"
                    if (vv.StartsWith("HOME")) vv = "HOME/" + vv.Substring(4);
                    else if (vv.StartsWith("AWAY")) vv = "AWAY/" + vv.Substring(4);
                }

                var parts = vv.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2) return SelectionOutcome.Unknown;

                var side = parts[0]; // HOME/AWAY
                var yn = parts[1];   // YES/NO

                if (side != "HOME" && side != "AWAY") return SelectionOutcome.Unknown;
                if (yn != "YES" && yn != "NO" && yn != "Y" && yn != "N") return SelectionOutcome.Unknown;

                bool wantYes = (yn == "YES" || yn == "Y");
                int goals = (side == "HOME") ? h : a;

                bool ok = wantYes ? (goals > 0) : (goals == 0);
                return ok ? SelectionOutcome.Won : SelectionOutcome.Lost;
            }
            return null;
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


    }

}
