using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NextStakeWebApp.Models
{
    [Table("standings")]
    public class Standing
    {
        [Key]
        [Column("leagueid")] public int LeagueId { get; set; }
        [Column("teamid")] public int TeamId { get; set; }
        [Column("season")] public int Season { get; set; }

        [Column("rank")] public int? Rank { get; set; }
        [Column("points")] public int? Points { get; set; }
        [Column("description")] public string? Description { get; set; }
        [Column("goalsdiff")] public int? GoalsDiff { get; set; }

        [Column("allplayed")] public int? AllPlayed { get; set; }
        [Column("allwin")] public int? AllWin { get; set; }
        [Column("alldraw")] public int? AllDraw { get; set; }
        [Column("alllose")] public int? AllLose { get; set; }
        [Column("allgoalfor")] public int? AllGoalFor { get; set; }
        [Column("allgoalagainst")] public int? AllGoalAgainst { get; set; }

        [Column("homeplayed")] public int? HomePlayed { get; set; }
        [Column("homewin")] public int? HomeWin { get; set; }
        [Column("homedraw")] public int? HomeDraw { get; set; }
        [Column("homelose")] public int? HomeLose { get; set; }
        [Column("homegoalfor")] public int? HomeGoalFor { get; set; }
        [Column("homegoalagainst")] public int? HomeGoalAgainst { get; set; }

        [Column("awayplayed")] public int? AwayPlayed { get; set; }
        [Column("awaywin")] public int? AwayWin { get; set; }
        [Column("awaydraw")] public int? AwayDraw { get; set; }
        [Column("awaylose")] public int? AwayLose { get; set; }
        [Column("awaygoalfor")] public int? AwayGoalFor { get; set; }
        [Column("awaygoalagainst")] public int? AwayGoalAgainst { get; set; }

        [Column("groupname")] public string? GroupName { get; set; }
    }
}
