using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AIMemory.Ingestor.Configuration;
using AIMemory.Ingestor.ConfigApp.Services;

namespace AIMemory.Ingestor.ConfigApp.ViewModels;

public partial class SetupWizardViewModel : ObservableObject
{
    [ObservableProperty] private int _currentStep;
    [ObservableProperty] private string _apiUrl = DiscoverApiUrl();
    [ObservableProperty] private string _apiKey = string.Empty;
    [ObservableProperty] private string _clientId = Environment.MachineName.ToLowerInvariant();
    [ObservableProperty] private string _connectionTestResult = string.Empty;
    [ObservableProperty] private bool _isTestingConnection;

    public ObservableCollection<SourceConfigViewModel> DetectedSources { get; } = [];

    public bool CanGoBack => CurrentStep > 0;
    public bool CanGoForward => CurrentStep < 2;
    public bool IsLastStep => CurrentStep == 2;

    [RelayCommand]
    private void NextStep()
    {
        if (CurrentStep == 0)
            DetectSources();

        if (CurrentStep < 2)
        {
            CurrentStep++;
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
            OnPropertyChanged(nameof(IsLastStep));
        }
    }

    [RelayCommand]
    private void PreviousStep()
    {
        if (CurrentStep > 0)
        {
            CurrentStep--;
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
            OnPropertyChanged(nameof(IsLastStep));
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsTestingConnection = true;
        ConnectionTestResult = "Testing...";

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            if (!string.IsNullOrEmpty(ApiKey))
                client.DefaultRequestHeaders.Add("X-API-Key", ApiKey);

            var response = await client.GetAsync($"{ApiUrl.TrimEnd('/')}/health");
            ConnectionTestResult = response.IsSuccessStatusCode
                ? "Connected successfully"
                : $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
        }
        catch (Exception ex)
        {
            ConnectionTestResult = $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    private void DetectSources()
    {
        if (DetectedSources.Count > 0) return;

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var claudeProjectsPath = Path.Combine(userHome, ".claude", "projects");
        if (Directory.Exists(claudeProjectsPath))
        {
            DetectedSources.Add(new SourceConfigViewModel
            {
                Name = "claude-code",
                Enabled = true,
                AdapterType = "ClaudeCode",
                WatchPaths = claudeProjectsPath,
                FilePatterns = "*.jsonl",
                Segment = "personal"
            });
        }

        var codexPath = Path.Combine(userHome, ".codex");
        if (Directory.Exists(codexPath))
        {
            DetectedSources.Add(new SourceConfigViewModel
            {
                Name = "codex-cli",
                Enabled = true,
                AdapterType = "CodexCli",
                WatchPaths = codexPath,
                FilePatterns = "history.jsonl",
                Segment = "personal"
            });
        }
    }

    public void SaveConfig()
    {
        var config = new IngestorConfig
        {
            AIMemoryApiBaseUrl = ApiUrl,
            ApiKey = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey,
            ClientId = ClientId,
            ScanIntervalSeconds = 5,
            BatchSize = 100,
            Sources = DetectedSources.Select(s => s.ToConfig()).ToList(),
            Redaction = new RedactionConfig { Mode = "basic", Exclusions = [] },
            Outbox = new OutboxConfig { MaxSizeMb = 100 }
        };

        ConfigFileService.Save(config);
    }

    private static string DiscoverApiUrl()
    {
        try
        {
            var portFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "AIMemory", "Api", "port");
            if (File.Exists(portFilePath)
                && int.TryParse(File.ReadAllText(portFilePath).Trim(), out var port)
                && port > 0)
            {
                return $"http://localhost:{port}";
            }
        }
        catch
        {
            // Best effort — port file may not exist yet
        }

        return "";
    }
}
