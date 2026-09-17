using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSharpToRust.Contracts;

public static class ArtifactJson
{
    public const string SchemaVersion = "1.0";

    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}

public sealed record SourceReference(string File, int StartLine, int EndLine);
public sealed record ParameterFact(string Name, string Type, string RefKind, bool IsOptional, string? DefaultValue);
public sealed record DiagnosticFact(string Code, string Severity, string Message, SourceReference? Source = null);
public sealed record RelationshipFact(string Kind, string TargetId, string TargetDisplay, SourceReference Source);

public sealed class ExtractionArtifact
{
    public string SchemaVersion { get; set; } = ArtifactJson.SchemaVersion;
    public string TaskId { get; set; } = "";
    public string ExtractionId { get; set; } = "";
    public string InputPath { get; set; } = "";
    public string RootDirectory { get; set; } = "";
    public string Configuration { get; set; } = "Debug";
    public string? RequestedFramework { get; set; }
    public string Status { get; set; } = "partial";
    public List<ProjectFact> Projects { get; set; } = [];
    public List<DiagnosticFact> Diagnostics { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
}

public sealed class ProjectFact
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    public string? TargetFramework { get; set; }
    public string OutputKind { get; set; } = "";
    public string LanguageVersion { get; set; } = "";
    public string NullableContext { get; set; } = "";
    public bool CheckOverflow { get; set; }
    public List<string> Defines { get; set; } = [];
    public List<string> ProjectReferences { get; set; } = [];
    public List<string> AssemblyReferences { get; set; } = [];
    public List<SymbolFact> Symbols { get; set; } = [];
    public List<DiagnosticFact> Diagnostics { get; set; } = [];
}

public sealed class SymbolFact
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? ContainingSymbolId { get; set; }
    public string Accessibility { get; set; } = "";
    public bool IsPublicApi { get; set; }
    public bool IsStatic { get; set; }
    public bool IsAsync { get; set; }
    public string? Type { get; set; }
    public string Documentation { get; set; } = "";
    public List<string> Attributes { get; set; } = [];
    public List<ParameterFact> Parameters { get; set; } = [];
    public SourceReference Source { get; set; } = new("", 0, 0);
    public List<SourceReference> AdditionalSources { get; set; } = [];
    public string Declaration { get; set; } = "";
    public List<RelationshipFact> Relationships { get; set; } = [];
    public List<string> MigrationSignals { get; set; } = [];
}

public sealed class RequirementsArtifact
{
    public string SchemaVersion { get; set; } = ArtifactJson.SchemaVersion;
    public string TaskId { get; set; } = "";
    public string ExtractionId { get; set; } = "";
    public string Status { get; set; } = "draft";
    public List<Requirement> Requirements { get; set; } = [];
    public List<CoverageDecision> Coverage { get; set; } = [];
    public List<OpenQuestion> OpenQuestions { get; set; } = [];
}

public sealed class Requirement
{
    public string Id { get; set; } = "";
    public string Feature { get; set; } = "";
    public string Statement { get; set; } = "";
    public string Confidence { get; set; } = "inferred";
    public List<string> EvidenceIds { get; set; } = [];
    public List<string> Inputs { get; set; } = [];
    public List<string> Outputs { get; set; } = [];
    public List<string> Preconditions { get; set; } = [];
    public List<string> Postconditions { get; set; } = [];
    public List<string> SideEffects { get; set; } = [];
    public List<string> ErrorBehavior { get; set; } = [];
    public List<string> AcceptanceCriteria { get; set; } = [];
    public List<string> MigrationNotes { get; set; } = [];
    public List<string> DependsOn { get; set; } = [];
}

public sealed record CoverageDecision(string SymbolId, string Disposition, string Reason);
public sealed record OpenQuestion(string Id, string Question, List<string> EvidenceIds);
