namespace AIMemory.Models.Dtos;

public class CreateApiKeyRequest
{
    public string Name { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = [];
    public DateTimeOffset? ExpiresAt { get; set; }
}

public class UpdateApiKeyRequest
{
    public string? Name { get; set; }
    public List<string>? Scopes { get; set; }
    public bool? IsActive { get; set; }
}

public class ApiKeyResponse
{
    public Guid ApiKeyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = [];
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? CreatedBy { get; set; }
}

public class CreateApiKeyResponse
{
    public Guid ApiKeyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RawKey { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = [];
}
