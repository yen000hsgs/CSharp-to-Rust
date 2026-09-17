using System.Text;
using System.Text.Json;
using CSharpToRust.Contracts;
using CSharpToRust.Extraction;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharp.Extractor.Tests;

public class CompactViewsTests
{
    private static ExtractionArtifact Fixture() => new()
    {
        TaskId = "compact-test", ExtractionId = "snapshot-1", InputPath = "Calculator.csproj",
        RootDirectory = "sample", Status = "complete",
        Limitations = ["Explicit throws are not the complete exception contract."],
        Projects =
        [
            new ProjectFact
            {
                Id = "project:Calculator.csproj", Name = "Calculator", File = "Calculator.csproj",
                TargetFramework = ".NETCoreApp,Version=v8.0", OutputKind = "DynamicallyLinkedLibrary",
                LanguageVersion = "CSharp12", NullableContext = "Enable", Defines = ["TRACE"],
                AssemblyReferences = Enumerable.Range(0, 163).Select(i =>
                    $"System.Framework{i}, Version=8.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a").ToList(),
                Symbols =
                [
                    new SymbolFact
                    {
                        Id = "project:Calculator.csproj:M:Calculator.Divide(System.Decimal,System.Decimal)",
                        Name = "Divide", DisplayName = "Calculator.Divide(decimal, decimal)", Kind = "Method",
                        Accessibility = "Public", IsPublicApi = true, IsStatic = true, Type = "decimal",
                        Source = new("Calculator.cs", 4, 10), MigrationSignals = ["decimal", "throw"],
                        Declaration = """
                            /// <summary>Divide two numbers.</summary>
                            public static decimal Divide(decimal left, decimal right)
                            {
                                // Delegate validation, never discard this behavior.
                                Validate(right);
                                return left / right;
                            }
                            """,
                        Documentation = "<summary>Divide two numbers.</summary>",
                        Parameters = [new("left", "decimal", "None", false, null), new("right", "decimal", "None", false, null)],
                        Relationships =
                        [
                            new("calls", "project:Calculator.csproj:M:Calculator.Validate(System.Decimal)",
                                "Calculator.Validate(decimal)", new("Calculator.cs", 7, 7))
                        ]
                    },
                    new SymbolFact
                    {
                        Id = "project:Calculator.csproj:M:Calculator.Validate(System.Decimal)",
                        Name = "Validate", DisplayName = "Calculator.Validate(decimal)", Kind = "Method",
                        Accessibility = "Private", IsStatic = true, Type = "void", Source = new("Calculator.cs", 12, 15),
                        Declaration = "private static void Validate(decimal value) { if (value == 0m) throw new System.DivideByZeroException(); }",
                        Parameters = [new("value", "decimal", "None", false, null)], MigrationSignals = ["decimal", "throw"]
                    }
                ]
            }
        ]
    };

    private static CompactViewOptions Options(string command = "index", string? symbol = null,
        string part = "code", int offset = 0, int limit = 25, int maxChars = 2000, int maxBytes = 4096) =>
        new(command, "unused.json", symbol, part, offset, limit, maxChars, maxBytes);

    [Fact]
    public void Index_IsSmallPublicOnlyAndDoesNotMutateFullArtifact()
    {
        var artifact = Fixture();
        var original = JsonSerializer.Serialize(artifact, ArtifactJson.Options);
        var json = new CompactViews(artifact).Render(Options());
        using var result = JsonDocument.Parse(json);

        Assert.True(Encoding.UTF8.GetByteCount(json) < 1500);
        Assert.True(json.Length < original.Length / 10);
        Assert.DoesNotContain("System.Framework", json);
        Assert.DoesNotContain("Validate", json);
        Assert.DoesNotContain("return left", json);
        Assert.DoesNotContain("project:Calculator.csproj:M:", json);
        Assert.Contains("Calculator.cs", json);
        Assert.Contains("Calculator.Divide", json);
        Assert.Equal(1, result.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(original, JsonSerializer.Serialize(artifact, ArtifactJson.Options));
    }

    [Fact]
    public void Inspect_ResolvesAliasAndMakesPrivateDependencyRetrievable()
    {
        var view = new CompactViews(Fixture());
        using var relationPage = JsonDocument.Parse(view.Render(Options("inspect", "s1", "relations")));
        var relation = relationPage.RootElement.GetProperty("items")[0];
        Assert.Equal("s2", relation.GetProperty("target").GetString());
        using var privateView = JsonDocument.Parse(view.Render(Options("inspect", "s2")));

        Assert.Contains("DivideByZeroException", privateView.RootElement.GetProperty("text").GetString());
        Assert.Equal(Fixture().Projects[0].Symbols[1].Id,
            privateView.RootElement.GetProperty("symbolId").GetString());
    }

    [Fact]
    public void Context_RetainsBuildSettingsDiagnosticsAndLimitationsWithoutAssemblyCatalog()
    {
        var artifact = Fixture();
        artifact.Status = "partial";
        artifact.Projects[0].Diagnostics.Add(new("UNRESOLVED_CALL", "warning", "Missing dependency.", new("Calculator.cs", 7, 7)));
        var json = new CompactViews(artifact).Render(Options("context"));

        Assert.Contains("\"status\":\"partial\"", json);
        Assert.Contains("\"checkOverflow\":false", json);
        Assert.Contains("\"nullable\":\"Enable\"", json);
        Assert.Contains("TRACE", json);
        Assert.Contains("UNRESOLVED_CALL", json);
        Assert.Contains("Explicit throws", json);
        Assert.DoesNotContain("System.Framework", json);
    }

    [Fact]
    public void Inspect_KeepsExactSemanticMetadataAndSeparateDocumentation()
    {
        var artifact = Fixture();
        var symbol = artifact.Projects[0].Symbols[0];
        symbol.Parameters = [new("items", "A.Customer<B.Item?>", "Ref", true, "null")];
        symbol.Attributes = ["Custom.Contract(\"stable\")"];
        var view = new CompactViews(artifact);
        var facts = view.Render(Options("inspect", "s1", "facts"));
        var documentation = view.Render(Options("inspect", "s1", "documentation"));
        var raw = view.Render(Options("inspect", "s1", "raw"));

        Assert.Contains("A.Customer", facts);
        Assert.Contains("\"refKind\":\"Ref\"", facts);
        Assert.Contains("Custom.Contract", facts);
        Assert.Contains("Divide two numbers", documentation);
        Assert.Contains("Delegate validation", raw);
        Assert.DoesNotContain("Delegate validation", view.Render(Options("inspect", "s1")));
    }

    [Fact]
    public void Index_PaginatesByBudgetWithStableAliasesAndNoMissingSymbols()
    {
        var artifact = Fixture();
        artifact.Projects[0].Symbols = Enumerable.Range(0, 30).Select(i => new SymbolFact
        {
            Id = $"M:Api.Method{i:D2}", Name = $"Method{i:D2}", DisplayName = $"Api.Method{i:D2}(string)",
            Kind = "Method", Accessibility = "Public", IsPublicApi = true, Type = "string",
            Source = new($"File{i % 2}.cs", 1, 2), Declaration = $"public string Method{i:D2}(string x) => x;"
        }).ToList();
        var view = new CompactViews(artifact);
        var references = new HashSet<string>();
        var offset = 0;
        do
        {
            var json = view.Render(Options(offset: offset, limit: 25, maxBytes: 1024));
            Assert.True(Encoding.UTF8.GetByteCount(json + Environment.NewLine) <= 1024);
            using var page = JsonDocument.Parse(json);
            foreach (var group in page.RootElement.GetProperty("groups").EnumerateArray())
                foreach (var entry in group.GetProperty("symbols").EnumerateArray())
                    Assert.True(references.Add(entry.GetProperty("ref").GetString()!));
            var next = page.RootElement.GetProperty("nextOffset");
            if (next.ValueKind == JsonValueKind.Null) break;
            Assert.True(next.GetInt32() > offset);
            offset = next.GetInt32();
        } while (offset < 30);

        Assert.Equal(30, references.Count);
        artifact.Projects[0].Symbols.Reverse();
        Assert.Equal(view.Render(Options()), new CompactViews(artifact).Render(Options()));
    }

    [Fact]
    public void Inspect_PagesUnicodeTextAndNeverClaimsStoredTruncationIsResolved()
    {
        var artifact = Fixture();
        artifact.Status = "partial";
        var symbol = artifact.Projects[0].Symbols[0];
        symbol.Declaration = "public string Run() => \"" + string.Concat(Enumerable.Repeat("\U0001F600", 1000)) + "\";";
        artifact.Projects[0].Diagnostics.Add(new("SOURCE_TRUNCATED", "warning", $"Excerpt truncated for {symbol.Id}", symbol.Source));
        var view = new CompactViews(artifact);
        var combined = new StringBuilder();
        var offset = 0;
        while (true)
        {
            var json = view.Render(Options("inspect", "s1", "raw", offset, maxChars: 101, maxBytes: 1024));
            Assert.True(Encoding.UTF8.GetByteCount(json + Environment.NewLine) <= 1024);
            using var page = JsonDocument.Parse(json);
            Assert.Equal("partial", page.RootElement.GetProperty("status").GetString());
            Assert.True(page.RootElement.GetProperty("sourceTruncated").GetBoolean());
            var text = page.RootElement.GetProperty("text").GetString()!;
            Assert.False(char.IsLowSurrogate(text[0]));
            Assert.False(char.IsHighSurrogate(text[^1]));
            combined.Append(text);
            var next = page.RootElement.GetProperty("nextOffset");
            if (next.ValueKind == JsonValueKind.Null) break;
            Assert.True(next.GetInt32() > offset);
            offset = next.GetInt32();
        }
        Assert.Equal(symbol.Declaration, combined.ToString());
    }

    [Fact]
    public void InvalidSelectorsBoundsOrDuplicateIdsAreRejected()
    {
        var artifact = Fixture();
        var view = new CompactViews(artifact);
        Assert.Throws<ArgumentException>(() => view.Render(Options("inspect", "unknown")));
        Assert.Throws<ArgumentException>(() => view.Render(Options(offset: 20)));
        Assert.Throws<ArgumentException>(() => view.Render(Options(maxBytes: 100)));
        artifact.Projects[0].Symbols.Add(artifact.Projects[0].Symbols[0]);
        Assert.Throws<InvalidDataException>(() => new CompactViews(artifact));
    }

    [Fact]
    public void OversizedSingleEntryFailsExplicitlyRatherThanExceedingBudget()
    {
        var artifact = Fixture();
        artifact.Projects[0].Symbols[0].DisplayName = new string('x', 20000);
        Assert.Throws<InvalidDataException>(() => new CompactViews(artifact).Render(Options(maxBytes: 1024)));
    }

    [Fact]
    public void Context_PagesEveryDiagnosticAndLimitationWithinBudget()
    {
        var artifact = Fixture();
        artifact.Status = "partial";
        artifact.Diagnostics = Enumerable.Range(0, 30).Select(i =>
            new DiagnosticFact($"WARNING_{i}", "warning", new string('x', 100))).ToList();
        var view = new CompactViews(artifact);
        var diagnostics = new HashSet<string>();
        var offset = 0;
        var hasLimitations = false;
        var hasSettings = false;
        while (true)
        {
            var json = view.Render(Options("context", offset: offset, maxBytes: 1024));
            Assert.True(Encoding.UTF8.GetByteCount(json + Environment.NewLine) <= 1024);
            using var page = JsonDocument.Parse(json);
            foreach (var item in page.RootElement.GetProperty("items").EnumerateArray())
            {
                var kind = item.GetProperty("kind").GetString();
                hasLimitations |= kind == "limitation";
                hasSettings |= kind == "project";
                if (kind == "diagnostic")
                    Assert.True(diagnostics.Add(item.GetProperty("diagnostic").GetProperty("code").GetString()!));
            }
            var next = page.RootElement.GetProperty("nextOffset");
            if (next.ValueKind == JsonValueKind.Null) break;
            Assert.True(next.GetInt32() > offset);
            offset = next.GetInt32();
        }
        Assert.Equal(30, diagnostics.Count);
        Assert.True(hasLimitations && hasSettings);
    }

    [Fact]
    public void Relations_DistinguishExternalTargetsAndPreservePages()
    {
        var artifact = Fixture();
        var symbol = artifact.Projects[0].Symbols[0];
        symbol.Relationships.Add(new("calls", "external:M:External.Run", "External.Run()", symbol.Source));
        var view = new CompactViews(artifact);
        using var first = JsonDocument.Parse(view.Render(Options("inspect", "s1", "relations", limit: 1)));
        Assert.Equal(1, first.RootElement.GetProperty("nextOffset").GetInt32());
        using var last = JsonDocument.Parse(view.Render(Options("inspect", "s1", "relations", offset: 1)));
        var external = last.RootElement.GetProperty("items")[0];
        Assert.False(external.GetProperty("available").GetBoolean());
        Assert.Equal("External.Run()", external.GetProperty("target").GetString());
        Assert.Equal(JsonValueKind.Null, last.RootElement.GetProperty("nextOffset").ValueKind);
    }

    [Fact]
    public void Reader_RejectsCompleteStatusContradictingKnownAnalysisGaps()
    {
        var artifact = Fixture();
        artifact.Projects[0].Diagnostics.Add(new("SOURCE_TRUNCATED", "warning", "Missing source."));
        Assert.Throws<InvalidDataException>(() => new CompactViews(artifact));
    }

    [Fact]
    public void Members_ExposePrivateStateWithoutRequiringCallEdges()
    {
        var artifact = Fixture();
        var project = artifact.Projects[0];
        var owner = new SymbolFact
        {
            Id = "T:Calculator", Name = "Calculator", DisplayName = "Calculator",
            Kind = "NamedType", Accessibility = "Public", IsPublicApi = true,
            Source = new("Calculator.cs", 1, 20), Declaration = "public class Calculator"
        };
        foreach (var symbol in project.Symbols) symbol.ContainingSymbolId = owner.Id;
        project.Symbols.Add(owner);
        var view = new CompactViews(artifact);
        using var page = JsonDocument.Parse(view.Render(Options("inspect", owner.Id, "members")));
        Assert.Equal(2, page.RootElement.GetProperty("total").GetInt32());
        var privateMember = page.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("accessibility").GetString() == "Private");
        var alias = privateMember.GetProperty("ref").GetString();
        Assert.Contains("DivideByZeroException", view.Render(Options("inspect", alias)));
    }

    [Fact]
    public async Task Cli_ProvidesReadOnlyCompactViewsAndHelp()
    {
        var path = Path.GetTempFileName();
        try
        {
            var original = JsonSerializer.Serialize(Fixture(), ArtifactJson.Options);
            await File.WriteAllTextAsync(path, original);
            var result = await RunCli("index", "--input", path);
            Assert.Equal(0, result.Code);
            Assert.Empty(result.Error);
            using var json = JsonDocument.Parse(result.Output);
            Assert.Equal(1, json.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            var help = await RunCli("--help");
            Assert.Contains("inspect", help.Output);
            Assert.Contains("--allow-project-execution", help.Output);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"\\uD800\":1}")]
    public async Task Cli_RejectsMalformedArtifactsWithoutSuccessOutput(string input)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, input);
            var result = await RunCli("context", "--input", path);
            Assert.Equal(1, result.Code);
            Assert.Empty(result.Output);
            Assert.Contains("failed", result.Error);
            Assert.DoesNotContain("Unhandled exception", result.Error);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("schemaVersion", "\"2.0\"")]
    [InlineData("status", "\"unknown\"")]
    [InlineData("projects", "null")]
    [InlineData("projects", "[null]")]
    [InlineData("diagnostics", "[null]")]
    [InlineData("limitations", "[null]")]
    [InlineData("taskId", "\"\"")]
    public async Task Reader_RejectsInvalidCanonicalFacts(string property, string value)
    {
        var input = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Fixture(), ArtifactJson.Options))!;
        input[property] = System.Text.Json.Nodes.JsonNode.Parse(value);
        await AssertInvalidArtifact(input.ToJsonString());
    }

    [Fact]
    public async Task Reader_RejectsMissingDefaultedDuplicateAndNoncanonicalProperties()
    {
        var original = JsonSerializer.Serialize(Fixture(), ArtifactJson.Options);
        var input = System.Text.Json.Nodes.JsonNode.Parse(original)!;
        input["projects"]![0]!["symbols"]![0]!.AsObject().Remove("isPublicApi");
        await AssertInvalidArtifact(input.ToJsonString());
        await AssertInvalidArtifact(original.Replace("\"taskId\": \"compact-test\"", "\"TaskId\": \"compact-test\""));
        await AssertInvalidArtifact(original.Replace("\"taskId\": \"compact-test\"", "\"taskId\":\"other\",\"taskId\":\"compact-test\""));
        await AssertInvalidArtifact(original.Replace("\"startLine\": 4", "\"startLine\": \"4\""));
    }

    [Fact]
    public void Parse_CompactCommandsNeverRequireProjectExecution()
    {
        var options = CompactViewOptions.Parse(["inspect", "--input", "facts.json", "--symbol", "s2",
            "--part", "raw", "--offset", "12", "--limit", "3", "--max-chars", "101", "--max-bytes", "1024"]);
        Assert.Equal(Options("inspect", "s2", "raw", 12, 3, 101, 1024) with { Input = "facts.json" }, options);
    }

    [Theory]
    [InlineData("index", "--symbol", "s1")]
    [InlineData("index", "--part", "code")]
    [InlineData("index", "--max-chars", "2000")]
    [InlineData("context", "--limit", "0")]
    [InlineData("context", "--offset", "-1")]
    [InlineData("context", "--max-bytes", "100000")]
    [InlineData("context", "--unknown", "x")]
    [InlineData("context", "--input", "again.json")]
    [InlineData("inspect", "--part", "unknown")]
    public void Parse_RejectsMisleadingOrInvalidOptions(string command, string key, string value)
    {
        Assert.Throws<ArgumentException>(() => CompactViewOptions.Parse([command, "--input", "facts.json", key, value]));
    }

    private static async Task AssertInvalidArtifact(string json)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, json);
            await Assert.ThrowsAsync<InvalidDataException>(() => CompactViews.ReadAndRenderAsync(Options() with { Input = path }));
        }
        finally { File.Delete(path); }
    }

    private static async Task<(int Code, string Output, string Error)> RunCli(params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(CompactViews).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return (process.ExitCode, await output, await error);
    }

    [Theory]
    [InlineData("public int A() { /* explanation */ return 1 + 2; }")]
    [InlineData("public string A() => \"https://host/*not trivia*/\"; // end")]
    [InlineData("public int A(int x) => x + +x;")]
    [InlineData("public string A(int x) => $\"x = {x}\";")]
    [InlineData("public string A() => \"\"\"//literal\"\"\";")]
    public void CompactCode_RemovesTriviaButPreservesLexicalTokens(string source)
    {
        var compact = SourceCompactor.Compact(source);
        Assert.True(compact.Compacted);
        var expected = SyntaxFactory.ParseTokens(source).Select(t => (t.RawKind, t.Text));
        var actual = SyntaxFactory.ParseTokens(compact.Text).Select(t => (t.RawKind, t.Text));
        Assert.Equal(expected, actual);
        Assert.DoesNotContain("/* explanation */", compact.Text);
    }

    [Theory]
    [InlineData("#nullable enable\npublic string? A() => null;")]
    [InlineData("#if SPECIAL\npublic int A() => 1;\n#else\npublic int A() => 2;\n#endif")]
    [InlineData("public string A() => \"unterminated")]
    public void CompactCode_PreservesDirectiveOrMalformedExcerptsVerbatim(string source)
    {
        var compact = SourceCompactor.Compact(source);
        Assert.False(compact.Compacted);
        Assert.Equal(source, compact.Text);
    }
}
