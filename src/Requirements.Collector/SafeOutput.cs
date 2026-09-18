using System.Text.Json;
using CSharpToRust.Contracts;

namespace Requirements.Collector;

internal static class SafeOutput
{
    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string SourceRoot(string input, ExtractionArtifact extraction) =>
        Path.GetFullPath(extraction.RootDirectory, Path.GetDirectoryName(Path.GetFullPath(input))!);

    // Paths persisted into an artifact are written relative to that artifact's own directory, so a
    // document, context, or distribution plan keeps meaning after the package moves to another
    // checkout or machine. Every reader already resolves these fields against the same anchor
    // (SourceRoot above, DocumentValidation, and DistributionValidation.BindPath), so writing the
    // relative form is what makes the round trip location-independent. Falls back to the absolute
    // path when no relative path exists, such as a different volume.
    public static string PortablePath(string target, string artifactPath)
    {
        var absolute = Path.GetFullPath(target);
        var anchor = Path.GetDirectoryName(Path.GetFullPath(artifactPath));
        if (string.IsNullOrEmpty(anchor)) return absolute;
        var relative = Path.GetRelativePath(anchor, absolute);
        return string.IsNullOrEmpty(relative) || Path.IsPathFullyQualified(relative) ? absolute : relative;
    }

    public static void Write(string output, string input, ExtractionArtifact extraction,
        IReadOnlyDictionary<string, SymbolFact> symbols, RequirementsArtifact draft, bool force)
    {
        var destination = Path.GetFullPath(output);
        Preflight(destination, ".json", ProtectedPaths(input, extraction, symbols), force);
        if (File.Exists(destination))
        {
            var previous = StrictJson.Read<RequirementsArtifact>(destination);
            ArtifactValidation.Requirements(previous, extraction, symbols);
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(draft, ArtifactJson.Options);
        Publish([(destination, bytes)], force);
    }

    public static HashSet<string> ProtectedPaths(string input, ExtractionArtifact extraction,
        IReadOnlyDictionary<string, SymbolFact> symbols)
    {
        var sourceRoot = SourceRoot(input, extraction);
        var protectedPaths = new HashSet<string>(PathComparer) { Path.GetFullPath(input) };
        void Protect(string path) => protectedPaths.Add(Path.GetFullPath(path, sourceRoot));
        Protect(extraction.InputPath);
        foreach (var project in extraction.Projects)
        {
            Protect(project.File);
            foreach (var diagnostic in project.Diagnostics.Where(diagnostic => diagnostic.Source is not null))
                Protect(diagnostic.Source!.File);
        }
        foreach (var diagnostic in extraction.Diagnostics.Where(diagnostic => diagnostic.Source is not null))
            Protect(diagnostic.Source!.File);
        foreach (var symbol in symbols.Values)
        {
            Protect(symbol.Source.File);
            foreach (var source in symbol.AdditionalSources)
                Protect(source.File);
            foreach (var relationship in symbol.Relationships)
                Protect(relationship.Source.File);
        }
        return protectedPaths;
    }

    public static void WriteNew(IReadOnlyList<(string Path, string Extension, byte[] Bytes)> outputs,
        HashSet<string> protectedPaths)
    {
        var destinations = new HashSet<string>(PathComparer);
        foreach (var output in outputs)
        {
            var destination = Path.GetFullPath(output.Path);
            ArtifactValidation.Require(destinations.Add(destination), "Output paths must be distinct.");
            Preflight(destination, output.Extension, protectedPaths, force: false);
        }
        Publish(outputs.Select(output => (Path.GetFullPath(output.Path), output.Bytes)).ToList(), force: false);
    }

    private static void Preflight(string destination, string extension, HashSet<string> protectedPaths, bool force)
    {
        ArtifactValidation.Require(Path.GetExtension(destination).Equals(extension, StringComparison.OrdinalIgnoreCase),
            $"Output must use the {extension} extension.");
        RejectLinks(destination);
        ArtifactValidation.Require(!protectedPaths.Contains(destination), "Output cannot overwrite extraction, document, context, guide, project, or source evidence.");
        ArtifactValidation.Require(!Directory.Exists(destination), "Output cannot be an existing directory.");
        ArtifactValidation.Require(force || !File.Exists(destination),
            "Output already exists; use a new path (only prepare-legacy supports --force for the same snapshot).");
        for (var parent = Path.GetDirectoryName(destination); parent is not null; parent = Path.GetDirectoryName(parent))
            ArtifactValidation.Require(!File.Exists(parent), "An output parent path is an existing file.");
    }

    private static void Publish(IReadOnlyList<(string Destination, byte[] Bytes)> outputs, bool force)
    {
        var staged = new List<(string Staging, string Destination)>();
        var published = new List<string>();
        var success = false;
        try
        {
            // Stage every file before publishing any; ordinary write failures cannot leave half a package.
            foreach (var (destination, bytes) in outputs)
            {
                var parent = Path.GetDirectoryName(destination)!;
                Directory.CreateDirectory(parent);
                var staging = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.pending");
                using var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                staged.Add((staging, destination));
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            foreach (var (staging, destination) in staged)
            {
                // Rename rather than truncate, so existing files and hard-linked evidence are preserved.
                File.Move(staging, destination, overwrite: force);
                published.Add(destination);
            }
            success = true;
        }
        finally
        {
            // Roll back only new files published by this invocation, never legacy replacements.
            // This is not a sandbox against hostile concurrent filesystem changes.
            if (!success && !force)
                foreach (var destination in published)
                    File.Delete(destination);
            foreach (var (staging, _) in staged)
                if (File.Exists(staging))
                    File.Delete(staging);
        }
    }

    private static void RejectLinks(string destination)
    {
        for (string? path = destination; path is not null; path = Path.GetDirectoryName(path))
        {
            if (Path.Exists(path))
                ArtifactValidation.Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0,
                    "Output paths through symbolic links, junctions, or reparse points are not supported.");
        }
    }
}
