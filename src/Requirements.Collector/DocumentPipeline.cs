using System.Text;
using System.Text.Json;
using CSharpToRust.Contracts;

namespace Requirements.Collector;

internal static class DocumentPipeline
{
    public static void Prepare(string input, string output, string contextPath, ExtractionArtifact extraction,
        IReadOnlyDictionary<string, SymbolFact> symbols)
    {
        var document = new FeatureDocument
        {
            Source = new()
            {
                Root = SafeOutput.PortablePath(SafeOutput.SourceRoot(input, extraction), output),
                Kind = Path.GetExtension(extraction.InputPath).ToLowerInvariant() is ".sln" or ".slnx"
                    ? "solution"
                    : extraction.Projects.Any(project => project.OutputKind is "ConsoleApplication" or "WindowsApplication")
                        ? "application" : "library"
            }
        };
        var context = new DocumentContext
        {
            TaskId = extraction.TaskId,
            ExtractionId = extraction.ExtractionId,
            UpstreamStatus = extraction.Status,
            DocumentPath = SafeOutput.PortablePath(output, contextPath),
            EvidenceReferences = [SafeOutput.PortablePath(input, contextPath)],
            Coverage = symbols.Values.Where(symbol => symbol.IsPublicApi).OrderBy(symbol => symbol.Id, StringComparer.Ordinal)
                .Select(symbol => new CoverageDecision(symbol.Id, "pending",
                    "Agent review required: author testable behavior from source, compiler evidence, and relevant tests, or justify exclusion.")).ToList(),
            OpenQuestions =
            [
                new("Q-AUTHORING",
                    "Which evidence-backed features and atomic requirements should be authored? Resolve behavioral uncertainty and any required mapping decisions before completion.",
                    [])
            ]
        };
        if (extraction.Status == "partial")
            context.OpenQuestions.Add(new("Q-EXTRACTION", "Resolve partial upstream extraction and missing evidence before completion.", []));
        var ready = DocumentValidation.Validate(document, context, extraction, symbols, input, output, contextPath);
        SafeOutput.WriteNew(
            [
                (output, ".json", JsonSerializer.SerializeToUtf8Bytes(document, ArtifactJson.Options)),
                (contextPath, ".json", JsonSerializer.SerializeToUtf8Bytes(context, ArtifactJson.Options))
            ], SafeOutput.ProtectedPaths(input, extraction, symbols));
        PrintSummary(document, context, output, contextPath, ready);
    }

    public static bool Validate(string input, string documentPath, string contextPath, ExtractionArtifact extraction,
        IReadOnlyDictionary<string, SymbolFact> symbols, string? renderOutput = null)
    {
        var (document, context, ready) = ReadValidated(input, documentPath, contextPath, extraction, symbols);
        if (renderOutput is not null)
        {
            SafeOutput.WriteNew([(renderOutput, ".md", Encoding.UTF8.GetBytes(DocumentMarkdown.Render(document, context, contextPath, renderOutput)))],
                ProtectedPaths(input, documentPath, contextPath, extraction, symbols, document, context));
        }
        PrintSummary(document, context, documentPath, contextPath, ready);
        return ready;
    }

    internal static (FeatureDocument Document, DocumentContext Context, bool Ready) ReadValidated(
        string input, string documentPath, string contextPath, ExtractionArtifact extraction,
        IReadOnlyDictionary<string, SymbolFact> symbols)
    {
        var document = StrictJson.Read<FeatureDocument>(documentPath);
        var context = StrictJson.Read<DocumentContext>(contextPath);
        var ready = DocumentValidation.Validate(document, context, extraction, symbols, input, documentPath, contextPath);
        return (document, context, ready);
    }

    internal static HashSet<string> ProtectedPaths(string input, string documentPath, string contextPath,
        ExtractionArtifact extraction, IReadOnlyDictionary<string, SymbolFact> symbols,
        FeatureDocument document, DocumentContext context)
    {
        var protectedPaths = SafeOutput.ProtectedPaths(input, extraction, symbols);
        protectedPaths.Add(Path.GetFullPath(documentPath));
        protectedPaths.Add(Path.GetFullPath(contextPath));
        var contextDirectory = DocumentValidation.DirectoryOf(contextPath);
        foreach (var reference in context.EvidenceReferences)
            protectedPaths.Add(Path.GetFullPath(reference, contextDirectory));
        if (context.PortingGuide is not null)
            protectedPaths.Add(Path.GetFullPath(context.PortingGuide.Path, contextDirectory));
        foreach (var reference in document.Features.SelectMany(feature => feature.SourceRefs))
            protectedPaths.Add(Path.GetFullPath(reference.Split('#', 2)[0], SafeOutput.SourceRoot(input, extraction)));
        return protectedPaths;
    }

    private static void PrintSummary(FeatureDocument document, DocumentContext context, string documentPath, string contextPath, bool ready) =>
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            context.TaskId, context.ExtractionId, context.Status, context.UpstreamStatus,
            documentPath = Path.GetFullPath(documentPath),
            contextPath = Path.GetFullPath(contextPath),
            structureAndTraceabilityValid = true,
            readyForDownstream = ready,
            semanticParityVerified = false,
            features = document.Features.Count,
            requirements = document.Features.Sum(DocumentValidation.RequirementCount),
            coverage = context.Coverage.Count,
            pendingCoverage = context.Coverage.Count(decision => decision.Disposition == "pending"),
            openQuestions = context.OpenQuestions.Count
        }, ArtifactJson.Options));
}
