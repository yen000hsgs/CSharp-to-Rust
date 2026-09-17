using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToRust.Extraction;

public sealed record CompactedSource(string Text, bool Compacted);

public static class SourceCompactor
{
    public static CompactedSource Compact(string source)
    {
        var tokens = SyntaxFactory.ParseTokens(source).ToArray();
        if (tokens.Any(token => token.ContainsDiagnostics ||
            token.LeadingTrivia.Concat(token.TrailingTrivia).Any(trivia =>
                trivia.IsDirective || trivia.IsKind(SyntaxKind.DisabledTextTrivia) || trivia.ContainsDiagnostics)))
            return new(source, false);

        var result = new System.Text.StringBuilder();
        SyntaxToken? previous = null;
        foreach (var token in tokens.Where(token => token.Text.Length != 0))
        {
            if (previous is { } before)
            {
                // Keep a separator whenever removing it would merge or change lexical tokens.
                var pair = SyntaxFactory.ParseTokens(before.Text + token.Text)
                    .Where(item => item.Text.Length != 0).ToArray();
                if (pair.Length != 2 || pair[0].RawKind != before.RawKind || pair[0].Text != before.Text ||
                    pair[1].RawKind != token.RawKind || pair[1].Text != token.Text)
                    result.Append(' ');
            }
            result.Append(token.Text);
            previous = token;
        }
        var compact = result.ToString();
        var reparsed = SyntaxFactory.ParseTokens(compact).Select(token => (token.RawKind, token.Text));
        if (!tokens.Select(token => (token.RawKind, token.Text)).SequenceEqual(reparsed))
            return new(source, false);
        return new(compact, true);
    }
}
