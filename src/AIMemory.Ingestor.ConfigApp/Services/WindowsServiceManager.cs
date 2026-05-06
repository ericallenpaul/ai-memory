using System.ServiceProcess;

namespace AIMemory.Ingestor.ConfigApp.Services;

public static class WindowsServiceManager
{
    private const string ServiceName = "AIMemoryIngestor";

    public static string GetStatus()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status.ToString();
        }
        catch (InvalidOperationException)
        {
            return "Not Installed";
        }
        catch
        {
            return "Unknown";
        }
    }

    public static bool IsInstalled()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            _ = sc.Status;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Start()
    {
        using var sc = new ServiceController(ServiceName);
        if (sc.Status == ServiceControllerStatus.Stopped)
        {
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        }
    }

    public static void Stop()
    {
        using var sc = new ServiceController(ServiceName);
        if (sc.Status == ServiceControllerStatus.Running)
        {
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }
    }

    public static void Restart()
    {
        Stop();
        Start();
    }
}
