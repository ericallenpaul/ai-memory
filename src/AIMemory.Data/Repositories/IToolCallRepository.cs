using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

public interface IToolCallRepository
{
    Task<ToolCall> AppendAsync(ToolCall toolCall);
    Task<List<ToolCall>> GetBySessionAsync(Guid sessionId);
}
