using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace NextStakeWebApp.Models
{
    [Keyless]
    [Table("nextmatch")] // mappiamo la vista esistente
    public class NextMatch
    {
        [Column("matchid")]
        public long MatchId { get; set; }

        [Column("idsquadracasa")]
        public long IdSquadraCasa { get; set; }

        [Column("squadracasa")]
        public string SquadraCasa { get; set; } = "";

        [Column("idsquadratrasferta")]
        public long IdSquadraTrasferta { get; set; }

        [Column("squadratrasferta")]
        public string SquadraTrasferta { get; set; } = "";

        [Column("leagueid")]
        public int LeagueId { get; set; }

        [Column("season")]
        public string? Season { get; set; }

        [Column("leagueround")]
        public string? LeagueRound { get; set; }
    }
}
