using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Data;

namespace NextStakeWebApp.bck.Api
{
    [ApiController]
    [Route("api/events")]
    [Authorize(AuthenticationSchemes = "Bearer")]
    public class EventsApiController : ControllerBase
    {
        private readonly ReadDbContext _read;

        public EventsApiController(ReadDbContext read)
        {
            _read = read;
        }

        // GET /api/events?d=2024-03-10
        [HttpGet]
        public async Task<IActionResult> GetEvents([FromQuery] string? d = null)
        {
            DateOnly selectedDate;
            if (string.IsNullOrWhiteSpace(d) || !DateOnly.TryParse(d, out selectedDate))
                selectedDate = DateOnly.FromDateTime(DateTime.Now);

            var dayStart = selectedDate.ToDateTime(TimeOnly.MinValue);
            var dayEnd = dayStart.AddDays(1);

            var events = await (
                from m in _read.Matches
                join lg in _read.Leagues on m.LeagueId equals lg.Id
                join th in _read.Teams on m.HomeId equals th.Id
                join ta in _read.Teams on m.AwayId equals ta.Id
                where m.Date >= dayStart && m.Date < dayEnd
                orderby m.Date
                select new
                {
                    matchId = m.Id,
                    leagueId = m.LeagueId,
                    leagueName = lg.Name,
                    leagueLogo = lg.Logo,
                    leagueFlag = lg.Flag,
                    countryName = lg.CountryName,
                    countryCode = lg.CountryCode,
                    home = th.Name,
                    away = ta.Name,
                    homeLogo = th.Logo,
                    awayLogo = ta.Logo,
                    homeGoal = m.HomeGoal,
                    awayGoal = m.AwayGoal,
                    kickoff = m.Date,
                    status = m.StatusShort
                }
            )
            .AsNoTracking()
            .ToListAsync();

            return Ok(new { date = selectedDate.ToString("yyyy-MM-dd"), events });
        }

        // GET /api/events/best
        [HttpGet("best")]
        public async Task<IActionResult> GetBestPicks()
        {
            try
            {
                var analysis = await _read.Analyses
                    .AsNoTracking()
                    .FirstOrDefaultAsync(a => a.ViewName == ".." && a.Description == "Partite in Pronostico");

                if (analysis == null)
                    return Ok(new { picks = new List<object>(), message = "Nessun pronostico disponibile." });

                var sql = (analysis.ViewValue ?? "").Trim();
                if (string.IsNullOrWhiteSpace(sql))
                    return Ok(new { picks = new List<object>(), message = "Query vuota." });

                var picks = await _read.Set<NextStakeWebApp.Models.BestPickRow>()
                    .FromSqlRaw(sql)
                    .AsNoTracking()
                    .ToListAsync();

                return Ok(new { picks, message = picks.Count == 0 ? "Nessun pronostico al momento." : null });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // GET /api/events/exchange
        [HttpGet("exchange")]
        public async Task<IActionResult> GetExchangePicks()
        {
            try
            {
                var analysis = await _read.Analyses
                    .AsNoTracking()
                    .FirstOrDefaultAsync(a => a.ViewName == "NextMatch_ExchangeToday");

                if (analysis == null)
                    return Ok(new { picks = new List<object>(), message = "Nessun exchange disponibile." });

                var sql = (analysis.ViewValue ?? "").Trim();
                if (string.IsNullOrWhiteSpace(sql))
                    return Ok(new { picks = new List<object>(), message = "Query vuota." });

                var picks = await _read.Set<NextStakeWebApp.Models.ExchangeTodayRow>()
                    .FromSqlRaw(sql)
                    .AsNoTracking()
                    .ToListAsync();

                return Ok(new { picks, message = picks.Count == 0 ? "Nessun exchange al momento." : null });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // GET /api/events/exchange-other
        [HttpGet("exchange-other")]
        public async Task<IActionResult> GetExchangeOtherPicks()
        {
            try
            {
                const string sql = "SELECT * FROM exchange_exact_lay_candidates_today_other";

                var picks = await _read.Set<NextStakeWebApp.Models.ExchangeOtherTodayRow>()
                    .FromSqlRaw(sql)
                    .AsNoTracking()
                    .ToListAsync();

                return Ok(new { picks, message = picks.Count == 0 ? "Nessun candidato al momento." : null });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }
}
