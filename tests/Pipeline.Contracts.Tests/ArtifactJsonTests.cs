using CSharpToRust.Contracts;
using System.Text.Json;

namespace Pipeline.Contracts.Tests;

public class ArtifactJsonTests
{
    [Fact]
    public void RoundTrip_PreservesVersionAndSourceEvidence()
    {
        var artifact = new ExtractionArtifact
        {
            TaskId = "task-1",
            ExtractionId = "snapshot-1",
            InputPath = "Calculator.csproj",
            RootDirectory = "sample",
            Status = "complete",
            Projects =
            [
                new ProjectFact
                {
                    Id = "project-1", Name = "Calculator", File = "Calculator.csproj",
                    Symbols =
                    [
                        new SymbolFact
                        {
                            Id = "project-1:M:Calculator.Add", Kind = "Method",
                            Name = "Add", DisplayName = "Calculator.Add(decimal, decimal)",
                            Accessibility = "Public", IsPublicApi = true,
                            Source = new SourceReference("Calculator.cs", 5, 5),
                            Declaration = "public static decimal Add(decimal a, decimal b) => a + b;"
                        }
                    ]
                }
            ]
        };

        var json = JsonSerializer.Serialize(artifact, ArtifactJson.Options);
        var result = JsonSerializer.Deserialize<ExtractionArtifact>(json, ArtifactJson.Options)!;

        Assert.Contains("\"schemaVersion\": \"1.0\"", json);
        Assert.Equal(artifact.Projects[0].Symbols[0].Source, result.Projects[0].Symbols[0].Source);
    }

    [Fact]
    public void UnknownProperty_IsRejected()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<SourceReference>(
                """{"file":"A.cs","startLine":1,"endLine":2,"invented":true}""",
                ArtifactJson.Options));
    }
}
