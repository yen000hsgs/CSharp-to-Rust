using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSharpToRust.Contracts;

namespace Requirements.Collector.Tests;

// Covers the hand-off workflow end to end rather than path-helper equality alone: a package prepared
// in one checkout must still validate after it moves to a checkout at a different nesting depth.
// Extraction identity being location-independent is not sufficient on its own, because the document,
// context, and distribution plan each carry their own path bindings.
public sealed class PortableHandoffTests : IDisposable
{
    private readonly string root;
    private readonly string workspace;
    private readonly string checkoutA;
    private readonly string checkoutB;

    public PortableHandoffTests()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "samples", "Calculator.sln")))
            current = current.Parent;
        root = current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        workspace = Path.Combine(root, "artifacts", "portable-handoff-tests", Guid.NewGuid().ToString("N"));
        // Deliberately different nesting depths, so a leaked absolute path cannot coincidentally agree.
        checkoutA = Path.Combine(workspace, "a");
        checkoutB = Path.Combine(workspace, "deep", "nested", "b");
        foreach (var checkout in new[] { checkoutA, checkoutB })
        {
            Directory.CreateDirectory(checkout);
            Save(Path.Combine(checkout, "extraction.json"), CalculatorEvidenceFixture.Create());
        }
    }

    [Fact]
    public void PreparedPackageValidatesAfterRelocationToAnotherCheckout()
    {
        PrepareIn(checkoutA);
        Success(Validate(checkoutA));

        Relocate("extraction.json", "document.json", "document.context.json");

        Success(Validate(checkoutB));
    }

    [Fact]
    public void DistributionPlanValidatesAfterRelocationToAnotherCheckout()
    {
        PrepareIn(checkoutA);
        Success(Run(checkoutA, "prepare-distribution", "--input", In(checkoutA, "extraction.json"),
            "--document", In(checkoutA, "document.json"), "--context", In(checkoutA, "document.context.json"),
            "--output", In(checkoutA, "distribution.json")));

        Relocate("extraction.json", "document.json", "document.context.json", "distribution.json");

        Success(Run(checkoutB, "validate-distribution", "--input", In(checkoutB, "extraction.json"),
            "--document", In(checkoutB, "document.json"), "--context", In(checkoutB, "document.context.json"),
            "--distribution", In(checkoutB, "distribution.json")));
    }

    [Fact]
    public void PersistedBindingsCarryNoAbsolutePaths()
    {
        PrepareIn(checkoutA);
        Success(Run(checkoutA, "prepare-distribution", "--input", In(checkoutA, "extraction.json"),
            "--document", In(checkoutA, "document.json"), "--context", In(checkoutA, "document.context.json"),
            "--output", In(checkoutA, "distribution.json")));

        var document = Read(In(checkoutA, "document.json"));
        var context = Read(In(checkoutA, "document.context.json"));
        var plan = Read(In(checkoutA, "distribution.json"));

        NotFullyQualified(document["source"]!["root"]!.GetValue<string>(), "source.root");
        NotFullyQualified(context["documentPath"]!.GetValue<string>(), "documentPath");
        foreach (var reference in context["evidenceReferences"]!.AsArray())
            NotFullyQualified(reference!.GetValue<string>(), "evidenceReferences[]");
        NotFullyQualified(plan["documentPath"]!.GetValue<string>(), "distribution documentPath");
        NotFullyQualified(plan["contextPath"]!.GetValue<string>(), "distribution contextPath");

        // A relative binding is only useful if it still resolves to the real file it names.
        Assert.Equal(In(checkoutA, "document.json"),
            Path.GetFullPath(context["documentPath"]!.GetValue<string>(), checkoutA));
        Assert.Equal(In(checkoutA, "document.context.json"),
            Path.GetFullPath(plan["contextPath"]!.GetValue<string>(), checkoutA));
    }

    [Fact]
    public void RelocationStillRejectsAPackageBelongingToADifferentExtraction()
    {
        PrepareIn(checkoutA);
        var other = CalculatorEvidenceFixture.Create();
        other.TaskId = "a-different-task";
        CalculatorEvidenceFixture.RefreshIdentity(other);
        Save(In(checkoutB, "extraction.json"), other);
        foreach (var name in new[] { "document.json", "document.context.json" })
            File.Copy(In(checkoutA, name), In(checkoutB, name), overwrite: true);

        // Portability must not become a way to pair a document with an unrelated snapshot.
        Invalid(Validate(checkoutB));
    }

    private void PrepareIn(string checkout) =>
        Success(Run(checkout, "prepare", "--input", In(checkout, "extraction.json"),
            "--output", In(checkout, "document.json"), "--context", In(checkout, "document.context.json")));

    private Result Validate(string checkout) =>
        Run(checkout, "validate", "--input", In(checkout, "extraction.json"),
            "--document", In(checkout, "document.json"), "--context", In(checkout, "document.context.json"));

    private void Relocate(params string[] names)
    {
        foreach (var name in names)
            File.Copy(In(checkoutA, name), In(checkoutB, name), overwrite: true);
    }

    private static void NotFullyQualified(string value, string name) =>
        Assert.False(Path.IsPathFullyQualified(value), $"{name} must stay relative for hand-off, but was '{value}'.");

    private static string In(string checkout, string name) => Path.Combine(checkout, name);
    private static JsonNode Read(string path) => JsonNode.Parse(File.ReadAllText(path))!;
    private static void Save<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, ArtifactJson.Options));

    private string CollectorAssemblyPath => Path.Combine(root, "src", "Requirements.Collector", "bin",
        new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net8.0", "Requirements.Collector.dll");

    private Result Run(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(CollectorAssemblyPath);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30000), "Collector timed out.");
        return new(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static void Success(Result result) =>
        Assert.True(result.ExitCode == 0, $"Exit {result.ExitCode}: {result.Stderr}\n{result.Stdout}");

    private static void Invalid(Result result) =>
        Assert.True(result.ExitCode == 2, $"Expected exit 2, got {result.ExitCode}: {result.Stderr}\n{result.Stdout}");

    public void Dispose() => Directory.Delete(workspace, recursive: true);
    private sealed record Result(int ExitCode, string Stdout, string Stderr);
}
