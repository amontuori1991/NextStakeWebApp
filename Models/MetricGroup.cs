using System.Collections.Generic;

namespace NextStakeWebApp.Models
{
    /// <summary>
    /// Rappresenta un gruppo di metriche (es. Goal, Corner, Falli, ecc.)
    /// </summary>
    public class MetricGroup
    {
        public Dictionary<string, string> Metrics { get; set; } = new();
    }

    /// <summary>
    /// Rappresenta la forma recente (ultime 5 partite) di una squadra.
    /// </summary>
    public class FormRow
    {
        public long MatchId { get; set; }
        public DateTime DateUtc { get; set; }
        public string Opponent { get; set; } = "";
        public string Score { get; set; } = "";
        public string Result { get; set; } = ""; // W / D / L
        public bool IsHome { get; set; }
    }

    /// <summary>
    /// Rappresenta una riga della classifica.
    /// </summary>
    public class StandingRow
    {
        public int Rank { get; set; }
        public string TeamName { get; set; } = "";
        public int Points { get; set; }
        public int Played { get; set; }
        public int Win { get; set; }
        public int Draw { get; set; }
        public int Lose { get; set; }
        public int GF { get; set; } // goal fatti
        public int GA { get; set; } // goal subiti
        public int Diff => GF - GA; // differenza reti
        public long TeamId { get; set; }
    }
}
