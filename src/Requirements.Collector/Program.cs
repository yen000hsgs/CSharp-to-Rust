using System.Globalization;
using System.Text;
using System.Text.Json;
using CSharpToRust.Contracts;
using Requirements.Collector;

return CollectorCommand.Run(args);

internal static class CollectorCommand
{
    private const string Usage = """
        Requirements Collector (.NET 8)
          prepare --input <extraction.json> --output <document.json> --context <document.context.json>
          validate --input <extraction.json> --document <document.json> --context <document.context.json> [--require-ready]
          render --input <extraction.json> --document <document.json> --context <document.context.json> --output <requirements.md>
          prepare-distribution --input <extraction.json> --document <document.json> --context <document.context.json> --output <distribution.json>
          validate-distribution --input <extraction.json> --document <document.json> --context <document.context.json> --distribution <distribution.json> [--require-ready]
          inspect --input <extraction.json> --symbol <exact-id> [--offset <n>] [--max-chars <1..8192>]
          prepare-legacy --input <extraction.json> --output <draft.json> [--force]
          validate-legacy --input <extraction.json> --requirements <requirements.json>
        prepare creates a new two-file draft, never inferred features or behavior.
        render creates a new deterministic Markdown view; legacy commands retain schema 1.0.
        inspect returns bounded snapshot evidence; it does not read live source.
        validate checks structure and traceability only, never semantic parity.
        prepare-distribution creates a new unassigned draft; validate-distribution checks whole-feature planning and input bindings.
        Exit 0: successful operation (possibly draft/partial); exit 2: invalid input, usage, or I/O.
        """;

    public static int Run(string[] args)
    {
        try
        {
            if (args is ["--help"] or ["-h"])
            {
                Console.WriteLine(Usage);
                return 0;
            }
            ArtifactValidation.Require(args.Length > 0, Usage);
            var command = args[0];
            string[] allowed = command switch
            {
                "prepare" => ["--input", "--output", "--context"],
                "validate" => ["--input", "--document", "--context", "--require-ready"],
                "render" => ["--input", "--document", "--context", "--output"],
                "prepare-distribution" => ["--input", "--document", "--context", "--output"],
                "validate-distribution" => ["--input", "--document", "--context", "--distribution", "--require-ready"],
                "prepare-legacy" => ["--input", "--output", "--force"],
                "inspect" => ["--input", "--symbol", "--offset", "--max-chars"],
                "validate-legacy" => ["--input", "--requirements"],
                _ => throw new InvalidDataException($"Unknown command '{command}'.\n{Usage}")
            };
            var options = Parse(args[1..], allowed);
            var input = Required(options, "--input");
            var extraction = StrictJson.Read<ExtractionArtifact>(input);
            var symbols = ArtifactValidation.Extraction(extraction);
            switch (command)
            {
                case "prepare":
                    DocumentPipeline.Prepare(input, Required(options, "--output"), Required(options, "--context"), extraction, symbols);
                    break;
                case "prepare-distribution":
                    DistributionPipeline.Prepare(input, Required(options, "--document"), Required(options, "--context"),
                        Required(options, "--output"), extraction, symbols);
                    break;
                case "validate-distribution":
                    DistributionPipeline.Validate(input, Required(options, "--document"), Required(options, "--context"),
                        Required(options, "--distribution"), extraction, symbols, options.ContainsKey("--require-ready"));
                    break;
                case "validate":
                case "render":
                    var ready = DocumentPipeline.Validate(input, Required(options, "--document"), Required(options, "--context"),
                        extraction, symbols, command == "render" ? Required(options, "--output") : null);
                    ArtifactValidation.Require(!options.ContainsKey("--require-ready") || ready,
                        "Document is not ready for downstream: complete authoring and resolve context blockers.");
                    break;
                case "prepare-legacy":
                    var output = Required(options, "--output");
                    var draft = Prepare(extraction, symbols.Values);
                    ArtifactValidation.Requirements(draft, extraction, symbols);
                    SafeOutput.Write(output, input, extraction, symbols, draft, options.ContainsKey("--force"));
                    PrintSummary(extraction, draft, Path.GetFullPath(output));
                    break;
                case "validate-legacy":
                    var requirementsPath = Required(options, "--requirements");
                    var requirements = StrictJson.Read<RequirementsArtifact>(requirementsPath);
                    ArtifactValidation.Requirements(requirements, extraction, symbols);
                    PrintSummary(extraction, requirements, Path.GetFullPath(requirementsPath));
                    break;
                case "inspect":
                    var id = Required(options, "--symbol");
                    ArtifactValidation.Require(symbols.TryGetValue(id, out var symbol), $"Unknown extraction symbol ID '{id}'.");
                    Inspect(extraction, symbol, Number(options, "--offset", 0, 0, int.MaxValue),
                        Number(options, "--max-chars", 4096, 1, 8192));
                    break;
            }
            return 0;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException
            or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 2;
        }
    }

    private static Dictionary<string, string> Parse(string[] args, string[] allowed)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var key = args[index];
            ArtifactValidation.Require(allowed.Contains(key, StringComparer.Ordinal), $"Unknown option '{key}'.");
            var value = "true";
            if (key is not ("--force" or "--require-ready"))
            {
                ArtifactValidation.Require(index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal),
                    $"Missing value for '{key}'.");
                value = args[++index];
                ArtifactValidation.Require(!string.IsNullOrWhiteSpace(value), $"Empty value for '{key}'.");
            }
            ArtifactValidation.Require(options.TryAdd(key, value), $"Duplicate option '{key}'.");
        }
        return options;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) ? value : throw new InvalidDataException($"Missing required option '{key}'.");

    private static int Number(IReadOnlyDictionary<string, string> options, string key, int fallback, int minimum, int maximum)
    {
        if (!options.TryGetValue(key, out var value))
            return fallback;
        ArtifactValidation.Require(int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number >= minimum && number <= maximum, $"{key} must be an integer between {minimum} and {maximum}.");
        return number;
    }

    private static RequirementsArtifact Prepare(ExtractionArtifact extraction, IEnumerable<SymbolFact> symbols)
    {
        var result = new RequirementsArtifact
        {
            TaskId = extraction.TaskId,
            ExtractionId = extraction.ExtractionId,
            Status = "draft",
            Coverage = symbols.Where(symbol => symbol.IsPublicApi).OrderBy(symbol => symbol.Id, StringComparer.Ordinal)
                .Select(symbol => new CoverageDecision(symbol.Id, "pending",
                    "AI review required: inspect compiler facts, declaration, observable behavior, and relevant tests.")).ToList(),
            OpenQuestions =
            [
                new("Q-AI-REVIEW",
                    "Which testable requirements or justified exclusions follow from the evidence? AI population and interpretation review are required.",
                    [])
            ]
        };
        if (extraction.Status == "partial")
            result.OpenQuestions.Add(new("Q-EXTRACTION",
                "Extraction is partial. Ask the orchestrator to resolve extraction diagnostics, limitations, and missing evidence before claiming complete coverage.",
                []));
        return result;
    }

    private static void Inspect(ExtractionArtifact extraction, SymbolFact symbol, int offset, int maximum)
    {
        ArtifactValidation.Require(offset <= symbol.Declaration.Length, "--offset exceeds the declaration length.");
        ArtifactValidation.Require(offset == symbol.Declaration.Length || !char.IsLowSurrogate(symbol.Declaration[offset]),
            "--offset cannot split a Unicode surrogate pair.");
        var count = Math.Min(maximum, symbol.Declaration.Length - offset);
        if (count > 0 && char.IsHighSurrogate(symbol.Declaration[offset + count - 1]))
            count--;
        ArtifactValidation.Require(count > 0 || offset == symbol.Declaration.Length,
            "--max-chars must be at least 2 to include this Unicode surrogate pair.");
        var next = offset + count;
        var project = extraction.Projects.Single(project => project.Symbols.Contains(symbol));
        var packet = new
        {
            extraction.SchemaVersion, extraction.TaskId, extraction.ExtractionId, extraction.Status,
            symbolId = symbol.Id,
            evidenceKind = "compiler-facts-and-untrusted-source-excerpt",
            project = new
            {
                project.Id, project.TargetFramework, project.OutputKind, project.LanguageVersion,
                project.NullableContext, project.CheckOverflow, project.Defines
            },
            facts = new
            {
                symbol.Kind, symbol.Name, symbol.DisplayName, symbol.ContainingSymbolId, symbol.Accessibility,
                symbol.IsPublicApi, symbol.IsStatic, symbol.IsAsync, symbol.Type, symbol.Attributes, symbol.Parameters,
                symbol.Source, symbol.AdditionalSources, symbol.Relationships, symbol.MigrationSignals
            },
            declaration = symbol.Declaration.Substring(offset, count),
            offset,
            totalCharacters = symbol.Declaration.Length,
            truncated = next < symbol.Declaration.Length,
            nextOffset = next < symbol.Declaration.Length ? (int?)next : null,
            diagnostics = DiagnosticSummary(extraction)
        };
        var json = JsonSerializer.Serialize(packet, ArtifactJson.Options);
        ArtifactValidation.Require(Encoding.UTF8.GetByteCount(json) <= 64 * 1024,
            "Symbol metadata exceeds the 64 KiB evidence packet limit; ask the orchestrator for a narrowed extraction.");
        Console.WriteLine(json);
    }

    private static object DiagnosticSummary(ExtractionArtifact extraction)
    {
        var diagnostics = extraction.Diagnostics.Concat(extraction.Projects.SelectMany(project => project.Diagnostics)).ToList();
        return new
        {
            count = diagnostics.Count,
            errors = diagnostics.Count(diagnostic => diagnostic.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)),
            warnings = diagnostics.Count(diagnostic => diagnostic.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase)),
            limitations = extraction.Limitations.Count
        };
    }

    private static void PrintSummary(ExtractionArtifact extraction, RequirementsArtifact artifact, string path) =>
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            extraction.TaskId, extraction.ExtractionId, artifact.Status,
            artifactPath = path,
            complete = artifact.Status == "complete",
            structureAndTraceabilityValid = true,
            semanticParityVerified = false,
            requirements = artifact.Requirements.Count,
            pendingCoverage = artifact.Coverage.Count(decision => decision.Disposition == "pending"),
            openQuestions = artifact.OpenQuestions.Count,
            extractionStatus = extraction.Status,
            diagnostics = DiagnosticSummary(extraction)
        }, ArtifactJson.Options));
}
