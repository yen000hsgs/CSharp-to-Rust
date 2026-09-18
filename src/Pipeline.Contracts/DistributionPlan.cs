namespace CSharpToRust.Contracts;

public sealed class DistributionPlan
{
    public const string CurrentSchemaVersion = "1.0";
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string TaskId { get; set; } = "";
    public string ExtractionId { get; set; } = "";
    public string DocumentPath { get; set; } = "";
    public string ContextPath { get; set; } = "";
    public string DocumentSha256 { get; set; } = "";
    public string ContextSha256 { get; set; } = "";
    public string Status { get; set; } = "draft";
    public List<DistributionPackage> Packages { get; set; } = [];
    public List<DistributionConcern> SharedConcerns { get; set; } = [];
    public List<UnassignedDistributionFeature> UnassignedFeatures { get; set; } = [];
    public List<DistributionQuestion> OpenQuestions { get; set; } = [];
}

public sealed record DistributionPackage(string Id, string Name, string Summary, List<string> FeatureIds, List<string> DependsOn);
public sealed record DistributionConcern(string Id, string Statement, List<string> PackageIds);
public sealed record UnassignedDistributionFeature(string FeatureId, string Reason);
public sealed record DistributionQuestion(string Id, string Question, List<string> FeatureIds);
