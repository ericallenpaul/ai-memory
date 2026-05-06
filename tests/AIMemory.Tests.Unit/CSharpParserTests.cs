using AIMemory.CodeIndex.Parsers;

namespace AIMemory.Tests.Unit;

public class CSharpParserTests
{
    private readonly CSharpParser _parser = new();

    [Fact]
    public void Parse_SimpleClass_ReturnsClassSymbol()
    {
        const string code = """
            namespace MyApp;

            public class MyService
            {
            }
            """;

        var symbols = _parser.Parse("MyService.cs", code);

        Assert.Contains(symbols, s => s.Name == "MyService" && s.Kind == "class");
    }

    [Fact]
    public void Parse_ClassWithMethod_ReturnsBothSymbols()
    {
        const string code = """
            namespace MyApp;

            public class Calculator
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """;

        var symbols = _parser.Parse("Calculator.cs", code);

        Assert.Contains(symbols, s => s.Name == "Calculator" && s.Kind == "class");
        Assert.Contains(symbols, s => s.Name == "Add" && s.Kind == "method");
    }

    [Fact]
    public void Parse_ClassWithProperty_ReturnsPropertySymbol()
    {
        const string code = """
            namespace MyApp;

            public class Person
            {
                public string Name { get; set; } = string.Empty;
            }
            """;

        var symbols = _parser.Parse("Person.cs", code);

        Assert.Contains(symbols, s => s.Name == "Name" && s.Kind == "property");
    }

    [Fact]
    public void Parse_Method_HasCorrectQualifiedName()
    {
        const string code = """
            namespace MyApp;

            public class OrderService
            {
                public void ProcessOrder(int orderId)
                {
                }
            }
            """;

        var symbols = _parser.Parse("OrderService.cs", code);

        var method = symbols.FirstOrDefault(s => s.Kind == "method");
        Assert.NotNull(method);
        // Qualified name includes namespace prefix from GetQualifiedName walk
        Assert.Equal("MyApp.OrderService.ProcessOrder", method.QualifiedName);
    }

    [Fact]
    public void Parse_Class_HasCorrectQualifiedNameWithNamespace()
    {
        const string code = """
            namespace MyApp.Services;

            public class UserService
            {
            }
            """;

        var symbols = _parser.Parse("UserService.cs", code);

        var cls = symbols.FirstOrDefault(s => s.Kind == "class");
        Assert.NotNull(cls);
        Assert.Equal("MyApp.Services.UserService", cls.QualifiedName);
    }

    [Fact]
    public void Parse_Interface_ReturnsInterfaceSymbol()
    {
        const string code = """
            namespace MyApp;

            public interface IRepository
            {
                void Save();
            }
            """;

        var symbols = _parser.Parse("IRepository.cs", code);

        Assert.Contains(symbols, s => s.Name == "IRepository" && s.Kind == "interface");
    }

    [Fact]
    public void Parse_Enum_ReturnsEnumSymbol()
    {
        const string code = """
            namespace MyApp;

            public enum Status
            {
                Active,
                Inactive
            }
            """;

        var symbols = _parser.Parse("Status.cs", code);

        Assert.Contains(symbols, s => s.Name == "Status" && s.Kind == "enum");
    }

    [Fact]
    public void Parse_Record_ReturnsRecordSymbol()
    {
        const string code = """
            namespace MyApp;

            public record Point(int X, int Y);
            """;

        var symbols = _parser.Parse("Point.cs", code);

        Assert.Contains(symbols, s => s.Name == "Point" && s.Kind == "record");
    }

    [Fact]
    public void Parse_Constructor_ReturnsConstructorSymbol()
    {
        const string code = """
            namespace MyApp;

            public class Widget
            {
                public Widget(string name)
                {
                }
            }
            """;

        var symbols = _parser.Parse("Widget.cs", code);

        Assert.Contains(symbols, s => s.Name == "Widget" && s.Kind == "constructor");
    }

    [Fact]
    public void Parse_MethodParentName_IsSetToContainingClass()
    {
        const string code = """
            namespace MyApp;

            public class EmailSender
            {
                public void Send(string to, string body)
                {
                }
            }
            """;

        var symbols = _parser.Parse("EmailSender.cs", code);

        var method = symbols.FirstOrDefault(s => s.Kind == "method");
        Assert.NotNull(method);
        Assert.Equal("MyApp.EmailSender", method.ParentName);
    }

    [Fact]
    public void Parse_StartAndEndLines_ArePositive()
    {
        const string code = """
            namespace MyApp;

            public class SimpleClass
            {
                public void DoWork() { }
            }
            """;

        var symbols = _parser.Parse("SimpleClass.cs", code);

        foreach (var symbol in symbols)
        {
            Assert.True(symbol.StartLine >= 1, $"Symbol {symbol.Name} has StartLine < 1");
            Assert.True(symbol.EndLine >= symbol.StartLine,
                $"Symbol {symbol.Name} has EndLine < StartLine");
        }
    }

    [Fact]
    public void Parse_EmptyContent_ReturnsNoSymbols()
    {
        var symbols = _parser.Parse("Empty.cs", string.Empty);

        Assert.Empty(symbols);
    }

    [Fact]
    public void Parse_MultipleClassesInOneFile_ReturnsAllClasses()
    {
        const string code = """
            namespace MyApp;

            public class Alpha { }

            public class Beta { }

            public class Gamma { }
            """;

        var symbols = _parser.Parse("Multiple.cs", code);

        var classSymbols = symbols.Where(s => s.Kind == "class").ToList();
        Assert.Equal(3, classSymbols.Count);
        Assert.Contains(classSymbols, s => s.Name == "Alpha");
        Assert.Contains(classSymbols, s => s.Name == "Beta");
        Assert.Contains(classSymbols, s => s.Name == "Gamma");
    }

    [Fact]
    public void Parse_PropertyQualifiedName_IncludesClassName()
    {
        const string code = """
            namespace MyApp;

            public class Config
            {
                public string ConnectionString { get; set; } = string.Empty;
            }
            """;

        var symbols = _parser.Parse("Config.cs", code);

        var prop = symbols.FirstOrDefault(s => s.Kind == "property");
        Assert.NotNull(prop);
        // QualifiedName is built from parent class name (which includes namespace)
        Assert.Equal("MyApp.Config.ConnectionString", prop.QualifiedName);
    }
}
