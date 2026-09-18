using CSharpToRust.Contracts;
using CSharpToRust.Extraction;

namespace CSharp.Extractor.Tests;

// Regression cover for the extraction identity being tied to the checkout location: the same
// commit extracted from two directories produced two ids, which blocked handing artifacts between
// machines and caching by id.
public sealed class ExtractionIdentityTests
{
    private static ExtractionArtifact Artifact(string rootDirectory) => new()
    {
        TaskId = "identity-test",
        InputPath = "Calculator.csproj",
        RootDirectory = rootDirectory,
        Configuration = "Debug",
        Status = "complete",
        Projects =
        [
            new ProjectFact
            {
                Id = "project:Calculator.csproj",
                Name = "Calculator",
                File = "Calculator.csproj",
                TargetFramework = "net8.0",
                OutputKind = "DynamicallyLinkedLibrary",
                LanguageVersion = "CSharp12",
                NullableContext = "Enable"
            }
        ]
    };

    [Fact]
    public void ExtractionIdIsIndependentOfCheckoutLocation()
    {
        var cloneA = ProjectExtractor.ComputeExtractionId(
            Artifact(Path.Combine("C:", "work", "cloneA", "samples", "Calculator")));
        var cloneB = ProjectExtractor.ComputeExtractionId(
            Artifact(Path.Combine("D:", "ci", "agent7", "_work", "cloneB", "samples", "Calculator")));

        Assert.Equal(cloneA, cloneB);
        Assert.Matches("^[0-9a-f]{64}$", cloneA);
    }

    [Fact]
    public void ExtractionIdStillChangesWhenExtractedContentChanges()
    {
        // Guards the fix from over-correcting into a constant: excluding rootDirectory must not
        // stop the id from binding everything else in the artifact.
        var baseline = Artifact("..");
        var changed = Artifact("..");
        changed.Projects[0].TargetFramework = "net9.0";

        Assert.NotEqual(ProjectExtractor.ComputeExtractionId(baseline),
            ProjectExtractor.ComputeExtractionId(changed));
    }

    [Fact]
    public void ComputeExtractionIdLeavesTheArtifactIntact()
    {
        var artifact = Artifact(Path.Combine("..", "..", "samples", "Calculator"));

        ProjectExtractor.ComputeExtractionId(artifact);

        Assert.Equal(Path.Combine("..", "..", "samples", "Calculator"), artifact.RootDirectory);
    }

    [Fact]
    public void PortableRootResolvesBackToTheSourcesFromTheArtifactDirectory()
    {
        var repository = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "portable-root-check"));
        var sources = Path.Combine(repository, "samples", "Calculator");
        var artifactPath = Path.Combine(repository, "artifacts", "extraction.json");

        var emitted = ProjectExtractor.PortableRoot(sources, artifactPath);

        Assert.False(Path.IsPathFullyQualified(emitted));
        // Mirrors how SafeOutput.SourceRoot rehydrates the path on the consuming machine.
        Assert.Equal(sources,
            Path.GetFullPath(emitted, Path.GetDirectoryName(Path.GetFullPath(artifactPath))!));
    }

    [Fact]
    public void PortableRootKeepsTheAbsolutePathWhenNoRelativePathExists()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sources = Path.Combine("C:", "work", "clone", "samples", "Calculator");

        var emitted = ProjectExtractor.PortableRoot(sources, Path.Combine("D:", "artifacts", "extraction.json"));

        Assert.Equal(sources, emitted);
    }
}
