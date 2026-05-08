using AIMemory.Identity;

namespace AIMemory.Tests.Unit.Identity;

public class InstallSaltStoreTests : IDisposable
{
    private readonly string _dir;

    public InstallSaltStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"aimemory-salt-test-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void GetOrCreate_Generates32ByteSalt_OnFirstCall()
    {
        var store = new InstallSaltStore(_dir);
        var salt = store.GetOrCreate();

        Assert.Equal(InstallSaltStore.SaltLengthBytes, salt.Length);
        Assert.True(File.Exists(Path.Combine(_dir, InstallSaltStore.DefaultFileName)));
    }

    [Fact]
    public void GetOrCreate_ReturnsSameSalt_OnRepeatedCalls()
    {
        var store = new InstallSaltStore(_dir);
        var first = store.GetOrCreate();
        var second = store.GetOrCreate();

        Assert.Equal(first, second);
    }

    [Fact]
    public void GetOrCreate_ReadsExistingSalt_FromDisk()
    {
        var first = new InstallSaltStore(_dir).GetOrCreate();
        var second = new InstallSaltStore(_dir).GetOrCreate();

        Assert.Equal(first, second);
    }

    [Fact]
    public void GetOrCreate_ThrowsOnWrongLength()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, InstallSaltStore.DefaultFileName), new byte[16]); // wrong length

        var store = new InstallSaltStore(_dir);
        Assert.Throws<InvalidDataException>(() => store.GetOrCreate());
    }

    [Fact]
    public void GetOrCreate_ProducesRandomSalts_AcrossInstalls()
    {
        var dirA = Path.Combine(Path.GetTempPath(), $"aimemory-salt-a-{Guid.NewGuid():N}");
        var dirB = Path.Combine(Path.GetTempPath(), $"aimemory-salt-b-{Guid.NewGuid():N}");
        try
        {
            var saltA = new InstallSaltStore(dirA).GetOrCreate();
            var saltB = new InstallSaltStore(dirB).GetOrCreate();
            Assert.NotEqual(saltA, saltB);
        }
        finally
        {
            if (Directory.Exists(dirA)) Directory.Delete(dirA, true);
            if (Directory.Exists(dirB)) Directory.Delete(dirB, true);
        }
    }
}
