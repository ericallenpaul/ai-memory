using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

public interface IMessageRepository
{
    Task<Message> AppendAsync(Message message);
    Task<List<Message>> GetBySessionAsync(Guid sessionId, int limit = 100);
    Task AppendBatchAsync(IEnumerable<Message> messages);
}
