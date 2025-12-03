namespace NextStakeWebApp.ApiSports;

/// <summary>
/// Modello base della risposta Api-Football /fixtures
/// </summary>
public class ApiFootballFixturesResponse
{
    public List<ApiFootballFixtureItem> Response { get; set; } = new();
}

public class ApiFootballFixtureItem
{
    public ApiFootballFixture Fixture { get; set; } = new();
    public ApiFootballGoals Goals { get; set; } = new();
}

public class ApiFootballFixture
{
    public int Id { get; set; }
    public ApiFootballStatus Status { get; set; } = new();
}

public class ApiFootballStatus
{
    public string Short { get; set; } = "";   // es. "NS", "1H", "HT", "FT"
    public int? Elapsed { get; set; }         // minutaggio
}

public class ApiFootballGoals
{
    public int? Home { get; set; }
    public int? Away { get; set; }
}
