using System.Windows;
using System.Windows.Controls;
using AIMemory.Ingestor.ConfigApp.ViewModels;
using AIMemory.Ingestor.ConfigApp.Views;

namespace AIMemory.Ingestor.ConfigApp;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    private readonly GeneralSettingsPage _generalPage;
    private readonly SourcesPage _sourcesPage;
    private readonly RedactionPage _redactionPage;
    private readonly ServiceStatusPage _servicePage;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        _generalPage = new GeneralSettingsPage { DataContext = _viewModel };
        _sourcesPage = new SourcesPage { DataContext = _viewModel };
        _redactionPage = new RedactionPage { DataContext = _viewModel };
        _servicePage = new ServiceStatusPage { DataContext = _viewModel };

        ContentArea.Content = _generalPage;
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedIndex < 0) return;

        ContentArea.Content = NavList.SelectedIndex switch
        {
            0 => _generalPage,
            1 => _sourcesPage,
            2 => _redactionPage,
            3 => _servicePage,
            _ => _generalPage
        };
    }
}
