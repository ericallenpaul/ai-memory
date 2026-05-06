namespace AIMemory.Models.Dtos;

public class StatsResponse
{
    public int TotalSessions { get; set; }
    public int TotalMessages { get; set; }
    public long TotalTokensIn { get; set; }
    public long TotalTokensOut { get; set; }
    public decimal TotalCostUsd { get; set; }
    public int SessionsToday { get; set; }
    public List<DailyStats> DailyStats { get; set; } = [];
}

public class DailyStats
{
    public string Date { get; set; } = string.Empty;
    public int Sessions { get; set; }
    public int Messages { get; set; }
}
