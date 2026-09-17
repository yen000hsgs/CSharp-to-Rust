namespace CSharpToRust.Extraction;

public sealed record CompactViewOptions(string Command, string Input, string? Symbol = null,
    string Part = "code", int Offset = 0, int Limit = 25, int MaxChars = 2000, int MaxBytes = 4096)
{
    public const string Usage = """
        CSharp.Extractor index --input <extraction.json> [--offset N] [--limit 1..100] [--max-bytes 512..16384]
        CSharp.Extractor context --input <extraction.json> [--offset N] [--limit 1..100] [--max-bytes 512..16384]
        CSharp.Extractor inspect --input <extraction.json> --symbol <short-ref|canonical-id>
          [--part code|raw|facts|relations|members|documentation] [--offset N] [--limit 1..100]
          [--max-chars 1..8000] [--max-bytes 512..16384]
        Read-only views; no project execution. Full extraction JSON stays unchanged on disk.
        """;

    public void Validate()
    {
        if (Command is not ("index" or "context" or "inspect") || string.IsNullOrWhiteSpace(Input))
            throw new ArgumentException(Usage);
        if (Offset < 0 || Limit is < 1 or > 100 || MaxChars is < 1 or > 8000 || MaxBytes is < 512 or > 16384)
            throw new ArgumentException("Invalid page bounds. " + Usage);
        if (Command == "inspect")
        {
            if (string.IsNullOrWhiteSpace(Symbol) || Part is not ("code" or "raw" or "facts" or "relations" or "members" or "documentation"))
                throw new ArgumentException("inspect requires a symbol and a valid part. " + Usage);
            if (Part == "facts" && Offset != 0)
                throw new ArgumentException("The facts part has no text offset.");
        }
        else if (Symbol is not null || Part != "code" || MaxChars != 2000)
            throw new ArgumentException("Symbol, part, and text-length options apply only to inspect.");
    }

    public static CompactViewOptions Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("index" or "context" or "inspect"))
            throw new ArgumentException(Usage);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            var key = args[i];
            if (key is not ("--input" or "--symbol" or "--part" or "--offset" or "--limit" or "--max-chars" or "--max-bytes"))
                throw new ArgumentException($"Unknown compact-view option: {key}");
            if (args[0] != "inspect" && key is "--symbol" or "--part" or "--max-chars")
                throw new ArgumentException($"{key} applies only to inspect.");
            if (++i == args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Missing value for {key}.");
            if (!values.TryAdd(key, args[i])) throw new ArgumentException($"Duplicate option: {key}");
        }
        int Number(string key, int fallback) => !values.TryGetValue(key, out var value) ? fallback :
            int.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number) ? number :
                throw new ArgumentException($"Invalid integer for {key}.");
        var result = new CompactViewOptions(args[0], values.GetValueOrDefault("--input", ""),
            values.GetValueOrDefault("--symbol"), values.GetValueOrDefault("--part", "code"),
            Number("--offset", 0), Number("--limit", 25), Number("--max-chars", 2000), Number("--max-bytes", 4096));
        result.Validate();
        return result;
    }
}
