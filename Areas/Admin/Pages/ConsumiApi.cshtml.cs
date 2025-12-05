using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;
using System.Linq;

namespace NextStakeWebApp.Areas.Admin.Pages
{
    [Authorize(Policy = "RequirePlan1")]
    public class ConsumiApiModel : PageModel
    {
        private readonly ApplicationDbContext _context;

        public ConsumiApiModel(ApplicationDbContext context)
        {
            _context = context;
        }

        // Tabella completa (come prima)
        public IList<CallCounter> Records { get; set; } = new List<CallCounter>();

        // Dati aggregati per il grafico (giorno più recente)
        public List<OriginUsage> TodayUsage { get; set; } = new();

        public int TodayTotal { get; set; }

        // Tetto massimo giornaliero (per testo e percentuali)
        public int DailyCap => 7500;

        public async Task OnGetAsync()
        {
            Records = await _context.CallCounter
                .OrderByDescending(c => c.Date)
                .ToListAsync();

            if (Records.Any())
            {
                // Prendiamo il giorno più recente presente in tabella
                var latestDate = Records.Max(c => c.Date.Date);

                TodayUsage = Records
                    .Where(c => c.Date.Date == latestDate)
                    .GroupBy(c => c.Origin)
                    .Select(g => new OriginUsage
                    {
                        Origin = g.Key,
                        Counter = g.Sum(x => x.Counter)
                    })
                    .OrderByDescending(x => x.Counter)
                    .ToList();

                TodayTotal = TodayUsage.Sum(x => x.Counter);
            }
        }

        public class OriginUsage
        {
            public string Origin { get; set; } = string.Empty;
            public int Counter { get; set; }
        }
    }
}
