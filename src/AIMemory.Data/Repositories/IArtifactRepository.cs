using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

public interface IArtifactRepository
{
    Task<Artifact> AppendAsync(Artifact artifact);
    Task<List<Artifact>> GetBySessionAsync(Guid sessionId);
}
