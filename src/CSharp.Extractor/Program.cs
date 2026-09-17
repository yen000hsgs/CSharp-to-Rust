using System.Text.Json;
using CSharpToRust.Contracts;
using CSharpToRust.Extraction;

if (args is ["--help"] or ["-h"])
{
    Console.WriteLine(ExtractorOptions.Usage);
    Console.WriteLine(CompactViewOptions.Usage);
    return 0;
}

try
{
    if (args.Length > 0 && args[0] is "index" or "context" or "inspect")
    {
        Console.WriteLine(await CompactViews.ReadAndRenderAsync(CompactViewOptions.Parse(args)));
        return 0;
    }
    var options = ExtractorOptions.Parse(args);
    if (!File.Exists(options.Input))
        throw new FileNotFoundException("Input project or solution does not exist.", options.Input);
    if (File.Exists(options.Output))
        throw new IOException("Output already exists. Choose a new artifact path; existing files are never overwritten.");
    var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(options.Output))!;
    if (!Directory.Exists(outputDirectory))
        throw new DirectoryNotFoundException($"Create the output directory first: {outputDirectory}");

    var artifact = await ProjectExtractor.ExtractAsync(options);
    await using (var stream = new FileStream(options.Output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    {
        await JsonSerializer.SerializeAsync(stream, artifact, ArtifactJson.Options);
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        taskId = artifact.TaskId, extractionId = artifact.ExtractionId, status = artifact.Status,
        artifact = Path.GetFullPath(options.Output), projects = artifact.Projects.Count,
        symbols = artifact.Projects.Sum(project => project.Symbols.Count),
        diagnostics = artifact.Diagnostics.Count + artifact.Projects.Sum(project => project.Diagnostics.Count),
        limitations = artifact.Limitations
    }, ArtifactJson.Options));
    return artifact.Status == "complete" ? 0 : 2;
}
catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or
    UnauthorizedAccessException or InvalidOperationException or JsonException)
{
    Console.Error.WriteLine($"Extraction failed: {exception.Message}");
    return 1;
}
