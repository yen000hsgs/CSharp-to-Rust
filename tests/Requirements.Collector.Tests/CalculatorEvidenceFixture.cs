using System.Security.Cryptography;
using System.Text.Json;
using CSharpToRust.Contracts;

namespace Requirements.Collector.Tests;

internal static class CalculatorEvidenceFixture
{
    private const string ProjectId = "project:Calculator.csproj";
    private const string TypeId = ProjectId + ":T:CalculatorSample.Calculator";

    public static ExtractionArtifact Create()
    {
        var extraction = new ExtractionArtifact
        {
            TaskId = "calculator-portable-fixture",
            InputPath = "Calculator.csproj",
            RootDirectory = ".",
            Status = "complete",
            Projects =
            [
                new ProjectFact
                {
                    Id = ProjectId, Name = "Calculator", File = "Calculator.csproj",
                    TargetFramework = ".NETCoreApp,Version=v8.0", OutputKind = "DynamicallyLinkedLibrary",
                    LanguageVersion = "CSharp12", NullableContext = "Enable", CheckOverflow = false,
                    Defines = ["DEBUG", "TRACE"],
                    Symbols =
                    [
                        new SymbolFact
                        {
                            Id = TypeId, Kind = "NamedType", Name = "Calculator",
                            DisplayName = "CalculatorSample.Calculator", Accessibility = "Public",
                            IsPublicApi = true, IsStatic = true, Source = new("Calculator.cs", 3, 20),
                            Declaration = "public static class Calculator\n"
                        },
                        Method("Add", "public static decimal Add(decimal left, decimal right) => left + right;", 5),
                        Method("Subtract", "public static decimal Subtract(decimal left, decimal right) => left - right;", 7),
                        Method("Multiply", "public static decimal Multiply(decimal left, decimal right) => left * right;", 9),
                        new SymbolFact
                        {
                            Id = ProjectId + ":M:CalculatorSample.Calculator.Divide(System.Decimal,System.Decimal)",
                            Kind = "Method", Name = "Divide", DisplayName = "CalculatorSample.Calculator.Divide(decimal, decimal)",
                            ContainingSymbolId = TypeId, Accessibility = "Public", IsPublicApi = true, IsStatic = true,
                            Type = "decimal", Source = new("Calculator.cs", 11, 19),
                            Parameters = [new("dividend", "decimal", "None", false, null), new("divisor", "decimal", "None", false, null)],
                            Declaration = string.Join("\n",
                                "public static decimal Divide(decimal dividend, decimal divisor)",
                                "    {",
                                "        if (divisor == 0m)",
                                "        {",
                                "            throw new DivideByZeroException(\"Cannot divide by zero.\");",
                                "        }",
                                "",
                                "        return dividend / divisor;",
                                "    }"),
                            Relationships = [new("throws", "external:T:System.DivideByZeroException", "System.DivideByZeroException", new("Calculator.cs", 15, 15))],
                            MigrationSignals = ["decimal", "throw"]
                        }
                    ]
                }
            ],
            Limitations =
            [
                "Synthetic typed test fixture, not a compiler-produced snapshot; source declarations are checked against the sample.",
                "Explicit throw relationships do not enumerate implicit decimal operator exceptions; tests are supplemental evidence."
            ]
        };
        RefreshIdentity(extraction);
        return extraction;
    }

    public static void RefreshIdentity(ExtractionArtifact extraction)
    {
        // This test-only digest is deliberately distinguished from extractor provenance.
        extraction.ExtractionId = "";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(extraction, ArtifactJson.Options);
        extraction.ExtractionId = "test-fixture-sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static SymbolFact Method(string name, string declaration, int line) => new()
    {
        Id = ProjectId + $":M:CalculatorSample.Calculator.{name}(System.Decimal,System.Decimal)",
        Kind = "Method", Name = name, DisplayName = $"CalculatorSample.Calculator.{name}(decimal, decimal)",
        ContainingSymbolId = TypeId, Accessibility = "Public", IsPublicApi = true, IsStatic = true,
        Type = "decimal", Source = new("Calculator.cs", line, line), Declaration = declaration,
        Parameters = [new("left", "decimal", "None", false, null), new("right", "decimal", "None", false, null)],
        MigrationSignals = ["decimal"]
    };
}
