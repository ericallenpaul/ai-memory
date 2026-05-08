namespace AIMemory.Ingestor.Hashing;

/// <summary>
/// Computes <c>content_sha256</c> for a file. Implementations stream the file in fixed-size
/// chunks rather than loading the entire content into memory — every file in a watch path is
/// a hot-path call, and the user's repos may include ≥100 MB binary blobs.
///
/// <para>Output: lowercase hex, 64 chars, no encoding wrapper. Matches the format produced by
/// <see cref="System.Security.Cryptography.SHA256.HashData(System.ReadOnlySpan{byte})"/> followed
/// by <see cref="System.Convert.ToHexStringLower(byte[])"/>.</para>
/// </summary>
public interface IFileHasher
{
    /// <summary>
    /// Computes the SHA-256 of the file at <paramref name="absolutePath"/> by streaming its
    /// raw bytes. Throws <see cref="System.IO.FileNotFoundException"/> if the file is missing
    /// and <see cref="System.IO.IOException"/> on read errors.
    /// </summary>
    string HashFile(string absolutePath);

    /// <summary>
    /// Computes the SHA-256 of an arbitrary stream by reading it to end. Useful for in-memory
    /// content (e.g. content already buffered for parsing). Does not seek — caller is responsible
    /// for stream position.
    /// </summary>
    string HashStream(System.IO.Stream stream);

    /// <summary>
    /// Computes the SHA-256 of the supplied bytes. Provided so callers that already hold the
    /// full content (e.g. parsed source files) don't need to wrap a <see cref="System.IO.MemoryStream"/>.
    /// </summary>
    string HashBytes(System.ReadOnlySpan<byte> bytes);
}
