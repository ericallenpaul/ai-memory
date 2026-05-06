using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using AIMemory.Ingestor.Checkpointing;
using AIMemory.Ingestor.Configuration;
using AIMemory.Ingestor.ConfigApp.Services;

namespace AIMemory.Ingestor.ConfigApp.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private string _apiUrl = string.Empty;
    [ObservableProperty] private string _apiKey = string.Empty;
    [ObservableProperty] private string _clientId = string.Empty;
    [ObservableProperty] private int _scanIntervalSeconds = 5;
    [ObservableProperty] private int _batchSize = 100;

    // Redaction
    [ObservableProperty] private string _redactionMode = "basic";
    [ObservableProperty] private string _exclusions = string.Empty;

    // Service
    [ObservableProperty] private string _serviceStatus = "Unknown";
    [ObservableProperty] private string _statusMessage = string.Empty;

    public ObservableCollection<SourceConfigViewModel> Sources { get; } = [];
    public ObservableCollection<CheckpointEntry> Checkpoints { get; } = [];

    public static string[] RedactionModes => ["off", "basic", "aggressive"];

    public MainViewModel()
    {
        LoadConfig();
    }

    private void LoadConfig()
    {
        var config = ConfigFileService.Load();

        ApiUrl = config.AIMemoryApiBaseUrl;
        ApiKey = config.ApiKey ?? string.Empty;
        ClientId = config.ClientId;
        ScanIntervalSeconds = config.ScanIntervalSeconds;
        BatchSize = config.BatchSize;
        RedactionMode = config.Redaction.Mode;
        Exclusions = string.Join(Environment.NewLine, config.Redaction.Exclusions);

        Sources.Clear();
        foreach (var source in config.Sources)
            Sources.Add(SourceConfigViewModel.FromConfig(source));

        RefreshServiceStatus();
        RefreshCheckpoints();
    }

    [RelayCommand]
    private void Save()
    {
        var config = new IngestorConfig
        {
            AIMemoryApiBaseUrl = ApiUrl,
            ApiKey = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey,
            ClientId = ClientId,
            ScanIntervalSeconds = ScanIntervalSeconds,
            BatchSize = BatchSize,
            Sources = Sources.Select(s => s.ToConfig()).ToList(),
            Redaction = new RedactionConfig
            {
                Mode = RedactionMode,
                Exclusions = Exclusions.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(e => e.Trim()).Where(e => e.Length > 0).ToList()
            },
            Outbox = new OutboxConfig { MaxSizeMb = 100 }
        };

        ConfigFileService.Save(config);
        StatusMessage = "Configuration saved.";
    }

    [RelayCommand]
    private void AddSource()
    {
        Sources.Add(new SourceConfigViewModel
        {
            Name = "new-source",
            Enabled = true,
            Segment = "personal"
        });
    }

    [RelayCommand]
    private void RemoveSource(SourceConfigViewModel source)
    {
        Sources.Remove(source);
    }

    [RelayCommand]
    private void RefreshServiceStatus()
    {
        ServiceStatus = WindowsServiceManager.GetStatus();
    }

    [RelayCommand]
    private void StartService()
    {
        try
        {
            WindowsServiceManager.Start();
            StatusMessage = "Service started.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to start service: {ex.Message}";
        }
        RefreshServiceStatus();
    }

    [RelayCommand]
    private void StopService()
    {
        try
        {
            WindowsServiceManager.Stop();
            StatusMessage = "Service stopped.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to stop service: {ex.Message}";
        }
        RefreshServiceStatus();
    }

    [RelayCommand]
    private void RestartService()
    {
        try
        {
            WindowsServiceManager.Restart();
            StatusMessage = "Service restarted.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to restart service: {ex.Message}";
        }
        RefreshServiceStatus();
    }

    private void RefreshCheckpoints()
    {
        Checkpoints.Clear();

        var checkpointPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AIMemory", "Ingestor", "checkpoints.json");

        if (!File.Exists(checkpointPath)) return;

        try
        {
            using var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => { });
            var store = new JsonCheckpointStore(checkpointPath,
                loggerFactory.CreateLogger<JsonCheckpointStore>());

            foreach (var (path, cp) in store.GetAll())
            {
                Checkpoints.Add(new CheckpointEntry
                {
                    FilePath = path,
                    Offset = cp.LastProcessedOffset,
                    FileSize = cp.FileSize,
                    LastSeen = cp.LastSeenAt
                });
            }
        }
        catch
        {
            // Best effort — checkpoint file may be locked by the service
        }
    }
}

public class CheckpointEntry
{
    public string FilePath { get; set; } = string.Empty;
    public long Offset { get; set; }
    public long FileSize { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}
