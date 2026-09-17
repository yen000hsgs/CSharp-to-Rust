using System.Diagnostics.CodeAnalysis;
using CSharpToRust.Contracts;

namespace Requirements.Collector;

internal static class ArtifactValidation
{
    public static Dictionary<string, SymbolFact> Extraction(ExtractionArtifact artifact)
    {
        Version(artifact.SchemaVersion);
        Text(artifact.TaskId, "taskId");
        Text(artifact.ExtractionId, "extractionId");
        PathText(artifact.InputPath, "inputPath");
        PathText(artifact.RootDirectory, "rootDirectory");
        Text(artifact.Configuration, "configuration");
        OptionalText(artifact.RequestedFramework, "requestedFramework");
        Choice(artifact.Status, "status", "complete", "partial");
        Strings(artifact.Limitations, "limitations");
        var hasErrors = Diagnostics(artifact.Diagnostics, "diagnostics");
        Items(artifact.Projects, "projects");
        Require(artifact.Projects.Count > 0 || artifact.Status == "partial", "Complete extraction must contain a project.");
        var projectIds = new HashSet<string>(StringComparer.Ordinal);
        var symbols = new Dictionary<string, SymbolFact>(StringComparer.Ordinal);
        foreach (var project in artifact.Projects)
        {
            Text(project.Id, "project.id");
            Require(projectIds.Add(project.Id), $"Duplicate project ID '{project.Id}'.");
            Text(project.Name, $"{project.Id}.name");
            PathText(project.File, $"{project.Id}.file");
            OptionalText(project.TargetFramework, $"{project.Id}.targetFramework");
            Text(project.OutputKind, $"{project.Id}.outputKind");
            Text(project.LanguageVersion, $"{project.Id}.languageVersion");
            Text(project.NullableContext, $"{project.Id}.nullableContext");
            Strings(project.Defines, $"{project.Id}.defines");
            Strings(project.ProjectReferences, $"{project.Id}.projectReferences");
            Strings(project.AssemblyReferences, $"{project.Id}.assemblyReferences");
            hasErrors |= Diagnostics(project.Diagnostics, $"{project.Id}.diagnostics");
            Items(project.Symbols, $"{project.Id}.symbols");
            foreach (var symbol in project.Symbols)
            {
                Text(symbol.Id, "symbol.id");
                Require(symbols.TryAdd(symbol.Id, symbol), $"Duplicate symbol ID '{symbol.Id}'.");
                Text(symbol.Kind, $"{symbol.Id}.kind");
                Text(symbol.Name, $"{symbol.Id}.name");
                Text(symbol.DisplayName, $"{symbol.Id}.displayName");
                Text(symbol.Accessibility, $"{symbol.Id}.accessibility");
                OptionalText(symbol.ContainingSymbolId, $"{symbol.Id}.containingSymbolId");
                OptionalText(symbol.Type, $"{symbol.Id}.type");
                Require(symbol.Documentation is not null, $"{symbol.Id}.documentation cannot be null.");
                Text(symbol.Declaration, $"{symbol.Id}.declaration");
                Strings(symbol.Attributes, $"{symbol.Id}.attributes");
                Strings(symbol.MigrationSignals, $"{symbol.Id}.migrationSignals");
                Span(symbol.Source, $"{symbol.Id}.source");
                Items(symbol.AdditionalSources, $"{symbol.Id}.additionalSources");
                foreach (var source in symbol.AdditionalSources)
                    Span(source, $"{symbol.Id}.additionalSources");
                Items(symbol.Parameters, $"{symbol.Id}.parameters");
                var parameters = new HashSet<string>(StringComparer.Ordinal);
                foreach (var parameter in symbol.Parameters)
                {
                    Text(parameter.Name, $"{symbol.Id}.parameter.name");
                    Require(parameters.Add(parameter.Name), $"{symbol.Id} has duplicate parameter '{parameter.Name}'.");
                    Text(parameter.Type, $"{symbol.Id}.parameter.type");
                    Text(parameter.RefKind, $"{symbol.Id}.parameter.refKind");
                    Require(parameter.IsOptional || parameter.DefaultValue is null,
                        $"{symbol.Id}: non-optional parameter '{parameter.Name}' cannot have defaultValue.");
                }
                Items(symbol.Relationships, $"{symbol.Id}.relationships");
                foreach (var relationship in symbol.Relationships)
                {
                    Text(relationship.Kind, $"{symbol.Id}.relationship.kind");
                    Text(relationship.TargetId, $"{symbol.Id}.relationship.targetId");
                    Text(relationship.TargetDisplay, $"{symbol.Id}.relationship.targetDisplay");
                    Span(relationship.Source, $"{symbol.Id}.relationship.source");
                }
            }
        }
        Require(artifact.Status != "complete" || !hasErrors, "Complete extraction cannot contain Error diagnostics.");
        Require(artifact.Status != "complete" || !ExtractionStatus.HasAnalysisGaps(artifact),
            "Complete extraction contains analysis gaps.");
        return symbols;
    }

    public static void Requirements(RequirementsArtifact artifact, ExtractionArtifact extraction,
        IReadOnlyDictionary<string, SymbolFact> symbols)
    {
        Version(artifact.SchemaVersion);
        Require(artifact.TaskId == extraction.TaskId, "Requirements taskId does not match extraction.");
        Require(artifact.ExtractionId == extraction.ExtractionId, "Requirements extractionId does not match extraction.");
        Choice(artifact.Status, "status", "draft", "partial", "blocked", "complete");
        Items(artifact.Requirements, "requirements");
        Items(artifact.Coverage, "coverage");
        Items(artifact.OpenQuestions, "openQuestions");
        var requirements = new Dictionary<string, Requirement>(StringComparer.Ordinal);
        var evidence = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requirement in artifact.Requirements)
        {
            Text(requirement.Id, "requirement.id");
            Require(requirements.TryAdd(requirement.Id, requirement), $"Duplicate requirement ID '{requirement.Id}'.");
            Text(requirement.Feature, $"{requirement.Id}.feature");
            Text(requirement.Statement, $"{requirement.Id}.statement");
            Choice(requirement.Confidence, $"{requirement.Id}.confidence", "confirmed", "inferred", "uncertain");
            Evidence(requirement.EvidenceIds, symbols, $"{requirement.Id}.evidenceIds");
            Require(requirement.EvidenceIds.Count > 0, $"{requirement.Id} requires evidenceIds.");
            evidence.UnionWith(requirement.EvidenceIds);
            Strings(requirement.Inputs, $"{requirement.Id}.inputs");
            Strings(requirement.Outputs, $"{requirement.Id}.outputs");
            Strings(requirement.Preconditions, $"{requirement.Id}.preconditions");
            Strings(requirement.Postconditions, $"{requirement.Id}.postconditions");
            Strings(requirement.SideEffects, $"{requirement.Id}.sideEffects");
            Strings(requirement.ErrorBehavior, $"{requirement.Id}.errorBehavior");
            Strings(requirement.AcceptanceCriteria, $"{requirement.Id}.acceptanceCriteria");
            Require(requirement.AcceptanceCriteria.Count > 0, $"{requirement.Id} requires acceptanceCriteria.");
            Strings(requirement.MigrationNotes, $"{requirement.Id}.migrationNotes");
            UniqueStrings(requirement.DependsOn, $"{requirement.Id}.dependsOn");
        }
        Dependencies(requirements);
        Traceability(artifact.Coverage, artifact.OpenQuestions, symbols, evidence);
        if (artifact.Status == "complete")
        {
            Require(extraction.Status == "complete", "Complete requirements cannot be based on a partial extraction.");
            Require(artifact.OpenQuestions.Count == 0, "Complete requirements cannot have unresolved open questions.");
            Require(artifact.Coverage.All(decision => decision.Disposition != "pending"), "Complete requirements cannot have pending coverage.");
            Require(artifact.Requirements.All(requirement => requirement.Confidence != "uncertain"), "Complete requirements cannot contain uncertain requirements.");
        }
    }

    public static void Traceability(List<CoverageDecision> decisions, List<OpenQuestion> questions,
        IReadOnlyDictionary<string, SymbolFact> symbols, HashSet<string> evidence)
    {
        Items(decisions, "coverage");
        Items(questions, "openQuestions");
        var coverage = new HashSet<string>(StringComparer.Ordinal);
        foreach (var decision in decisions)
        {
            Text(decision.SymbolId, "coverage.symbolId");
            Require(symbols.ContainsKey(decision.SymbolId), $"Unknown coverage symbol '{decision.SymbolId}'.");
            Require(coverage.Add(decision.SymbolId), $"Duplicate coverage decision '{decision.SymbolId}'.");
            Choice(decision.Disposition, $"{decision.SymbolId}.disposition", "required", "excluded", "pending");
            Text(decision.Reason, $"{decision.SymbolId}.reason");
            if (decision.Disposition == "required")
                Require(evidence.Contains(decision.SymbolId), $"Required symbol '{decision.SymbolId}' has no evidence-linked requirement.");
            if (decision.Disposition == "excluded")
                Require(!evidence.Contains(decision.SymbolId), $"Excluded symbol '{decision.SymbolId}' also has a requirement.");
        }
        foreach (var symbol in symbols.Values.Where(symbol => symbol.IsPublicApi))
            Require(coverage.Contains(symbol.Id), $"Public API symbol '{symbol.Id}' is missing a coverage decision.");
        var questionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in questions)
        {
            Text(question.Id, "openQuestion.id");
            Require(questionIds.Add(question.Id), $"Duplicate open question ID '{question.Id}'.");
            Text(question.Question, $"{question.Id}.question");
            Evidence(question.EvidenceIds, symbols, $"{question.Id}.evidenceIds");
        }
    }

    private static void Dependencies(IReadOnlyDictionary<string, Requirement> requirements)
    {
        var incoming = requirements.ToDictionary(pair => pair.Key, pair => pair.Value.DependsOn.Count, StringComparer.Ordinal);
        var dependents = requirements.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var requirement in requirements.Values)
            foreach (var dependency in requirement.DependsOn)
            {
                Require(requirements.ContainsKey(dependency), $"{requirement.Id} depends on unknown requirement '{dependency}'.");
                dependents[dependency].Add(requirement.Id);
            }
        var ready = new Queue<string>(incoming.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var visited = 0;
        while (ready.TryDequeue(out var id))
        {
            visited++;
            foreach (var dependent in dependents[id])
                if (--incoming[dependent] == 0)
                    ready.Enqueue(dependent);
        }
        Require(visited == requirements.Count, "Requirement dependencies contain a cycle.");
    }

    public static void Evidence(List<string>? evidence, IReadOnlyDictionary<string, SymbolFact> symbols, string path)
    {
        UniqueStrings(evidence, path);
        foreach (var id in evidence!)
            Require(symbols.ContainsKey(id), $"{path} contains unknown extraction symbol ID '{id}'.");
    }

    private static bool Diagnostics(List<DiagnosticFact>? diagnostics, string path)
    {
        Items(diagnostics, path);
        var hasErrors = false;
        foreach (var diagnostic in diagnostics)
        {
            Text(diagnostic.Code, $"{path}.code");
            Text(diagnostic.Message, $"{path}.message");
            Text(diagnostic.Severity, $"{path}.severity");
            Choice(diagnostic.Severity.ToLowerInvariant(), $"{path}.severity", "hidden", "info", "warning", "error");
            hasErrors |= diagnostic.Severity.Equals("error", StringComparison.OrdinalIgnoreCase);
            if (diagnostic.Source is not null)
                Span(diagnostic.Source, $"{path}.source");
        }
        return hasErrors;
    }

    private static void Span(SourceReference? source, string path)
    {
        Require(source is not null, $"{path} cannot be null.");
        PathText(source.File, $"{path}.file");
        Require(source.StartLine >= 1 && source.EndLine >= source.StartLine,
            $"{path} must have 1-based inclusive lines with endLine >= startLine.");
    }

    private static void Version(string version) =>
        Require(version == ArtifactJson.SchemaVersion, $"Unsupported schemaVersion; expected '{ArtifactJson.SchemaVersion}'.");

    public static void Choice(string? value, string path, params string[] choices) =>
        Require(value is not null && choices.Contains(value, StringComparer.Ordinal), $"{path} must be one of: {string.Join(", ", choices)}.");

    public static void OptionalText(string? value, string path)
    {
        if (value is not null)
            Text(value, path);
    }

    public static void Text([NotNull] string? value, string path) =>
        Require(!string.IsNullOrWhiteSpace(value), $"{path} must be a nonempty string.");

    public static void PathText(string? value, string path)
    {
        Text(value, path);
        Require(value.IndexOfAny(Path.GetInvalidPathChars()) < 0, $"{path} contains invalid path characters.");
    }

    public static void Strings([NotNull] List<string>? values, string path)
    {
        Items(values, path);
        foreach (var value in values)
            Text(value, $"{path}[]");
    }

    public static void UniqueStrings([NotNull] List<string>? values, string path)
    {
        Strings(values, path);
        Require(values.Distinct(StringComparer.Ordinal).Count() == values.Count, $"{path} contains duplicate IDs.");
    }

    public static void Items<T>([NotNull] List<T>? items, string path)
    {
        Require(items is not null, $"{path} must be a non-null array.");
        Require(items.All(item => item is not null), $"{path} cannot contain null records or values.");
    }

    public static void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
