using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using AIMemory.Models.Entities;

namespace AIMemory.Data;

public class AIMemoryDbContext : DbContext
{
    public AIMemoryDbContext(DbContextOptions<AIMemoryDbContext> options) : base(options) { }

    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<ToolCall> ToolCalls => Set<ToolCall>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<IngestionLogEntry> IngestionLog => Set<IngestionLogEntry>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<MessageEmbedding> MessageEmbeddings => Set<MessageEmbedding>();
    public DbSet<CodeRepository> CodeRepositories => Set<CodeRepository>();
    public DbSet<CodeFile> CodeFiles => Set<CodeFile>();
    public DbSet<CodeSymbol> CodeSymbols => Set<CodeSymbol>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    private bool IsSqlite => Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var jsonDocConverter = new ValueConverter<JsonDocument?, string?>(
            v => v != null ? v.RootElement.GetRawText() : null,
            v => v != null ? JsonDocument.Parse(v, default) : null);

        var tagsConverter = new ValueConverter<List<string>, string>(
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
            v => JsonSerializer.Deserialize<List<string>>(v, JsonSerializerOptions.Default) ?? new List<string>());

        // SQLite cannot sort/filter DateTimeOffset natively. Store as ISO 8601 strings which sort correctly.
        if (IsSqlite)
        {
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                    {
                        property.SetValueConverter(
                            property.ClrType == typeof(DateTimeOffset)
                                ? new ValueConverter<DateTimeOffset, string>(
                                    v => v.ToUniversalTime().ToString("o"),
                                    v => DateTimeOffset.Parse(v))
                                : new ValueConverter<DateTimeOffset?, string?>(
                                    v => v.HasValue ? v.Value.ToUniversalTime().ToString("o") : null,
                                    v => v != null ? DateTimeOffset.Parse(v) : null));
                    }
                }
            }
        }

        modelBuilder.Entity<Session>(entity =>
        {
            entity.ToTable("sessions");
            entity.HasKey(e => e.SessionId);
            entity.Property(e => e.SessionId).HasColumnName("session_id");
            entity.Property(e => e.ExternalId).HasColumnName("external_id");
            entity.Property(e => e.Title).HasColumnName("title").IsRequired();
            entity.Property(e => e.Project).HasColumnName("project");
            entity.Property(e => e.Repo).HasColumnName("repo");
            entity.Property(e => e.Branch).HasColumnName("branch");
            entity.Property(e => e.Source).HasColumnName("source");
            entity.Property(e => e.MachineName).HasColumnName("machine_name");
            entity.Property(e => e.IsArchived).HasColumnName("is_archived");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");

            if (IsSqlite)
            {
                entity.Property(e => e.Tags).HasColumnName("tags")
                    .HasColumnType("TEXT").HasConversion(tagsConverter);
            }
            else
            {
                entity.Property(e => e.Tags).HasColumnName("tags");
            }

            entity.HasIndex(e => e.ExternalId).IsUnique();
            entity.HasIndex(e => e.Project);
            entity.HasIndex(e => e.Repo);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.Source);
            entity.HasIndex(e => e.MachineName);
        });

        modelBuilder.Entity<Message>(entity =>
        {
            entity.ToTable("messages");
            entity.HasKey(e => e.MessageId);
            entity.Property(e => e.MessageId).HasColumnName("message_id");
            entity.Property(e => e.SessionId).HasColumnName("session_id");
            entity.Property(e => e.ExternalId).HasColumnName("external_id");
            entity.Property(e => e.Role).HasColumnName("role").IsRequired();
            entity.Property(e => e.Content).HasColumnName("content").IsRequired();
            entity.Property(e => e.Provider).HasColumnName("provider");
            entity.Property(e => e.Model).HasColumnName("model");
            entity.Property(e => e.RequestId).HasColumnName("request_id");
            entity.Property(e => e.TokenIn).HasColumnName("token_in");
            entity.Property(e => e.TokenOut).HasColumnName("token_out");
            entity.Property(e => e.CostUsd).HasColumnName("cost_usd").HasPrecision(10, 6);
            entity.Property(e => e.LatencyMs).HasColumnName("latency_ms");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");

            entity.Ignore(e => e.Session);

            entity.HasIndex(e => e.SessionId);
            entity.HasIndex(e => e.Provider);
            entity.HasIndex(e => e.Model);
            entity.HasIndex(e => e.CreatedAt);
        });

        modelBuilder.Entity<ToolCall>(entity =>
        {
            entity.ToTable("tool_calls");
            entity.HasKey(e => e.ToolCallId);
            entity.Property(e => e.ToolCallId).HasColumnName("tool_call_id");
            entity.Property(e => e.SessionId).HasColumnName("session_id");
            entity.Property(e => e.ExternalId).HasColumnName("external_id");
            entity.Property(e => e.ToolName).HasColumnName("tool_name").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");

            if (IsSqlite)
            {
                entity.Property(e => e.ArgumentsJson).HasColumnName("arguments_json")
                    .HasColumnType("TEXT").HasConversion(jsonDocConverter);
                entity.Property(e => e.ResultJson).HasColumnName("result_json")
                    .HasColumnType("TEXT").HasConversion(jsonDocConverter);
            }
            else
            {
                entity.Property(e => e.ArgumentsJson).HasColumnName("arguments_json").HasColumnType("jsonb");
                entity.Property(e => e.ResultJson).HasColumnName("result_json").HasColumnType("jsonb");
            }

            entity.Ignore(e => e.Session);

            entity.HasIndex(e => e.SessionId);
            entity.HasIndex(e => e.ToolName);
        });

        modelBuilder.Entity<Artifact>(entity =>
        {
            entity.ToTable("artifacts");
            entity.HasKey(e => e.ArtifactId);
            entity.Property(e => e.ArtifactId).HasColumnName("artifact_id");
            entity.Property(e => e.SessionId).HasColumnName("session_id");
            entity.Property(e => e.ExternalId).HasColumnName("external_id");
            entity.Property(e => e.Type).HasColumnName("type").IsRequired();
            entity.Property(e => e.PathOrUrl).HasColumnName("path_or_url");
            entity.Property(e => e.Hash).HasColumnName("hash");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");

            if (IsSqlite)
            {
                entity.Property(e => e.MetadataJson).HasColumnName("metadata_json")
                    .HasColumnType("TEXT").HasConversion(jsonDocConverter);
            }
            else
            {
                entity.Property(e => e.MetadataJson).HasColumnName("metadata_json").HasColumnType("jsonb");
            }

            entity.Ignore(e => e.Session);

            entity.HasIndex(e => e.SessionId);
            entity.HasIndex(e => e.Type);
        });

        modelBuilder.Entity<IngestionLogEntry>(entity =>
        {
            entity.ToTable("ingestion_log");
            entity.HasKey(e => e.IdempotencyKey);
            entity.Property(e => e.IdempotencyKey).HasColumnName("idempotency_key");
            entity.Property(e => e.EventType).HasColumnName("event_type").IsRequired();
            entity.Property(e => e.Source).HasColumnName("source").IsRequired();
            entity.Property(e => e.SourcePath).HasColumnName("source_path").IsRequired();
            entity.Property(e => e.RecordOffset).HasColumnName("record_offset");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.MachineName).HasColumnName("machine_name");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");

            entity.HasIndex(e => e.Source);
            entity.HasIndex(e => e.SourcePath);
        });

        modelBuilder.Entity<AdminUser>(entity =>
        {
            entity.ToTable("admin_users");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Username).HasColumnName("username").IsRequired();
            entity.Property(e => e.PasswordHash).HasColumnName("password_hash").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");

            entity.HasIndex(e => e.Username).IsUnique();
        });

        modelBuilder.Entity<MessageEmbedding>(entity =>
        {
            entity.ToTable("message_embeddings");
            entity.HasKey(e => e.MessageId);
            entity.Property(e => e.MessageId).HasColumnName("message_id");
            entity.Property(e => e.SessionId).HasColumnName("session_id");
            entity.Property(e => e.Embedding).HasColumnName("embedding").IsRequired();
            entity.Property(e => e.Dimensions).HasColumnName("dimensions");
            entity.Property(e => e.Model).HasColumnName("model").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");

            entity.HasIndex(e => e.SessionId);
        });

        modelBuilder.Entity<CodeRepository>(entity =>
        {
            entity.ToTable("code_repositories");
            entity.HasKey(e => e.RepositoryId);
            entity.Property(e => e.RepositoryId).HasColumnName("repository_id");
            entity.Property(e => e.Name).HasColumnName("name").IsRequired();
            entity.Property(e => e.SourceType).HasColumnName("source_type").IsRequired();
            entity.Property(e => e.SourcePath).HasColumnName("source_path").IsRequired();
            entity.Property(e => e.DefaultBranch).HasColumnName("default_branch");
            entity.Property(e => e.FileCount).HasColumnName("file_count");
            entity.Property(e => e.SymbolCount).HasColumnName("symbol_count");
            entity.Property(e => e.IndexedAt).HasColumnName("indexed_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");

            entity.HasIndex(e => e.Name).IsUnique();
            entity.HasIndex(e => e.SourceType);
        });

        modelBuilder.Entity<CodeFile>(entity =>
        {
            entity.ToTable("code_files");
            entity.HasKey(e => e.FileId);
            entity.Property(e => e.FileId).HasColumnName("file_id");
            entity.Property(e => e.RepositoryId).HasColumnName("repository_id");
            entity.Property(e => e.FilePath).HasColumnName("file_path").IsRequired();
            entity.Property(e => e.Language).HasColumnName("language").IsRequired();
            entity.Property(e => e.FileSize).HasColumnName("file_size");
            entity.Property(e => e.ContentHash).HasColumnName("content_hash").IsRequired();
            entity.Property(e => e.IndexedAt).HasColumnName("indexed_at");

            entity.Ignore(e => e.Repository);

            entity.HasIndex(e => e.RepositoryId);
            entity.HasIndex(e => new { e.RepositoryId, e.FilePath }).IsUnique();
            entity.HasIndex(e => e.Language);
        });

        modelBuilder.Entity<CodeSymbol>(entity =>
        {
            entity.ToTable("code_symbols");
            entity.HasKey(e => e.SymbolId);
            entity.Property(e => e.SymbolId).HasColumnName("symbol_id");
            entity.Property(e => e.FileId).HasColumnName("file_id");
            entity.Property(e => e.RepositoryId).HasColumnName("repository_id");
            entity.Property(e => e.SymbolKey).HasColumnName("symbol_key").IsRequired();
            entity.Property(e => e.Name).HasColumnName("name").IsRequired();
            entity.Property(e => e.QualifiedName).HasColumnName("qualified_name").IsRequired();
            entity.Property(e => e.Kind).HasColumnName("kind").IsRequired();
            entity.Property(e => e.Signature).HasColumnName("signature");
            entity.Property(e => e.Summary).HasColumnName("summary");
            entity.Property(e => e.StartLine).HasColumnName("start_line");
            entity.Property(e => e.EndLine).HasColumnName("end_line");
            entity.Property(e => e.StartByte).HasColumnName("start_byte");
            entity.Property(e => e.EndByte).HasColumnName("end_byte");
            entity.Property(e => e.ParentSymbolKey).HasColumnName("parent_symbol_key");
            entity.Property(e => e.IndexedAt).HasColumnName("indexed_at");

            entity.Ignore(e => e.File);

            entity.HasIndex(e => e.FileId);
            entity.HasIndex(e => e.RepositoryId);
            entity.HasIndex(e => e.SymbolKey);
            entity.HasIndex(e => e.Kind);
            entity.HasIndex(e => e.Name);
            entity.HasIndex(e => e.QualifiedName);
        });

        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.ToTable("api_keys");
            entity.HasKey(e => e.ApiKeyId);
            entity.Property(e => e.ApiKeyId).HasColumnName("api_key_id");
            entity.Property(e => e.Name).HasColumnName("name").IsRequired();
            entity.Property(e => e.KeyHash).HasColumnName("key_hash").IsRequired();
            entity.Property(e => e.KeyPrefix).HasColumnName("key_prefix").IsRequired();
            entity.Property(e => e.IsActive).HasColumnName("is_active");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.LastUsedAt).HasColumnName("last_used_at");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");

            if (IsSqlite)
            {
                entity.Property(e => e.Scopes).HasColumnName("scopes")
                    .HasColumnType("TEXT").HasConversion(tagsConverter);
            }
            else
            {
                entity.Property(e => e.Scopes).HasColumnName("scopes");
            }

            entity.HasIndex(e => e.KeyHash).IsUnique();
            entity.HasIndex(e => e.IsActive);
        });
    }
}
