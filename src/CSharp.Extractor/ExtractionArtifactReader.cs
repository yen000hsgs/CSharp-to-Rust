using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using CSharpToRust.Contracts;

namespace CSharpToRust.Extraction;

internal static class ExtractionArtifactReader
{
    public static async Task<ExtractionArtifact> ReadAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > 64 * 1024 * 1024)
            throw new InvalidDataException("Extraction exceeds the 64 MiB input limit.");
        using var document = await JsonDocument.ParseAsync(stream);
        var artifact = document.RootElement.Deserialize<ExtractionArtifact>(ArtifactJson.Options)
            ?? throw new InvalidDataException("Extraction must not be null.");
        EnsureCanonicalShape(document.RootElement, JsonSerializer.SerializeToElement(artifact, ArtifactJson.Options));
        Validate(artifact);
        return artifact;
    }

    private static void EnsureCanonicalShape(JsonElement input, JsonElement canonical)
    {
        if (input.ValueKind != canonical.ValueKind)
            throw new InvalidDataException("Use canonical extraction JSON types.");
        if (canonical.ValueKind == JsonValueKind.Object)
        {
            if (input.EnumerateObject().Count() != canonical.EnumerateObject().Count())
                throw new InvalidDataException("Extraction contains missing or duplicate fields.");
            foreach (var property in canonical.EnumerateObject())
            {
                if (!input.TryGetProperty(property.Name, out var value))
                    throw new InvalidDataException($"Missing canonical field: {property.Name}");
                EnsureCanonicalShape(value, property.Value);
            }
        }
        else if (canonical.ValueKind == JsonValueKind.Array)
        {
            if (input.GetArrayLength() != canonical.GetArrayLength())
                throw new InvalidDataException("Inconsistent extraction arrays.");
            for (var i = 0; i < canonical.GetArrayLength(); i++)
                EnsureCanonicalShape(input[i], canonical[i]);
        }
    }

    public static void Validate(ExtractionArtifact artifact)
    {
        static void Require([DoesNotReturnIf(false)] bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }
        static bool Text(string? text) => !string.IsNullOrWhiteSpace(text);
        static bool Strings(List<string>? values) => values is not null && values.All(Text);
        static bool Source(SourceReference? source) => source is not null && Text(source.File) &&
            source.StartLine > 0 && source.EndLine >= source.StartLine;
        static bool Diagnostics(List<DiagnosticFact>? diagnostics) => diagnostics is not null &&
            diagnostics.All(d => d is not null && Text(d.Code) && Text(d.Severity) && Text(d.Message) &&
                (d.Source is null || Source(d.Source)));

        Require(artifact is not null && artifact.SchemaVersion == ArtifactJson.SchemaVersion, "Unsupported extraction schema.");
        Require(Text(artifact.TaskId) && Text(artifact.ExtractionId) && Text(artifact.InputPath) &&
            Text(artifact.RootDirectory) && Text(artifact.Configuration), "Extraction identity and configuration are required.");
        Require(artifact.Status is "complete" or "partial", "Extraction status must be complete or partial.");
        Require(artifact.Projects is not null && Strings(artifact.Limitations) && Diagnostics(artifact.Diagnostics),
            "Invalid extraction collections.");
        var projectIds = new HashSet<string>(StringComparer.Ordinal);
        var symbolIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in artifact.Projects)
        {
            Require(project is not null && Text(project.Id) && projectIds.Add(project.Id) &&
                Text(project.Name) && Text(project.File), "Missing or duplicate project identity.");
            Require(Text(project.OutputKind) && Text(project.LanguageVersion) && Text(project.NullableContext) &&
                Strings(project.Defines) && Strings(project.ProjectReferences) && Strings(project.AssemblyReferences) &&
                Diagnostics(project.Diagnostics) && project.Symbols is not null, "Invalid project facts.");
            foreach (var symbol in project.Symbols)
            {
                Require(symbol is not null && Text(symbol.Id) && symbolIds.Add(symbol.Id) && Text(symbol.Name) &&
                    Text(symbol.Kind) && Text(symbol.DisplayName) && Text(symbol.Accessibility), "Missing or duplicate symbol identity.");
                Require(Source(symbol.Source) && symbol.AdditionalSources is not null && symbol.AdditionalSources.All(Source) &&
                    symbol.Declaration is not null && symbol.Documentation is not null &&
                    Strings(symbol.Attributes) && Strings(symbol.MigrationSignals), "Invalid symbol evidence.");
                Require(symbol.Parameters is not null && symbol.Parameters.All(p =>
                    p is not null && Text(p.Name) && Text(p.Type) && Text(p.RefKind)), "Invalid parameter facts.");
                Require(symbol.Relationships is not null && symbol.Relationships.All(r =>
                    r is not null && Text(r.Kind) && Text(r.TargetId) && Text(r.TargetDisplay) && Source(r.Source)),
                    "Invalid relationship facts.");
            }
        }
        if (artifact.Status == "complete")
            Require(artifact.Projects.Count > 0 && !ExtractionStatus.HasAnalysisGaps(artifact),
                "Complete extraction contains analysis gaps or no projects.");
    }
}
