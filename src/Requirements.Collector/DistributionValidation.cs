using System.Text.RegularExpressions;
using CSharpToRust.Contracts;
using static Requirements.Collector.ArtifactValidation;

namespace Requirements.Collector;

internal static class DistributionValidation
{
    private static readonly Regex Slug = new(@"\A[a-z][a-z0-9]*(?:[._-][a-z0-9]+)*\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Digest = new(@"\A[a-f0-9]{64}\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static bool Validate(DistributionPlan plan, FeatureDocument document, DocumentContext context,
        string documentPath, string contextPath, string distributionPath, string documentSha256, string contextSha256,
        bool upstreamReady)
    {
        Require(plan.SchemaVersion == DistributionPlan.CurrentSchemaVersion, "Distribution schemaVersion must be '1.0'.");
        Require(plan.TaskId == context.TaskId, "Distribution taskId does not match the validated context.");
        Require(plan.ExtractionId == context.ExtractionId, "Distribution extractionId does not match the validated context.");
        BindPath(plan.DocumentPath, documentPath, distributionPath, "documentPath");
        BindPath(plan.ContextPath, contextPath, distributionPath, "contextPath");
        BindDigest(plan.DocumentSha256, documentSha256, "documentSha256");
        BindDigest(plan.ContextSha256, contextSha256, "contextSha256");
        Choice(plan.Status, "status", "draft", "partial", "blocked", "complete");
        Items(plan.Packages, "packages");
        Items(plan.SharedConcerns, "sharedConcerns");
        Items(plan.UnassignedFeatures, "unassignedFeatures");
        Items(plan.OpenQuestions, "openQuestions");

        var features = document.Features.Select(feature => feature.Id).ToHashSet(StringComparer.Ordinal);
        var packages = new Dictionary<string, DistributionPackage>(StringComparer.Ordinal);
        foreach (var package in plan.Packages)
        {
            SlugId(package.Id, "package ID");
            Require(packages.TryAdd(package.Id, package), $"Duplicate package ID '{package.Id}'.");
        }
        var accounted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in plan.Packages)
        {
            Text(package.Name, $"{package.Id}.name");
            Text(package.Summary, $"{package.Id}.summary");
            UniqueStrings(package.FeatureIds, $"{package.Id}.featureIds");
            Require(package.FeatureIds.Count > 0, $"{package.Id}.featureIds must not be empty.");
            foreach (var id in package.FeatureIds)
                Assign(id, features, accounted);
            UniqueStrings(package.DependsOn, $"{package.Id}.dependsOn");
        }
        foreach (var unassigned in plan.UnassignedFeatures)
        {
            Text(unassigned.FeatureId, "unassignedFeatures.featureId");
            Text(unassigned.Reason, $"{unassigned.FeatureId}.reason");
            Assign(unassigned.FeatureId, features, accounted);
        }
        Require(features.SetEquals(accounted), "Every feature must be assigned exactly once to a package or unassignedFeatures.");
        Dependencies(packages);

        var concerns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var concern in plan.SharedConcerns)
        {
            SlugId(concern.Id, "shared concern ID");
            Require(concerns.Add(concern.Id), $"Duplicate shared concern ID '{concern.Id}'.");
            Text(concern.Statement, $"{concern.Id}.statement");
            UniqueStrings(concern.PackageIds, $"{concern.Id}.packageIds");
            Require(concern.PackageIds.Count > 0, $"{concern.Id}.packageIds must not be empty.");
            foreach (var id in concern.PackageIds)
                Require(packages.ContainsKey(id), $"{concern.Id}.packageIds contains unknown package '{id}'.");
        }
        var questions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in plan.OpenQuestions)
        {
            Text(question.Id, "openQuestions.id");
            Require(questions.Add(question.Id), $"Duplicate open question ID '{question.Id}'.");
            Text(question.Question, $"{question.Id}.question");
            UniqueStrings(question.FeatureIds, $"{question.Id}.featureIds");
            foreach (var id in question.FeatureIds)
                Require(features.Contains(id), $"{question.Id}.featureIds contains unknown feature '{id}'.");
        }

        var resolved = upstreamReady && document.Features.Count > 0 && plan.Packages.Count > 0
            && plan.UnassignedFeatures.Count == 0 && plan.OpenQuestions.Count == 0;
        Require(plan.Status != "complete" || resolved,
            "Complete distribution requires a nonempty document, packages covering all features, no unassigned features or open questions, and ready upstream document/context.");
        return plan.Status == "complete" && resolved;
    }

    private static void SlugId(string id, string path)
    {
        Text(id, path);
        Require(Slug.IsMatch(id), $"{path} '{id}' must be a simple lowercase slug.");
    }

    private static void BindPath(string value, string supplied, string distributionPath, string name)
    {
        PathText(value, name);
        Require(SafeOutput.PathComparer.Equals(Path.GetFullPath(value, DocumentValidation.DirectoryOf(distributionPath)),
            Path.GetFullPath(supplied)), $"Distribution {name} does not match the supplied input.");
    }

    private static void BindDigest(string value, string expected, string name)
    {
        Text(value, name);
        Require(Digest.IsMatch(value), $"{name} must be a lowercase 64-character SHA256 hex digest.");
        Require(value == expected, $"{name} does not match the current validated input bytes.");
    }

    private static void Assign(string id, HashSet<string> features, HashSet<string> accounted)
    {
        Require(features.Contains(id), $"Unknown distribution feature '{id}'.");
        Require(accounted.Add(id), $"Feature '{id}' is assigned more than once across packages/unassignedFeatures.");
    }

    private static void Dependencies(IReadOnlyDictionary<string, DistributionPackage> packages)
    {
        var incoming = packages.ToDictionary(pair => pair.Key, pair => pair.Value.DependsOn.Count, StringComparer.Ordinal);
        var dependents = packages.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var package in packages.Values)
            foreach (var dependency in package.DependsOn)
            {
                Require(dependency != package.Id, $"Package '{package.Id}' cannot depend on itself.");
                Require(packages.ContainsKey(dependency), $"{package.Id} depends on unknown package '{dependency}'.");
                dependents[dependency].Add(package.Id);
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
        Require(visited == packages.Count, "Package dependencies contain a cycle.");
    }
}
