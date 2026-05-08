using System.Security.Cryptography;
using AIMemory.Ingestor.Hashing;

namespace AIMemory.Tests.Unit.Hashing;

/// <summary>
/// Verifies that the streaming hasher produces the same digest as a one-shot
/// <see cref="SHA256.HashData(byte[])"/> for inputs across the size spectrum the brief
/// calls out (small / mid / large). Coverage:
///
/// <list type="bullet">
///   <item>Empty input — boundary case where the buffer never gets a byte.</item>
///   <item>Sub-buffer (small) — single read.</item>
///   <item>Just over the buffer (~64 KB + 1) — exercise the buffer-boundary read.</item>
///   <item>4 KB and 16 MB sizes called out by the brief.</item>
/// </list>
/// </summary>
public class StreamingFileHasherTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StreamingFileHasher _hasher = new();

    public StreamingFileHasherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"aimemory-hasher-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void HashBytes_MatchesOneShotSha256_ForEmptyInput()
    {
        var observed = _hasher.HashBytes(ReadOnlySpan<byte>.Empty);
        var expected = Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>()));
        Assert.Equal(expected, observed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(1024)]
    [InlineData(4096)]                 // ~4 KB — brief callout
    [InlineData(StreamingFileHasher.BufferSize - 1)]
    [InlineData(StreamingFileHasher.BufferSize)]
    [InlineData(StreamingFileHasher.BufferSize + 1)]
    [InlineData(StreamingFileHasher.BufferSize * 3 + 17)]
    public void HashBytes_MatchesOneShotSha256_AcrossSizes(int size)
    {
        var data = DeterministicBytes(size, seed: 0x5EED);
        var observed = _hasher.HashBytes(data);
        var expected = Convert.ToHexStringLower(SHA256.HashData(data));
        Assert.Equal(expected, observed);
    }

    [Fact]
    public void HashStream_MatchesHashBytes()
    {
        var data = DeterministicBytes(123_456, seed: 0xABCD);
        using var ms = new MemoryStream(data);

        var streamed = _hasher.HashStream(ms);
        var oneShot = _hasher.HashBytes(data);

        Assert.Equal(oneShot, streamed);
    }

    [Fact]
    public void HashFile_MatchesHashBytes()
    {
        var data = DeterministicBytes(StreamingFileHasher.BufferSize * 2 + 999, seed: 0x1234);
        var path = Path.Combine(_tempDir, "fixture.bin");
        File.WriteAllBytes(path, data);

        var fileHash = _hasher.HashFile(path);
        var byteHash = _hasher.HashBytes(data);

        Assert.Equal(byteHash, fileHash);
    }

    [Fact]
    public void HashFile_HandlesLargeFile_16MB_WithoutLoadingFully()
    {
        // 16 MB is the upper bound called out in the brief. We write it once, hash it, and
        // verify against System.Security.Cryptography.SHA256.HashData over the same bytes.
        const int size = 16 * 1024 * 1024;
        var data = DeterministicBytes(size, seed: 0xC001);
        var path = Path.Combine(_tempDir, "large.bin");
        File.WriteAllBytes(path, data);

        var observed = _hasher.HashFile(path);
        var expected = Convert.ToHexStringLower(SHA256.HashData(data));

        Assert.Equal(expected, observed);
    }

    [Fact]
    public void HashFile_ProducesLowercase64CharHex()
    {
        var path = Path.Combine(_tempDir, "shape.bin");
        File.WriteAllBytes(path, "hello"u8.ToArray());

        var hash = _hasher.HashFile(path);

        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
            $"non-hex char: '{c}'"));
    }

    [Fact]
    public void HashFile_Throws_WhenPathMissing()
    {
        Assert.Throws<FileNotFoundException>(() =>
            _hasher.HashFile(Path.Combine(_tempDir, "does-not-exist")));
    }

    [Fact]
    public void HashFile_ArgumentValidation()
    {
        Assert.Throws<ArgumentException>(() => _hasher.HashFile(""));
        Assert.Throws<ArgumentException>(() => _hasher.HashFile("   "));
    }

    private static byte[] DeterministicBytes(int size, int seed)
    {
        // Random with a fixed seed gives reproducible test inputs without touching the
        // process-global state of System.Security.Cryptography.
        var rng = new Random(seed);
        var buf = new byte[size];
        rng.NextBytes(buf);
        return buf;
    }
}
