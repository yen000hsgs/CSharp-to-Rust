using System.Globalization;
using System.Text;
using System.Text.Json;
using CSharpToRust.Contracts;

namespace Requirements.Collector;

internal static class DocumentMarkdown
{
    private static readonly JsonSerializerOptions CompactJson = new(ArtifactJson.Options) { WriteIndented = false };

    public static string Render(FeatureDocument document, DocumentContext context, string contextPath, string outputPath)
    {
        var result = new StringBuilder();
        void Line(string text = "") => result.Append(text).Append('\n');
        Line("# Requirements");
        Line();
        Line($"Source: {Text(document.Source.Language)} / {Text(document.Source.Kind)} / {Text(document.Source.Root)}");
        var fullContextPath = Path.GetFullPath(contextPath);
        var relative = Path.GetRelativePath(DocumentValidation.DirectoryOf(outputPath), fullContextPath).Replace('\\', '/');
        string link;
        if (Path.IsPathRooted(relative))
        {
            ArtifactValidation.Require(Uri.TryCreate(fullContextPath, UriKind.Absolute, out var uri) && uri.IsFile,
                "Context path cannot be represented as a file URI.");
            link = uri.AbsoluteUri.Replace("(", "%28", StringComparison.Ordinal).Replace(")", "%29", StringComparison.Ordinal);
        }
        else
        {
            link = string.Join("/", relative.Split('/').Select(Uri.EscapeDataString));
        }
        Line($"Context: [{Text(Path.GetFileName(contextPath))}]({link}); status: **{context.Status}**; upstream: {context.UpstreamStatus}.");
        Line("Structure and traceability only; semantic parity is not verified.");
        foreach (var feature in document.Features)
        {
            Line();
            Line($"## `{feature.Id}` - {Text(feature.Name)}");
            Line();
            Line($"**API:** {Text(feature.Signature)} ({Text(feature.Visibility)})");
            Line();
            Line($"**Summary:** {Text(feature.Summary)}");
            if (feature.Parameters.Count > 0)
            {
                Line();
                Line("**Parameters**");
                foreach (var parameter in feature.Parameters)
                    Line($"- {Text(parameter.Name)}: {Text(parameter.Type)}"
                        + (parameter.Constraints.Count == 0 ? "" : $"; {string.Join("; ", parameter.Constraints.Select(Text))}"));
            }
            Line();
            Line($"**Returns:** {Text(feature.Returns.Type)} - {Text(feature.Returns.Description)}");
            if (feature.Behaviors.Count > 0)
            {
                Line();
                Line("**Behaviors**");
                foreach (var behavior in feature.Behaviors)
                    Line($"- `{behavior.Id}`: {Text(behavior.Statement)}");
            }
            if (feature.Errors.Count > 0)
            {
                Line();
                Line("**Errors**");
                foreach (var error in feature.Errors)
                    Line($"- `{error.Id}`: when {Text(error.Condition)}; result: {Text(error.Result)}");
            }
            if (feature.Invariants.Count > 0)
            {
                Line();
                Line("**Invariants**");
                foreach (var invariant in feature.Invariants)
                    Line($"- `{invariant.Id}`: {Text(invariant.Statement)}");
            }
            if (feature.Examples.Count > 0)
            {
                Line();
                Line("**Examples**");
                foreach (var example in feature.Examples)
                    Line($"- `{example.Id}`: input {Text(JsonSerializer.Serialize(example.Input, CompactJson))}; expected {Text(JsonSerializer.Serialize(example.Expected, CompactJson))}");
            }
            if (feature.SourceRefs.Count > 0)
            {
                Line();
                Line($"Source refs: {string.Join("; ", feature.SourceRefs.Select(Text))}");
            }
        }
        return result.ToString();
    }

    private static string Text(string value)
    {
        var result = new StringBuilder();
        foreach (var character in value)
        {
            if (char.IsControl(character) || char.GetUnicodeCategory(character)
                is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                result.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            else if (character == '&') result.Append("&amp;");
            else if (character == '<') result.Append("&lt;");
            else if (character == '>') result.Append("&gt;");
            else if (character == '|') result.Append("&#124;");
            else
            {
                if ("\\`*_{}[]()#!~".Contains(character))
                    result.Append('\\');
                result.Append(character);
            }
        }
        return result.ToString();
    }
}
