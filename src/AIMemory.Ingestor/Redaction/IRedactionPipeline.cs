namespace AIMemory.Ingestor.Redaction;

public interface IRedactionPipeline
{
    string Redact(string content);
    bool ShouldExcludeFile(string filePath);
    bool IsEnabled { get; }
}
