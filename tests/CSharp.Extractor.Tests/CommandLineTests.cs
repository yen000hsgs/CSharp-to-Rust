using System.Diagnostics;
using System.Text.Json;
using CSharpToRust.Contracts;
using CSharpToRust.Extraction;

namespace CSharp.Extractor.Tests;

public class CommandLineTests
{
    [Fact]
    public void Parse_RequiresExplicitTrustBeforeProjectExecution()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            ExtractorOptions.Parse(["extract", "--input", "Example.csproj", "--output", "out.json"]));
        Assert.Contains("--allow-project-execution", exception.Message);
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--input")]
    public void Parse_RejectsInvalidArguments(string argument)
    {
        Assert.Throws<ArgumentException>(() => ExtractorOptions.Parse(["extract", argument]));
    }

    [Fact]
    public void Parse_PreservesConfigurationAndFramework()
    {
        var options = ExtractorOptions.Parse([
            "extract", "--input", "Example.csproj", "--output", "out.json",
            "--task-id", "task-1", "--configuration", "Release", "--framework", "net8.0",
            "--allow-project-execution"]);

        Assert.Equal("Release", options.Configuration);
        Assert.Equal("net8.0", options.Framework);
        Assert.Equal("task-1", options.TaskId);
    }

    [Fact]
    public async Task ExtractAsync_DirectCallRequiresExecutionApproval()
    {
        var options = new ExtractorOptions("missing.csproj", "out.json", "task", "Debug", null);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => ProjectExtractor.ExtractAsync(options));

        Assert.Contains("approval", error.Message);
    }

    [Fact]
    public void BuiltCli_KeepsNet8TargetAndRollsForwardForNewerSdkDiscovery()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.ChangeExtension(ExtractorAssemblyPath, ".runtimeconfig.json")));
        var options = document.RootElement.GetProperty("runtimeOptions");

        Assert.Equal("net8.0", options.GetProperty("tfm").GetString());
        Assert.True(options.TryGetProperty("rollForward", out var rollForward),
            "The shipped CLI must select a runtime that can load newer installed SDKs even when runtime 8 is present.");
        Assert.Equal("LatestMajor", rollForward.GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuiltCli_ExtractsTrustedCalculatorWithoutRuntimeOverrides(bool useAppHost)
    {
        var directory = Path.Combine(RepositoryRoot, "artifacts", "extractor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var outputPath = Path.Combine(directory, "extraction.json");
        try
        {
            var executable = useAppHost
                ? Path.ChangeExtension(ExtractorAssemblyPath, OperatingSystem.IsWindows() ? ".exe" : null)
                : "dotnet";
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = RepositoryRoot, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var variable in new[]
            {
                "DOTNET_ROLL_FORWARD", "DOTNET_ROLL_FORWARD_TO_PRERELEASE",
                "DOTNET_ROLL_FORWARD_ON_NO_CANDIDATE_FX"
            })
                start.Environment.Remove(variable);
            if (!useAppHost) start.ArgumentList.Add(ExtractorAssemblyPath);
            foreach (var argument in new[]
            {
                "extract", "--input", Path.Combine(RepositoryRoot, "samples", "Calculator", "Calculator.csproj"),
                "--output", outputPath, "--task-id", "trusted-sample-smoke", "--allow-project-execution"
            })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw;
            }
            var error = await stderr;
            Assert.True(process.ExitCode == 0, $"Exit {process.ExitCode}: {error}");
            Assert.Empty(error);
            using var summary = JsonDocument.Parse(await stdout);
            Assert.Equal("complete", summary.RootElement.GetProperty("status").GetString());
            var artifact = JsonSerializer.Deserialize<ExtractionArtifact>(
                await File.ReadAllTextAsync(outputPath), ArtifactJson.Options)!;
            Assert.Equal("complete", artifact.Status);
            Assert.Empty(artifact.Diagnostics);
            Assert.NotEmpty(artifact.Limitations);
            var project = Assert.Single(artifact.Projects);
            Assert.Empty(project.Diagnostics);
            Assert.Equal(".NETCoreApp,Version=v8.0", project.TargetFramework);
            Assert.Equal(new[] { "Add", "Calculator", "Divide", "Multiply", "Subtract" },
                project.Symbols.Select(symbol => symbol.Name).Order(StringComparer.Ordinal));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string RepositoryRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null && !File.Exists(Path.Combine(current.FullName, "CSharpToRust.sln")))
                current = current.Parent;
            return current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }

    private static string ExtractorAssemblyPath => Path.Combine(RepositoryRoot, "src", "CSharp.Extractor", "bin",
        new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net8.0", "CSharp.Extractor.dll");
}
