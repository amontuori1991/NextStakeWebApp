using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NextStakeWebApp.Models
{
    [Table("matches")]
    public class MatchCore
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("date")]
        public DateTime Date { get; set; } // timestamp without time zone

        [Column("venueid")] public int? VenueId { get; set; }

        [Column("statusshort")] public string? StatusShort { get; set; }
        [Column("statuselapsed")] public int? StatusElapsed { get; set; }
        [Column("statusextra")] public int? StatusExtra { get; set; }

        [Column("leagueid")] public int LeagueId { get; set; }
        [Column("leagueround")] public string? LeagueRound { get; set; }
        [Column("matchround")] public int? MatchRound { get; set; }
        [Column("season")] public int Season { get; set; }

        [Column("homeid")] public int HomeId { get; set; }
        [Column("awayid")] public int AwayId { get; set; }

        [Column("homegoal")] public int? HomeGoal { get; set; }
        [Column("awaygoal")] public int? AwayGoal { get; set; }
        [Column("result")] public string? Result { get; set; }

        [Column("homehalftimegoal")] public int? HomeHalftimeGoal { get; set; }
        [Column("awayhalftimegoal")] public int? AwayHalftimeGoal { get; set; }
        [Column("homefulltimegoal")] public int? HomeFulltimeGoal { get; set; }
        [Column("awayfulltimegoal")] public int? AwayFulltimeGoal { get; set; }
        [Column("homeextratimegoal")] public int? HomeExtratimeGoal { get; set; }
        [Column("awayextratimegoal")] public int? AwayExtratimeGoal { get; set; }
        [Column("homepenaltygoal")] public int? HomePenaltyGoal { get; set; }
        [Column("awaypenaltygoal")] public int? AwayPenaltyGoal { get; set; }
    }
}
