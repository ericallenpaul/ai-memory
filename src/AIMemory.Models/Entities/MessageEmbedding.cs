namespace AIMemory.Models.Entities;

public class MessageEmbedding
{
    public Guid MessageId { get; set; }
    public Guid SessionId { get; set; }
    public byte[] Embedding { get; set; } = [];
    public int Dimensions { get; set; }
    public string Model { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
