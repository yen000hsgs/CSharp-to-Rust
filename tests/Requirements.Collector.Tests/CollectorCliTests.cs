using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSharpToRust.Contracts;

namespace Requirements.Collector.Tests;

public sealed class CollectorCliTests : IDisposable
{
    private readonly string root;
    private readonly string directory;
    private readonly string input;
    private readonly string output;

    public CollectorCliTests()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "samples", "Calculator.sln")))
            current = current.Parent;
        root = current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        directory = Path.Combine(root, "artifacts", "collector-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        input = Path.Combine(directory, "extraction.json");
        output = Path.Combine(directory, "requirements.json");
        Save(input, Extraction());
    }

    [Fact]
    public void PrepareCreatesDeterministicDraftWithoutInventingRequirements()
    {
        var result = Run("prepare-legacy", "--input", input, "--output", output);
        AssertSuccess(result);
        var first = File.ReadAllText(output);
        var draft = JsonSerializer.Deserialize<RequirementsArtifact>(first, ArtifactJson.Options)!;
        Assert.Equal("task-1", draft.TaskId);
        Assert.Equal("snapshot-1", draft.ExtractionId);
        Assert.Equal("draft", draft.Status);
        Assert.Empty(draft.Requirements);
        Assert.Single(draft.Coverage);
        Assert.Equal("pending", draft.Coverage[0].Disposition);
        Assert.NotEmpty(draft.Coverage[0].Reason);
        Assert.NotEmpty(draft.OpenQuestions);
        var next = Path.Combine(directory, "other.json");
        AssertSuccess(Run("prepare-legacy", "--input", input, "--output", next));
        Assert.Equal(first, File.ReadAllText(next));
        var validation = Run("validate-legacy", "--input", input, "--requirements", output);
        AssertSuccess(validation);
        Assert.Contains("\"status\": \"draft\"", validation.Stdout);
        Assert.Contains("\"complete\": false", validation.Stdout);
        Assert.Contains("\"semanticParityVerified\": false", validation.Stdout);
    }

    [Fact]
    public void ValidRequirementsAreTraceableButNotProofOfSemanticParity()
    {
        Save(output, Requirements());
        var result = Run("validate-legacy", "--input", input, "--requirements", output);
        AssertSuccess(result);
        Assert.Contains("\"complete\": true", result.Stdout);
        Assert.Contains("\"semanticParityVerified\": false", result.Stdout);
        Assert.Contains("task-1", result.Stdout);
        Assert.Contains("snapshot-1", result.Stdout);
    }

    [Fact]
    public void CalculatorFixturesDoNotRequireMachineSpecificSnapshotFiles()
    {
        var fixtures = Path.Combine(root, "tests", "Requirements.Collector.Tests", "Fixtures");
        Assert.False(File.Exists(Path.Combine(fixtures, "calculator-extraction.json")),
            "Real machine-specific snapshots belong in ignored artifacts, not portable regression fixtures.");
        Assert.True(File.Exists(Path.Combine(fixtures, "calculator-requirements.template.json")));
    }

    [Fact]
    public void PortableCalculatorFixtureHasReviewedEvidenceAndValidRequirements()
    {
        var fixtures = Path.Combine(root, "tests", "Requirements.Collector.Tests", "Fixtures");
        var extractionPath = input;
        var requirementsPath = Path.Combine(directory, "calculator-requirements.json");
        var extraction = CalculatorEvidenceFixture.Create();
        Save(extractionPath, extraction);
        var template = JsonSerializer.Deserialize<RequirementsArtifact>(
            File.ReadAllText(Path.Combine(fixtures, "calculator-requirements.template.json")), ArtifactJson.Options)!;
        Assert.Equal("fixture-binding-required", template.ExtractionId);
        template.TaskId = extraction.TaskId;
        template.ExtractionId = extraction.ExtractionId;
        Save(requirementsPath, template);
        Assert.StartsWith("test-fixture-sha256:", extraction.ExtractionId);
        Assert.False(Path.IsPathFullyQualified(extraction.RootDirectory));
        Assert.Equal("complete", extraction.Status);
        var project = Assert.Single(extraction.Projects);
        Assert.False(project.CheckOverflow);
        Assert.Equal(5, project.Symbols.Count);
        var source = File.ReadAllText(Path.Combine(root, "samples", "Calculator", "Calculator.cs"))
            .ReplaceLineEndings("\n");
        foreach (var symbol in project.Symbols)
            Assert.Contains(symbol.Declaration.ReplaceLineEndings("\n"), source, StringComparison.Ordinal);
        var before = File.ReadAllBytes(extractionPath);
        AssertSuccess(Run("prepare-legacy", "--input", extractionPath, "--output", output));
        var draft = JsonSerializer.Deserialize<RequirementsArtifact>(File.ReadAllText(output), ArtifactJson.Options)!;
        Assert.Equal("draft", draft.Status);
        Assert.Empty(draft.Requirements);
        Assert.Equal(5, draft.Coverage.Count);
        Assert.All(draft.Coverage, decision => Assert.Equal("pending", decision.Disposition));
        AssertSuccess(Run("validate-legacy", "--input", extractionPath, "--requirements", output));
        AssertSuccess(Run("validate-legacy", "--input", extractionPath, "--requirements", requirementsPath));
        Assert.Equal(before, File.ReadAllBytes(extractionPath));
        var requirements = JsonSerializer.Deserialize<RequirementsArtifact>(File.ReadAllText(requirementsPath), ArtifactJson.Options)!;
        Assert.Equal("partial", requirements.Status);
        Assert.Equal(9, requirements.Requirements.Count);
        Assert.Equal(5, requirements.Coverage.Count);
        Assert.All(requirements.Coverage, decision => Assert.Equal("required", decision.Disposition));
        Assert.Equal("Q-DECIMAL-BOUNDARIES", Assert.Single(requirements.OpenQuestions).Id);
        var zero = Assert.Single(requirements.Requirements.Where(requirement => requirement.Id == "CALC-DIVIDE-ZERO"));
        Assert.Contains(zero.ErrorBehavior, behavior => behavior.Contains("Cannot divide by zero.", StringComparison.Ordinal));
        var overflow = Assert.Single(requirements.Requirements.Where(requirement => requirement.Id == "CALC-OVERFLOW"));
        Assert.Equal("inferred", overflow.Confidence);
        Assert.Equal(4, overflow.EvidenceIds.Count);
        Assert.Contains(overflow.ErrorBehavior, behavior => behavior.Contains("OverflowException", StringComparison.Ordinal));
        Assert.Contains(overflow.MigrationNotes, note => note.Contains("checkOverflow=false", StringComparison.Ordinal));
        Assert.Contains(overflow.MigrationNotes, note => note.Contains("supplemental", StringComparison.Ordinal));
    }

    [Fact]
    public void PortableSnapshotIdentityIsDeterministicAndChangesWithItsContents()
    {
        var extraction = CalculatorEvidenceFixture.Create();
        Assert.Equal(extraction.ExtractionId, CalculatorEvidenceFixture.Create().ExtractionId);
        foreach (var change in new Action<ExtractionArtifact>[]
        {
            artifact => artifact.RootDirectory = "other-root",
            artifact => artifact.Projects[0].File = "Other.csproj",
            artifact => artifact.Projects[0].Symbols[0].Declaration += " ",
            artifact => artifact.Limitations.Add("Another limitation.")
        })
        {
            var changed = CalculatorEvidenceFixture.Create();
            change(changed);
            CalculatorEvidenceFixture.RefreshIdentity(changed);
            Assert.NotEqual(extraction.ExtractionId, changed.ExtractionId);
        }
    }

    [Fact]
    public void ZeroDiagnosticsDoesNotHideLimitationsOrVerifyParity()
    {
        var extraction = Extraction();
        extraction.Limitations = ["Runtime behavior still requires review.", "Only one build configuration was extracted."];
        Save(input, extraction);
        Save(output, Requirements());
        var result = Run("validate-legacy", "--input", input, "--requirements", output);
        AssertSuccess(result);
        using var summary = JsonDocument.Parse(result.Stdout);
        Assert.Equal(0, summary.RootElement.GetProperty("diagnostics").GetProperty("count").GetInt32());
        Assert.Equal(2, summary.RootElement.GetProperty("diagnostics").GetProperty("limitations").GetInt32());
        Assert.False(summary.RootElement.GetProperty("semanticParityVerified").GetBoolean());
    }

    [Fact]
    public void ValidationDoesNotPromoteInferredConfidenceToClaimCompletion()
    {
        var requirements = Requirements();
        requirements.Requirements[0].Confidence = "inferred";
        Save(output, requirements);
        var before = File.ReadAllBytes(output);
        AssertSuccess(Run("validate-legacy", "--input", input, "--requirements", output));
        Assert.Equal(before, File.ReadAllBytes(output));
        Assert.Equal("inferred", JsonSerializer.Deserialize<RequirementsArtifact>(
            File.ReadAllText(output), ArtifactJson.Options)!.Requirements[0].Confidence);
    }

    [Fact]
    public void ExclusionWithRationaleCoversAnOtherwiseUnrequiredPublicSymbol()
    {
        var requirements = Requirements();
        requirements.Requirements.Clear();
        requirements.Coverage = [new("p:M:Demo.Read", "excluded", "Test-only surface is not shipped.")];
        Save(output, requirements);
        AssertSuccess(Run("validate-legacy", "--input", input, "--requirements", output));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("missing-schema")]
    [InlineData("missing-boolean")]
    [InlineData("null-projects")]
    [InlineData("null-project")]
    [InlineData("null-symbols")]
    [InlineData("null-symbol")]
    [InlineData("duplicate-symbol")]
    [InlineData("duplicate-project")]
    [InlineData("invalid-span")]
    [InlineData("null-source")]
    [InlineData("blank-file")]
    [InlineData("invalid-file")]
    [InlineData("invalid-input-path")]
    [InlineData("invalid-root-path")]
    [InlineData("invalid-project-path")]
    [InlineData("invalid-additional-span")]
    [InlineData("null-additional-source")]
    [InlineData("null-parameter")]
    [InlineData("nonoptional-default")]
    [InlineData("null-relationship")]
    [InlineData("invalid-relationship-span")]
    [InlineData("null-attribute")]
    [InlineData("null-diagnostic")]
    [InlineData("invalid-diagnostic-span")]
    [InlineData("unknown-property")]
    [InlineData("unknown-nested-property")]
    [InlineData("duplicate-property")]
    [InlineData("case-alias")]
    [InlineData("numeric-string")]
    [InlineData("status")]
    [InlineData("failed")]
    [InlineData("complete-with-error")]
    [InlineData("blank-optional")]
    [InlineData("blank-task")]
    [InlineData("blank-extraction")]
    [InlineData("null-limitations")]
    [InlineData("null-migration-signals")]
    public void InvalidExtractionIsRejectedWithoutReplacingAnArtifact(string mutation)
    {
        var json = JsonSerializer.SerializeToNode(Extraction(), ArtifactJson.Options)!;
        var project = json["projects"]![0]!;
        var symbol = project["symbols"]![0]!;
        switch (mutation)
        {
            case "schema": json["schemaVersion"] = "2.0"; break;
            case "missing-schema": json.AsObject().Remove("schemaVersion"); break;
            case "missing-boolean": symbol.AsObject().Remove("isPublicApi"); break;
            case "null-projects": json["projects"] = null; break;
            case "null-project": json["projects"]![0] = null; break;
            case "null-symbols": project["symbols"] = null; break;
            case "null-symbol": project["symbols"]![0] = null; break;
            case "duplicate-symbol": project["symbols"]!.AsArray().Add(symbol.DeepClone()); break;
            case "duplicate-project": json["projects"]!.AsArray().Add(project.DeepClone()); break;
            case "invalid-span": symbol["source"]!["endLine"] = 0; break;
            case "null-source": symbol["source"] = null; break;
            case "blank-file": symbol["source"]!["file"] = ""; break;
            case "invalid-file": symbol["source"]!["file"] = "bad\0.cs"; break;
            case "invalid-input-path": json["inputPath"] = "bad\0.csproj"; break;
            case "invalid-root-path": json["rootDirectory"] = "bad\0"; break;
            case "invalid-project-path": project["file"] = "bad\0.csproj"; break;
            case "invalid-additional-span":
                symbol["additionalSources"] = JsonSerializer.SerializeToNode(new[] { new SourceReference("Other.cs", -1, 4) }); break;
            case "null-additional-source": symbol["additionalSources"] = new JsonArray((JsonNode?)null); break;
            case "null-parameter": symbol["parameters"] = new JsonArray((JsonNode?)null); break;
            case "nonoptional-default":
                symbol["parameters"] = JsonSerializer.SerializeToNode(new[] { new ParameterFact("a", "int", "None", false, "7") }, ArtifactJson.Options); break;
            case "null-relationship": symbol["relationships"] = new JsonArray((JsonNode?)null); break;
            case "invalid-relationship-span":
                symbol["relationships"] = JsonSerializer.SerializeToNode(new[] { new RelationshipFact("Calls", "external:M:X", "X", new("Demo.cs", 7, 2)) }, ArtifactJson.Options); break;
            case "null-attribute": symbol["attributes"] = new JsonArray((JsonNode?)null); break;
            case "null-diagnostic": json["diagnostics"] = new JsonArray((JsonNode?)null); break;
            case "invalid-diagnostic-span":
                json["diagnostics"] = JsonSerializer.SerializeToNode(new[] { new DiagnosticFact("CS1", "Warning", "warning", new("Demo.cs", 0, 0)) }, ArtifactJson.Options); break;
            case "unknown-property": json["invented"] = true; break;
            case "unknown-nested-property": symbol["source"]!["invented"] = true; break;
            case "case-alias": json["TaskId"] = "other"; break;
            case "numeric-string": symbol["source"]!["startLine"] = "1"; break;
            case "status": json["status"] = "ready"; break;
            case "failed": json["status"] = "failed"; break;
            case "complete-with-error":
                json["diagnostics"] = JsonSerializer.SerializeToNode(new[] { new DiagnosticFact("CS1", "Error", "error") }, ArtifactJson.Options); break;
            case "blank-optional": project["targetFramework"] = " "; break;
            case "blank-task": json["taskId"] = ""; break;
            case "blank-extraction": json["extractionId"] = " "; break;
            case "null-limitations": json["limitations"] = null; break;
            case "null-migration-signals": symbol["migrationSignals"] = null; break;
        }
        var text = json.ToJsonString();
        if (mutation == "duplicate-property")
            text = text.Replace("\"taskId\":\"task-1\"", "\"taskId\":\"task-1\",\"taskId\":\"task-1\"", StringComparison.Ordinal);
        File.WriteAllText(input, text);
        AssertInvalid(Run("inspect", "--input", input, "--symbol", "p:M:Demo.Read"));
        var newOutput = Path.Combine(directory, "must-not-exist.json");
        AssertInvalid(Run("prepare-legacy", "--input", input, "--output", newOutput));
        Assert.False(File.Exists(newOutput));
        File.WriteAllText(output, "existing artifact");
        var result = Run("prepare-legacy", "--input", input, "--output", output, "--force");
        AssertInvalid(result);
        Assert.Equal("existing artifact", File.ReadAllText(output));
        Assert.Empty(Directory.GetFiles(directory, "*.pending"));
    }

    [Theory]
    [InlineData("task")]
    [InlineData("snapshot")]
    [InlineData("schema")]
    [InlineData("status")]
    [InlineData("null-requirements")]
    [InlineData("null-requirement")]
    [InlineData("blank-id")]
    [InlineData("duplicate-id")]
    [InlineData("blank-statement")]
    [InlineData("empty-criteria")]
    [InlineData("null-criterion")]
    [InlineData("blank-criterion")]
    [InlineData("null-inputs")]
    [InlineData("confidence")]
    [InlineData("empty-evidence")]
    [InlineData("unknown-evidence")]
    [InlineData("duplicate-evidence")]
    [InlineData("unknown-dependency")]
    [InlineData("cycle")]
    [InlineData("missing-coverage")]
    [InlineData("duplicate-coverage")]
    [InlineData("unknown-coverage")]
    [InlineData("blank-rationale")]
    [InlineData("invalid-disposition")]
    [InlineData("required-without-requirement")]
    [InlineData("excluded-with-requirement")]
    [InlineData("pending-complete")]
    [InlineData("null-coverage")]
    [InlineData("null-coverage-record")]
    [InlineData("open-questions")]
    [InlineData("null-open-questions")]
    [InlineData("null-open-question")]
    [InlineData("unknown-question-evidence")]
    [InlineData("duplicate-question")]
    [InlineData("blank-question")]
    [InlineData("uncertain-complete")]
    [InlineData("partial-extraction")]
    public void InvalidRequirementsFailExplicitlyAndRemainUnchanged(string mutation)
    {
        var requirements = Requirements();
        var json = JsonSerializer.SerializeToNode(requirements, ArtifactJson.Options)!;
        var requirement = json["requirements"]![0]!;
        switch (mutation)
        {
            case "task": json["taskId"] = "wrong"; break;
            case "snapshot": json["extractionId"] = "wrong"; break;
            case "schema": json["schemaVersion"] = "2"; break;
            case "status": json["status"] = "ready"; break;
            case "null-requirements": json["requirements"] = null; break;
            case "null-requirement": json["requirements"]![0] = null; break;
            case "blank-id": requirement["id"] = " "; break;
            case "duplicate-id": json["requirements"]!.AsArray().Add(requirement.DeepClone()); break;
            case "blank-statement": requirement["statement"] = " "; break;
            case "empty-criteria": requirement["acceptanceCriteria"] = new JsonArray(); break;
            case "null-criterion": requirement["acceptanceCriteria"] = new JsonArray((JsonNode?)null); break;
            case "blank-criterion": requirement["acceptanceCriteria"] = new JsonArray(""); break;
            case "null-inputs": requirement["inputs"] = null; break;
            case "confidence": requirement["confidence"] = "certain"; break;
            case "empty-evidence": requirement["evidenceIds"] = new JsonArray(); break;
            case "unknown-evidence": requirement["evidenceIds"] = new JsonArray("M:NotInSnapshot"); break;
            case "duplicate-evidence": requirement["evidenceIds"]!.AsArray().Add("p:M:Demo.Read"); break;
            case "unknown-dependency": requirement["dependsOn"] = new JsonArray("REQ-missing"); break;
            case "cycle": requirement["dependsOn"] = new JsonArray("REQ-1"); break;
            case "missing-coverage": json["coverage"] = new JsonArray(); break;
            case "duplicate-coverage": json["coverage"]!.AsArray().Add(json["coverage"]![0]!.DeepClone()); break;
            case "unknown-coverage": json["coverage"]![0]!["symbolId"] = "M:unknown"; break;
            case "blank-rationale": json["coverage"]![0]!["reason"] = ""; break;
            case "invalid-disposition": json["coverage"]![0]!["disposition"] = "covered"; break;
            case "required-without-requirement": json["requirements"] = new JsonArray(); break;
            case "excluded-with-requirement": json["coverage"]![0]!["disposition"] = "excluded"; break;
            case "pending-complete": json["coverage"]![0]!["disposition"] = "pending"; break;
            case "null-coverage": json["coverage"] = null; break;
            case "null-coverage-record": json["coverage"]![0] = null; break;
            case "open-questions":
                json["openQuestions"] = JsonSerializer.SerializeToNode(new[] { new OpenQuestion("Q-1", "What is returned?", []) }, ArtifactJson.Options); break;
            case "null-open-questions": json["openQuestions"] = null; break;
            case "null-open-question": json["openQuestions"] = new JsonArray((JsonNode?)null); break;
            case "unknown-question-evidence":
                json["status"] = "partial";
                json["openQuestions"] = JsonSerializer.SerializeToNode(new[] { new OpenQuestion("Q-1", "What is returned?", ["bad"]) }, ArtifactJson.Options); break;
            case "duplicate-question":
                json["status"] = "partial";
                json["openQuestions"] = JsonSerializer.SerializeToNode(new[] { new OpenQuestion("Q-1", "Question?", []), new OpenQuestion("Q-1", "Other?", []) }, ArtifactJson.Options); break;
            case "blank-question":
                json["status"] = "partial";
                json["openQuestions"] = JsonSerializer.SerializeToNode(new[] { new OpenQuestion("Q-1", "", []) }, ArtifactJson.Options); break;
            case "uncertain-complete": requirement["confidence"] = "uncertain"; break;
            case "partial-extraction":
                var extraction = Extraction(); extraction.Status = "partial"; Save(input, extraction); break;
        }
        File.WriteAllText(output, json.ToJsonString());
        var before = File.ReadAllBytes(output);
        AssertInvalid(Run("validate-legacy", "--input", input, "--requirements", output));
        Assert.Equal(before, File.ReadAllBytes(output));
    }

    [Fact]
    public void IndirectDependencyCycleIsRejected()
    {
        var requirements = Requirements();
        requirements.Requirements[0].DependsOn = ["REQ-2"];
        requirements.Requirements.Add(new Requirement
        {
            Id = "REQ-2", Feature = "Reading", Statement = "Reading returns one.",
            EvidenceIds = ["p:M:Demo.Read"], AcceptanceCriteria = ["Read() returns 1."],
            DependsOn = ["REQ-1"]
        });
        Save(output, requirements);
        var result = Run("validate-legacy", "--input", input, "--requirements", output);
        AssertInvalid(result);
        Assert.Contains("cycle", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":\"1.0\",}")]
    public void MalformedInputCannotCreateOutput(string text)
    {
        File.WriteAllText(input, text);
        AssertInvalid(Run("prepare-legacy", "--input", input, "--output", output));
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData("extraction", "\\uD800", false)]
    [InlineData("extraction", "\\uDC00", false)]
    [InlineData("extraction", "\\uD800", true)]
    [InlineData("extraction", "\\uDC00", true)]
    [InlineData("requirements", "\\uD800", false)]
    [InlineData("requirements", "\\uDC00", false)]
    [InlineData("requirements", "\\uD800", true)]
    [InlineData("requirements", "\\uDC00", true)]
    [InlineData("force-output", "\\uD800", false)]
    [InlineData("force-output", "\\uDC00", false)]
    [InlineData("force-output", "\\uD800", true)]
    [InlineData("force-output", "\\uDC00", true)]
    public void MalformedUnicodePropertyNamesReturnDocumentedErrorAndPreserveFiles(
        string surface, string escapedSurrogate, bool nested)
    {
        var malformed = "{\"" + escapedSurrogate + "\":0}";
        if (nested)
            malformed = "{\"nested\":[" + malformed + "]}";
        Save(output, Requirements());
        File.WriteAllText(surface == "extraction" ? input : output, malformed);
        var originalInput = File.ReadAllBytes(input);
        var originalOutput = File.ReadAllBytes(output);

        var result = surface == "requirements"
            ? Run("validate-legacy", "--input", input, "--requirements", output)
            : Run("prepare-legacy", "--input", input, "--output", output, "--force");

        AssertInvalid(result);
        Assert.Contains("Unicode", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unhandled exception", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Stdout);
        Assert.Equal(originalInput, File.ReadAllBytes(input));
        Assert.Equal(originalOutput, File.ReadAllBytes(output));
        Assert.Empty(Directory.GetFiles(directory, "*.pending"));
    }

    [Fact]
    public void PartialExtractionProducesDraftAndRetainsUncertainty()
    {
        var extraction = Extraction();
        extraction.Status = "partial";
        extraction.Diagnostics.Add(new("CS42", "Warning", "Missing generated body."));
        extraction.Limitations.Add("Generated source unavailable.");
        Save(input, extraction);
        AssertSuccess(Run("prepare-legacy", "--input", input, "--output", output));
        var draft = JsonSerializer.Deserialize<RequirementsArtifact>(File.ReadAllText(output), ArtifactJson.Options)!;
        Assert.Equal("draft", draft.Status);
        Assert.Contains(draft.OpenQuestions, question => question.Question.Contains("partial", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NonpublicEvidenceAndExternalRelationshipsDoNotAddPublicCoverage()
    {
        var extraction = Extraction();
        var internalSymbol = new SymbolFact
        {
            Id = "p:M:Demo.Helper", Name = "Helper", Kind = "Method", DisplayName = "Demo.Helper()",
            Accessibility = "Private", Declaration = "private int Helper() => 1;", Source = new("Demo.cs", 2, 2),
            Parameters = [new("value", "object", "None", true, null)],
            Relationships = [new("Invocation", "M:External.Read", "External.Read()", new("Demo.cs", 2, 2))]
        };
        extraction.Projects[0].Symbols.Add(internalSymbol);
        Save(input, extraction);
        var requirements = Requirements();
        requirements.Requirements[0].EvidenceIds.Add(internalSymbol.Id);
        Save(output, requirements);
        AssertSuccess(Run("validate-legacy", "--input", input, "--requirements", output));
        AssertSuccess(Run("inspect", "--input", input, "--symbol", internalSymbol.Id));
    }

    [Fact]
    public void PartialRequirementsAllowUncertaintyAndPendingCoverage()
    {
        var requirements = Requirements();
        requirements.Status = "partial";
        requirements.Requirements[0].Confidence = "uncertain";
        requirements.Coverage[0] = requirements.Coverage[0] with { Disposition = "pending" };
        requirements.OpenQuestions.Add(new("Q-1", "Does this hold for all supported configurations?", ["p:M:Demo.Read"]));
        Save(output, requirements);
        var result = Run("validate-legacy", "--input", input, "--requirements", output);
        AssertSuccess(result);
        Assert.Contains("\"complete\": false", result.Stdout);
    }

    [Fact]
    public void InspectionDoesNotSilentlyCorruptUnicodeAtPageBoundaries()
    {
        var extraction = Extraction();
        extraction.Projects[0].Symbols[0].Declaration = "a\U0001F600z";
        Save(input, extraction);
        var result = Run("inspect", "--input", input, "--symbol", "p:M:Demo.Read", "--max-chars", "2");
        AssertSuccess(result);
        using var packet = JsonDocument.Parse(result.Stdout);
        Assert.Equal("a", packet.RootElement.GetProperty("declaration").GetString());
        Assert.Equal(1, packet.RootElement.GetProperty("nextOffset").GetInt32());
        AssertInvalid(Run("inspect", "--input", input, "--symbol", "p:M:Demo.Read", "--offset", "2"));
        AssertInvalid(Run("inspect", "--input", input, "--symbol", "p:M:Demo.Read", "--offset", "1", "--max-chars", "1"));
    }

    [Fact]
    public void OversizedInspectionMetadataFailsRatherThanDumpingTheProject()
    {
        var extraction = Extraction();
        extraction.Projects[0].Symbols[0].Attributes = [new string('a', 65537)];
        Save(input, extraction);
        var result = Run("inspect", "--input", input, "--symbol", "p:M:Demo.Read");
        AssertInvalid(result);
        Assert.Empty(result.Stdout);
        Assert.Contains("packet limit", result.Stderr);
    }

    [Fact]
    public void FailedExtractionWithNoProjectsCannotBecomeComplete()
    {
        var extraction = Extraction();
        extraction.Projects.Clear();
        Save(input, extraction);
        AssertInvalid(Run("prepare-legacy", "--input", input, "--output", output));
        extraction.Status = "partial";
        extraction.Diagnostics.Add(new("LOAD", "error", "Project could not be loaded."));
        Save(input, extraction);
        AssertSuccess(Run("prepare-legacy", "--input", input, "--output", output));
        AssertSuccess(Run("validate-legacy", "--input", input, "--requirements", output));
    }

    [Fact]
    public void InspectIsBoundedAndNeverReadsAnArbitrarySourceFile()
    {
        var extraction = Extraction();
        extraction.Projects[0].Symbols[0].Declaration = new string('x', 1000);
        extraction.Projects[0].Symbols[0].Documentation = "UNTRUSTED " + new string('y', 10000);
        Save(input, extraction);
        var result = Run("inspect", "--input", input, "--symbol", "p:M:Demo.Read", "--offset", "0", "--max-chars", "100");
        AssertSuccess(result);
        using var packet = JsonDocument.Parse(result.Stdout);
        Assert.Equal("task-1", packet.RootElement.GetProperty("taskId").GetString());
        Assert.Equal(100, packet.RootElement.GetProperty("declaration").GetString()!.Length);
        Assert.True(packet.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(100, packet.RootElement.GetProperty("nextOffset").GetInt32());
        Assert.DoesNotContain("UNTRUSTED", result.Stdout);
        var last = Run("inspect", "--input", input, "--symbol", "p:M:Demo.Read", "--offset", "900", "--max-chars", "100");
        AssertSuccess(last);
        Assert.Contains("\"truncated\": false", last.Stdout);
    }

    [Theory]
    [InlineData("--symbol", "missing")]
    [InlineData("--max-chars", "0")]
    [InlineData("--max-chars", "999999999")]
    [InlineData("--offset", "-1")]
    [InlineData("--offset", "999999")]
    public void InvalidInspectionArgumentsFail(string option, string value)
    {
        var arguments = new List<string> { "inspect", "--input", input };
        if (option != "--symbol")
            arguments.AddRange(["--symbol", "p:M:Demo.Read"]);
        arguments.AddRange([option, value]);
        AssertInvalid(Run([.. arguments]));
    }

    [Fact]
    public void OutputRequiresCreateNewOrExplicitForceForSameSnapshotRequirements()
    {
        AssertSuccess(Run("prepare-legacy", "--input", input, "--output", output));
        var before = File.ReadAllBytes(output);
        AssertInvalid(Run("prepare-legacy", "--input", input, "--output", output));
        Assert.Equal(before, File.ReadAllBytes(output));
        AssertSuccess(Run("prepare-legacy", "--input", input, "--output", output, "--force"));
        var foreign = Requirements();
        foreign.ExtractionId = "different-snapshot";
        Save(output, foreign);
        before = File.ReadAllBytes(output);
        AssertInvalid(Run("prepare-legacy", "--input", input, "--output", output, "--force"));
        Assert.Equal(before, File.ReadAllBytes(output));
    }

    [Fact]
    public void OutputCannotOverwriteExtractionOrSourceEvenWithForce()
    {
        var before = File.ReadAllBytes(input);
        AssertInvalid(Run("prepare-legacy", "--input", input, "--output", input, "--force"));
        Assert.Equal(before, File.ReadAllBytes(input));
        var source = Path.Combine(directory, "Demo.cs");
        File.WriteAllText(source, "do not change source");
        AssertInvalid(Run("prepare-legacy", "--input", input, "--output", source, "--force"));
        Assert.Equal("do not change source", File.ReadAllText(source));
        var extraction = Extraction();
        extraction.RootDirectory = directory;
        extraction.Projects[0].Symbols[0].Source = new("evidence.json", 1, 1);
        Save(input, extraction);
        var evidence = Path.Combine(directory, "evidence.json");
        Save(evidence, Requirements());
        before = File.ReadAllBytes(evidence);
        AssertInvalid(Run("prepare-legacy", "--input", input, "--output", evidence, "--force"));
        Assert.Equal(before, File.ReadAllBytes(evidence));
    }

    [Fact]
    public void InvalidOutputDoesNotCreateDirectoriesOrClobberUnrelatedJson()
    {
        var invalidExtension = Path.Combine(directory, "new", "Output.cs");
        AssertInvalid(Run("prepare-legacy", "--input", input, "--output", invalidExtension));
        Assert.False(Directory.Exists(Path.GetDirectoryName(invalidExtension)));
        File.WriteAllText(output, "{\"important\":true}");
        AssertInvalid(Run("prepare-legacy", "--input", input, "--output", output, "--force"));
        Assert.Equal("{\"important\":true}", File.ReadAllText(output));
    }

    [Fact]
    public void ForceDoesNotReplaceRequirementsWithNullRecords()
    {
        var json = JsonSerializer.SerializeToNode(Requirements(), ArtifactJson.Options)!;
        json["coverage"] = new JsonArray((JsonNode?)null);
        File.WriteAllText(output, json.ToJsonString());
        var before = File.ReadAllBytes(output);
        AssertInvalid(Run("prepare-legacy", "--input", input, "--output", output, "--force"));
        Assert.Equal(before, File.ReadAllBytes(output));
    }

    [Fact]
    public void ArgumentsAndMissingFilesHaveExplicitNonzeroDiagnostics()
    {
        foreach (var arguments in new[]
        {
            Array.Empty<string>(),
            new[] { "unknown" },
            new[] { "prepare-legacy", "--input", input, "--output", output, "--bogus" },
            new[] { "prepare-legacy", "--input", input, "--input", input, "--output", output },
            new[] { "prepare-legacy", "--input", input, "--output" },
            new[] { "prepare-legacy", "--input", Path.Combine(directory, "missing.json"), "--output", output },
            new[] { "validate-legacy", "--input", input, "--requirements", output, "--force" }
        })
            AssertInvalid(Run(arguments));
    }

    private ExtractionArtifact Extraction() => new()
    {
        TaskId = "task-1", ExtractionId = "snapshot-1", InputPath = Path.Combine(directory, "Demo.csproj"),
        RootDirectory = directory, Status = "complete",
        Projects =
        [
            new ProjectFact
            {
                Id = "p", Name = "Demo", File = "Demo.csproj", TargetFramework = "net8.0",
                OutputKind = "DynamicallyLinkedLibrary", LanguageVersion = "CSharp12", NullableContext = "Enable",
                Symbols =
                [
                    new SymbolFact
                    {
                        Id = "p:M:Demo.Read", Kind = "Method", Name = "Read", DisplayName = "Demo.Read()",
                        Accessibility = "Public", IsPublicApi = true, Type = "int",
                        Source = new("Demo.cs", 1, 1), Declaration = "public int Read() => 1;"
                    }
                ]
            }
        ]
    };

    private static RequirementsArtifact Requirements() => new()
    {
        TaskId = "task-1", ExtractionId = "snapshot-1", Status = "complete",
        Requirements =
        [
            new Requirement
            {
                Id = "REQ-1", Feature = "Reading", Statement = "Read returns one.",
                Confidence = "confirmed", EvidenceIds = ["p:M:Demo.Read"],
                Outputs = ["Integer 1"], AcceptanceCriteria = ["Calling Read() returns integer 1."]
            }
        ],
        Coverage = [new("p:M:Demo.Read", "required", "Return value is the public behavior.")]
    };

    private static void Save<T>(string path, T value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, ArtifactJson.Options));

    private Result Run(params string[] arguments)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(root, "src", "Requirements.Collector", "bin", configuration, "net8.0", "Requirements.Collector.dll"));
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30000), "Collector timed out.");
        return new(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static void AssertSuccess(Result result) =>
        Assert.True(result.ExitCode == 0, $"Exit {result.ExitCode}: {result.Stderr}\n{result.Stdout}");

    private static void AssertInvalid(Result result)
    {
        Assert.True(result.ExitCode == 2, $"Expected validation/usage exit 2, got {result.ExitCode}: {result.Stderr}\n{result.Stdout}");
        Assert.Contains("error", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
    private sealed record Result(int ExitCode, string Stdout, string Stderr);
}
