using System.Security.Cryptography;

namespace AIMemory.Ingestor.Hashing;

/// <summary>
/// Default <see cref="IFileHasher"/>. Uses <see cref="IncrementalHash"/> with a 64 KiB read
/// buffer so peak memory stays bounded regardless of file size.
/// </summary>
public sealed class StreamingFileHasher : IFileHasher
{
    /// <summary>64 KiB read buffer — design doc §2.3 recommends this size.</summary>
    public const int BufferSize = 64 * 1024;

    public string HashFile(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            throw new ArgumentException("absolutePath must be non-empty", nameof(absolutePath));

        // FileShare.Read so a concurrent reader (e.g. the parser, when it runs in the same
        // cycle) doesn't block; FileOptions.SequentialScan hints to the OS that we're going
        // to read the file in order with no random access.
        using var stream = new FileStream(
            absolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan);
        return HashStream(stream);
    }

    public string HashStream(Stream stream)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            hasher.AppendData(buffer, 0, read);

        var digest = hasher.GetHashAndReset();
        return Convert.ToHexStringLower(digest);
    }

    public string HashBytes(ReadOnlySpan<byte> bytes)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(bytes, digest);
        return Convert.ToHexStringLower(digest);
    }
}
