using AIMemory.CodeIndex.Parsers;

namespace AIMemory.Tests.Unit;

public class ParserRegistryTests
{
    private readonly ParserRegistry _registry;

    public ParserRegistryTests()
    {
        _registry = new ParserRegistry([
            new CSharpParser(),
            new PythonParser(),
            new TypeScriptParser(),
            new GoParser()
        ]);
    }

    // IsSupported tests

    [Theory]
    [InlineData("Program.cs")]
    [InlineData("MyClass.cs")]
    [InlineData("/path/to/Service.cs")]
    public void IsSupported_ReturnsTrueForCSharpFiles(string filePath)
    {
        Assert.True(_registry.IsSupported(filePath));
    }

    [Theory]
    [InlineData("main.py")]
    [InlineData("utils.py")]
    [InlineData("/src/scripts/helpers.py")]
    public void IsSupported_ReturnsTrueForPythonFiles(string filePath)
    {
        Assert.True(_registry.IsSupported(filePath));
    }

    [Theory]
    [InlineData("app.ts")]
    [InlineData("component.tsx")]
    [InlineData("index.js")]
    [InlineData("Button.jsx")]
    public void IsSupported_ReturnsTrueForTypeScriptAndJavaScriptFiles(string filePath)
    {
        Assert.True(_registry.IsSupported(filePath));
    }

    [Theory]
    [InlineData("main.go")]
    [InlineData("server.go")]
    [InlineData("/workspace/cmd/app.go")]
    public void IsSupported_ReturnsTrueForGoFiles(string filePath)
    {
        Assert.True(_registry.IsSupported(filePath));
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("data.json")]
    [InlineData("config.yaml")]
    [InlineData("Makefile")]
    [InlineData("image.png")]
    [InlineData("noextension")]
    [InlineData("")]
    public void IsSupported_ReturnsFalseForUnsupportedExtensions(string filePath)
    {
        Assert.False(_registry.IsSupported(filePath));
    }

    // GetParser tests

    [Fact]
    public void GetParser_ReturnsCSharpParserForDotCsFiles()
    {
        var parser = _registry.GetParser("Foo.cs");

        Assert.NotNull(parser);
        Assert.IsType<CSharpParser>(parser);
    }

    [Fact]
    public void GetParser_ReturnsPythonParserForDotPyFiles()
    {
        var parser = _registry.GetParser("script.py");

        Assert.NotNull(parser);
        Assert.IsType<PythonParser>(parser);
    }

    [Fact]
    public void GetParser_ReturnsTypeScriptParserForDotTsFiles()
    {
        var parser = _registry.GetParser("app.ts");

        Assert.NotNull(parser);
        Assert.IsType<TypeScriptParser>(parser);
    }

    [Fact]
    public void GetParser_ReturnsTypeScriptParserForDotTsxFiles()
    {
        var parser = _registry.GetParser("Component.tsx");

        Assert.NotNull(parser);
        Assert.IsType<TypeScriptParser>(parser);
    }

    [Fact]
    public void GetParser_ReturnsGoParserForDotGoFiles()
    {
        var parser = _registry.GetParser("main.go");

        Assert.NotNull(parser);
        Assert.IsType<GoParser>(parser);
    }

    [Fact]
    public void GetParser_ReturnsNullForUnsupportedExtension()
    {
        var parser = _registry.GetParser("README.md");

        Assert.Null(parser);
    }

    [Fact]
    public void GetParser_ReturnsNullForNoExtension()
    {
        var parser = _registry.GetParser("Makefile");

        Assert.Null(parser);
    }

    // GetLanguage tests

    [Theory]
    [InlineData("Program.cs", "csharp")]
    [InlineData("main.py", "python")]
    [InlineData("app.ts", "typescript")]
    [InlineData("app.tsx", "typescript")]
    [InlineData("index.js", "typescript")]
    [InlineData("main.go", "go")]
    public void GetLanguage_ReturnsCorrectLanguageForSupportedFiles(string filePath, string expectedLanguage)
    {
        var language = _registry.GetLanguage(filePath);

        Assert.Equal(expectedLanguage, language);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("data.json")]
    [InlineData("Makefile")]
    public void GetLanguage_ReturnsUnknownForUnsupportedFiles(string filePath)
    {
        var language = _registry.GetLanguage(filePath);

        Assert.Equal("unknown", language);
    }

    // Extension case-insensitivity tests

    [Theory]
    [InlineData("Program.CS")]
    [InlineData("Program.Cs")]
    [InlineData("MAIN.PY")]
    [InlineData("App.TS")]
    public void IsSupported_IsCaseInsensitiveForExtensions(string filePath)
    {
        Assert.True(_registry.IsSupported(filePath));
    }
}
