namespace AIMemory.Models.Dtos;

public class SetupRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string DatabaseProvider { get; set; } = "SQLite";
    public string? ConnectionString { get; set; }
    public int? Port { get; set; }
}

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
