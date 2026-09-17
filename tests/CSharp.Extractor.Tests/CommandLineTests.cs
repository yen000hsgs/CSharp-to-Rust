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
}
