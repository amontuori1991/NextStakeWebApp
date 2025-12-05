using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NextStakeWebApp.Models
{
    public class LiveMatchState
    {
        public int MatchId { get; set; }
        public string LastStatus { get; set; } = "";
        public int? LastHome { get; set; }
        public int? LastAway { get; set; }
        public int? LastElapsed { get; set; }
        public DateTime LastUpdatedUtc { get; set; }
    }
}