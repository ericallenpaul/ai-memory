using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AIMemory.Data;

/// <summary>
/// Design-time factory for EF Core migrations.
/// Uses SQLite by default so generated migrations are portable.
/// Set AIMEMORY_MIGRATION_PROVIDER=PostgreSQL to generate PostgreSQL-specific migrations.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AIMemoryDbContext>
{
    public AIMemoryDbContext CreateDbContext(string[] args)
    {
        var provider = Environment.GetEnvironmentVariable("AIMEMORY_MIGRATION_PROVIDER") ?? "SQLite";
        var optionsBuilder = new DbContextOptionsBuilder<AIMemoryDbContext>();

        if (string.Equals(provider, "PostgreSQL", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = Environment.GetEnvironmentVariable("AIMEMORY_CONNECTION_STRING")
                ?? "Host=localhost;Database=aimemory;Username=aimemory_app;Password=OB_app_2026!secure";
            optionsBuilder.UseNpgsql(connectionString);
        }
        else
        {
            optionsBuilder.UseSqlite("Data Source=:memory:");
        }

        return new AIMemoryDbContext(optionsBuilder.Options);
    }
}
