namespace CSharpToRust.Extraction;

public sealed record ExtractorOptions(
    string Input, string Output, string TaskId, string Configuration, string? Framework,
    bool AllowProjectExecution = false)
{
    public const string Usage =
        "CSharp.Extractor extract --input <project.csproj|solution.sln> --output <new.json> " +
        "--allow-project-execution [--task-id <id>] [--configuration Debug|Release] [--framework <tfm>]";

    public static ExtractorOptions Parse(string[] args)
    {
        if (args.Length == 0 || args[0] != "extract")
            throw new ArgumentException(Usage);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var trusted = false;
        for (var i = 1; i < args.Length; i++)
        {
            var key = args[i];
            if (key == "--allow-project-execution")
            {
                if (trusted) throw new ArgumentException($"Duplicate option: {key}");
                trusted = true;
                continue;
            }

            if (key is not ("--input" or "--output" or "--task-id" or "--configuration" or "--framework"))
                throw new ArgumentException($"Unknown option: {key}");
            if (++i == args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Missing value for {key}.");
            if (!values.TryAdd(key, args[i]))
                throw new ArgumentException($"Duplicate option: {key}");
        }

        if (!trusted)
            throw new ArgumentException(
                "Project loading can execute MSBuild tasks and source generators. " +
                "Review the input, then explicitly supply --allow-project-execution. This flag is not a sandbox.");
        if (!values.TryGetValue("--input", out var input) || !values.TryGetValue("--output", out var output))
            throw new ArgumentException("--input and --output are required.");
        if (Path.GetExtension(input).ToLowerInvariant() is not (".csproj" or ".sln"))
            throw new ArgumentException("Input must be a .csproj or .sln file.");
        if (!string.Equals(Path.GetExtension(output), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output must be a new .json file.");

        return new(input, output, values.GetValueOrDefault("--task-id", Guid.NewGuid().ToString("N")),
            values.GetValueOrDefault("--configuration", "Debug"), values.GetValueOrDefault("--framework"), trusted);
    }
}
