using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

public class MessageRepository : IMessageRepository
{
    private readonly AIMemoryDbContext _db;
    private readonly ILogger<MessageRepository> _logger;

    public MessageRepository(AIMemoryDbContext db, ILogger<MessageRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Message> AppendAsync(Message message)
    {
        if (message.MessageId == Guid.Empty)
            message.MessageId = Guid.NewGuid();
        if (message.CreatedAt == default)
            message.CreatedAt = DateTimeOffset.UtcNow;

        _db.Messages.Add(message);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Appended message {MessageId} to session {SessionId}", message.MessageId, message.SessionId);
        return message;
    }

    public async Task<List<Message>> GetBySessionAsync(Guid sessionId, int limit = 100)
    {
        return await _db.Messages
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.CreatedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task AppendBatchAsync(IEnumerable<Message> messages)
    {
        foreach (var message in messages)
        {
            if (message.MessageId == Guid.Empty)
                message.MessageId = Guid.NewGuid();
            if (message.CreatedAt == default)
                message.CreatedAt = DateTimeOffset.UtcNow;
        }

        _db.Messages.AddRange(messages);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Batch appended {Count} messages", messages.Count());
    }
}
