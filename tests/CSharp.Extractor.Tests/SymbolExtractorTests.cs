using System.Text.Json;
using CSharpToRust.Contracts;
using CSharpToRust.Extraction;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharp.Extractor.Tests;

public class SymbolExtractorTests
{
    private static readonly MetadataReference[] References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Select(path => MetadataReference.CreateFromFile(path)).ToArray();

    private static CSharpCompilation Compile(params string[] sources) =>
        CSharpCompilation.Create("Fixture",
            sources.Select((source, index) => CSharpSyntaxTree.ParseText(source,
                new CSharpParseOptions(LanguageVersion.CSharp12), Path.GetFullPath($"Part{index}.cs"))),
            References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

    private static CSharpCompilation CompileFiles(string root, params (string File, string Source)[] sources) =>
        Compile().AddSyntaxTrees(sources.Select(source => CSharpSyntaxTree.ParseText(source.Source,
            new CSharpParseOptions(LanguageVersion.CSharp12), Path.Combine(root, source.File))));

    [Fact]
    public void Extract_OmitsAmbientAssemblyCatalogFromSerializedFacts()
    {
        var compilation = Compile("public static class Api { public static int Value() => 1; }");
        var availableAssemblies = compilation.ReferencedAssemblyNames.Select(name => name.ToString()).ToArray();
        Assert.NotEmpty(availableAssemblies);

        var project = SymbolExtractor.Extract(compilation, "sample.csproj", Directory.GetCurrentDirectory());
        var json = JsonSerializer.Serialize(project, ArtifactJson.Options);

        Assert.Empty(project.AssemblyReferences);
        foreach (var identity in availableAssemblies)
            Assert.DoesNotContain(identity, json);
        using var serialized = JsonDocument.Parse(json);
        Assert.Empty(serialized.RootElement.GetProperty("assemblyReferences").EnumerateArray());
    }

    [Fact]
    public void Extract_OmitsCatalogWithoutChangingCompilerReferencesOrExternalCalls()
    {
        var compilation = Compile("""
            public static class Api
            {
                public static int Absolute(int value) => System.Math.Abs(value);
            }
            """);
        var compilerReferences = compilation.References.ToArray();

        var project = SymbolExtractor.Extract(compilation, "sample.csproj", Directory.GetCurrentDirectory());
        var method = Assert.Single(project.Symbols, symbol => symbol.Name == "Absolute");
        var call = Assert.Single(method.Relationships, relation => relation.Kind == "calls");

        Assert.Contains("System.Math.Abs", call.TargetDisplay);
        Assert.StartsWith("assembly:", call.TargetId);
        Assert.Equal(compilerReferences, compilation.References);
        Assert.DoesNotContain(project.Diagnostics, diagnostic => diagnostic.Severity == "error");
        Assert.Empty(project.AssemblyReferences);
    }

    [Fact]
    public void Extract_RecordsSignaturesSourceAndDecimalExceptionSemantics()
    {
        var compilation = Compile("""
            namespace Example;
            /// <summary>Arithmetic SDK.</summary>
            public static class Calculator
            {
                public static decimal Divide(decimal left, decimal right)
                {
                    if (right == 0m) throw new System.DivideByZeroException("Cannot divide by zero.");
                    return left / right;
                }
            }
            """);

        var project = SymbolExtractor.Extract(compilation, "sample.csproj", Directory.GetCurrentDirectory());
        var method = Assert.Single(project.Symbols, symbol => symbol.Name == "Divide");

        Assert.True(method.IsPublicApi);
        Assert.Equal("decimal", method.Type);
        Assert.Equal(2, method.Parameters.Count);
        Assert.Contains("throw", method.MigrationSignals);
        Assert.Contains("decimal", method.MigrationSignals);
        Assert.Contains("return left / right;", method.Declaration);
        Assert.Contains(method.Relationships, relation =>
            relation.Kind == "throws" && relation.TargetDisplay.Contains("DivideByZeroException"));
        Assert.Equal(5, method.Source.StartLine);
        Assert.Contains("Arithmetic SDK", project.Symbols.Single(s => s.Name == "Calculator").Documentation);
        Assert.DoesNotContain(project.Diagnostics, diagnostic => diagnostic.Severity == "error");
    }

    [Fact]
    public void Extract_ResolvesOverloadsAndPublicVisibility()
    {
        var compilation = Compile("""
            public class Api
            {
                public int Pick(int value) => value;
                public string Pick(string value) => value;
                public int Run() => Pick(1);
                private int Hidden() => 1;
                private class PrivateType { public int Invisible() => 1; }
            }
            """);

        var project = SymbolExtractor.Extract(compilation, "sample.csproj", Directory.GetCurrentDirectory());
        var overloads = project.Symbols.Where(symbol => symbol.Name == "Pick").ToList();
        Assert.Equal(2, overloads.Count);
        Assert.Equal(2, overloads.Select(symbol => symbol.Id).Distinct().Count());
        var called = Assert.Single(project.Symbols.Single(s => s.Name == "Run").Relationships,
            relation => relation.Kind == "calls");
        Assert.Equal(overloads.Single(s => s.Parameters[0].Type == "int").Id, called.TargetId);
        Assert.False(project.Symbols.Single(s => s.Name == "Hidden").IsPublicApi);
        Assert.False(project.Symbols.Single(s => s.Name == "Invisible").IsPublicApi);
    }

    [Fact]
    public void Extract_MergesPartialTypesAndCapturesMigrationSensitiveCode()
    {
        var compilation = Compile("""
            public partial class Api
            {
                public async System.Threading.Tasks.Task<string?> Run(System.IDisposable resource)
                {
                    using (resource)
                    {
                        try { await System.Threading.Tasks.Task.Yield(); return null; }
                        catch (System.Exception e) when (e.Message.Length > 0) { throw; }
                        finally { System.Console.WriteLine("cleanup"); }
                    }
                }
            }
            """, "public partial class Api { public int Value { get; set; } }");

        var project = SymbolExtractor.Extract(compilation, "sample.csproj", Directory.GetCurrentDirectory());
        var type = Assert.Single(project.Symbols, symbol => symbol.Name == "Api");
        Assert.Single(type.AdditionalSources);
        var method = project.Symbols.Single(symbol => symbol.Name == "Run");
        Assert.True(method.IsAsync);
        foreach (var signal in new[] { "async", "await", "try", "catch", "catch-filter", "finally", "using", "rethrow", "nullable" })
        {
            Assert.Contains(signal, method.MigrationSignals);
        }
    }

    [Fact]
    public void Extract_MissingReferenceReportsErrorsAndUnresolvedCalls()
    {
        var compilation = Compile("public class Api { public void Run() { Missing.Call(); } }");

        var project = SymbolExtractor.Extract(compilation, "sample.csproj", Directory.GetCurrentDirectory());

        Assert.Contains(project.Diagnostics, diagnostic => diagnostic.Severity == "error");
        Assert.Contains(project.Diagnostics, diagnostic => diagnostic.Code == "UNRESOLVED_CALL");
    }

    [Fact]
    public void Extract_InheritanceAndInterfaceRelationshipsAreExplicit()
    {
        var project = SymbolExtractor.Extract(Compile("""
            public interface IApi { void Run(); }
            public class Base { public virtual void Run() {} }
            public class Api : Base, IApi { public override void Run() {} }
            """), "sample.csproj", Directory.GetCurrentDirectory());
        var type = project.Symbols.Single(s => s.Name == "Api");

        Assert.Contains(type.Relationships, relation => relation.Kind == "inherits");
        Assert.Contains(type.Relationships, relation => relation.Kind == "implements");
        Assert.Contains(project.Symbols.Single(s => s.Name == "Run" && s.ContainingSymbolId == type.Id)
            .Relationships, relation => relation.Kind == "overrides");
    }

    [Fact]
    public void Extract_CrossProjectCallsUseTargetProjectIdentity()
    {
        var library = Compile("public static class Library { public static int Get() => 1; }")
            .WithAssemblyName("Library");
        var caller = Compile("public static class Caller { public static int Run() => Library.Get(); }")
            .WithAssemblyName("Caller").AddReferences(library.ToMetadataReference());
        var projects = new Dictionary<string, string>
        {
            [library.Assembly.Identity.ToString()] = "project:Library.csproj",
            [caller.Assembly.Identity.ToString()] = "project:Caller.csproj"
        };

        var result = SymbolExtractor.Extract(caller, "Caller.csproj", Directory.GetCurrentDirectory(), projects);

        Assert.Equal("project:Library.csproj:M:Library.Get",
            Assert.Single(result.Symbols.Single(symbol => symbol.Name == "Run").Relationships).TargetId);
    }

    [Fact]
    public void Extract_RecordSynthesizedBehaviorIsReportedAsGap()
    {
        var result = SymbolExtractor.Extract(Compile("public record Customer(string Name);"),
            "sample.csproj", Directory.GetCurrentDirectory());

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SYNTHESIZED_MEMBERS");
        Assert.Contains("Customer(string Name)", result.Symbols.Single(symbol => symbol.Name == "Customer").Declaration);
    }

    [Fact]
    public void Extract_LargeMemberIsExplicitlyTruncated()
    {
        var result = SymbolExtractor.Extract(Compile(
            "public static class Api { public static string Run() => \"" + new string('x', 18000) + "\"; }"),
            "sample.csproj", Directory.GetCurrentDirectory());

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOURCE_TRUNCATED");
        Assert.Equal(SymbolExtractor.MaximumDeclarationLength,
            result.Symbols.Single(symbol => symbol.Name == "Run").Declaration.Length);
    }

    [Fact]
    public void Extract_FieldRetainsMutabilityAndTypeDeclaration()
    {
        var result = SymbolExtractor.Extract(Compile(
            "public static class Api { public static readonly decimal Rate = 1.5m; }"),
            "sample.csproj", Directory.GetCurrentDirectory());

        Assert.Contains("public static readonly decimal Rate = 1.5m;",
            result.Symbols.Single(symbol => symbol.Name == "Rate").Declaration);
    }

    [Fact]
    public void Extract_TopLevelCodeIsReportedRatherThanSilentlyOmitted()
    {
        var result = SymbolExtractor.Extract(Compile("System.Console.WriteLine(\"hello\");")
            .WithOptions(new CSharpCompilationOptions(OutputKind.ConsoleApplication)),
            "sample.csproj", Directory.GetCurrentDirectory());

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TOP_LEVEL_CODE");
    }

    [Fact]
    public void Extract_DelegateIncludesReturnTypeAndParameters()
    {
        var result = SymbolExtractor.Extract(Compile("public delegate bool Predicate(int value);"),
            "sample.csproj", Directory.GetCurrentDirectory());
        var predicate = Assert.Single(result.Symbols);

        Assert.Equal("bool", predicate.Type);
        Assert.Equal("int", Assert.Single(predicate.Parameters).Type);
    }

    [Fact]
    public void Extract_ReducedExtensionCallIdentifiesReceiverOverload()
    {
        var result = SymbolExtractor.Extract(Compile("""
            public static class Extensions
            {
                public static int F(this int value) => value;
                public static int F() => 0;
                public static int Run() => 3.F();
            }
            """), "sample.csproj", Directory.GetCurrentDirectory());

        var target = Assert.Single(result.Symbols.Single(s => s.Name == "Run").Relationships);
        Assert.Equal(result.Symbols.Single(s => s.Name == "F" && s.Parameters.Count == 1).Id, target.TargetId);
    }

    [Fact]
    public void Extract_LocalFunctionsHaveDistinctEvidenceAndTargets()
    {
        var result = SymbolExtractor.Extract(Compile("""
            public static class Api
            {
                public static int Local() => 0;
                public static int A() { int Local() => 1; return Local(); }
                public static int B() { int Local() => 2; return Local(); }
            }
            """), "sample.csproj", Directory.GetCurrentDirectory());

        var locals = result.Symbols.Where(s => s.Name == "Local").ToList();
        Assert.Equal(3, locals.Count);
        Assert.Equal(3, locals.Select(s => s.Id).Distinct().Count());
        foreach (var name in new[] { "A", "B" })
        {
            var parent = result.Symbols.Single(s => s.Name == name);
            var call = Assert.Single(parent.Relationships, relation => relation.Kind == "calls");
            var local = Assert.Single(locals, local => local.ContainingSymbolId == parent.Id);
            Assert.Equal(local.Id, call.TargetId);
            Assert.False(local.IsPublicApi);
        }
    }

    [Fact]
    public void Extract_PrimaryConstructorReportsCoverageGap()
    {
        var result = SymbolExtractor.Extract(Compile(
            "public class Api(int value) { public int Value => value; }"),
            "sample.csproj", Directory.GetCurrentDirectory());

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PRIMARY_CONSTRUCTOR");
    }

    [Fact]
    public void Extract_NameofIsNotAnUnresolvedCall()
    {
        var result = SymbolExtractor.Extract(Compile("""
            public static class Api
            {
                public static void Validate(string value)
                {
                    if (value is null) throw new System.ArgumentNullException(nameof(value));
                }
            }
            """), "sample.csproj", Directory.GetCurrentDirectory());

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "UNRESOLVED_CALL");
    }

    [Fact]
    public void Extract_EnumRetainsDeclarationOrderAndImplicitValues()
    {
        var first = SymbolExtractor.Extract(Compile("public enum Choices { First, Second }"),
            "sample.csproj", Directory.GetCurrentDirectory());
        var second = SymbolExtractor.Extract(Compile("public enum Choices { Second, First }"),
            "sample.csproj", Directory.GetCurrentDirectory());

        Assert.NotEqual(first.Symbols.Single(s => s.Name == "Choices").Declaration,
            second.Symbols.Single(s => s.Name == "Choices").Declaration);
        Assert.Contains("{ First, Second }", first.Symbols.Single(s => s.Name == "Choices").Declaration);
    }

    [Fact]
    public void Extract_LocalsInsideLambdasInPartialFilesDoNotMerge()
    {
        var result = SymbolExtractor.Extract(Compile(
            "public static partial class Api { public static System.Func<int> A() => () => { int Local() => 1; return Local(); }; }",
            "public static partial class Api { public static System.Func<int> B() => () => { int Local() => 2; return Local(); }; }"),
            "sample.csproj", Directory.GetCurrentDirectory());

        var locals = result.Symbols.Where(s => s.Name == "Local").ToList();
        Assert.Equal(2, locals.Count);
        Assert.Equal(2, locals.Select(s => s.Id).Distinct().Count());
        Assert.All(locals, local =>
        {
            Assert.Empty(local.AdditionalSources);
            Assert.Contains(result.Symbols, parent => parent.Id == local.ContainingSymbolId);
        });
    }

    [Fact]
    public void Extract_LocalInsideGetterReferencesRetainedProperty()
    {
        var result = SymbolExtractor.Extract(Compile(
            "public static class Api { public static int Value { get { int Local() => 1; return Local(); } } }"),
            "sample.csproj", Directory.GetCurrentDirectory());

        var property = result.Symbols.Single(s => s.Name == "Value");
        var local = result.Symbols.Single(s => s.Name == "Local");
        Assert.Equal(property.Id, local.ContainingSymbolId);
        Assert.Equal(local.Id, Assert.Single(property.Relationships).TargetId);
    }

    [Fact]
    public void Extract_FileLocalTypesKeepSeparateMembersAndCallTargets()
    {
        var root = Path.GetFullPath("file-local-fixture");
        var project = SymbolExtractor.Extract(CompileFiles(root,
            (Path.Combine("first", "Helper.cs"),
                "file static class Helper { public static int Value() => 1; } public static class A { public static int Read() => Helper.Value(); }"),
            (Path.Combine("second", "Helper.cs"),
                "file static class Helper { public static int Value() => 2; } public static class B { public static int Read() => Helper.Value(); }")),
            "sample.csproj", root);

        Assert.Empty(project.Diagnostics);
        Assert.Equal(2, project.Symbols.Count(symbol => symbol.Name == "Helper"));
        Assert.Equal(2, project.Symbols.Count(symbol => symbol.Name == "Value"));
        foreach (var (name, value) in new[] { ("A", 1), ("B", 2) })
        {
            var caller = project.Symbols.Single(symbol => symbol.Id == $"project:sample.csproj:M:{name}.Read");
            var call = Assert.Single(caller.Relationships);
            var target = Assert.Single(project.Symbols, symbol => symbol.Id == call.TargetId);
            var owner = Assert.Single(project.Symbols, symbol => symbol.Id == target.ContainingSymbolId);
            Assert.Equal("calls", call.Kind);
            Assert.Equal("Value", target.Name);
            Assert.Equal($"public static int Value() => {value};", target.Declaration);
            Assert.Equal(caller.Source.File, target.Source.File);
            Assert.Equal(caller.Source.File, owner.Source.File);
            Assert.Equal("Helper", owner.Name);
            Assert.False(owner.IsPublicApi);
            Assert.Empty(target.AdditionalSources);
            Assert.Empty(owner.AdditionalSources);
        }
    }

    [Fact]
    public void Extract_FileLocalNestedGenericsResolveOriginalDefinitions()
    {
        var sources = new[] { ("A", 1), ("B", 2) }.Select(item => $$"""
            file static class Helper<T>
            {
                public static class Nested<U>
                {
                    public static V Identity<V>(V value) { _ = Marker(); return value; }
                    public static int Marker() => {{item.Item2}};
                }
            }
            public static class {{item.Item1}}
            {
                public static string Read() => Helper<int>.Nested<bool>.Identity<string>("value");
                public static int Again() => Helper<string>.Nested<long>.Identity<int>(1);
            }
            """).ToArray();
        var project = SymbolExtractor.Extract(Compile(sources), "sample.csproj", Directory.GetCurrentDirectory());

        Assert.Empty(project.Diagnostics);
        Assert.Equal(2, project.Symbols.Count(symbol => symbol.Name == "Helper"));
        Assert.Equal(2, project.Symbols.Count(symbol => symbol.Name == "Nested"));
        foreach (var (name, value) in new[] { ("A", 1), ("B", 2) })
        {
            var caller = project.Symbols.Single(symbol => symbol.Id == $"project:sample.csproj:T:{name}");
            var helper = project.Symbols.Single(symbol => symbol.Name == "Helper" && symbol.Source.File == caller.Source.File);
            var nested = Assert.Single(project.Symbols, symbol => symbol.ContainingSymbolId == helper.Id);
            Assert.Equal("Nested", nested.Name);
            var identity = project.Symbols.Single(symbol => symbol.Name == "Identity" && symbol.ContainingSymbolId == nested.Id);
            var marker = project.Symbols.Single(symbol => symbol.Name == "Marker" && symbol.ContainingSymbolId == nested.Id);
            Assert.Equal($"public static int Marker() => {value};", marker.Declaration);
            Assert.Equal(marker.Id, Assert.Single(identity.Relationships).TargetId);
            foreach (var method in project.Symbols.Where(symbol => symbol.ContainingSymbolId == caller.Id))
                Assert.Equal(identity.Id, Assert.Single(method.Relationships).TargetId);
        }
        Assert.All(project.Symbols, symbol => Assert.Empty(symbol.AdditionalSources));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Extract_FileLocalIdsRemainStableAcrossRelocationAndInputOrder(bool reverse)
    {
        (string File, string Source)[] sources =
        [
            (Path.Combine("first", "Helper.cs"),
                "file static class Helper { public static int Value() => 1; } public static class A { public static int Read() => Helper.Value(); }"),
            (Path.Combine("second", "Helper.cs"),
                "file static class Helper { public static int Value() => 2; } public static class B { public static int Read() => Helper.Value(); }"),
            ("Ordinary.cs", "public static class Ordinary { public static int Read() => System.Math.Abs(-1); }")
        ];
        var root = Path.GetFullPath("original-checkout");
        var relocatedRoot = Path.GetFullPath("relocated-checkout");
        var original = SymbolExtractor.Extract(CompileFiles(root, sources), "sample.csproj", root);
        var relocated = SymbolExtractor.Extract(
            CompileFiles(relocatedRoot, reverse ? sources.Reverse().ToArray() : sources), "sample.csproj", relocatedRoot);

        Assert.Empty(original.Diagnostics);
        Assert.Empty(relocated.Diagnostics);
        Assert.Equal(2, original.Symbols.Count(symbol => symbol.Name == "Helper"));
        Assert.Equal(JsonSerializer.Serialize(original, ArtifactJson.Options),
            JsonSerializer.Serialize(relocated, ArtifactJson.Options));
    }

    [Fact]
    public void Extract_FileLocalScopePreservesPartialMergingAndOrdinaryExternalIds()
    {
        const string read = "public static int Read() => System.Math.Abs(-1);";
        var root = Directory.GetCurrentDirectory();
        var project = SymbolExtractor.Extract(Compile(
            "file static partial class Helper { public static int Value() => 1; } " +
            "file static partial class Helper { public static int Other() => 2; } " +
            "public static partial class Api { " + read + " }",
            "file static class Helper { public static int Value() => 3; } " +
            "public static partial class Api { public static int Other() => 4; }"), "sample.csproj", root);
        var ordinary = SymbolExtractor.Extract(Compile("public static class Api { " + read + " }"), "sample.csproj", root);

        Assert.Empty(project.Diagnostics);
        Assert.Equal(2, project.Symbols.Count(symbol => symbol.Name == "Helper"));
        var firstHelper = project.Symbols.Single(symbol => symbol.Name == "Helper" && symbol.Source.File == "Part0.cs");
        Assert.Single(firstHelper.AdditionalSources);
        Assert.Equal(2, project.Symbols.Count(symbol => symbol.ContainingSymbolId == firstHelper.Id));
        var api = project.Symbols.Single(symbol => symbol.Name == "Api");
        Assert.Equal(ordinary.Symbols.Single(symbol => symbol.Name == "Api").Id, api.Id);
        Assert.Single(api.AdditionalSources);
        var method = project.Symbols.Single(symbol => symbol.Name == "Read");
        var ordinaryMethod = ordinary.Symbols.Single(symbol => symbol.Name == "Read");
        Assert.Equal(ordinaryMethod.Id, method.Id);
        Assert.Equal(api.Id, method.ContainingSymbolId);
        Assert.Equal(Assert.Single(ordinaryMethod.Relationships).TargetId, Assert.Single(method.Relationships).TargetId);
        Assert.StartsWith("assembly:", Assert.Single(method.Relationships).TargetId);
    }
}
