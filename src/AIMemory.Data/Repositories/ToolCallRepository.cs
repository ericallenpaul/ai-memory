using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

public class ToolCallRepository : IToolCallRepository
{
    private readonly AIMemoryDbContext _db;
    private readonly ILogger<ToolCallRepository> _logger;

    public ToolCallRepository(AIMemoryDbContext db, ILogger<ToolCallRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ToolCall> AppendAsync(ToolCall toolCall)
    {
        if (toolCall.ToolCallId == Guid.Empty)
            toolCall.ToolCallId = Guid.NewGuid();
        if (toolCall.CreatedAt == default)
            toolCall.CreatedAt = DateTimeOffset.UtcNow;

        _db.ToolCalls.Add(toolCall);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Appended tool call {ToolCallId} ({ToolName}) to session {SessionId}",
            toolCall.ToolCallId, toolCall.ToolName, toolCall.SessionId);
        return toolCall;
    }

    public async Task<List<ToolCall>> GetBySessionAsync(Guid sessionId)
    {
        return await _db.ToolCalls
            .Where(t => t.SessionId == sessionId)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync();
    }
}
