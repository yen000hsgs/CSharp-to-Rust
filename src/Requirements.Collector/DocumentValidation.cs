using System.Text.Json;
using System.Text.RegularExpressions;
using CSharpToRust.Contracts;
using static Requirements.Collector.ArtifactValidation;

namespace Requirements.Collector;

internal static class DocumentValidation
{
    private static readonly Regex FeatureId = new(@"\A[a-z_][a-z0-9_]*(\.[a-z_][a-z0-9_]*)+\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Sequence = new(@"\A[1-9][0-9]*\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static bool Validate(FeatureDocument document, DocumentContext context, ExtractionArtifact extraction,
        IReadOnlyDictionary<string, SymbolFact> symbols, string inputPath, string documentPath, string contextPath)
    {
        Require(document.Source is not null, "source must be a non-null object.");
        Choice(document.Source.Language, "source.language", "csharp");
        Choice(document.Source.Kind, "source.kind", "library", "sdk", "application", "solution");
        PathText(document.Source.Root, "source.root");
        Require(SafeOutput.PathComparer.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(document.Source.Root, DirectoryOf(documentPath))),
            Path.TrimEndingDirectorySeparator(SafeOutput.SourceRoot(inputPath, extraction))),
            "source.root does not match the extraction source root.");
        OptionalText(document.Source.Name, "source.name");
        OptionalText(document.Source.TargetCrate, "source.target_crate");
        Items(document.Features, "features");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var features = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in document.Features)
        {
            Text(feature.Id, "feature.id");
            Require(FeatureId.IsMatch(feature.Id), $"Feature ID '{feature.Id}' must be dot-scoped lowercase (underscores allowed).");
            Require(ids.Add(feature.Id), $"Duplicate feature ID '{feature.Id}'.");
            features.Add(feature.Id);
        }
        foreach (var feature in document.Features)
            ValidateFeature(feature, ids);

        Require(context.SchemaVersion == DocumentContext.CurrentSchemaVersion, "Context schemaVersion must be '2.0'.");
        Require(context.TaskId == extraction.TaskId, "Context taskId does not match extraction.");
        Require(context.ExtractionId == extraction.ExtractionId, "Context extractionId does not match extraction.");
        Choice(context.Status, "status", "draft", "partial", "blocked", "complete");
        Choice(context.UpstreamStatus, "upstreamStatus", "complete", "partial");
        Require(extraction.Status == "complete" || context.UpstreamStatus == "partial",
            "Context upstreamStatus cannot be complete when compiler extraction is partial.");
        PathText(context.DocumentPath, "documentPath");
        Require(SafeOutput.PathComparer.Equals(Path.GetFullPath(context.DocumentPath, DirectoryOf(contextPath)),
            Path.GetFullPath(documentPath)), "Context documentPath does not match the supplied document.");
        UniqueStrings(context.EvidenceReferences, "evidenceReferences");
        foreach (var reference in context.EvidenceReferences)
            PathText(reference, "evidenceReferences[]");
        Items(context.FeatureEvidence, "featureEvidence");
        var linkedFeatures = new HashSet<string>(StringComparer.Ordinal);
        var evidence = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in context.FeatureEvidence)
        {
            Text(entry.FeatureId, "featureEvidence.featureId");
            Require(features.Contains(entry.FeatureId), $"Unknown featureEvidence feature '{entry.FeatureId}'.");
            Require(linkedFeatures.Add(entry.FeatureId), $"Duplicate featureEvidence for '{entry.FeatureId}'.");
            Choice(entry.Confidence, $"{entry.FeatureId}.confidence", "confirmed", "inferred", "uncertain");
            Evidence(entry.EvidenceIds, symbols, $"{entry.FeatureId}.evidenceIds");
            Require(entry.EvidenceIds.Count > 0, $"{entry.FeatureId} requires evidenceIds.");
            evidence.UnionWith(entry.EvidenceIds);
        }
        Require(features.SetEquals(linkedFeatures), "Every feature requires exactly one featureEvidence entry.");
        Traceability(context.Coverage, context.OpenQuestions, symbols, evidence);
        if (context.PortingGuide is not null)
        {
            PathText(context.PortingGuide.Path, "portingGuide.path");
            Text(context.PortingGuide.Revision, "portingGuide.revision");
        }

        var hasRequirements = document.Features.Count > 0 && document.Features.All(feature => RequirementCount(feature) > 0);
        Require(context.Status == "draft" || hasRequirements,
            "A non-draft document must contain features with at least one atomic requirement per feature.");
        var resolved = hasRequirements && extraction.Status == "complete" && context.UpstreamStatus == "complete"
            && context.Coverage.All(decision => decision.Disposition != "pending")
            && context.OpenQuestions.Count == 0
            && context.FeatureEvidence.All(entry => entry.Confidence != "uncertain")
            && context.PortingGuide?.Approved != false;
        Require(context.Status != "complete" || resolved,
            "Complete context requires nonempty atomic requirements, complete upstream evidence, no pending coverage, questions, uncertain evidence, or unapproved supplied guide.");
        return context.Status == "complete" && resolved;
    }

    public static int RequirementCount(DocumentFeature feature) =>
        feature.Behaviors.Count + feature.Errors.Count + feature.Invariants.Count + feature.Examples.Count;

    public static string DirectoryOf(string path) => Path.GetDirectoryName(Path.GetFullPath(path))!;

    private static void ValidateFeature(DocumentFeature feature, HashSet<string> ids)
    {
        Text(feature.Name, $"{feature.Id}.name");
        Text(feature.Signature, $"{feature.Id}.signature");
        Text(feature.Summary, $"{feature.Id}.summary");
        Text(feature.Visibility, $"{feature.Id}.visibility");
        Strings(feature.SourceRefs, $"{feature.Id}.source_refs");
        foreach (var reference in feature.SourceRefs)
            PathText(reference.Split('#', 2)[0], $"{feature.Id}.source_refs[]");
        Items(feature.Parameters, $"{feature.Id}.params");
        var parameterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in feature.Parameters)
        {
            Text(parameter.Name, $"{feature.Id}.param.name");
            Require(parameterNames.Add(parameter.Name), $"{feature.Id} has duplicate parameter '{parameter.Name}'.");
            Text(parameter.Type, $"{feature.Id}.param.type");
            Strings(parameter.Constraints, $"{feature.Id}.{parameter.Name}.constraints");
        }
        Require(feature.Returns is not null, $"{feature.Id}.returns must be a non-null object.");
        Text(feature.Returns.Type, $"{feature.Id}.returns.type");
        Text(feature.Returns.Description, $"{feature.Id}.returns.description");
        Items(feature.Behaviors, $"{feature.Id}.behaviors");
        Items(feature.Errors, $"{feature.Id}.errors");
        Items(feature.Invariants, $"{feature.Id}.invariants");
        Items(feature.Examples, $"{feature.Id}.examples");
        foreach (var behavior in feature.Behaviors)
        {
            AtomicId(behavior.Id, feature.Id, 'b', ids);
            Text(behavior.Statement, $"{behavior.Id}.statement");
        }
        foreach (var error in feature.Errors)
        {
            AtomicId(error.Id, feature.Id, 'e', ids);
            Text(error.Condition, $"{error.Id}.condition");
            Text(error.Result, $"{error.Id}.result");
        }
        foreach (var invariant in feature.Invariants)
        {
            AtomicId(invariant.Id, feature.Id, 'i', ids);
            Text(invariant.Statement, $"{invariant.Id}.statement");
        }
        foreach (var example in feature.Examples)
        {
            AtomicId(example.Id, feature.Id, 'x', ids);
            Require(example.Input.ValueKind == JsonValueKind.Object, $"{example.Id}.input must be a JSON object.");
            Require(example.Expected.ValueKind != JsonValueKind.Undefined, $"{example.Id}.expected is required (null is allowed).");
            foreach (var parameter in feature.Parameters.Where(parameter => IsDecimal(parameter.Type)))
                if (example.Input.TryGetProperty(parameter.Name, out var value))
                    Require(value.ValueKind is JsonValueKind.String or JsonValueKind.Null,
                        $"{example.Id}.input.{parameter.Name}: decimal values must be JSON strings, not numbers.");
            if (IsDecimal(feature.Returns.Type))
            {
                var value = example.Expected;
                var explicitError = value.ValueKind == JsonValueKind.Object
                    && value.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
                Require(value.ValueKind is JsonValueKind.String or JsonValueKind.Null || explicitError,
                    $"{example.Id}.expected: decimal values must be JSON strings, not numbers (explicit error objects are allowed).");
            }
        }
    }

    private static bool IsDecimal(string type) => type.Replace("global::", "", StringComparison.Ordinal).TrimEnd('?')
        is "decimal" or "System.Decimal";

    private static void AtomicId(string id, string feature, char kind, HashSet<string> ids)
    {
        Text(id, $"{feature}.requirement.id");
        var prefix = $"{feature}.{kind}";
        Require(id.StartsWith(prefix, StringComparison.Ordinal) && Sequence.IsMatch(id[prefix.Length..]),
            $"Requirement ID '{id}' must be scoped under '{feature}' as '.{kind}N' with a positive integer N.");
        Require(ids.Add(id), $"Duplicate document ID '{id}'.");
    }
}
