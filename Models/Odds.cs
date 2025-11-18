using System;
using Microsoft.EntityFrameworkCore;

namespace NextStakeWebApp.Models
{
    [Keyless] // leggiamo solo, non ci serve una PK
    public class Odds
    {
        // id = matchid
        public long Id { get; set; }

        // 8 = Bet365 (per ora lasciamo numero)
        public int Bookmaker { get; set; }

        // tipo di quota (per ora numerico)
        public int Betid { get; set; }

        // descrizione grezza
        public string? Description { get; set; }

        // value grezzo (es. "1", "X", "Over 2.5"...)
        public string? Value { get; set; }

        // quota numerica (es. 6.50)
        public float Odd { get; set; }

        // data/ora aggiornamento quota
        public DateTime Dateupd { get; set; }
    }
}
