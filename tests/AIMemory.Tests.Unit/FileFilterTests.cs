using AIMemory.CodeIndex.Security;

namespace AIMemory.Tests.Unit;

public class FileFilterTests
{
    private readonly FileFilter _filter = new();

    // ShouldIncludeDirectory tests

    [Theory]
    [InlineData(".git")]
    [InlineData("node_modules")]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData(".vs")]
    [InlineData(".idea")]
    [InlineData("vendor")]
    [InlineData("__pycache__")]
    [InlineData("dist")]
    [InlineData("build")]
    [InlineData(".next")]
    [InlineData("coverage")]
    public void ShouldIncludeDirectory_ExcludesWellKnownDirectories(string dirName)
    {
        var result = _filter.ShouldIncludeDirectory(dirName);

        Assert.False(result);
    }

    [Theory]
    [InlineData("src")]
    [InlineData("lib")]
    [InlineData("tests")]
    [InlineData("Controllers")]
    [InlineData("Services")]
    [InlineData("Models")]
    public void ShouldIncludeDirectory_IncludesOrdinaryDirectories(string dirName)
    {
        var result = _filter.ShouldIncludeDirectory(dirName);

        Assert.True(result);
    }

    [Fact]
    public void ShouldIncludeDirectory_IsCaseInsensitive()
    {
        Assert.False(_filter.ShouldIncludeDirectory("BIN"));
        Assert.False(_filter.ShouldIncludeDirectory("Node_Modules"));
        Assert.False(_filter.ShouldIncludeDirectory("OBJ"));
    }

    // ShouldIncludeFile tests

    [Theory]
    [InlineData("foo.exe")]
    [InlineData("foo.dll")]
    [InlineData("foo.png")]
    [InlineData("foo.jpg")]
    [InlineData("foo.zip")]
    [InlineData("foo.pdf")]
    [InlineData("foo.env")]
    [InlineData("foo.pem")]
    [InlineData("foo.key")]
    [InlineData("foo.map")]
    [InlineData("foo.lock")]
    [InlineData("foo.woff")]
    [InlineData("foo.ttf")]
    public void ShouldIncludeFile_ExcludesBinaryAndSecretExtensions(string fileName)
    {
        var result = _filter.ShouldIncludeFile(fileName, 100);

        Assert.False(result);
    }

    [Theory]
    [InlineData("credentials.json")]
    [InlineData("secrets.json")]
    [InlineData("appsettings.Development.json")]
    [InlineData(".env.local")]
    [InlineData(".env.production")]
    [InlineData("package-lock.json")]
    [InlineData("yarn.lock")]
    [InlineData(".DS_Store")]
    [InlineData("Thumbs.db")]
    public void ShouldIncludeFile_ExcludesSecretFileNames(string fileName)
    {
        var result = _filter.ShouldIncludeFile(fileName, 100);

        Assert.False(result);
    }

    [Fact]
    public void ShouldIncludeFile_ExcludesFilesExceedingMaxSize()
    {
        _filter.MaxFileSizeBytes = 1024;

        var result = _filter.ShouldIncludeFile("large.cs", 2048);

        Assert.False(result);
    }

    [Fact]
    public void ShouldIncludeFile_IncludesFilesAtExactMaxSize()
    {
        _filter.MaxFileSizeBytes = 1024;

        var result = _filter.ShouldIncludeFile("exactly.cs", 1024);

        Assert.True(result);
    }

    [Fact]
    public void ShouldIncludeFile_ExcludesMinifiedJavaScript()
    {
        Assert.False(_filter.ShouldIncludeFile("bundle.min.js", 100));
    }

    [Fact]
    public void ShouldIncludeFile_ExcludesMinifiedCss()
    {
        Assert.False(_filter.ShouldIncludeFile("styles.min.css", 100));
    }

    [Theory]
    [InlineData("Program.cs")]
    [InlineData("app.ts")]
    [InlineData("main.go")]
    [InlineData("utils.py")]
    [InlineData("index.html")]
    [InlineData("README.md")]
    public void ShouldIncludeFile_IncludesSourceCodeFiles(string fileName)
    {
        var result = _filter.ShouldIncludeFile(fileName, 100);

        Assert.True(result);
    }

    // IsBinaryFile tests

    [Fact]
    public void IsBinaryFile_ReturnsTrueForFileWithNullBytes()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            // Write content containing a null byte
            var bytes = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x00, 0x57, 0x6F, 0x72, 0x6C, 0x64 };
            File.WriteAllBytes(tmpFile, bytes);

            var result = _filter.IsBinaryFile(tmpFile);

            Assert.True(result);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void IsBinaryFile_ReturnsFalseForPlainTextFile()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile, "public class Foo { public void Bar() { } }");

            var result = _filter.IsBinaryFile(tmpFile);

            Assert.False(result);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void IsBinaryFile_ReturnsTrueForNonExistentFile()
    {
        var result = _filter.IsBinaryFile("/nonexistent/path/file.cs");

        Assert.True(result);
    }

    // GetRelativePath tests

    [Fact]
    public void GetRelativePath_ReturnsForwardSlashSeparatedPath()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "repo");
        var filePath = Path.Combine(rootPath, "src", "Foo.cs");

        var result = _filter.GetRelativePath(filePath, rootPath);

        Assert.Contains("/", result);
        Assert.DoesNotContain("\\", result);
    }
}
