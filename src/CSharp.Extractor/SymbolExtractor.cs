using System.Globalization;
using CSharpToRust.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CSharpToRust.Extraction;

public static class SymbolExtractor
{
    public const int MaximumDeclarationLength = 16000;

    public static ProjectFact Extract(CSharpCompilation compilation, string projectFile, string root,
        IReadOnlyDictionary<string, string>? assemblyProjects = null)
    {
        // Leave the legacy assembly catalog empty; available compiler references are not source usage.
        var project = new ProjectFact
        {
            Id = SymbolIdentity.ProjectId(projectFile),
            Name = compilation.AssemblyName ?? Path.GetFileNameWithoutExtension(projectFile),
            File = projectFile.Replace('\\', '/'),
            OutputKind = compilation.Options.OutputKind.ToString(),
            NullableContext = compilation.Options.NullableContextOptions.ToString(),
            CheckOverflow = compilation.Options.CheckOverflow,
            LanguageVersion = compilation.LanguageVersion.ToString(),
            Defines = compilation.SyntaxTrees.SelectMany(tree =>
                ((CSharpParseOptions)tree.Options).PreprocessorSymbolNames).Distinct().Order().ToList(),
            TargetFramework = compilation.Assembly.GetAttributes()
                .FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() ==
                    "System.Runtime.Versioning.TargetFrameworkAttribute")
                ?.ConstructorArguments.FirstOrDefault().Value as string
        };
        var assemblies = assemblyProjects ?? new Dictionary<string, string>
        {
            [compilation.Assembly.Identity.ToString()] = project.Id
        };
        project.Diagnostics.AddRange(compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Select(diagnostic => new DiagnosticFact(diagnostic.Id, diagnostic.Severity.ToString().ToLowerInvariant(),
                diagnostic.GetMessage(CultureInfo.InvariantCulture),
                diagnostic.Location.IsInSource ? SymbolIdentity.Source(diagnostic.Location, root) : null)));

        var facts = new Dictionary<string, SymbolFact>(StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees.OrderBy(tree => tree.FilePath, StringComparer.Ordinal))
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var statement in tree.GetRoot().ChildNodes().OfType<GlobalStatementSyntax>())
                project.Diagnostics.Add(new("TOP_LEVEL_CODE", "warning",
                    "Top-level executable statements are outside the source-declaration inventory.",
                    SymbolIdentity.Source(statement.GetLocation(), root)));
            foreach (var node in tree.GetRoot().DescendantNodes().Where(IsDeclaration))
            {
                var symbol = model.GetDeclaredSymbol(node);
                if (symbol is null || symbol.IsImplicitlyDeclared) continue;
                var id = SymbolIdentity.Id(symbol, project.Id, assemblies, root);
                var sourceNode = node is VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax field }
                    ? (SyntaxNode)field : node;
                var source = SymbolIdentity.Source(sourceNode.GetLocation(), root);
                if (facts.TryGetValue(id, out var existing))
                {
                    existing.AdditionalSources.Add(source);
                    AppendBody(existing, node, symbol, model, project, root, assemblies);
                    continue;
                }
                var fact = new SymbolFact
                {
                    Id = id, Kind = symbol.Kind.ToString(), Name = symbol.Name,
                    DisplayName = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                    ContainingSymbolId = symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction }
                        ? SymbolIdentity.Id(SymbolIdentity.RetainedOwner(symbol)!, project.Id, assemblies, root)
                        : symbol.ContainingType is null ? null : SymbolIdentity.Id(symbol.ContainingType, project.Id, assemblies, root),
                    Accessibility = symbol.DeclaredAccessibility.ToString(),
                    IsPublicApi = SymbolIdentity.IsPublicApi(symbol), IsStatic = symbol.IsStatic,
                    IsAsync = symbol is IMethodSymbol { IsAsync: true },
                    Type = ValueType(symbol)?.ToDisplayString(),
                    Documentation = symbol.GetDocumentationCommentXml() ?? "",
                    Attributes = symbol.GetAttributes().Select(attribute =>
                        attribute.ToString() ?? "<unresolved-attribute>").Order().ToList(),
                    Parameters = Parameters(symbol).Select(parameter => new ParameterFact(
                        parameter.Name, parameter.Type.ToDisplayString(), parameter.RefKind.ToString(),
                        parameter.IsOptional, parameter.HasExplicitDefaultValue ?
                            Convert.ToString(parameter.ExplicitDefaultValue, CultureInfo.InvariantCulture) ?? "null" : null)).ToList(),
                    Source = source
                };
                AppendBody(fact, node, symbol, model, project, root, assemblies);
                facts.Add(id, fact);
            }
        }

        project.Symbols = facts.Values.OrderBy(fact => fact.Id, StringComparer.Ordinal).ToList();
        return project;
    }

    private static bool IsDeclaration(SyntaxNode node) => node is
        BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or BaseMethodDeclarationSyntax or
        PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax or EnumMemberDeclarationSyntax or LocalFunctionStatementSyntax
        || node is VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax };

    private static ITypeSymbol? ValueType(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.ReturnType,
        IPropertySymbol property => property.Type,
        IFieldSymbol field => field.Type,
        IEventSymbol @event => @event.Type,
        INamedTypeSymbol { DelegateInvokeMethod: { } method } => method.ReturnType,
        _ => null
    };

    private static IEnumerable<IParameterSymbol> Parameters(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.Parameters,
        IPropertySymbol property => property.Parameters,
        INamedTypeSymbol { DelegateInvokeMethod: { } method } => method.Parameters,
        _ => []
    };

    private static void AppendBody(SymbolFact fact, SyntaxNode node, ISymbol symbol, SemanticModel model,
        ProjectFact project, string root, IReadOnlyDictionary<string, string> assemblies)
    {
        var text = node switch
        {
            EnumDeclarationSyntax @enum => @enum.ToString(),
            BaseTypeDeclarationSyntax type when type.OpenBraceToken.RawKind != 0 && !type.OpenBraceToken.IsMissing =>
                node.SyntaxTree.GetText().ToString(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(
                    node.SpanStart, type.OpenBraceToken.SpanStart)),
            VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax field } => field.ToString(),
            _ => node.ToString()
        };
        var combined = string.IsNullOrEmpty(fact.Declaration) ? text : fact.Declaration + Environment.NewLine + text;
        if (combined.Length > MaximumDeclarationLength)
        {
            var length = MaximumDeclarationLength;
            if (char.IsHighSurrogate(combined[length - 1])) length--;
            combined = combined[..length];
            project.Diagnostics.Add(new("SOURCE_TRUNCATED", "warning",
                $"Declaration excerpt for {fact.Id} exceeds {MaximumDeclarationLength} characters; read original source.",
                SymbolIdentity.Source(node.GetLocation(), root)));
        }
        fact.Declaration = combined;
        var signals = new HashSet<string>(fact.MigrationSignals, StringComparer.Ordinal);
        void Relation(string kind, ISymbol target, SyntaxNode site) =>
            fact.Relationships.Add(new(kind, SymbolIdentity.Id(target, project.Id, assemblies, root),
                target.ToDisplayString(), SymbolIdentity.Source(site.GetLocation(), root)));

        if (symbol is INamedTypeSymbol named)
        {
            if (named.BaseType is { SpecialType: not SpecialType.System_Object } parent)
                Relation("inherits", parent, node);
            foreach (var @interface in named.Interfaces) Relation("implements", @interface, node);
            if (named.TypeKind == TypeKind.Delegate) signals.Add("delegate");
            if (node is TypeDeclarationSyntax { ParameterList: not null })
            {
                signals.Add("primary-constructor");
                project.Diagnostics.Add(new("PRIMARY_CONSTRUCTOR", "warning",
                    $"Type {fact.Id} declares a primary constructor. Its header is retained, " +
                    "but no separate callable constructor fact is emitted.",
                    SymbolIdentity.Source(node.GetLocation(), root)));
            }
            if (named.GetMembers().Any(member =>
                member.IsImplicitlyDeclared && SymbolIdentity.IsPublicApi(member) &&
                member is not IMethodSymbol { AssociatedSymbol: not null }))
            {
                signals.Add("synthesized-members");
                project.Diagnostics.Add(new("SYNTHESIZED_MEMBERS", "warning",
                    $"Type {fact.Id} has implicit public members (for example constructors or record helpers). " +
                    "This source-declaration extractor does not emit their synthesized implementations.",
                    SymbolIdentity.Source(node.GetLocation(), root)));
            }
        }
        if (symbol is IMethodSymbol { OverriddenMethod: { } overridden }) Relation("overrides", overridden, node);
        if (symbol is IMethodSymbol implementation)
            foreach (var target in implementation.ExplicitInterfaceImplementations) Relation("implements", target, node);
        if (symbol is IMethodSymbol { IsAsync: true }) signals.Add("async");
        if (symbol.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() ==
            "System.Runtime.InteropServices.DllImportAttribute")) signals.Add("native-interop");

        // Type bodies are analyzed through their members, not duplicated on the container.
        var nodes = node is BaseTypeDeclarationSyntax ?
            node.ChildNodes().Where(child => child is not MemberDeclarationSyntax).SelectMany(child => child.DescendantNodesAndSelf())
            : node.DescendantNodesAndSelf(descendIntoChildren: child =>
                child == node || child is not LocalFunctionStatementSyntax);
        foreach (var child in nodes)
        {
            switch (child)
            {
                case InvocationExpressionSyntax:
                case ObjectCreationExpressionSyntax:
                case ImplicitObjectCreationExpressionSyntax:
                    if (model.GetOperation(child) is INameOfOperation) break;
                    var target = model.GetSymbolInfo(child).Symbol;
                    if (target is not null)
                    {
                        Relation(child is InvocationExpressionSyntax ? "calls" : "constructs", target, child);
                        if (target.ContainingNamespace?.ToDisplayString().StartsWith("System.Reflection", StringComparison.Ordinal) == true
                            || target.Name == "GetType" && target.ContainingType?.SpecialType == SpecialType.System_Object)
                            signals.Add("reflection");
                    }
                    else
                    {
                        project.Diagnostics.Add(new("UNRESOLVED_CALL", "warning",
                            $"Call target could not be resolved in {fact.Id}.",
                            SymbolIdentity.Source(child.GetLocation(), root)));
                    }
                    break;
                case ThrowStatementSyntax statement:
                    signals.Add(statement.Expression is null ? "rethrow" : "throw");
                    if (statement.Expression is not null && model.GetTypeInfo(statement.Expression).Type is { } thrown)
                        Relation("throws", thrown, child);
                    break;
                case ThrowExpressionSyntax expression:
                    signals.Add("throw");
                    if (model.GetTypeInfo(expression.Expression).Type is { } expressionType)
                        Relation("throws", expressionType, child);
                    break;
                case CatchClauseSyntax clause:
                    signals.Add("catch");
                    if (clause.Declaration is not null && model.GetTypeInfo(clause.Declaration.Type).Type is { } caught)
                        Relation("catches", caught, child);
                    break;
                case TryStatementSyntax: signals.Add("try"); break;
                case CatchFilterClauseSyntax: signals.Add("catch-filter"); break;
                case FinallyClauseSyntax: signals.Add("finally"); break;
                case UsingStatementSyntax: signals.Add("using"); break;
                case LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 }: signals.Add("using"); break;
                case AwaitExpressionSyntax: signals.Add("await"); break;
                case LockStatementSyntax: signals.Add("lock"); break;
                case NullableTypeSyntax: signals.Add("nullable"); break;
                case CheckedExpressionSyntax:
                case CheckedStatementSyntax: signals.Add("explicit-overflow-context"); break;
                case UnsafeStatementSyntax:
                case PointerTypeSyntax:
                case FunctionPointerTypeSyntax: signals.Add("unsafe"); break;
                case YieldStatementSyntax: signals.Add("iterator"); break;
            }
            if (child is ExpressionSyntax or TypeSyntax)
            {
                var typeInfo = model.GetTypeInfo(child).Type;
                if (typeInfo?.SpecialType == SpecialType.System_Decimal) signals.Add("decimal");
                if (typeInfo?.TypeKind == TypeKind.Dynamic) signals.Add("dynamic");
                if (typeInfo?.IsReferenceType == true) signals.Add("reference-semantics");
            }
        }
        if (fact.Type == "decimal" || fact.Parameters.Any(parameter => parameter.Type == "decimal"))
            signals.Add("decimal");
        if (symbol is IMethodSymbol methodSymbol &&
            (methodSymbol.ReturnType.NullableAnnotation == NullableAnnotation.Annotated ||
             methodSymbol.Parameters.Any(parameter => parameter.NullableAnnotation == NullableAnnotation.Annotated)))
            signals.Add("nullable");

        fact.MigrationSignals = signals.Order(StringComparer.Ordinal).ToList();
        fact.Relationships = fact.Relationships.Distinct().OrderBy(relation => relation.Source.File, StringComparer.Ordinal)
            .ThenBy(relation => relation.Source.StartLine).ThenBy(relation => relation.Kind, StringComparer.Ordinal)
            .ThenBy(relation => relation.TargetId, StringComparer.Ordinal).ToList();
    }
}
