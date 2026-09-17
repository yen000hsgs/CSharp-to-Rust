using System.Text.Json;
using System.Text.Json.Nodes;
using CSharpToRust.Contracts;

namespace Pipeline.Contracts.Tests;

public sealed class FeatureDocumentJsonTests
{
    [Fact]
    public void DocumentWireShapeContainsOnlyPrCompatibleSourceAndFeatures()
    {
        var document = new FeatureDocument
        {
            Source = new() { Root = "test-source", Kind = "sdk" },
            Features =
            [
                new()
                {
                    Id = "odd.do_sth_uncommon", Name = "doSthUncommon", Signature = "object doSthUncommon(string value)",
                    Summary = "Explicit test-authored content.", Visibility = "public",
                    Parameters = [new("value", "string", [])], Returns = new("object", "The fixture result."),
                    Examples =
                    [
                        new("odd.do_sth_uncommon.x1",
                            JsonSerializer.Deserialize<JsonElement>("""{"empty":"","number":123456789012345678901234567890,"decimal":"0.1000000000000000000000000000"}"""),
                            JsonSerializer.Deserialize<JsonElement>("null"))
                    ],
                    SourceRefs = ["Odd.cs#L1"]
                }
            ]
        };
        var json = JsonSerializer.Serialize(document, ArtifactJson.Options);
        var node = JsonNode.Parse(json)!;
        Assert.Equal(new[] { "source", "features" }, node.AsObject().Select(entry => entry.Key));
        Assert.Equal(new[] { "language", "kind", "root" }, node["source"]!.AsObject().Select(entry => entry.Key));
        Assert.NotNull(node["features"]![0]!["params"]);
        Assert.NotNull(node["features"]![0]!["source_refs"]);
        Assert.Null(node["features"]![0]!["parameters"]);
        Assert.Null(node["features"]![0]!["sourceRefs"]);
        var roundTrip = JsonSerializer.Deserialize<FeatureDocument>(json, ArtifactJson.Options)!;
        var example = Assert.Single(Assert.Single(roundTrip.Features).Examples);
        Assert.Equal(JsonValueKind.Null, example.Expected.ValueKind);
        Assert.Equal("123456789012345678901234567890", example.Input.GetProperty("number").GetRawText());
        Assert.Equal("0.1000000000000000000000000000", example.Input.GetProperty("decimal").GetString());
        Assert.Equal("", example.Input.GetProperty("empty").GetString());
        Assert.Equal(json, JsonSerializer.Serialize(roundTrip, ArtifactJson.Options));
    }

    [Fact]
    public void OptionalSourceNamesAndVersionedContextHaveExplicitWireNames()
    {
        var source = new DocumentSource { Language = "csharp", Kind = "library", Root = ".", Name = "Odd", TargetCrate = "odd_crate" };
        var json = JsonSerializer.Serialize(source, ArtifactJson.Options);
        Assert.Contains("\"target_crate\": \"odd_crate\"", json);
        Assert.DoesNotContain("targetCrate", json);
        var context = new DocumentContext
        {
            TaskId = "test", ExtractionId = "fixture", DocumentPath = "document.json",
            UpstreamStatus = "partial", EvidenceReferences = ["extraction.json"]
        };
        var node = JsonNode.Parse(JsonSerializer.Serialize(context, ArtifactJson.Options))!;
        Assert.Equal("2.0", node["schemaVersion"]!.GetValue<string>());
        Assert.Equal(new[]
        {
            "schemaVersion", "taskId", "extractionId", "status", "upstreamStatus", "documentPath",
            "evidenceReferences", "featureEvidence", "coverage", "openQuestions", "portingGuide"
        }, node.AsObject().Select(entry => entry.Key));
        Assert.Null(node["portingGuide"]);
        Assert.Equal("1.0", ArtifactJson.SchemaVersion);
    }
}
