using CommunityToolkit.Mvvm.ComponentModel;
using AIMemory.Ingestor.Configuration;

namespace AIMemory.Ingestor.ConfigApp.ViewModels;

public partial class SourceConfigViewModel : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _adapterType = string.Empty;
    [ObservableProperty] private string _watchPaths = string.Empty;
    [ObservableProperty] private string _filePatterns = string.Empty;
    [ObservableProperty] private string _segment = "personal";

    public static string[] SegmentOptions => ["personal", "work"];

    public static SourceConfigViewModel FromConfig(SourceConfig config) => new()
    {
        Name = config.Name,
        Enabled = config.Enabled,
        AdapterType = config.AdapterType,
        WatchPaths = string.Join(Environment.NewLine, config.WatchPaths),
        FilePatterns = string.Join(", ", config.FilePatterns),
        Segment = config.Segment
    };

    public SourceConfig ToConfig() => new()
    {
        Name = Name,
        Enabled = Enabled,
        AdapterType = AdapterType,
        WatchPaths = WatchPaths.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList(),
        FilePatterns = FilePatterns.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList(),
        Segment = Segment
    };
}
