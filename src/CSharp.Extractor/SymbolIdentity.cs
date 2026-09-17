using CSharpToRust.Contracts;
using Microsoft.CodeAnalysis;

namespace CSharpToRust.Extraction;

internal static class SymbolIdentity
{
    public static string ProjectId(string file) => $"project:{file.Replace('\\', '/')}";

    public static string Id(ISymbol symbol, string projectId,
        IReadOnlyDictionary<string, string> assemblyProjects, string root)
    {
        if (symbol is IMethodSymbol { ReducedFrom: { } extension }) symbol = extension;
        symbol = symbol.OriginalDefinition;
        if (symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction } local)
        {
            var source = local.Locations.First(location => location.IsInSource);
            var file = Source(source, root).File;
            return $"{Id(local.ContainingSymbol, projectId, assemblyProjects, root)}:{local.MethodKind}:{local.Name}:{file}:{source.SourceSpan.Start}";
        }
        var assembly = symbol.ContainingAssembly?.Identity.ToString() ?? "";
        var prefix = assemblyProjects.GetValueOrDefault(assembly,
            symbol.Locations.Any(location => location.IsInSource) ? projectId : $"assembly:{assembly}");
        // Documentation IDs omit file-local scope, including on nested types and members.
        for (var type = symbol as INamedTypeSymbol ?? symbol.ContainingType; type is not null; type = type.ContainingType)
        {
            if (!type.IsFileLocal) continue;
            var file = Source(type.Locations.First(location => location.IsInSource), root).File;
            prefix += $":file:{Uri.EscapeDataString(file)}";
            break;
        }
        var documentationId = symbol.GetDocumentationCommentId();
        if (documentationId is not null) return $"{prefix}:{documentationId}";
        var location = symbol.Locations.FirstOrDefault();
        return $"{prefix}:{symbol.Kind}:{symbol.ToDisplayString()}:{location?.SourceSpan.Start}";
    }

    public static SourceReference Source(Location location, string root)
    {
        var span = location.GetLineSpan();
        var file = string.IsNullOrEmpty(span.Path) ? "<generated>" :
            Path.IsPathRooted(span.Path) ? Path.GetRelativePath(root, span.Path) : span.Path;
        return new(file.Replace('\\', '/'), span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1);
    }

    public static bool IsPublicApi(ISymbol symbol)
    {
        for (var current = symbol; current is not null && current is not INamespaceSymbol; current = current.ContainingSymbol)
        {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal))
                return false;
        }
        return true;
    }

    public static ISymbol? RetainedOwner(ISymbol symbol)
    {
        var owner = symbol.ContainingSymbol;
        while (owner is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction })
            owner = owner.ContainingSymbol;
        return owner is IMethodSymbol { AssociatedSymbol: { } associated } ? associated : owner;
    }
}
