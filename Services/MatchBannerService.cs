using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;

namespace NextStakeWebApp.Services
{
    public interface IMatchBannerService
    {
        Task<string> CreateAsync(
            string homeName, string awayName,
            string? homeLogoUrl, string? awayLogoUrl,
            string league, DateTime kickoffLocal, long matchId);
    }

    public class MatchBannerService : IMatchBannerService
    {
        private readonly IWebHostEnvironment _env;
        private readonly IHttpClientFactory _http;

        public MatchBannerService(IWebHostEnvironment env, IHttpClientFactory http)
        {
            _env = env;
            _http = http;
        }

        public async Task<string> CreateAsync(
            string homeName, string awayName,
            string? homeLogoUrl, string? awayLogoUrl,
            string league, DateTime kickoffLocal, long matchId)
        {
            const int W = 1200, H = 628;

            var webroot = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            var outputDir = Path.Combine(webroot, "temp");
            Directory.CreateDirectory(outputDir);
            var path = Path.Combine(outputDir, $"match_{matchId}.jpg");

            using var img = new Image<Rgba32>(W, H);

            // Sfondo solido
            img.Mutate(c => c.Fill(Color.ParseHex("#1F2430")));

            // Font
            Font fontTitle;
            Font fontSub;
            Font fontVs;
            try
            {
                var fam = SystemFonts.TryGet("Segoe UI", out var f1) ? f1
                        : (SystemFonts.TryGet("Arial", out var f2) ? f2
                        : SystemFonts.Collection.Families.First());
                fontTitle = fam.CreateFont(64, FontStyle.Bold);
                fontSub = fam.CreateFont(30, FontStyle.Regular);
                fontVs = fam.CreateFont(84, FontStyle.Bold);
            }
            catch
            {
                var fam = SystemFonts.Collection.Families.First();
                fontTitle = fam.CreateFont(64, FontStyle.Bold);
                fontSub = fam.CreateFont(30, FontStyle.Regular);
                fontVs = fam.CreateFont(84, FontStyle.Bold);
            }

            // Header: lega + data/ora (usa RichTextOptions)
            img.Mutate(c =>
            {
                c.DrawText(new RichTextOptions(fontSub)
                {
                    Origin = new PointF(40, 40),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                }, league, Color.White);

                c.DrawText(new RichTextOptions(fontSub)
                {
                    Origin = new PointF(40, 84),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                }, kickoffLocal.ToString("yyyy-MM-dd HH:mm"), Color.White);
            });

            // Helper: carica logo da URL
            async Task<Image<Rgba32>?> LoadLogoAsync(string? url)
            {
                if (string.IsNullOrWhiteSpace(url)) return null;
                try
                {
                    var client = _http.CreateClient();
                    var bytes = await client.GetByteArrayAsync(url);
                    return Image.Load<Rgba32>(bytes);
                }
                catch { return null; }
            }

            var homeLogo = await LoadLogoAsync(homeLogoUrl);
            var awayLogo = await LoadLogoAsync(awayLogoUrl);

            var logoSize = 240;
            var leftX = 180; var rightX = W - 180 - logoSize;
            var centerY = H / 2 - logoSize / 2;

            if (homeLogo != null)
            {
                homeLogo.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(logoSize, logoSize), Mode = ResizeMode.Max }));
                img.Mutate(c => c.DrawImage(homeLogo, new Point(leftX, centerY), 1f));
            }
            if (awayLogo != null)
            {
                awayLogo.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(logoSize, logoSize), Mode = ResizeMode.Max }));
                img.Mutate(c => c.DrawImage(awayLogo, new Point(rightX, centerY), 1f));
            }

            // Nomi squadre + "VS" centrato
            img.Mutate(c =>
            {
                // Home (sx)
                c.DrawText(homeName, fontTitle, Color.White, new PointF(140, centerY + logoSize + 40));

                // Away (dx allineato a destra)
                var szAway = TextMeasurer.MeasureSize(awayName, new RichTextOptions(fontTitle));
                c.DrawText(awayName, fontTitle, Color.White,
                           new PointF(W - 140 - szAway.Width, centerY + logoSize + 40));

                // VS centrato
                var vsText = "VS";
                var szVs = TextMeasurer.MeasureSize(vsText, new RichTextOptions(fontVs));
                c.DrawText(vsText, fontVs, Color.White,
                           new PointF((W - szVs.Width) / 2, (H - szVs.Height) / 2));
            });

            await img.SaveAsJpegAsync(path, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 92 });

            homeLogo?.Dispose();
            awayLogo?.Dispose();

            return path;
        }
    }
}
