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
    }
}
