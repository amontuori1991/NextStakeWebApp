namespace NextStakeWebApp.Models
{
    public class ExchangeTodayRow
    {
        public long Match_Id { get; set; }
        public DateTime Match_Date { get; set; }
        public int LeagueId { get; set; }
        public int Season { get; set; }

        public int HomeId { get; set; }
        public string Home_Name { get; set; } = "";
        public int AwayId { get; set; }
        public string Away_Name { get; set; } = "";

        // QUOTE (real → float)
        public float? Odd_Home { get; set; }
        public float? Odd_Draw { get; set; }
        public float? Odd_Away { get; set; }
        public float? Odd_Over_15 { get; set; }
        public float? Odd_Over_25 { get; set; }

        public int? Home_Rank { get; set; }
        public int? Away_Rank { get; set; }
        public int? Rank_Diff { get; set; }

        // xG (real)
        public float Xg_Pred_Home { get; set; }
        public float Xg_Pred_Away { get; set; }
        public float Xg_Pred_Total { get; set; }

        public int Home_Hist_N { get; set; }
        public int Away_Hist_N { get; set; }

        public string? Favorite_Side { get; set; }
        public float? Favorite_Odd { get; set; }

        public string Score_To_Lay { get; set; } = "";
        public int Lay_Ok { get; set; }
        public int Rating { get; set; }
    }
}
