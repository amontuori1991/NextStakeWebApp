using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace NextStakeWebApp.Models
{
    [Keyless]
    public class BestPickRow
    {
        public long Id { get; set; }

        [Column("Data Evento")]
        public DateTime EventDate { get; set; }

        [Column("Competizione")]
        public string Competition { get; set; } = "";

        [Column("Partita")]
        public string Match { get; set; } = "";

        [Column("Goal Simulati Casa")]
        public decimal GoalSimulatiCasa { get; set; }

        [Column("Goal Simulati Ospite")]
        public decimal GoalSimulatiOspite { get; set; }

        [Column("Totale Goal Simulati")]
        public decimal TotaleGoalSimulati { get; set; }

        [Column("Esito")]
        public string Esito { get; set; } = "";

        [Column("OverUnderRange")]
        public string OverUnderRange { get; set; } = "";

        // ✅ Aggiornati con i nomi reali presenti nella query finale
        [Column("Over1_5")]
        public decimal Over15 { get; set; }

        [Column("Over2_5")]
        public decimal Over25 { get; set; }

        [Column("Over3_5")]
        public decimal Over35 { get; set; }

        [Column("GG_NG")]
        public string GG_NG { get; set; } = "";

        [Column("Multigoal Attesi Casa")]
        public string MultigoalCasa { get; set; } = "";

        [Column("Multigoal Attesi Ospite")]
        public string MultigoalOspite { get; set; } = "";

        [Column("ComboFinale")]
        public string ComboFinale { get; set; } = "";
    }
}
