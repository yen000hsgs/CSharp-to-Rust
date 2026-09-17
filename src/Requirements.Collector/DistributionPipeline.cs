using System.Text.Json;
using CSharpToRust.Contracts;

namespace Requirements.Collector;

internal static class DistributionPipeline
{
    public static void Prepare(string input, string documentPath, string contextPath, string output,
        ExtractionArtifact extraction, IReadOnlyDictionary<string, SymbolFact> symbols)
    {
        var upstream = ReadValidated(input, documentPath, contextPath, extraction, symbols);
        var plan = new DistributionPlan
        {
            TaskId = upstream.Context.TaskId,
            ExtractionId = upstream.Context.ExtractionId,
            DocumentPath = Path.GetFullPath(documentPath),
            ContextPath = Path.GetFullPath(contextPath),
            DocumentSha256 = upstream.DocumentSha256,
            ContextSha256 = upstream.ContextSha256,
            UnassignedFeatures = upstream.Document.Features.Select(feature => new UnassignedDistributionFeature(feature.Id,
                "Agent review required: assign this whole feature, with all original atomic requirements, to an implementation package.")).ToList(),
            OpenQuestions =
            [
                new("Q-AUTHORING",
                    "Which implementation packages, dependencies, and shared concerns should organize the existing features? Author and review the distribution before completion.",
                    [])
            ]
        };
        var ready = ValidatePlan(plan, upstream, documentPath, contextPath, output);
        SafeOutput.WriteNew([(output, ".json", JsonSerializer.SerializeToUtf8Bytes(plan, ArtifactJson.Options))],
            DocumentPipeline.ProtectedPaths(input, documentPath, contextPath, extraction, symbols, upstream.Document, upstream.Context));
        PrintSummary(plan, upstream, output, ready);
    }

    public static void Validate(string input, string documentPath, string contextPath, string distributionPath,
        ExtractionArtifact extraction, IReadOnlyDictionary<string, SymbolFact> symbols, bool requireReady)
    {
        var upstream = ReadValidated(input, documentPath, contextPath, extraction, symbols);
        var plan = StrictJson.Read<DistributionPlan>(distributionPath);
        var ready = ValidatePlan(plan, upstream, documentPath, contextPath, distributionPath);
        ArtifactValidation.Require(!requireReady || ready,
            "Distribution is not ready for downstream: complete planning and resolve upstream document/context blockers.");
        PrintSummary(plan, upstream, distributionPath, ready);
    }

    private static bool ValidatePlan(DistributionPlan plan, ValidatedInputs upstream,
        string documentPath, string contextPath, string distributionPath) =>
        DistributionValidation.Validate(plan, upstream.Document, upstream.Context, documentPath, contextPath, distributionPath,
            upstream.DocumentSha256, upstream.ContextSha256, upstream.Ready);

    private static ValidatedInputs ReadValidated(string input, string documentPath, string contextPath,
        ExtractionArtifact extraction, IReadOnlyDictionary<string, SymbolFact> symbols)
    {
        var document = StrictJson.ReadSnapshot<FeatureDocument>(documentPath);
        var context = StrictJson.ReadSnapshot<DocumentContext>(contextPath);
        var ready = DocumentValidation.Validate(document.Value, context.Value, extraction, symbols, input, documentPath, contextPath);
        return new(document.Value, context.Value, ready, document.Sha256, context.Sha256);
    }

    private static void PrintSummary(DistributionPlan plan, ValidatedInputs upstream, string path, bool ready)
    {
        var assigned = plan.Packages.SelectMany(package => package.FeatureIds).ToHashSet(StringComparer.Ordinal);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            plan.TaskId, plan.ExtractionId, plan.Status,
            distributionPath = Path.GetFullPath(path),
            upstreamReadyForDownstream = upstream.Ready,
            readyForDownstream = ready,
            structureAndTraceabilityValid = true,
            packageCount = plan.Packages.Count,
            assignedFeatureCount = assigned.Count,
            unassignedFeatureCount = plan.UnassignedFeatures.Count,
            assignedRequirementCount = upstream.Document.Features.Where(feature => assigned.Contains(feature.Id))
                .Sum(DocumentValidation.RequirementCount),
            openQuestionCount = plan.OpenQuestions.Count,
            semanticParityVerified = false
        }, ArtifactJson.Options));
    }

    private sealed record ValidatedInputs(FeatureDocument Document, DocumentContext Context, bool Ready,
        string DocumentSha256, string ContextSha256);
}
