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

                if (picks.Count == 0)
                    return Ok(new { picks = new List<object>(), message = "Nessun pronostico al momento." });

                // Arricchisci con loghi
                var matchIds = picks.Select(b => b.Id).Distinct().ToList();
                var assets = await (
                    from m in _read.Matches
                    join lg in _read.Leagues on m.LeagueId equals lg.Id
                    join th in _read.Teams on m.HomeId equals th.Id
                    join ta in _read.Teams on m.AwayId equals ta.Id
                    where matchIds.Contains(m.Id)
                    select new
                    {
                        m.Id,
                        leagueLogo = lg.Logo,
                        leagueFlag = lg.Flag,
                        countryCode = lg.CountryCode,
                        homeLogo = th.Logo,
                        awayLogo = ta.Logo
                    }
                ).AsNoTracking().ToDictionaryAsync(k => (long)k.Id);

                var result = picks.Select(p => new
                {
                    p.Id,
                    p.EventDate,
                    p.Competition,
                    p.Match,
                    p.Esito,
                    p.OverUnderRange,
                    p.GG_NG,
                    p.ComboFinale,
                    p.Over15,
                    p.Over25,
                    leagueLogo = assets.ContainsKey((long)p.Id) ? assets[(long)p.Id].leagueLogo : null,
                    leagueFlag = assets.ContainsKey((long)p.Id) ? assets[(long)p.Id].leagueFlag : null,
                    countryCode = assets.ContainsKey((long)p.Id) ? assets[(long)p.Id].countryCode : null,
                    homeLogo = assets.ContainsKey((long)p.Id) ? assets[(long)p.Id].homeLogo : null,
                    awayLogo = assets.ContainsKey((long)p.Id) ? assets[(long)p.Id].awayLogo : null,
                });

                return Ok(new { picks = result, message = (string?)null });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

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

                if (picks.Count == 0)
                    return Ok(new { picks = new List<object>(), message = "Nessun exchange al momento." });

                var matchIds = picks.Select(x => (int)x.Match_Id).Distinct().ToList();
                var assets = await GetAssetsAsync(matchIds);

                var result = picks.Select(p => new
                {
                    p.Match_Id,
                    p.Match_Date,
                    p.Home_Name,
                    p.Away_Name,
                    p.Odd_Home,
                    p.Odd_Draw,
                    p.Odd_Away,
                    p.Home_Rank,
                    p.Away_Rank,
                    p.Rank_Diff,
                    p.Xg_Pred_Total,
                    p.Favorite_Side,
                    p.Favorite_Odd,
                    Score_To_Lay_Contrarian = p.Score_To_Lay,
                    p.Lay_Ok,
                    p.Rating,
                    leagueLogo = assets.ContainsKey((long)p.Match_Id) ? assets[(long)p.Match_Id].leagueLogo : null,
                    leagueFlag = assets.ContainsKey((long)p.Match_Id) ? assets[(long)p.Match_Id].leagueFlag : null,
                    countryCode = assets.ContainsKey((long)p.Match_Id) ? assets[(long)p.Match_Id].countryCode : null,
                    homeLogo = assets.ContainsKey((long)p.Match_Id) ? assets[(long)p.Match_Id].homeLogo : null,
                    awayLogo = assets.ContainsKey((long)p.Match_Id) ? assets[(long)p.Match_Id].awayLogo : null,
                });

                return Ok(new { picks = result, message = (string?)null });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

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

                if (picks.Count == 0)
                    return Ok(new { picks = new List<object>(), message = "Nessun candidato al momento." });

                var matchIds = picks.Select(x => (int)x.Match_Id).Distinct().ToList();
                var assets = await GetAssetsAsync(matchIds);

                var result = picks.Select(p => new
                {
                    p.Match_Id,
                    p.Match_Date,
                    p.Home_Name,
                    p.Away_Name,
                    p.Odd_Home,
                    p.Odd_Draw,
                    p.Odd_Away,
                    p.Home_Rank,
                    p.Away_Rank,
                    p.Rank_Diff,
                    p.Xg_Pred_Total,
                    p.Favorite_Side,
                    p.Favorite_Odd,
                    Score_To_Lay_Contrarian = p.Score_To_Lay,
                    p.Lay_Ok,
                    p.Rating,
                    leagueLogo = assets.ContainsKey((long)p.Match_Id) ? assets[(long)p.Match_Id].leagueLogo : null,
                    leagueFlag = assets.ContainsKey((long)p.Match_Id) ? assets[(long)p.Match_Id].leagueFlag : null,
                    countryCode = assets.ContainsKey((long)p.Match_Id) ? assets[(long)p.Match_Id].countryCode : null,
                    homeLogo = assets.ContainsKey((long)p.Match_Id) ? assets[(long)p.Match_Id].homeLogo : null,
                    awayLogo = assets.ContainsKey((long)p.Match_Id) ? assets[(long)p.Match_Id].awayLogo : null,
                });

                return Ok(new { picks = result, message = (string?)null });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // Helper condiviso
        private async Task<Dictionary<long, dynamic>> GetAssetsAsync(List<int> matchIds)
        {
            var assets = await (
                from m in _read.Matches
                join lg in _read.Leagues on m.LeagueId equals lg.Id
                join th in _read.Teams on m.HomeId equals th.Id
                join ta in _read.Teams on m.AwayId equals ta.Id
                where matchIds.Contains(m.Id)
                select new
                {
                    id = (long)m.Id,
                    leagueLogo = lg.Logo,
                    leagueFlag = lg.Flag,
                    countryCode = lg.CountryCode,
                    homeLogo = th.Logo,
                    awayLogo = ta.Logo
                }
            ).AsNoTracking().ToListAsync();

            return assets.ToDictionary(k => k.id, v => (dynamic)v);
        }
    }
}
