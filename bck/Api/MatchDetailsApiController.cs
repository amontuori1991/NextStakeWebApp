using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Api;
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
            var cached = await _read.Set<PredictionDbRow>()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.MatchId == id);

            if (cached != null)
            {
                prediction = new
                {
                    esito = cached.Esito,
                    gG_NG = cached.GG_NG,
                    overUnderRange = cached.OverUnderRange,
                    comboFinale = cached.ComboFinale,
                    over1_5 = cached.Over1_5,
                    over2_5 = cached.Over2_5,
                    over3_5 = cached.Over3_5,
                    goalSimHome = cached.GoalSimulatiCasa,
                    goalSimAway = cached.GoalSimulatiOspite,
                    totaleGoal = cached.TotaleGoalSimulati,
                    multigoalCasa = cached.MultigoalCasa,
                    multigoalOspite = cached.MultigoalOspite
                };
            }
            else
            {
                var analysisRow = await _read.Analyses
                    .AsNoTracking()
                    .FirstOrDefaultAsync(a => a.ViewName == "NextMatch_Prediction_New");

                if (analysisRow?.ViewValue is { Length: > 0 } sql)
                {
                    var execSql = sql.Trim().TrimEnd(';').Replace("@MatchId", id.ToString());

                    var wrapSql = "SELECT"
                        + @" ""Id"" AS ""Id"","
                        + @" ""Esito"" AS ""Esito"","
                        + @" ""GG_NG"" AS ""GG_NG"","
                        + @" ""OverUnderRange"" AS ""OverUnderRange"","
                        + @" ""ComboFinale"" AS ""ComboFinale"","
                        + @" ""Over1_5"" AS ""Over1_5"","
                        + @" ""Over2_5"" AS ""Over2_5"""
                        + " FROM (" + execSql + ") AS _pred";

                    var rows = await _read.Set<BestPickRow>()
                        .FromSqlRaw(wrapSql)
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
                        banca1Affidabilita = ex.Banca1Affidabilita,
                        bancaXAffidabilita = ex.BancaXAffidabilita,
                        banca2Affidabilita = ex.Banca2Affidabilita,
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

        // ── 8. ANALISI ────────────────────────────────────────────
        object? analysis = null;
        try
        {
            var analysisController = new MatchAnalysisApiController(_read);
            var analysisResult = await analysisController.GetAsync(id);
            if (analysisResult is OkObjectResult ok)
                analysis = ok.Value;
        }
        catch { }

        // ── 9. GIOCATORI ──────────────────────────────────────────
        List<object> homePlayers = new();
        List<object> awayPlayers = new();
        try { homePlayers = await LoadPlayersForTeamAsync(match.homeId, match.season, match.leagueId); } catch { }
        try { awayPlayers = await LoadPlayersForTeamAsync(match.awayId, match.season, match.leagueId); } catch { }

        return Ok(new { match, prediction, exchange, exchangeError, homeForm, awayForm, standings, odds, analysis, homePlayers, awayPlayers });
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

    // ── Helper: giocatori di un team per stagione/lega ────────
    private async Task<List<object>> LoadPlayersForTeamAsync(int teamId, int season, int leagueId)
    {
        var result = new List<object>();
        if (teamId <= 0) return result;

        var cs = _read.Database.GetConnectionString();
        await using var conn = new Npgsql.NpgsqlConnection(cs);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT
    p.id           AS ""playerId"",
    ps.teamid      AS ""teamId"",
    COALESCE(NULLIF(p.name, ''),
        TRIM(COALESCE(p.firstname, '') || ' ' || COALESCE(p.lastname, ''))
    )              AS ""name"",
    p.age          AS ""age"",
    p.nationality  AS ""nationality"",
    p.height       AS ""height"",
    p.weight       AS ""weight"",
    p.injured      AS ""injured"",
    p.photo        AS ""photo"",
    ROUND(AVG(ps.minutes::numeric), 2)             AS ""minutes"",
    ROUND(AVG(ps.rating::numeric), 2)              AS ""rating"",
    ROUND(AVG(ps.shotstotal::numeric), 2)          AS ""shotsTotal"",
    ROUND(AVG(ps.shotson::numeric), 2)             AS ""shotsOn"",
    ROUND(AVG(ps.goalstotal::numeric), 2)          AS ""goalsTotal"",
    ROUND(AVG(ps.goalsconceded::numeric), 2)       AS ""goalsConceded"",
    ROUND(AVG(ps.assists::numeric), 2)             AS ""assists"",
    ROUND(AVG(ps.goalssaves::numeric), 2)          AS ""goalsSaves"",
    ROUND(AVG(ps.passestotal::numeric), 2)         AS ""passesTotal"",
    ROUND(AVG(ps.passeskey::numeric), 2)           AS ""passesKey"",
    ROUND(AVG(ps.passesaccuracy::numeric), 2)      AS ""passesAccuracy"",
    ROUND(AVG(ps.tacklestotal::numeric), 2)        AS ""tacklesTotal"",
    ROUND(AVG(ps.tacklesblocks::numeric), 2)       AS ""tacklesBlocks"",
    ROUND(AVG(ps.interceptions::numeric), 2)       AS ""interceptions"",
    ROUND(AVG(ps.duelstotal::numeric), 2)          AS ""duelsTotal"",
    ROUND(AVG(ps.duelswon::numeric), 2)            AS ""duelsWon"",
    ROUND(AVG(ps.dribblesattempts::numeric), 2)    AS ""dribblesAttempts"",
    ROUND(AVG(ps.dribblessuccess::numeric), 2)     AS ""dribblesSuccess"",
    ROUND(AVG(ps.dribblespast::numeric), 2)        AS ""dribblesPast"",
    ROUND(AVG(ps.foulsdrawn::numeric), 2)          AS ""foulsDrawn"",
    ROUND(AVG(ps.foulscommitted::numeric), 2)      AS ""foulsCommitted"",
    ROUND(AVG(ps.cardsyellow::numeric), 2)         AS ""cardsYellow"",
    ROUND(AVG(ps.cardsred::numeric), 2)            AS ""cardsRed"",
    ROUND(AVG(ps.penaltywon::numeric), 2)          AS ""penaltyWon"",
    ROUND(AVG(ps.penaltycommitted::numeric), 2)    AS ""penaltyCommitted"",
    ROUND(AVG(ps.penaltyscored::numeric), 2)       AS ""penaltyScored"",
    ROUND(AVG(ps.penaltymissed::numeric), 2)       AS ""penaltyMissed"",
    ROUND(AVG(ps.penaltysaved::numeric), 2)        AS ""penaltySaved""
FROM players_statistics ps
INNER JOIN players p ON p.id = ps.playerid
INNER JOIN matches m ON ps.id = m.id
WHERE ps.teamid = @teamId
  AND m.season = @Season
  AND m.leagueid = @LeagueId
GROUP BY
    p.id, ps.teamid, p.name, p.firstname, p.lastname,
    p.age, p.nationality, p.height, p.weight, p.injured, p.photo
ORDER BY ""name"";
";

        cmd.Parameters.AddWithValue("teamId", teamId);
        cmd.Parameters.AddWithValue("Season", season);
        cmd.Parameters.AddWithValue("LeagueId", leagueId);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            T? Get<T>(string col)
            {
                int ord = reader.GetOrdinal(col);
                if (reader.IsDBNull(ord)) return default;
                object val = reader.GetValue(ord);
                var t = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
                if (t == typeof(int) && val is long l) val = (int)l;
                if (t == typeof(long) && val is int i) val = (long)i;
                return (T)Convert.ChangeType(val, t);
            }

            result.Add(new
            {
                playerId = Get<int>("playerId"),
                teamId = Get<int>("teamId"),
                name = Get<string>("name") ?? "",
                age = Get<int?>("age"),
                nationality = Get<string>("nationality"),
                height = Get<string>("height"),
                weight = Get<string>("weight"),
                injured = Get<bool?>("injured") ?? false,
                photo = Get<string>("photo"),
                position = (string?)null,
                minutes = Get<int?>("minutes") ?? 0,
                rating = Get<string>("rating"),
                shotsTotal = Get<int?>("shotsTotal") ?? 0,
                shotsOn = Get<int?>("shotsOn") ?? 0,
                goalsTotal = Get<int?>("goalsTotal") ?? 0,
                goalsConceded = Get<int?>("goalsConceded") ?? 0,
                assists = Get<int?>("assists") ?? 0,
                goalsSaves = Get<int?>("goalsSaves") ?? 0,
                passesTotal = Get<int?>("passesTotal") ?? 0,
                passesKey = Get<int?>("passesKey") ?? 0,
                passesAccuracy = Get<int?>("passesAccuracy") ?? 0,
                tacklesTotal = Get<int?>("tacklesTotal") ?? 0,
                tacklesBlocks = Get<int?>("tacklesBlocks") ?? 0,
                interceptions = Get<int?>("interceptions") ?? 0,
                duelsTotal = Get<int?>("duelsTotal") ?? 0,
                duelsWon = Get<int?>("duelsWon") ?? 0,
                dribblesAttempts = Get<int?>("dribblesAttempts") ?? 0,
                dribblesSuccess = Get<int?>("dribblesSuccess") ?? 0,
                dribblesPast = Get<int?>("dribblesPast") ?? 0,
                foulsDrawn = Get<int?>("foulsDrawn") ?? 0,
                foulsCommitted = Get<int?>("foulsCommitted") ?? 0,
                cardsYellow = Get<int?>("cardsYellow") ?? 0,
                cardsRed = Get<int?>("cardsRed") ?? 0,
                penaltyWon = Get<int?>("penaltyWon") ?? 0,
                penaltyCommitted = Get<int?>("penaltyCommitted") ?? 0,
                penaltyScored = Get<int?>("penaltyScored") ?? 0,
                penaltyMissed = Get<int?>("penaltyMissed") ?? 0,
                penaltySaved = Get<int?>("penaltySaved") ?? 0,
            });
        }

        return result;
    }
}