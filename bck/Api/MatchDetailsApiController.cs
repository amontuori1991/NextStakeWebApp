using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;

namespace NextStakeWebApp.bck.Api;

[ApiController]
[Route("api/match")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class MatchDetailsApiController : ControllerBase
{
    private readonly ReadDbContext _read;

    public MatchDetailsApiController(ReadDbContext read)
    {
        _read = read;
    }

    // ─────────────────────────────────────────────────────────
    // GET /api/match/{id}/details
    // ─────────────────────────────────────────────────────────
    [HttpGet("{id}/details")]
    public async Task<IActionResult> GetDetails(long id)
    {
        // ── 1. INFO PARTITA BASE ──────────────────────────────
        var match = await (
            from m in _read.Matches
            join lg in _read.Leagues on m.LeagueId equals lg.Id
            join th in _read.Teams on m.HomeId equals th.Id
            join ta in _read.Teams on m.AwayId equals ta.Id
            where m.Id == id
            select new
            {
                matchId = (long)m.Id,
                leagueId = m.LeagueId,
                season = m.Season,
                leagueName = lg.Name,
                leagueLogo = lg.Logo,
                leagueFlag = lg.Flag,
                countryName = lg.CountryName,
                countryCode = lg.CountryCode,
                homeId = m.HomeId,
                home = th.Name,
                homeLogo = th.Logo,
                awayId = m.AwayId,
                away = ta.Name,
                awayLogo = ta.Logo,
                kickoffUtc = m.Date,
                statusShort = m.StatusShort,
                goalsHome = m.HomeGoal,
                goalsAway = m.AwayGoal
            }
        ).AsNoTracking().FirstOrDefaultAsync();

        if (match == null)
            return NotFound(new { error = "Partita non trovata" });

        // ── 2. PRONOSTICO ─────────────────────────────────────
        // BestPickRow è keyless (FromSqlRaw dalla query in Analyses)
        // Filtriamo per Id == matchId dopo aver eseguito la view
        object? prediction = null;
        try
        {
            var analysisProno = await _read.Analyses
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.ViewName == ".." && a.Description == "Partite in Pronostico");

            if (analysisProno?.ViewValue is { Length: > 0 } sql)
            {
                // Avvolgiamo la query in una subquery e filtriamo per Id
                var fullSql = $@"SELECT * FROM ({sql.Trim()}) AS _sub WHERE ""Id"" = {id}";
                var rows = await _read.Set<BestPickRow>()
                    .FromSqlRaw(fullSql)
                    .AsNoTracking()
                    .ToListAsync();

                var p = rows.FirstOrDefault();
                if (p != null)
                    prediction = new
                    {
                        esito = p.Esito,
                        gG_NG = p.GG_NG,
                        overUnderRange = p.OverUnderRange,
                        comboFinale = p.ComboFinale,
                        over1_5 = p.Over15,
                        over2_5 = p.Over25
                    };
            }
        }
        catch { /* pronostico non disponibile per questa partita */ }

        // ── 3. EXCHANGE ───────────────────────────────────────
        // ExchangeTodayRow ha Match_Id, Odd_Home, Odd_Draw, Odd_Away,
        // Score_To_Lay, Rating, Lay_Ok, Favorite_Side, Favorite_Odd, Rank_Diff
        object? exchange = null;
        try
        {
            var exRows = await _read.Set<ExchangeTodayRow>()
                .FromSqlRaw($@"SELECT * FROM ""NextMatch_ExchangeToday"" WHERE match_id = {id}")
                .AsNoTracking()
                .ToListAsync();

            var ex = exRows.FirstOrDefault();
            if (ex != null)
                exchange = new
                {
                    scoreToLay = ex.Score_To_Lay,
                    rating = ex.Rating,
                    layOk = ex.Lay_Ok,
                    favoriteSide = ex.Favorite_Side,
                    favoriteOdd = ex.Favorite_Odd,
                    oddHome = ex.Odd_Home,
                    oddDraw = ex.Odd_Draw,
                    oddAway = ex.Odd_Away,
                    rankDiff = ex.Rank_Diff
                };
        }
        catch { /* exchange non disponibile */ }

        // ── 4 + 5. FORMA HOME e AWAY (LINQ su _read.Matches) ─
        // Usiamo i DbSet già disponibili — nessuna SQL raw necessaria
        List<object> homeForm = new();
        List<object> awayForm = new();
        try
        {
            homeForm = await GetFormAsync(match.homeId, id);
            awayForm = await GetFormAsync(match.awayId, id);
        }
        catch { }

        // ── 6. CLASSIFICA ─────────────────────────────────────
        // Standing non ha TeamLogo → join Teams per logo
        // Proprietà reali: AllPlayed, AllWin, AllDraw, AllLose, AllGoalFor, AllGoalAgainst
        List<object> standings = new();
        try
        {
            standings = await (
                from s in _read.Standings
                join t in _read.Teams on s.TeamId equals t.Id
                where s.LeagueId == match.leagueId && s.Season == match.season
                orderby s.Rank
                select new
                {
                    teamId = s.TeamId,
                    teamName = t.Name,
                    teamLogo = t.Logo,
                    rank = s.Rank,
                    points = s.Points,
                    goalsDiff = s.GoalsDiff,
                    played = s.AllPlayed,
                    won = s.AllWin,
                    drawn = s.AllDraw,
                    lost = s.AllLose,
                    goalsFor = s.AllGoalFor,
                    goalsAgainst = s.AllGoalAgainst
                }
            ).AsNoTracking().Take(25).Cast<object>().ToListAsync();
        }
        catch { }

        // ── 7. QUOTE 1X2 ──────────────────────────────────────
        // Odds.Id = match_id (fixture), Odd è float, Bookmaker è int
        List<object> odds = new();
        try
        {
            odds = await _read.Odds
                .Where(o => o.Id == id && (o.Betid == 1 || o.Betid == 5))
                .OrderBy(o => o.Betid).ThenBy(o => o.Odd)
                .Select(o => new
                {
                    betId = o.Betid,
                    description = o.Description,
                    value = o.Value,
                    odd = (double)o.Odd,
                    bookmaker = o.Bookmaker,
                    dateUpdated = o.Dateupd
                })
                .AsNoTracking()
                .Cast<object>()
                .ToListAsync();
        }
        catch { }

        return Ok(new { match, prediction, exchange, homeForm, awayForm, standings, odds });
    }

    // ── Helper: ultime 5 partite di un team via LINQ ──────────
    private async Task<List<object>> GetFormAsync(int teamId, long excludeMatchId)
    {
        // Prendi le ultime 5 partite con risultato definitivo
        var recentMatches = await (
            from m in _read.Matches
            join th in _read.Teams on m.HomeId equals th.Id
            join ta in _read.Teams on m.AwayId equals ta.Id
            where (m.HomeId == teamId || m.AwayId == teamId)
               && m.Id != excludeMatchId
               && m.HomeGoal != null
               && m.AwayGoal != null
            orderby m.Date descending
            select new
            {
                m.Date,
                IsHome = m.HomeId == teamId,
                Opponent = m.HomeId == teamId ? ta.Name : th.Name,
                HomeGoal = m.HomeGoal,
                AwayGoal = m.AwayGoal
            }
        ).AsNoTracking().Take(5).ToListAsync();

        return recentMatches.Select(m =>
        {
            var scored = m.IsHome ? m.HomeGoal : m.AwayGoal;
            var conceded = m.IsHome ? m.AwayGoal : m.HomeGoal;
            var result = scored > conceded ? "W" : scored < conceded ? "L" : "D";
            return (object)new
            {
                dateUtc = m.Date,
                opponent = m.Opponent,
                isHome = m.IsHome,
                score = $"{m.HomeGoal}-{m.AwayGoal}",
                result
            };
        }).ToList();
    }
}
