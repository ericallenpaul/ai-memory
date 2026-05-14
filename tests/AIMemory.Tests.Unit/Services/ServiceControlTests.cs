using AIMemory.Api.Services;
using Xunit;

namespace AIMemory.Tests.Unit.Services;

public class ServiceControlTests
{
    [Theory]
    [InlineData("aimemory-api", true)]
    [InlineData("aimemory-ingestor", true)]
    [InlineData("AIMEMORY-API", true)] // case-insensitive
    [InlineData("aimemory-mcp", false)]
    [InlineData("spooler", false)]
    [InlineData("", false)]
    public void AllowedNames_OnlyAccepts_AimemoryServices(string name, bool allowed)
    {
        Assert.Equal(allowed, ServiceControl.AllowedNames.Contains(name));
    }

    [Fact]
    public void ParseScQueryState_Running()
    {
        const string output = @"
SERVICE_NAME: aimemory-api
        TYPE               : 10  WIN32_OWN_PROCESS
        STATE              : 4  RUNNING
                                (STOPPABLE, NOT_PAUSABLE, ACCEPTS_SHUTDOWN)
        WIN32_EXIT_CODE    : 0  (0x0)
        SERVICE_EXIT_CODE  : 0  (0x0)
        CHECKPOINT         : 0x0
        WAIT_HINT          : 0x0
";
        Assert.Equal(ServiceState.Running, ServiceControl.ParseScQueryState(output));
    }

    [Fact]
    public void ParseScQueryState_Stopped()
    {
        const string output = @"
SERVICE_NAME: aimemory-api
        STATE              : 1  STOPPED
";
        Assert.Equal(ServiceState.Stopped, ServiceControl.ParseScQueryState(output));
    }

    [Fact]
    public void ParseScQueryState_StartPending()
    {
        const string output = "        STATE              : 2  START_PENDING\n";
        Assert.Equal(ServiceState.StartPending, ServiceControl.ParseScQueryState(output));
    }

    [Fact]
    public void ParseScQueryState_StopPending()
    {
        const string output = "        STATE              : 3  STOP_PENDING\n";
        Assert.Equal(ServiceState.StopPending, ServiceControl.ParseScQueryState(output));
    }

    [Fact]
    public void ParseScQueryState_MissingState_ReturnsUnknown()
    {
        const string output = "SERVICE_NAME: aimemory-api\n        TYPE               : 10  WIN32_OWN_PROCESS\n";
        Assert.Equal(ServiceState.Unknown, ServiceControl.ParseScQueryState(output));
    }

    [Fact]
    public void ParseScQueryPid_Running_ReturnsPid()
    {
        const string output = @"
SERVICE_NAME: aimemory-api
        STATE              : 4  RUNNING
        PID                : 12345
        FLAGS              :
";
        Assert.Equal(12345, ServiceControl.ParseScQueryPid(output));
    }

    [Fact]
    public void ParseScQueryPid_Stopped_ReturnsNull()
    {
        // When a service is stopped, sc.exe doesn't emit a PID line.
        const string output = "SERVICE_NAME: aimemory-api\n        STATE              : 1  STOPPED\n";
        Assert.Null(ServiceControl.ParseScQueryPid(output));
    }

    [Fact]
    public void ParseScQueryPid_ZeroPid_TreatedAsNull()
    {
        // sc.exe emits "PID : 0" for services that are stopped but still have a stub entry.
        const string output = "        STATE              : 1  STOPPED\n        PID                : 0\n";
        Assert.Null(ServiceControl.ParseScQueryPid(output));
    }
}
