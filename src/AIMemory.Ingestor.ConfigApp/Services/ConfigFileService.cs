using System.IO;
using System.Text.Json;
using AIMemory.Ingestor.Configuration;

namespace AIMemory.Ingestor.ConfigApp.Services;

public static class ConfigFileService
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "AIMemory", "Ingestor");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "appsettings.json");

    public static bool ConfigFileExists() => File.Exists(ConfigPath);

    public static string GetConfigPath() => ConfigPath;

    public static IngestorConfig Load()
    {
        if (!File.Exists(ConfigPath))
            return new IngestorConfig();

        try
        {
            var json = File.ReadAllText(ConfigPath);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("Ingestor", out var section))
            {
                return JsonSerializer.Deserialize<IngestorConfig>(section.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new IngestorConfig();
            }

            return new IngestorConfig();
        }
        catch
        {
            return new IngestorConfig();
        }
    }

    public static void Save(IngestorConfig config)
    {
        Directory.CreateDirectory(ConfigDir);

        var wrapper = new Dictionary<string, object>
        {
            ["Logging"] = new Dictionary<string, object>
            {
                ["LogLevel"] = new Dictionary<string, string>
                {
                    ["Default"] = "Information",
                    ["Microsoft.Hosting.Lifetime"] = "Information"
                }
            },
            ["Ingestor"] = config
        };

        var json = JsonSerializer.Serialize(wrapper, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        File.WriteAllText(ConfigPath, json);
    }
}
