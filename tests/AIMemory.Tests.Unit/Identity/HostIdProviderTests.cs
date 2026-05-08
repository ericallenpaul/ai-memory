using System.Security.Cryptography;
using System.Text;
using AIMemory.Identity;

namespace AIMemory.Tests.Unit.Identity;

public class HostIdProviderTests : IDisposable
{
    private readonly string _saltDir;

    public HostIdProviderTests()
    {
        _saltDir = Path.Combine(Path.GetTempPath(), $"aimemory-host-test-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_saltDir))
            Directory.Delete(_saltDir, recursive: true);
    }

    private InstallSaltStore Salt() => new(_saltDir);

    private sealed class FixedMachineGuidReader : IMachineGuidReader
    {
        public byte[] Bytes { get; }
        public FixedMachineGuidReader(byte[] bytes) { Bytes = bytes; }
        public byte[] ReadBytes() => Bytes;
    }

    [Fact]
    public void GetHostId_Returns64CharLowercaseHex()
    {
        var provider = new HostIdProvider(Salt(),
            new FixedMachineGuidReader(Encoding.UTF8.GetBytes("test-machine-guid")));

        var hostId = provider.GetHostId();

        Assert.Equal(64, hostId.Length);
        Assert.All(hostId, c => Assert.True((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
            $"Non-hex char in host_id: '{c}'"));
    }

    [Fact]
    public void GetHostId_IsDeterministic_GivenSameInputs()
    {
        var p1 = new HostIdProvider(Salt(),
            new FixedMachineGuidReader(Encoding.UTF8.GetBytes("guid")));
        var p2 = new HostIdProvider(Salt(),
            new FixedMachineGuidReader(Encoding.UTF8.GetBytes("guid")));

        Assert.Equal(p1.GetHostId(), p2.GetHostId());
    }

    [Fact]
    public void GetHostId_DiffersWhenMachineGuidDiffers()
    {
        var p1 = new HostIdProvider(Salt(),
            new FixedMachineGuidReader(Encoding.UTF8.GetBytes("guid-a")));
        var p2 = new HostIdProvider(Salt(),
            new FixedMachineGuidReader(Encoding.UTF8.GetBytes("guid-b")));

        Assert.NotEqual(p1.GetHostId(), p2.GetHostId());
    }

    [Fact]
    public void GetHostId_DiffersWhenSaltDiffers()
    {
        var saltA = Path.Combine(Path.GetTempPath(), $"salt-a-{Guid.NewGuid():N}");
        var saltB = Path.Combine(Path.GetTempPath(), $"salt-b-{Guid.NewGuid():N}");
        try
        {
            var fixedReader = new FixedMachineGuidReader(Encoding.UTF8.GetBytes("same-guid"));
            var p1 = new HostIdProvider(new InstallSaltStore(saltA), fixedReader);
            var p2 = new HostIdProvider(new InstallSaltStore(saltB), fixedReader);

            Assert.NotEqual(p1.GetHostId(), p2.GetHostId());
        }
        finally
        {
            if (Directory.Exists(saltA)) Directory.Delete(saltA, true);
            if (Directory.Exists(saltB)) Directory.Delete(saltB, true);
        }
    }

    [Fact]
    public void GetHostId_MatchesExplicitSha256Computation()
    {
        var guidBytes = Encoding.UTF8.GetBytes("explicit-machine-guid");
        var saltStore = Salt();
        var saltBytes = saltStore.GetOrCreate(); // realize salt before HostIdProvider sees it

        var provider = new HostIdProvider(saltStore, new FixedMachineGuidReader(guidBytes));
        var hostId = provider.GetHostId();

        // Reproduce the formula by hand: sha256_hex(machine_guid_bytes || install_salt).
        var combined = new byte[guidBytes.Length + saltBytes.Length];
        Buffer.BlockCopy(guidBytes, 0, combined, 0, guidBytes.Length);
        Buffer.BlockCopy(saltBytes, 0, combined, guidBytes.Length, saltBytes.Length);
        var expected = Convert.ToHexStringLower(SHA256.HashData(combined));

        Assert.Equal(expected, hostId);
    }

    [Fact]
    public void GetOsKind_ReturnsKnownTag()
    {
        var provider = new HostIdProvider(Salt(),
            new FixedMachineGuidReader(Encoding.UTF8.GetBytes("guid")));
        var kind = provider.GetOsKind();
        Assert.Contains(kind, new[] { "windows", "linux", "macos", "unknown" });
    }
}
