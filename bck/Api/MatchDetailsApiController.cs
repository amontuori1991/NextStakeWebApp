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
        object? prediction = null;
        try
        {
            var analysisRow = await _read.Analyses
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.ViewName == ".." && a.Description == "Partite in Pronostico");

            if (analysisRow?.ViewValue is { Length: > 0 } sql)
            {
                var execSql = sql.Trim().TrimEnd(';').Replace("@MatchId", id.ToString());

                var rows = await _read.Set<BestPickRow>()
                    .FromSqlRaw(execSql)
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
        catch { }

        // ── 3. EXCHANGE ───────────────────────────────────────
        object? exchange = null;
        string? exchangeError = null;
        try
        {
            var exRow = await _read.Analyses
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.ViewName == "NextMatch_Prediction_Exchange");

            if (exRow?.ViewValue is { Length: > 0 } exQuery)
            {
                var innerSql = exQuery.Trim().TrimEnd(';').Replace("@MatchId", id.ToString());

                // Wrapper con alias semplici perché le colonne originali hanno spazi
                var wrapSql = "SELECT"
                    + @" matchid AS ""MatchId"","
                    + @" ""Banca 1 - Affidabilità %"" AS ""Banca1Affidabilita"","
                    + @" ""Banca X - Affidabilità %"" AS ""BancaXAffidabilita"","
                    + @" ""Banca 2 - Affidabilità %"" AS ""Banca2Affidabilita"","
                    + @" ""Bancata consigliata"" AS ""BancataConsigliata"","
                    + @" ""Banca Risultato 1"" AS ""BancaRisultato1"","
                    + @" ""Banca Risultato 2"" AS ""BancaRisultato2"","
                    + @" ""Banca Risultato 3"" AS ""BancaRisultato3"""
                    + " FROM (" + innerSql + ") AS _exwrap";

                var rows = await _read.Set<ExchangePredictionRow>()
                    .FromSqlRaw(wrapSql)
                    .AsNoTracking()
                    .ToListAsync();

                var ex = rows.FirstOrDefault();
                if (ex != null)
                    exchange = new
                    {
                        banca1Pct = ex.Banca1Affidabilita,
                        bancaXPct = ex.BancaXAffidabilita,
                        banca2Pct = ex.Banca2Affidabilita,
                        bancataConsigliata = ex.BancataConsigliata,
                        bancaRisultato1 = ex.BancaRisultato1,
                        bancaRisultato2 = ex.BancaRisultato2,
                        bancaRisultato3 = ex.BancaRisultato3
                    };
            }
        }
        catch (Exception exErr) { exchangeError = exErr.Message; }

        // ── 4 + 5. FORMA HOME e AWAY ──────────────────────────
        List<object> homeForm = new();
        List<object> awayForm = new();
        try { homeForm = await GetFormAsync(match.homeId, id); } catch { }
        try { awayForm = await GetFormAsync(match.awayId, id); } catch { }

        // ── 6. CLASSIFICA ─────────────────────────────────────
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

        return Ok(new { match, prediction, exchange, exchangeError, homeForm, awayForm, standings, odds });
    }

    // ── Helper: ultime 5 partite di un team (solo FT) ────────
    private async Task<List<object>> GetFormAsync(int teamId, long excludeMatchId)
    {
        var recent = await (
            from m in _read.Matches
            join th in _read.Teams on m.HomeId equals th.Id
            join ta in _read.Teams on m.AwayId equals ta.Id
            where (m.HomeId == teamId || m.AwayId == teamId)
               && m.Id != excludeMatchId
               && m.HomeGoal != null
               && m.AwayGoal != null
               && m.StatusShort == "FT"
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

        return recent.Select(m =>
        {
            var scored = (m.IsHome ? m.HomeGoal : m.AwayGoal) ?? 0;
            var conceded = (m.IsHome ? m.AwayGoal : m.HomeGoal) ?? 0;
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
