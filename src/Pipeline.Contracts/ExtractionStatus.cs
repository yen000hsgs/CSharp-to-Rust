namespace CSharpToRust.Contracts;

public static class ExtractionStatus
{
    public static bool HasAnalysisGaps(ExtractionArtifact artifact) =>
        artifact.Diagnostics.Count != 0 ||
        artifact.Projects.Any(project => project.Diagnostics.Any(diagnostic =>
            diagnostic.Severity == "error" || diagnostic.Code is
                "UNRESOLVED_CALL" or "SOURCE_TRUNCATED" or "FRAMEWORK_UNKNOWN" or
                "SYNTHESIZED_MEMBERS" or "PRIMARY_CONSTRUCTOR" or "TOP_LEVEL_CODE"));
}
