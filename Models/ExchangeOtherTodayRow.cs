namespace NextStakeWebApp.Models
{
    // Rappresenta le colonne della VIEW: exchange_exact_lay_candidates_today_other
    // Nota: proprietà in PascalCase ma mappate via EF (snake_case -> underscore).
    public class ExchangeOtherTodayRow
    {
        public long Match_Id { get; set; }
        public System.DateTime Match_Date { get; set; }
        public int LeagueId { get; set; }
        public int Season { get; set; }

        public int HomeId { get; set; }
        public string? Home_Name { get; set; }

        public int AwayId { get; set; }
        public string? Away_Name { get; set; }

        public float? Odd_Home { get; set; }
        public float? Odd_Draw { get; set; }
        public float? Odd_Away { get; set; }

        public float? Odd_Under_15 { get; set; }
        public float? Odd_Under_25 { get; set; }
        public float? Odd_Under_35 { get; set; }

        public int? Home_Rank { get; set; }
        public int? Away_Rank { get; set; }
        public int? Rank_Diff { get; set; }

        public float? Xg_Pred_Home { get; set; }
        public float? Xg_Pred_Away { get; set; }
        public float? Xg_Pred_Total { get; set; }

        public long Home_Hist_N { get; set; }
        public long Away_Hist_N { get; set; }

        public string? Favorite_Side { get; set; }
        public float? Favorite_Odd { get; set; }

        // La view espone più campi, noi useremo questo per la UI
        public string? Score_To_Lay { get; set; }

        public string? Lay_Mode { get; set; } // "contrarian" / "exchange"
        public int Lay_Ok { get; set; }
        public int Rating { get; set; }

        public float? Xg_Gap { get; set; }
        public float? Balance { get; set; }
    }
}
