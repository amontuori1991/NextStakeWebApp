using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NextStakeWebApp.Data;
using NextStakeWebApp.Models;
using System.Security.Claims;

namespace NextStakeWebApp.bck.Api
{
    [ApiController]
    [Route("api/favorites")]
    [Authorize(AuthenticationSchemes = "Bearer")]
    public class FavoritesApiController : ControllerBase
    {
        private readonly ReadDbContext _read;

        public FavoritesApiController(ReadDbContext read)
        {
            _read = read;
        }

        // GET /api/favorites - lista match preferiti dell'utente con dati completi
        [HttpGet]
        public async Task<IActionResult> GetFavorites()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();

            var favoriteMatchIds = await _read.Set<FavoriteMatch>()
                .AsNoTracking()
                .Where(f => f.UserId == userId)
                .Select(f => f.MatchId)
                .ToListAsync();

            if (!favoriteMatchIds.Any())
                return Ok(new { events = new List<object>() });

            var today = DateTime.UtcNow.Date;
            var todayEnd = today.AddDays(1);

            var events = await (
                from m in _read.Matches
                join lg in _read.Leagues on m.LeagueId equals lg.Id
                join th in _read.Teams on m.HomeId equals th.Id
                join ta in _read.Teams on m.AwayId equals ta.Id
                where favoriteMatchIds.Contains(m.Id)
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

            return Ok(new { events });
        }

        // GET /api/favorites/ids - solo gli ID dei preferiti (per sapere quali stelle evidenziare)
        [HttpGet("ids")]
        public async Task<IActionResult> GetFavoriteIds()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();

            var ids = await _read.Set<FavoriteMatch>()
                .AsNoTracking()
                .Where(f => f.UserId == userId)
                .Select(f => f.MatchId)
                .ToListAsync();

            return Ok(new { ids });
        }

        // POST /api/favorites/{matchId} - aggiungi preferito
        [HttpPost("{matchId}")]
        public async Task<IActionResult> AddFavorite(long matchId)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();

            var exists = await _read.Set<FavoriteMatch>()
                .AnyAsync(f => f.UserId == userId && f.MatchId == matchId);

            if (exists)
                return Ok(new { added = false, message = "Già nei preferiti" });

            var favorite = new FavoriteMatch
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                MatchId = matchId,
                CreatedAtUtc = DateTime.UtcNow
            };

            _read.Set<FavoriteMatch>().Add(favorite);
            await _read.SaveChangesAsync();

            return Ok(new { added = true });
        }

        // DELETE /api/favorites/{matchId} - rimuovi preferito
        [HttpDelete("{matchId}")]
        public async Task<IActionResult> RemoveFavorite(long matchId)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();

            var favorite = await _read.Set<FavoriteMatch>()
                .FirstOrDefaultAsync(f => f.UserId == userId && f.MatchId == matchId);

            if (favorite == null)
                return Ok(new { removed = false });

            _read.Set<FavoriteMatch>().Remove(favorite);
            await _read.SaveChangesAsync();

            return Ok(new { removed = true });
        }
    }
}