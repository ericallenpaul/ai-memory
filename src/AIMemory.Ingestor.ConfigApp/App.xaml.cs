using System.Windows;
using AIMemory.Ingestor.ConfigApp.Services;

namespace AIMemory.Ingestor.ConfigApp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!ConfigFileService.ConfigFileExists())
        {
            var wizard = new Views.SetupWizardPage();
            wizard.ShowDialog();

            if (wizard.DialogResult != true)
            {
                Shutdown();
                return;
            }
        }
    }
}
