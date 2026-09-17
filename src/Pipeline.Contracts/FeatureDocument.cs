using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSharpToRust.Contracts;

public sealed class FeatureDocument
{
    public DocumentSource Source { get; set; } = new();
    public List<DocumentFeature> Features { get; set; } = [];
}

public sealed class DocumentSource
{
    public string Language { get; set; } = "csharp";
    public string Kind { get; set; } = "";
    public string Root { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("target_crate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetCrate { get; set; }
}

public sealed class DocumentFeature
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Signature { get; set; } = "";
    public string Summary { get; set; } = "";

    [JsonPropertyName("params")]
    public List<DocumentParameter> Parameters { get; set; } = [];

    public DocumentReturn Returns { get; set; } = new("", "");
    public List<DocumentStatement> Behaviors { get; set; } = [];
    public List<DocumentError> Errors { get; set; } = [];
    public List<DocumentStatement> Invariants { get; set; } = [];
    public List<DocumentExample> Examples { get; set; } = [];
    public string Visibility { get; set; } = "";

    [JsonPropertyName("source_refs")]
    public List<string> SourceRefs { get; set; } = [];
}

public sealed record DocumentParameter(string Name, string Type, List<string> Constraints);
public sealed record DocumentReturn(string Type, string Description);
public sealed record DocumentStatement(string Id, string Statement);
public sealed record DocumentError(string Id, string Condition, string Result);
public sealed record DocumentExample(string Id, JsonElement Input, JsonElement Expected);

public sealed class DocumentContext
{
    public const string CurrentSchemaVersion = "2.0";
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string TaskId { get; set; } = "";
    public string ExtractionId { get; set; } = "";
    public string Status { get; set; } = "draft";
    public string UpstreamStatus { get; set; } = "partial";
    public string DocumentPath { get; set; } = "";
    public List<string> EvidenceReferences { get; set; } = [];
    public List<FeatureEvidence> FeatureEvidence { get; set; } = [];
    public List<CoverageDecision> Coverage { get; set; } = [];
    public List<OpenQuestion> OpenQuestions { get; set; } = [];
    public PortingGuideReference? PortingGuide { get; set; }
}

public sealed record FeatureEvidence(string FeatureId, string Confidence, List<string> EvidenceIds);
public sealed record PortingGuideReference(string Path, string Revision, bool Approved);
