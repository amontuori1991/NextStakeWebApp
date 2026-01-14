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
                    AwayGoal = m.AwayGoal
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

            const int HeaderH = 260;      // area header
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
    <linearGradient id=""bg"" x1=""0"" y1=""0"" x2=""1"" y2=""1"">
      <stop offset=""0%"" stop-color=""#070b16""/>
      <stop offset=""100%"" stop-color=""#0b1220""/>
    </linearGradient>
    <filter id=""shadow"" x=""-20%"" y=""-20%"" width=""140%"" height=""140%"">
      <feDropShadow dx=""0"" dy=""18"" stdDeviation=""22"" flood-color=""#000"" flood-opacity=""0.35""/>
    </filter>
  </defs>

  <style>
    .font { font-family: ui-sans-serif, system-ui, -apple-system, Segoe UI, Roboto, Arial; }
    .muted { fill: #94a3b8; }
    .white { fill: #e5e7eb; }
    .title { font-size: 56px; font-weight: 900; }
    .sub { font-size: 26px; font-weight: 700; }
    .badgeText { font-size: 22px; font-weight: 900; fill: #0b1220; }
    .pillText { font-size: 24px; font-weight: 800; fill: #0b1220; }
    .team { font-size: 34px; font-weight: 900; fill: #e5e7eb; }
    .date { font-size: 24px; font-weight: 800; fill: #94a3b8; }
  </style>
");

            // background
            sb.AppendLine($@"  <rect x=""0"" y=""0"" width=""{W}"" height=""{height}"" fill=""url(#bg)""/>");

            // card container (altezza corretta)
            sb.AppendLine($@"  <rect x=""{CardX}"" y=""{CardY}"" width=""{CardW}"" height=""{height - (CardY * 2)}"" rx=""34"" fill=""#0b1020"" opacity=""0.92"" filter=""url(#shadow)""/>");
            sb.AppendLine($@"  <rect x=""{CardX}"" y=""{CardY}"" width=""{CardW}"" height=""{height - (CardY * 2)}"" rx=""34"" fill=""none"" stroke=""#1f2937"" stroke-width=""2""/>");

            // header
            // LOGO IN ALTO A SINISTRA (PNG - compatibile con Skia)
            var headerLogo = LocalImageToDataUri("icons/android-chrome-512x512.png");
            if (!string.IsNullOrWhiteSpace(headerLogo))
            {
                sb.AppendLine($@"  <image href=""{headerLogo}"" x=""465"" y=""78"" width=""180"" height=""180""/>");

            }

            // badge stato
            // badge stato (stile uguale alla pill "Quota totale")
            // pill stato (stessa dimensione della quota)
            sb.AppendLine($@"  <rect x=""640"" y=""200"" width=""360"" height=""56"" rx=""22"" fill=""#e5e7eb""/>");
            sb.AppendLine($@"  <text x=""820"" y=""238"" text-anchor=""middle"" class=""font pillText"">{Esc(slipBadge)}</text>");




            // quota totale
            var totalLabel = totalOdd.HasValue ? totalOdd.Value.ToString("0.00") : "N/D";
            // pill quota totale (larghezza uniforme)
            sb.AppendLine($@"  <rect x=""80"" y=""200"" width=""360"" height=""56"" rx=""22"" fill=""#e5e7eb""/>");
            sb.AppendLine($@"  <text x=""260"" y=""238"" text-anchor=""middle"" class=""font pillText"">Quota totale: {Esc(totalLabel)}</text>");


            // page indicator
            if (pages > 1)
                sb.AppendLine($@"  <text x=""1000"" y=""238"" text-anchor=""end"" class=""font sub muted"">Pag. {pageNo}/{pages}</text>");

            // divider
            sb.AppendLine($@"  <line x1=""80"" y1=""280"" x2=""1000"" y2=""280"" stroke=""#1f2937"" stroke-width=""2""/>");

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

                int logoY = y + 18;
                if (!string.IsNullOrWhiteSpace(homeLogo))
                    sb.AppendLine($@"  <image href=""{homeLogo}"" x=""120"" y=""{logoY}"" width=""56"" height=""56""/>");
                if (!string.IsNullOrWhiteSpace(awayLogo))
                    sb.AppendLine($@"  <image href=""{awayLogo}"" x=""904"" y=""{logoY}"" width=""56"" height=""56""/>");

                int teamY = y + 92;
                int dateY = y + 122;
                sb.AppendLine($@"  <text x=""540"" y=""{teamY}"" text-anchor=""middle"" class=""font team"">{Esc(home)}  vs  {Esc(away)}</text>");
                if (!string.IsNullOrWhiteSpace(date))
                    sb.AppendLine($@"  <text x=""540"" y=""{dateY}"" text-anchor=""middle"" class=""font date"">{Esc(date)}</text>");

                int pillTop = y + 136;
                int pillH = 46;

                if (!string.IsNullOrWhiteSpace(market))
                {
                    sb.AppendLine($@"  <rect x=""120"" y=""{pillTop}"" width=""360"" height=""{pillH}"" rx=""18"" fill=""#e5e7eb""/>");
                    sb.AppendLine($@"  <text x=""140"" y=""{pillTop + 31}"" class=""font pillText"">{Esc(market)}</text>");
                }

                sb.AppendLine($@"  <rect x=""500"" y=""{pillTop}"" width=""340"" height=""{pillH}"" rx=""18"" fill=""#e5e7eb""/>");
                sb.AppendLine($@"  <text x=""520"" y=""{pillTop + 31}"" class=""font pillText"">Pick: {Esc(pick)}</text>");

                sb.AppendLine($@"  <rect x=""860"" y=""{pillTop}"" width=""120"" height=""{pillH}"" rx=""18"" fill=""#e5e7eb""/>");
                sb.AppendLine($@"  <text x=""920"" y=""{pillTop + 31}"" text-anchor=""middle"" class=""font pillText"">x {Esc(oddTxt)}</text>");

                y += (RowRectH + RowGap);
                idx++;
            }

            // ✅ footer separato (non può più sovrapporsi)
            // ✅ footer separato (non può più sovrapporsi)
            sb.AppendLine($@"  <line x1=""80"" y1=""{footerLineY}"" x2=""1000"" y2=""{footerLineY}"" stroke=""#1f2937"" stroke-width=""2""/>");





            // Destra: timestamp
            // Titolo schedina in basso a sinistra
            sb.AppendLine($@"  <text x=""80"" y=""{footerTextY}"" class=""font sub white"">{Esc(title)}</text>");

            sb.AppendLine($@"  <text x=""1000"" y=""{footerTextY}"" text-anchor=""end"" class=""font sub muted"">{slip.UpdatedAtUtc.ToLocalTime():dd/MM/yyyy HH:mm}</text>");



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
