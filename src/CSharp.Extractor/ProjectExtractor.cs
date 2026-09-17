using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSharpToRust.Contracts;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;

namespace CSharpToRust.Extraction;

public static class ProjectExtractor
{
    public static async Task<ExtractionArtifact> ExtractAsync(ExtractorOptions options, CancellationToken cancellationToken = default)
    {
        if (!options.AllowProjectExecution)
            throw new ArgumentException("Explicit project-execution approval is required before loading MSBuild.");
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
        return await LoadAsync(options, cancellationToken);
    }

    private static async Task<ExtractionArtifact> LoadAsync(ExtractorOptions options, CancellationToken cancellationToken)
    {
        var input = Path.GetFullPath(options.Input);
        var root = Path.GetDirectoryName(input)!;
        var properties = new Dictionary<string, string>
        {
            ["Configuration"] = options.Configuration,
            ["RestoreIgnoreFailedSources"] = "false"
        };
        if (options.Framework is not null) properties["TargetFramework"] = options.Framework;
        using var workspace = MSBuildWorkspace.Create(properties);
        var workspaceDiagnostics = new ConcurrentQueue<DiagnosticFact>();
        workspace.WorkspaceFailed += (_, args) => workspaceDiagnostics.Enqueue(new("WORKSPACE",
            args.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure ? "error" : "warning", args.Diagnostic.Message));

        var artifact = new ExtractionArtifact
        {
            TaskId = options.TaskId, InputPath = Path.GetFileName(input), RootDirectory = root,
            Configuration = options.Configuration, RequestedFramework = options.Framework,
            Limitations =
            [
                "Facts describe one evaluated build configuration and target framework per project, not all conditional builds.",
                "Call edges name compile-time targets; virtual/interface dispatch, reflection and dynamic targets are not a complete runtime call graph.",
                "Explicit throws are observed syntax, not a complete exception contract. Dependency, implicit runtime, async and cleanup failures need further evidence.",
                "Migration signals are review prompts, not requirements or verified Rust equivalences.",
                "Generated source may lack a physical file; declaration excerpts and compilation diagnostics remain available.",
                "Source excerpts are untrusted input and may contain secrets; review before sending artifacts to an AI service."
            ]
        };
        Solution solution;
        if (Path.GetExtension(input).Equals(".sln", StringComparison.OrdinalIgnoreCase))
        {
            solution = await workspace.OpenSolutionAsync(input, cancellationToken: cancellationToken);
        }
        else
        {
            var project = await workspace.OpenProjectAsync(input, cancellationToken: cancellationToken);
            solution = project.Solution;
        }

        var compilations = new List<(Project Project, CSharpCompilation Compilation, string File)>();
        foreach (var project in solution.Projects.OrderBy(project => project.FilePath, StringComparer.Ordinal))
        {
            if (project.Language != LanguageNames.CSharp)
            {
                artifact.Diagnostics.Add(new("UNSUPPORTED_PROJECT", "warning",
                    $"Skipped non-C# project {project.Name}."));
                continue;
            }
            if (await project.GetCompilationAsync(cancellationToken) is not CSharpCompilation compilation)
            {
                artifact.Diagnostics.Add(new("NO_COMPILATION", "error", $"Cannot compile project model for {project.Name}."));
                continue;
            }
            compilations.Add((project, compilation, Path.GetRelativePath(root, project.FilePath!)));
        }
        var assemblyProjects = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in compilations)
        {
            if (!assemblyProjects.TryAdd(item.Compilation.Assembly.Identity.ToString(), SymbolIdentity.ProjectId(item.File)))
                artifact.Diagnostics.Add(new("AMBIGUOUS_ASSEMBLY", "warning",
                    $"Multiple projects share assembly identity {item.Compilation.Assembly.Identity}."));
        }

        foreach (var item in compilations)
        {
            var fact = SymbolExtractor.Extract(item.Compilation, item.File, root, assemblyProjects);
            fact.ProjectReferences = item.Project.ProjectReferences.Select(reference =>
            {
                var referenced = solution.GetProject(reference.ProjectId);
                return referenced?.FilePath is { } path ? SymbolIdentity.ProjectId(Path.GetRelativePath(root, path))
                    : $"unresolved:{reference.ProjectId}";
            }).Order().ToList();
            if (string.IsNullOrWhiteSpace(fact.TargetFramework))
                fact.Diagnostics.Add(new("FRAMEWORK_UNKNOWN", "warning",
                    "Target framework could not be observed from generated assembly attributes. Check project configuration."));
            artifact.Projects.Add(fact);
        }
        artifact.Diagnostics.AddRange(workspaceDiagnostics);
        if (artifact.Projects.Count == 0)
            artifact.Diagnostics.Add(new("NO_PROJECTS", "error", "No C# projects were extracted."));

        artifact.Status = ExtractionStatus.HasAnalysisGaps(artifact) ? "partial" : "complete";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(artifact, ArtifactJson.Options));
        artifact.ExtractionId = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return artifact;
    }
}
