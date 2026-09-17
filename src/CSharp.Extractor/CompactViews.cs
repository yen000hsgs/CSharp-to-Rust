using System.Text;
using System.Text.Json;
using CSharpToRust.Contracts;

namespace CSharpToRust.Extraction;

public sealed class CompactViews
{
    private sealed record Entry(string Ref, string ProjectRef, ProjectFact Project, SymbolFact Symbol);

    private readonly ExtractionArtifact artifact;
    private readonly List<Entry> entries;
    private readonly Dictionary<string, Entry> byId;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public CompactViews(ExtractionArtifact artifact)
    {
        ExtractionArtifactReader.Validate(artifact);
        this.artifact = artifact;
        entries = artifact.Projects.OrderBy(p => p.Id, StringComparer.Ordinal)
            .SelectMany((project, i) => project.Symbols.Select(symbol => (ProjectRef: $"p{i + 1}", Project: project, Symbol: symbol)))
            .OrderBy(item => item.Symbol.Id, StringComparer.Ordinal)
            .Select((item, i) => new Entry($"s{i + 1}", item.ProjectRef, item.Project, item.Symbol)).ToList();
        byId = entries.ToDictionary(entry => entry.Symbol.Id, StringComparer.Ordinal);
    }

    public static async Task<string> ReadAndRenderAsync(CompactViewOptions options)
    {
        options.Validate();
        return new CompactViews(await ExtractionArtifactReader.ReadAsync(options.Input)).Render(options);
    }

    public string Render(CompactViewOptions options)
    {
        options.Validate();
        return options.Command switch
        {
            "index" => Index(options),
            "context" => Context(options),
            "inspect" => Inspect(options),
            _ => throw new ArgumentException(CompactViewOptions.Usage)
        };
    }

    private Dictionary<string, object?> Envelope(string view) => new()
    {
        ["view"] = view, ["taskId"] = artifact.TaskId, ["extractionId"] = artifact.ExtractionId, ["status"] = artifact.Status
    };

    private string Index(CompactViewOptions options)
    {
        var surface = entries.Where(entry => entry.Symbol.IsPublicApi)
            .OrderBy(entry => entry.Project.Id, StringComparer.Ordinal)
            .ThenBy(entry => entry.Symbol.Source.File, StringComparer.Ordinal)
            .ThenBy(entry => entry.Symbol.Id, StringComparer.Ordinal).ToList();
        return Page(surface, options, page =>
        {
            var result = Envelope("index");
            result["context"] = "Read context for build settings, diagnostics and limitations.";
            result["groups"] = page.GroupBy(entry => (entry.ProjectRef, entry.Symbol.Source.File)).Select(group => new
            {
                project = group.Key.ProjectRef, name = group.First().Project.Name, file = group.Key.File,
                symbols = group.Select(entry =>
                {
                    var symbol = entry.Symbol;
                    var row = new Dictionary<string, object?>
                    {
                        ["ref"] = entry.Ref, ["kind"] = symbol.Kind, ["signature"] = symbol.DisplayName,
                        ["line"] = symbol.Source.StartLine
                    };
                    if (symbol.Type is not null) row["returnsOrType"] = symbol.Type;
                    return row;
                }).ToArray()
            }).ToArray();
            return result;
        });
    }

    private string Context(CompactViewOptions options)
    {
        var items = new List<object>
        {
            new { kind = "scope", input = artifact.InputPath, configuration = artifact.Configuration,
                requestedFramework = artifact.RequestedFramework }
        };
        items.AddRange(artifact.Limitations.Select(text => new { kind = "limitation", text }));
        items.AddRange(artifact.Diagnostics.Select(d => new { kind = "diagnostic", diagnostic = d }));
        foreach (var (project, index) in artifact.Projects.OrderBy(p => p.Id, StringComparer.Ordinal).Select((p, i) => (p, i)))
        {
            var reference = $"p{index + 1}";
            items.Add(new
            {
                kind = "project", project = reference, name = project.Name, file = project.File,
                framework = project.TargetFramework, outputKind = project.OutputKind, language = project.LanguageVersion,
                nullable = project.NullableContext, checkOverflow = project.CheckOverflow,
                defines = project.Defines, projectReferences = project.ProjectReferences
            });
            items.AddRange(project.Diagnostics.Select(d => new { kind = "diagnostic", project = reference, diagnostic = d }));
        }
        return Page(items, options, page =>
        {
            var result = Envelope("context");
            result["items"] = page;
            return result;
        });
    }

    private string Inspect(CompactViewOptions options)
    {
        var entry = entries.FirstOrDefault(e => e.Ref == options.Symbol || e.Symbol.Id == options.Symbol)
            ?? throw new ArgumentException($"Unknown symbol: {options.Symbol}");
        var symbol = entry.Symbol;
        Dictionary<string, object?> Header()
        {
            var result = Envelope("inspect");
            result["ref"] = entry.Ref;
            result["symbolId"] = symbol.Id;
            result["part"] = options.Part;
            result["source"] = symbol.Source;
            if (symbol.AdditionalSources.Count > 0) result["additionalSources"] = symbol.AdditionalSources;
            result["sourceTruncated"] = entry.Project.Diagnostics.Any(d => d.Code == "SOURCE_TRUNCATED" &&
                (d.Message.Contains(symbol.Id, StringComparison.Ordinal) ||
                 d.Source is { } source && source.File == symbol.Source.File &&
                 source.StartLine <= symbol.Source.EndLine && source.EndLine >= symbol.Source.StartLine));
            return result;
        }
        if (options.Part == "members")
        {
            var members = entries.Where(e => e.Symbol.ContainingSymbolId == symbol.Id).Select(e => new
            {
                @ref = e.Ref, kind = e.Symbol.Kind, signature = e.Symbol.DisplayName,
                accessibility = e.Symbol.Accessibility, publicApi = e.Symbol.IsPublicApi
            }).ToList();
            return Page(members, options, page =>
            {
                var result = Header();
                result["items"] = page;
                return result;
            });
        }
        if (options.Part == "relations")
        {
            var items = symbol.Relationships.Select(relation =>
            {
                var target = byId.GetValueOrDefault(relation.TargetId);
                return new
                {
                    kind = relation.Kind, target = target?.Ref ?? relation.TargetDisplay,
                    available = target is not null, at = relation.Source
                };
            }).ToList();
            return Page(items, options, page =>
            {
                var result = Header();
                result["items"] = page;
                return result;
            });
        }
        if (options.Part == "facts")
        {
            var result = Header();
            result["signature"] = symbol.DisplayName;
            result["accessibility"] = symbol.Accessibility;
            result["publicApi"] = symbol.IsPublicApi;
            result["static"] = symbol.IsStatic;
            result["async"] = symbol.IsAsync;
            if (symbol.Type is not null) result["returnsOrType"] = symbol.Type;
            if (symbol.ContainingSymbolId is not null)
                result["owner"] = byId.GetValueOrDefault(symbol.ContainingSymbolId)?.Ref ?? symbol.ContainingSymbolId;
            if (symbol.Parameters.Count > 0) result["parameters"] = symbol.Parameters;
            if (symbol.Attributes.Count > 0) result["attributes"] = symbol.Attributes;
            if (symbol.MigrationSignals.Count > 0) result["signals"] = symbol.MigrationSignals;
            return Fit(result, options.MaxBytes);
        }

        var source = options.Part == "code" ? SourceCompactor.Compact(symbol.Declaration) :
            new CompactedSource(options.Part == "raw" ? symbol.Declaration : symbol.Documentation, false);
        return TextPage(source, options, Header);
    }

    private static string Page<T>(List<T> items, CompactViewOptions options,
        Func<T[], Dictionary<string, object?>> build)
    {
        if (options.Offset > items.Count) throw new ArgumentException("Offset exceeds available items.");
        var count = Math.Min(options.Limit, items.Count - options.Offset);
        while (true)
        {
            var result = build(items.Skip(options.Offset).Take(count).ToArray());
            result["offset"] = options.Offset;
            result["total"] = items.Count;
            result["nextOffset"] = options.Offset + count < items.Count ? options.Offset + count : null;
            var json = JsonSerializer.Serialize(result, JsonOptions);
            if (Fits(json, options.MaxBytes)) return json;
            if (count <= 1) throw new InvalidDataException("One view item exceeds --max-bytes; increase the budget or inspect narrower evidence.");
            count--;
        }
    }

    private static string TextPage(CompactedSource source, CompactViewOptions options,
        Func<Dictionary<string, object?>> header)
    {
        var text = source.Text;
        if (options.Offset > text.Length || options.Offset < text.Length && char.IsLowSurrogate(text[options.Offset]))
            throw new ArgumentException("Text offset exceeds the excerpt or splits a Unicode character.");
        var count = Math.Min(options.MaxChars, text.Length - options.Offset);
        while (true)
        {
            if (count > 0 && char.IsHighSurrogate(text[options.Offset + count - 1])) count--;
            if (count == 0 && options.Offset < text.Length)
                throw new InvalidDataException("Cannot fit the next character; increase --max-chars or --max-bytes.");
            var result = header();
            result["compacted"] = source.Compacted;
            result["text"] = text.Substring(options.Offset, count);
            result["offset"] = options.Offset;
            result["totalCharacters"] = text.Length;
            result["nextOffset"] = options.Offset + count < text.Length ? options.Offset + count : null;
            var json = JsonSerializer.Serialize(result, JsonOptions);
            if (Fits(json, options.MaxBytes)) return json;
            if (count == 0) throw new InvalidDataException("Symbol metadata exceeds --max-bytes.");
            count /= 2;
        }
    }

    private static string Fit(Dictionary<string, object?> result, int maxBytes)
    {
        var json = JsonSerializer.Serialize(result, JsonOptions);
        if (!Fits(json, maxBytes)) throw new InvalidDataException("Symbol facts exceed --max-bytes; increase the budget or inspect source.");
        return json;
    }

    private static bool Fits(string text, int maxBytes) =>
        Encoding.UTF8.GetByteCount(text) + Encoding.UTF8.GetByteCount(Environment.NewLine) <= maxBytes;
}
