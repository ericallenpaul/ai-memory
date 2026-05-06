using System.Windows;
using System.Windows.Controls;
using AIMemory.Ingestor.ConfigApp.ViewModels;

namespace AIMemory.Ingestor.ConfigApp.Views;

public partial class SetupWizardPage : Window
{
    public SetupWizardPage()
    {
        InitializeComponent();
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SetupWizardViewModel vm)
            vm.ApiKey = ((PasswordBox)sender).Password;
    }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SetupWizardViewModel vm)
        {
            vm.SaveConfig();
            DialogResult = true;
            Close();
        }
    }
}
