using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSharpToRust.Contracts;

namespace Requirements.Collector.Tests;

public sealed class DocumentCliTests : IDisposable
{
    private const string SymbolId = "p:M:Odd.doSthUncommon(System.String)";
    private const string FeatureId = "odd.do_sth_uncommon";
    private readonly string root;
    private readonly string directory;
    private readonly string input;
    private readonly string document;
    private readonly string context;

    public DocumentCliTests()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "samples", "Calculator.sln")))
            current = current.Parent;
        root = current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        directory = Path.Combine(root, "artifacts", "collector-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        input = Path.Combine(directory, "extraction.json");
        document = Path.Combine(directory, "document.json");
        context = Path.Combine(directory, "document.context.json");
        Save(input, Extraction());
    }

    [Fact]
    public void PrepareCreatesNewPairedDraftWithoutInventingFeatures()
    {
        Success(Run("prepare", "--input", input, "--output", document, "--context", context));
        var draft = Read(document);
        Assert.Equal(new[] { "source", "features" }, draft.AsObject().Select(property => property.Key));
        Assert.Empty(draft["features"]!.AsArray());
        Assert.Equal("csharp", draft["source"]!["language"]!.GetValue<string>());
        Assert.Equal("library", draft["source"]!["kind"]!.GetValue<string>());
        Assert.Equal(directory, draft["source"]!["root"]!.GetValue<string>());
        var metadata = Read(context);
        Assert.Equal("2.0", metadata["schemaVersion"]!.GetValue<string>());
        Assert.Equal("draft", metadata["status"]!.GetValue<string>());
        Assert.Equal(document, metadata["documentPath"]!.GetValue<string>());
        Assert.Equal("pending", Assert.Single(metadata["coverage"]!.AsArray())!["disposition"]!.GetValue<string>());
        Assert.NotEmpty(metadata["openQuestions"]!.AsArray());
        Assert.Empty(metadata["featureEvidence"]!.AsArray());
        var validation = Validate();
        Success(validation);
        Assert.False(ReadSummary(validation)["readyForDownstream"]!.GetValue<bool>());
        Invalid(Validate(ready: true));
    }

    [Fact]
    public void PrShapedDocumentAndSeparateContextAreReadyWithoutClaimingParity()
    {
        WriteAuthored();
        var before = File.ReadAllBytes(document);
        var result = Validate(ready: true);
        Success(result);
        var summary = ReadSummary(result);
        Assert.Equal("synthetic-test", summary["taskId"]!.GetValue<string>());
        Assert.Equal("fixture-only-1", summary["extractionId"]!.GetValue<string>());
        Assert.Equal("complete", summary["status"]!.GetValue<string>());
        Assert.True(summary["structureAndTraceabilityValid"]!.GetValue<bool>());
        Assert.True(summary["readyForDownstream"]!.GetValue<bool>());
        Assert.False(summary["semanticParityVerified"]!.GetValue<bool>());
        Assert.Equal(1, summary["features"]!.GetValue<int>());
        Assert.Equal(2, summary["requirements"]!.GetValue<int>());
        Assert.Equal(1, summary["coverage"]!.GetValue<int>());
        Assert.Equal(0, summary["openQuestions"]!.GetValue<int>());
        Assert.Equal(before, File.ReadAllBytes(document));
    }

    [Fact]
    public void DownstreamCalculatorFixturePassesActualStrictStructuralValidationWithoutCertifyingItsSemantics()
    {
        var fixturePath = Path.Combine(root, "tools", "testdata", "calculator-document.json");
        var original = File.ReadAllBytes(fixturePath);
        var fixture = Read(fixturePath);
        var extraction = Extraction();
        extraction.Status = "partial";
        extraction.RootDirectory = Path.GetFullPath(fixture["source"]!["root"]!.GetValue<string>(),
            Path.GetDirectoryName(fixturePath)!);
        Save(input, extraction);

        // Synthetic association exercises the wire contract, not the fixture's behavioral claims.
        var metadata = Metadata();
        metadata["documentPath"] = fixturePath;
        metadata["status"] = "partial";
        metadata["upstreamStatus"] = "partial";
        metadata["featureEvidence"] = new JsonArray(fixture["features"]!.AsArray().Select(feature =>
            (JsonNode)new JsonObject
            {
                ["featureId"] = feature!["id"]!.GetValue<string>(), ["confidence"] = "uncertain",
                ["evidenceIds"] = new JsonArray(SymbolId)
            }).ToArray());
        metadata["openQuestions"] = new JsonArray(new JsonObject
        {
            ["id"] = "Q-STRUCTURAL-ONLY",
            ["question"] = "This synthetic compatibility check does not establish behavioral truth.",
            ["evidenceIds"] = new JsonArray()
        });
        Save(context, metadata);

        var result = Run("validate", "--input", input, "--document", fixturePath, "--context", context);
        Success(result);
        var summary = ReadSummary(result);
        Assert.True(summary["structureAndTraceabilityValid"]!.GetValue<bool>());
        Assert.Equal(5, summary["features"]!.GetValue<int>());
        Assert.Equal(42, summary["requirements"]!.GetValue<int>());
        Assert.False(summary["readyForDownstream"]!.GetValue<bool>());
        Assert.False(summary["semanticParityVerified"]!.GetValue<bool>());
        Invalid(Run("validate", "--input", input, "--document", fixturePath, "--context", context, "--require-ready"));
        Assert.Equal(original, File.ReadAllBytes(fixturePath));
    }

    [Theory]
    [InlineData("name", "Odd library")]
    [InlineData("target_crate", "odd_library")]
    public void OptionalSourceFieldsAreAccepted(string key, string value)
    {
        var authored = Authored();
        authored["source"]![key] = value;
        WriteAuthored(authored);
        Success(Validate(ready: true));
    }

    [Fact]
    public void ArbitraryMethodAndJsonExamplesRemainAuthoredAndLossless()
    {
        var authored = Authored();
        authored["features"]![0]!["examples"]!.AsArray().Add(JsonNode.Parse(
            """{"id":"odd.do_sth_uncommon.x2","input":{"value":"","huge":123456789012345678901234567890,"nested":{"value":null}},"expected":null}"""));
        WriteAuthored(authored);
        Success(Validate(ready: true));
        Assert.Contains("123456789012345678901234567890", File.ReadAllText(document));
        var rendered = Path.Combine(directory, "requirements.md");
        Success(Render(rendered));
        var markdown = File.ReadAllText(rendered);
        Assert.Contains("doSthUncommon", markdown);
        Assert.Contains("123456789012345678901234567890", markdown);
        Assert.Contains("printed", markdown);
        Assert.Contains("null", markdown);
        Assert.DoesNotContain("arithmetic", markdown, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("missing-features")]
    [InlineData("null-features")]
    [InlineData("object-features")]
    [InlineData("string-feature")]
    [InlineData("null-feature")]
    [InlineData("missing-source")]
    [InlineData("unknown-root")]
    [InlineData("alias-features")]
    [InlineData("wrong-language")]
    [InlineData("wrong-kind")]
    [InlineData("wrong-root")]
    [InlineData("null-source")]
    [InlineData("null-optional-name")]
    [InlineData("empty-optional-name")]
    [InlineData("unknown-feature")]
    [InlineData("missing-signature")]
    [InlineData("empty-summary")]
    [InlineData("bare-behavior")]
    [InlineData("null-behavior")]
    [InlineData("missing-behavior-id")]
    [InlineData("null-params")]
    [InlineData("null-param")]
    [InlineData("duplicate-parameter")]
    [InlineData("null-constraints")]
    [InlineData("empty-constraint")]
    [InlineData("null-returns")]
    [InlineData("missing-return-description")]
    [InlineData("null-errors")]
    [InlineData("missing-error-condition")]
    [InlineData("null-invariants")]
    [InlineData("empty-invariant")]
    [InlineData("null-examples")]
    [InlineData("missing-expected")]
    [InlineData("array-input")]
    [InlineData("null-input")]
    [InlineData("null-source-refs")]
    [InlineData("empty-source-ref")]
    [InlineData("uppercase-id")]
    [InlineData("unscoped-id")]
    [InlineData("id-newline")]
    [InlineData("atomic-id-newline")]
    [InlineData("wrong-owner")]
    [InlineData("wrong-suffix")]
    [InlineData("zero-suffix")]
    [InlineData("duplicate-atomic-id")]
    [InlineData("duplicate-feature-id")]
    [InlineData("alias-params")]
    [InlineData("empty-requirements")]
    [InlineData("empty-complete")]
    public void InvalidDocumentCannotCertifySuccess(string mutation)
    {
        var authored = Authored();
        var feature = authored["features"]![0]!;
        switch (mutation)
        {
            case "missing-features": authored.AsObject().Remove("features"); break;
            case "null-features": authored["features"] = null; break;
            case "object-features": authored["features"] = new JsonObject(); break;
            case "string-feature": authored["features"] = new JsonArray("prose"); break;
            case "null-feature": authored["features"] = new JsonArray((JsonNode?)null); break;
            case "missing-source": authored.AsObject().Remove("source"); break;
            case "unknown-root": authored["metadata"] = new JsonObject(); break;
            case "alias-features": authored["Features"] = authored["features"]!.DeepClone(); break;
            case "wrong-language": authored["source"]!["language"] = "rust"; break;
            case "wrong-kind": authored["source"]!["kind"] = "calculator"; break;
            case "wrong-root": authored["source"]!["root"] = Path.Combine(directory, "wrong"); break;
            case "null-source": authored["source"] = null; break;
            case "null-optional-name": authored["source"]!["name"] = null; break;
            case "empty-optional-name": authored["source"]!["name"] = ""; break;
            case "unknown-feature": feature["confidence"] = "confirmed"; break;
            case "missing-signature": feature.AsObject().Remove("signature"); break;
            case "empty-summary": feature["summary"] = " "; break;
            case "bare-behavior": feature["behaviors"] = new JsonArray("Returns value"); break;
            case "null-behavior": feature["behaviors"] = new JsonArray((JsonNode?)null); break;
            case "missing-behavior-id": feature["behaviors"]![0]!.AsObject().Remove("id"); break;
            case "null-params": feature["params"] = null; break;
            case "null-param": feature["params"] = new JsonArray((JsonNode?)null); break;
            case "duplicate-parameter": feature["params"]!.AsArray().Add(feature["params"]![0]!.DeepClone()); break;
            case "null-constraints": feature["params"]![0]!["constraints"] = null; break;
            case "empty-constraint": feature["params"]![0]!["constraints"] = new JsonArray(""); break;
            case "null-returns": feature["returns"] = null; break;
            case "missing-return-description": feature["returns"]!.AsObject().Remove("description"); break;
            case "null-errors": feature["errors"] = null; break;
            case "missing-error-condition": feature["errors"] = JsonNode.Parse("""[{"id":"odd.do_sth_uncommon.e1","result":"Exception"}]"""); break;
            case "null-invariants": feature["invariants"] = null; break;
            case "empty-invariant": feature["invariants"] = JsonNode.Parse("""[{"id":"odd.do_sth_uncommon.i1","statement":""}]"""); break;
            case "null-examples": feature["examples"] = null; break;
            case "missing-expected": feature["examples"]![0]!.AsObject().Remove("expected"); break;
            case "array-input": feature["examples"]![0]!["input"] = new JsonArray(); break;
            case "null-input": feature["examples"]![0]!["input"] = null; break;
            case "null-source-refs": feature["source_refs"] = null; break;
            case "empty-source-ref": feature["source_refs"] = new JsonArray(""); break;
            case "uppercase-id": feature["id"] = "Odd.method"; break;
            case "unscoped-id": feature["id"] = "unscoped"; break;
            case "id-newline":
                feature["id"] = FeatureId + "\n";
                feature["behaviors"]![0]!["id"] = FeatureId + "\n.b1";
                feature["examples"]![0]!["id"] = FeatureId + "\n.x1";
                break;
            case "atomic-id-newline": feature["behaviors"]![0]!["id"] = FeatureId + ".b1\n"; break;
            case "wrong-owner": feature["behaviors"]![0]!["id"] = "other.feature.b1"; break;
            case "wrong-suffix": feature["behaviors"]![0]!["id"] = FeatureId + ".e1"; break;
            case "zero-suffix": feature["behaviors"]![0]!["id"] = FeatureId + ".b0"; break;
            case "duplicate-atomic-id": feature["behaviors"]!.AsArray().Add(feature["behaviors"]![0]!.DeepClone()); break;
            case "duplicate-feature-id": authored["features"]!.AsArray().Add(feature.DeepClone()); break;
            case "alias-params": feature["parameters"] = feature["params"]!.DeepClone(); break;
            case "empty-requirements": feature["behaviors"] = new JsonArray(); feature["examples"] = new JsonArray(); break;
            case "empty-complete": authored["features"] = new JsonArray(); break;
            default: throw new ArgumentException(mutation);
        }
        var metadata = Metadata();
        if (mutation == "id-newline") metadata["featureEvidence"]![0]!["featureId"] = FeatureId + "\n";
        WriteAuthored(authored, metadata);
        Invalid(Validate());
    }

    [Theory]
    [InlineData("document-root")]
    [InlineData("document-nested")]
    [InlineData("json-payload")]
    [InlineData("context")]
    public void DuplicateJsonPropertiesAreRejected(string location)
    {
        WriteAuthored();
        var path = location == "context" ? context : document;
        var text = File.ReadAllText(path);
        text = location switch
        {
            "document-root" => text.Replace("\"features\":", "\"features\": [], \"features\":", StringComparison.Ordinal),
            "document-nested" => text.Replace("\"summary\":", "\"summary\": \"duplicate\", \"summary\":", StringComparison.Ordinal),
            "json-payload" => text.Replace("\"value\": \"hello\"", "\"value\": \"lost\", \"value\": \"hello\"", StringComparison.Ordinal),
            "context" => text.Replace("\"status\":", "\"status\": \"draft\", \"status\":", StringComparison.Ordinal),
            _ => throw new ArgumentException(location)
        };
        File.WriteAllText(path, text);
        Invalid(Validate());
    }

    [Theory]
    [InlineData("input-number")]
    [InlineData("return-number")]
    [InlineData("input-object")]
    [InlineData("return-object")]
    public void DecimalExamplesRejectLossyNumericPayloads(string mutation)
    {
        var authored = DecimalDocument();
        var example = authored["features"]![0]!["examples"]![0]!;
        if (mutation == "input-number") example["input"]!["value"] = 0.1;
        if (mutation == "return-number") example["expected"] = 0.1;
        if (mutation == "input-object") example["input"]!["value"] = new JsonObject { ["value"] = "0.1" };
        if (mutation == "return-object") example["expected"] = new JsonObject { ["value"] = 0.1 };
        WriteAuthored(authored);
        Invalid(Validate());
    }

    [Fact]
    public void DecimalStringsAndExplicitErrorExpectedObjectsAreAccepted()
    {
        var authored = DecimalDocument();
        WriteAuthored(authored);
        Success(Validate(ready: true));
        authored["features"]![0]!["examples"]![0]!["expected"] = new JsonObject { ["error"] = "Explicitly specified failure" };
        WriteAuthored(authored);
        Success(Validate(ready: true));
        var markdown = Path.Combine(directory, "decimal.md");
        Success(Render(markdown));
        Assert.Contains("0.1000000000000000000000000000", File.ReadAllText(markdown));
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("partial")]
    [InlineData("blocked")]
    public void ValidUnfinishedStatusesNeverDispatch(string status)
    {
        var metadata = Metadata();
        metadata["status"] = status;
        WriteAuthored(metadata: metadata);
        var result = Validate();
        Success(result);
        Assert.False(ReadSummary(result)["readyForDownstream"]!.GetValue<bool>());
        Invalid(Validate(ready: true));
    }

    [Theory]
    [InlineData("narrative-partial")]
    [InlineData("compiler-partial")]
    [InlineData("pending")]
    [InlineData("questions")]
    [InlineData("uncertain")]
    [InlineData("unapproved-guide")]
    public void UnresolvedContextIsValidPartialButCannotBeCompleteOrReady(string condition)
    {
        var metadata = Metadata();
        switch (condition)
        {
            case "narrative-partial": metadata["upstreamStatus"] = "partial"; break;
            case "compiler-partial":
                var extraction = Extraction();
                extraction.Status = "partial";
                Save(input, extraction);
                metadata["upstreamStatus"] = "partial";
                break;
            case "pending": metadata["coverage"]![0]!["disposition"] = "pending"; break;
            case "questions": metadata["openQuestions"] = JsonNode.Parse("""[{"id":"Q-1","question":"Which mapping is approved?","evidenceIds":[]}]"""); break;
            case "uncertain": metadata["featureEvidence"]![0]!["confidence"] = "uncertain"; break;
            case "unapproved-guide": metadata["portingGuide"] = new JsonObject { ["path"] = "guide.md", ["revision"] = "r1", ["approved"] = false }; break;
        }
        metadata["status"] = "partial";
        WriteAuthored(metadata: metadata);
        var before = File.ReadAllBytes(context);
        Success(Validate());
        Invalid(Validate(ready: true));
        Assert.Equal(before, File.ReadAllBytes(context));
        metadata["status"] = "complete";
        WriteAuthored(metadata: metadata);
        Invalid(Validate());
    }

    [Theory]
    [InlineData("UNRESOLVED_CALL", false)]
    [InlineData("SOURCE_TRUNCATED", false)]
    [InlineData("FRAMEWORK_UNKNOWN", false)]
    [InlineData("SYNTHESIZED_MEMBERS", false)]
    [InlineData("PRIMARY_CONSTRUCTOR", false)]
    [InlineData("TOP_LEVEL_CODE", false)]
    [InlineData("WORKSPACE", true)]
    [InlineData("UNSUPPORTED_PROJECT", true)]
    public void CompleteExtractionCannotHideAnalysisGaps(string code, bool topLevel)
    {
        var extraction = Extraction();
        (topLevel ? extraction.Diagnostics : extraction.Projects[0].Diagnostics)
            .Add(new(code, "warning", "Compiler analysis is incomplete."));
        Save(input, extraction);
        var original = File.ReadAllBytes(input);
        WriteAuthored();

        var result = Validate(ready: true);
        Invalid(result);
        Assert.Contains("analysis gaps", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Stdout);
        Invalid(Validate());
        var rendered = Path.Combine(directory, "requirements.md");
        Invalid(Render(rendered));
        Assert.False(File.Exists(rendered));
        var preparedDocument = Path.Combine(directory, "prepared.json");
        var preparedContext = Path.Combine(directory, "prepared.context.json");
        Invalid(Run("prepare", "--input", input, "--output", preparedDocument, "--context", preparedContext));
        Assert.False(File.Exists(preparedDocument));
        Assert.False(File.Exists(preparedContext));
        Assert.Equal(original, File.ReadAllBytes(input));
    }

    [Theory]
    [InlineData("SOURCE_TRUNCATED", false)]
    [InlineData("WORKSPACE", true)]
    public void PartialAnalysisGapsRemainValidButNotReady(string code, bool topLevel)
    {
        var extraction = Extraction();
        extraction.Status = "partial";
        (topLevel ? extraction.Diagnostics : extraction.Projects[0].Diagnostics)
            .Add(new(code, "warning", "Compiler analysis is incomplete."));
        Save(input, extraction);
        Success(Run("prepare", "--input", input, "--output", document, "--context", context));
        Assert.Equal("partial", Read(context)["upstreamStatus"]!.GetValue<string>());
        Success(Validate());
        Invalid(Validate(ready: true));

        var metadata = Metadata();
        metadata["status"] = "partial";
        metadata["upstreamStatus"] = "partial";
        WriteAuthored(metadata: metadata);
        Success(Validate());
        Invalid(Validate(ready: true));
        Success(Render(Path.Combine(directory, "requirements.md")));
    }

    [Fact]
    public void OrdinaryProjectWarningDoesNotBecomeAnAnalysisGap()
    {
        var extraction = Extraction();
        extraction.Projects[0].Diagnostics.Add(new("CS0169", "warning", "The field is never used."));
        Save(input, extraction);
        WriteAuthored();
        Success(Validate(ready: true));
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("task")]
    [InlineData("version")]
    [InlineData("document-path")]
    [InlineData("unknown-field")]
    [InlineData("missing-field")]
    [InlineData("unknown-evidence")]
    [InlineData("empty-evidence")]
    [InlineData("duplicate-evidence")]
    [InlineData("missing-feature-evidence")]
    [InlineData("duplicate-feature-evidence")]
    [InlineData("dangling-feature-evidence")]
    [InlineData("unknown-coverage")]
    [InlineData("missing-coverage")]
    [InlineData("duplicate-coverage")]
    [InlineData("excluded-evidence")]
    [InlineData("required-without-evidence")]
    [InlineData("question-unknown-evidence")]
    [InlineData("duplicate-question")]
    [InlineData("null-feature-evidence")]
    [InlineData("null-coverage")]
    [InlineData("null-question")]
    [InlineData("null-references")]
    [InlineData("empty-reference")]
    [InlineData("invalid-reference")]
    [InlineData("blank-guide-revision")]
    [InlineData("missing-guide-approval")]
    [InlineData("wrong-guide-approval")]
    [InlineData("complete-upstream-over-partial-compiler")]
    public void InvalidContextIsRejected(string mutation)
    {
        var metadata = Metadata();
        switch (mutation)
        {
            case "snapshot": metadata["extractionId"] = "wrong"; break;
            case "task": metadata["taskId"] = "wrong"; break;
            case "version": metadata["schemaVersion"] = "1.0"; break;
            case "document-path": metadata["documentPath"] = Path.Combine(directory, "other.json"); break;
            case "unknown-field": metadata["runtimeParity"] = true; break;
            case "missing-field": metadata.AsObject().Remove("portingGuide"); break;
            case "unknown-evidence": metadata["featureEvidence"]![0]!["evidenceIds"] = new JsonArray("unknown"); break;
            case "empty-evidence": metadata["featureEvidence"]![0]!["evidenceIds"] = new JsonArray(); break;
            case "duplicate-evidence": metadata["featureEvidence"]![0]!["evidenceIds"]!.AsArray().Add(SymbolId); break;
            case "missing-feature-evidence": metadata["featureEvidence"] = new JsonArray(); break;
            case "duplicate-feature-evidence": metadata["featureEvidence"]!.AsArray().Add(metadata["featureEvidence"]![0]!.DeepClone()); break;
            case "dangling-feature-evidence": metadata["featureEvidence"]![0]!["featureId"] = "other.feature"; break;
            case "unknown-coverage": metadata["coverage"]![0]!["symbolId"] = "unknown"; break;
            case "missing-coverage": metadata["coverage"] = new JsonArray(); break;
            case "duplicate-coverage": metadata["coverage"]!.AsArray().Add(metadata["coverage"]![0]!.DeepClone()); break;
            case "excluded-evidence": metadata["coverage"]![0]!["disposition"] = "excluded"; break;
            case "required-without-evidence":
                var extraction = Extraction();
                extraction.Projects[0].Symbols.Add(new()
                {
                    Id = "p:T:Other", Kind = "NamedType", Name = "Other", DisplayName = "Other", Accessibility = "Public",
                    IsPublicApi = true, Source = new("Other.cs", 1, 1), Declaration = "public class Other {}"
                });
                Save(input, extraction);
                metadata["coverage"]!.AsArray().Add(new JsonObject { ["symbolId"] = "p:T:Other", ["disposition"] = "required", ["reason"] = "Public API" });
                break;
            case "question-unknown-evidence":
                metadata["status"] = "partial";
                metadata["openQuestions"] = JsonNode.Parse("""[{"id":"Q-1","question":"What happens?","evidenceIds":["unknown"]}]"""); break;
            case "duplicate-question":
                metadata["status"] = "partial";
                metadata["openQuestions"] = JsonNode.Parse("""[{"id":"Q-1","question":"What?","evidenceIds":[]},{"id":"Q-1","question":"Why?","evidenceIds":[]}]"""); break;
            case "null-feature-evidence": metadata["featureEvidence"] = new JsonArray((JsonNode?)null); break;
            case "null-coverage": metadata["coverage"] = null; break;
            case "null-question": metadata["openQuestions"] = new JsonArray((JsonNode?)null); break;
            case "null-references": metadata["evidenceReferences"] = null; break;
            case "empty-reference": metadata["evidenceReferences"] = new JsonArray(""); break;
            case "invalid-reference": metadata["evidenceReferences"] = new JsonArray("bad\0path"); break;
            case "blank-guide-revision": metadata["portingGuide"] = new JsonObject { ["path"] = "guide.md", ["revision"] = "", ["approved"] = true }; break;
            case "missing-guide-approval": metadata["portingGuide"] = new JsonObject { ["path"] = "guide.md", ["revision"] = "r1" }; break;
            case "wrong-guide-approval": metadata["portingGuide"] = new JsonObject { ["path"] = "guide.md", ["revision"] = "r1", ["approved"] = "true" }; break;
            case "complete-upstream-over-partial-compiler":
                var partial = Extraction();
                partial.Status = "partial";
                Save(input, partial);
                metadata["status"] = "partial";
                break;
            default: throw new ArgumentException(mutation);
        }
        WriteAuthored(metadata: metadata);
        Invalid(Validate());
    }

    [Fact]
    public void InferredEvidenceAndApprovedGuideAreNotPromotedToSemanticProof()
    {
        var metadata = Metadata();
        metadata["featureEvidence"]![0]!["confidence"] = "inferred";
        metadata["portingGuide"] = new JsonObject { ["path"] = "guide.md", ["revision"] = "r1", ["approved"] = true };
        WriteAuthored(metadata: metadata);
        var result = Validate(ready: true);
        Success(result);
        Assert.False(ReadSummary(result)["semanticParityVerified"]!.GetValue<bool>());
    }

    [Fact]
    public void PairedPreparationIsDeterministicAndRejectsForce()
    {
        Success(Run("prepare", "--input", input, "--output", document, "--context", context));
        var firstDocument = File.ReadAllBytes(document);
        var firstContext = File.ReadAllBytes(context);
        Invalid(Run("prepare", "--input", input, "--output", document, "--context", context, "--force"));
        Assert.Equal(firstDocument, File.ReadAllBytes(document));
        Assert.Equal(firstContext, File.ReadAllBytes(context));
        File.Delete(document);
        File.Delete(context);
        Success(Run("prepare", "--input", input, "--output", document, "--context", context));
        Assert.Equal(firstDocument, File.ReadAllBytes(document));
        Assert.Equal(firstContext, File.ReadAllBytes(context));
    }

    [Theory]
    [InlineData("existing-context")]
    [InlineData("same-path")]
    [InlineData("input-path")]
    [InlineData("source-path")]
    [InlineData("invalid-extension")]
    [InlineData("context-is-directory")]
    [InlineData("parent-is-file")]
    public void PairedPreparationPreflightsBothPathsBeforePublishing(string condition)
    {
        var second = context;
        switch (condition)
        {
            case "existing-context": File.WriteAllText(context, "authored data"); break;
            case "same-path": second = document; break;
            case "input-path": second = input; break;
            case "source-path":
                var extraction = Extraction();
                extraction.Projects[0].Symbols[0].Source = new("source.json", 1, 1);
                Save(input, extraction);
                second = Path.Combine(directory, "source.json");
                break;
            case "invalid-extension": second = Path.Combine(directory, "new", "bad.cs"); break;
            case "context-is-directory": Directory.CreateDirectory(context); break;
            case "parent-is-file":
                var parent = Path.Combine(directory, "parent");
                File.WriteAllText(parent, "keep");
                second = Path.Combine(parent, "context.json");
                break;
        }
        var before = File.ReadAllBytes(input);
        Invalid(Run("prepare", "--input", input, "--output", document, "--context", second));
        Assert.False(File.Exists(document));
        Assert.Equal(before, File.ReadAllBytes(input));
        if (condition == "existing-context") Assert.Equal("authored data", File.ReadAllText(context));
        Assert.Empty(Directory.GetFiles(directory, "*.pending", SearchOption.AllDirectories));
    }

    [Fact]
    public void PairedPreparationDoesNotPublishWhenSecondStagingWriteFails()
    {
        var second = Path.Combine(directory, new string('x', 240) + ".json");
        Invalid(Run("prepare", "--input", input, "--output", document, "--context", second));
        Assert.False(File.Exists(document));
        Assert.False(File.Exists(second));
        Assert.Empty(Directory.GetFiles(directory, "*.pending", SearchOption.AllDirectories));
    }

    [Fact]
    public void PairedPreparationRetainsAnExistingDocumentAndDoesNotCreateContext()
    {
        File.WriteAllText(document, "authored document");
        Invalid(Run("prepare", "--input", input, "--output", document, "--context", context));
        Assert.Equal("authored document", File.ReadAllText(document));
        Assert.False(File.Exists(context));
    }

    [Fact]
    public void ResolvedRelativePathsBindTheSameDocumentAndSourceRoot()
    {
        var authored = Authored();
        authored["source"]!["root"] = ".";
        var metadata = Metadata();
        metadata["documentPath"] = ".\\document.json";
        WriteAuthored(authored, metadata);
        Success(Validate(ready: true));
        Invalid(Run("prepare", "--input", input, "--output", Path.Combine(directory, "same.json"),
            "--context", Path.Combine(directory, ".", "same.json")));
        Assert.False(File.Exists(Path.Combine(directory, "same.json")));
    }

    [Theory]
    [InlineData("ConsoleApplication", "Odd.csproj", "application")]
    [InlineData("WindowsApplication", "Odd.csproj", "application")]
    [InlineData("DynamicallyLinkedLibrary", "Odd.sln", "solution")]
    [InlineData("DynamicallyLinkedLibrary", "Odd.slnx", "solution")]
    public void PrepareUsesProjectMetadataForSourceKind(string outputKind, string entry, string kind)
    {
        var extraction = Extraction();
        extraction.InputPath = entry;
        extraction.Projects[0].OutputKind = outputKind;
        Save(input, extraction);
        Success(Run("prepare", "--input", input, "--output", document, "--context", context));
        Assert.Equal(kind, Read(document)["source"]!["kind"]!.GetValue<string>());
        Assert.Empty(Read(document)["features"]!.AsArray());
    }

    [Fact]
    public void PartialExtractionDraftPreservesUpstreamStatusAndQuestions()
    {
        var extraction = Extraction();
        extraction.Status = "partial";
        Save(input, extraction);
        Success(Run("prepare", "--input", input, "--output", document, "--context", context));
        Assert.Equal("partial", Read(context)["upstreamStatus"]!.GetValue<string>());
        Assert.Equal(2, Read(context)["openQuestions"]!.AsArray().Count);
        Success(Validate());
        Invalid(Validate(ready: true));
    }

    [Fact]
    public void FeatureIdsCannotCollideWithAnotherFeaturesAtomicId()
    {
        var authored = Authored();
        var next = authored["features"]![0]!.DeepClone();
        var id = FeatureId + ".b1";
        next["id"] = id;
        next["behaviors"]![0]!["id"] = id + ".b1";
        next["examples"]![0]!["id"] = id + ".x1";
        authored["features"]!.AsArray().Add(next);
        var metadata = Metadata();
        metadata["featureEvidence"]!.AsArray().Add(new JsonObject
        {
            ["featureId"] = id, ["confidence"] = "confirmed", ["evidenceIds"] = new JsonArray(SymbolId)
        });
        WriteAuthored(authored, metadata);
        Invalid(Validate());
    }

    [Theory]
    [InlineData("project")]
    [InlineData("project-diagnostic")]
    [InlineData("diagnostic")]
    [InlineData("additional-source")]
    [InlineData("relationship")]
    public void PreparationProtectsEveryExtractionEvidencePath(string location)
    {
        var extraction = Extraction();
        const string evidence = "compiler-evidence.json";
        switch (location)
        {
            case "project": extraction.Projects[0].File = evidence; break;
            case "project-diagnostic": extraction.Projects[0].Diagnostics.Add(new("D1", "Info", "Evidence", new(evidence, 1, 1))); break;
            case "diagnostic":
                extraction.Status = "partial";
                extraction.Diagnostics.Add(new("D1", "Info", "Evidence", new(evidence, 1, 1)));
                break;
            case "additional-source": extraction.Projects[0].Symbols[0].AdditionalSources.Add(new(evidence, 1, 1)); break;
            case "relationship": extraction.Projects[0].Symbols[0].Relationships.Add(new("calls", "external:M:Other", "Other", new(evidence, 1, 1))); break;
        }
        Save(input, extraction);
        var result = Run("prepare", "--input", input, "--output", document, "--context", Path.Combine(directory, evidence));
        Invalid(result);
        Assert.Contains("cannot overwrite", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(document));
        Assert.False(File.Exists(Path.Combine(directory, evidence)));
    }

    [Fact]
    public void NewCommandsDoNotSilentlyReadOrOverwriteLegacyArtifacts()
    {
        Success(Run("prepare-legacy", "--input", input, "--output", document));
        Save(context, Metadata());
        var legacy = File.ReadAllBytes(document);
        Invalid(Validate());
        Invalid(Run("prepare", "--input", input, "--output", document, "--context", context));
        Assert.Equal(legacy, File.ReadAllBytes(document));
        WriteAuthored();
        Invalid(Run("validate-legacy", "--input", input, "--requirements", document));
    }

    [Fact]
    public void ErrorAndInvariantRequirementsKeepTheirIdsInCompactRendering()
    {
        var authored = Authored();
        // These are explicit hypothetical fixture requirements, not inferred production behavior.
        authored["features"]![0]!["errors"] = JsonNode.Parse(
            """[{"id":"odd.do_sth_uncommon.e1","condition":"the fixture failure mode is selected","result":"FixtureError"}]""");
        authored["features"]![0]!["invariants"] = JsonNode.Parse(
            """[{"id":"odd.do_sth_uncommon.i1","statement":"The supplied string remains unchanged."}]""");
        WriteAuthored(authored);
        var validation = Validate(ready: true);
        Success(validation);
        Assert.Equal(4, ReadSummary(validation)["requirements"]!.GetValue<int>());
        var markdown = Path.Combine(directory, "requirements.md");
        Success(Render(markdown));
        var text = File.ReadAllText(markdown);
        Assert.Contains(FeatureId + ".e1", text);
        Assert.Contains("FixtureError", text);
        Assert.Contains(FeatureId + ".i1", text);
        Assert.Contains("The supplied string remains unchanged.", text);
    }

    [Fact]
    public void ExclusionsWithReasonsCanAccountForOtherPublicSymbols()
    {
        var extraction = Extraction();
        extraction.Projects[0].Symbols.Add(new()
        {
            Id = "p:T:Other", Kind = "NamedType", Name = "Other", DisplayName = "Other",
            IsPublicApi = true, Accessibility = "Public", Source = new("Other.cs", 1, 1),
            Declaration = "public class Other {}"
        });
        Save(input, extraction);
        var metadata = Metadata();
        metadata["coverage"]!.AsArray().Add(new JsonObject
        {
            ["symbolId"] = "p:T:Other", ["disposition"] = "excluded", ["reason"] = "This fixture surface is outside the approved scope."
        });
        WriteAuthored(metadata: metadata);
        Success(Validate(ready: true));
    }

    [Theory]
    [InlineData("document")]
    [InlineData("context")]
    public void CompactReadersRetainStrictJsonDepthAndSizeLimits(string artifact)
    {
        WriteAuthored();
        var path = artifact == "document" ? document : context;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            stream.SetLength(64L * 1024 * 1024 + 1);
        var oversized = Validate();
        Invalid(oversized);
        Assert.Contains("input limit", oversized.Stderr);
        WriteAuthored();
        var nested = new string('[', 70) + "null" + new string(']', 70);
        File.WriteAllText(path, nested);
        Invalid(Validate());
    }

    [Theory]
    [InlineData("document")]
    [InlineData("context")]
    [InlineData("extraction")]
    [InlineData("existing")]
    [InlineData("source")]
    [InlineData("evidence")]
    [InlineData("guide")]
    [InlineData("source-ref")]
    public void RenderProtectsInputsAndEvidence(string target)
    {
        var metadata = Metadata();
        metadata["evidenceReferences"]!.AsArray().Add("tests.md");
        metadata["portingGuide"] = new JsonObject { ["path"] = "guide.md", ["revision"] = "r1", ["approved"] = true };
        var authored = Authored();
        authored["features"]![0]!["source_refs"]!.AsArray().Add("navigation.md#L1-L2");
        var extraction = Extraction();
        extraction.Projects[0].Symbols[0].AdditionalSources.Add(new("source.md", 1, 1));
        Save(input, extraction);
        WriteAuthored(authored, metadata);
        var path = target switch
        {
            "document" => document,
            "context" => context,
            "extraction" => input,
            "existing" => Path.Combine(directory, "existing.md"),
            "source" => Path.Combine(directory, "source.md"),
            "evidence" => Path.Combine(directory, "tests.md"),
            "guide" => Path.Combine(directory, "guide.md"),
            "source-ref" => Path.Combine(directory, "navigation.md"),
            _ => throw new ArgumentException(target)
        };
        if (target == "existing") File.WriteAllText(path, "authored");
        var before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        Invalid(Render(path));
        if (before is null) Assert.False(File.Exists(path));
        else Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void RenderIsCompactDeterministicAndEscapesUntrustedMarkdown()
    {
        var authored = Authored();
        var feature = authored["features"]![0]!;
        feature["summary"] = "value | column\n# heading\n```html\n<script>\u001b[31m";
        feature["examples"]![0]!["input"]!["value"] = "``` \n| injected | \0";
        WriteAuthored(authored);
        var first = Path.Combine(directory, "first.md");
        var second = Path.Combine(directory, "second.md");
        Success(Render(first));
        Success(Render(second));
        var markdown = File.ReadAllText(first);
        Assert.Equal(markdown, File.ReadAllText(second));
        Assert.Contains("document.context.json", markdown);
        Assert.Contains("complete", markdown);
        Assert.Contains(FeatureId + ".b1", markdown);
        Assert.Contains(FeatureId + ".x1", markdown);
        Assert.DoesNotContain("\n# heading", markdown);
        Assert.DoesNotContain("<script>", markdown);
        Assert.DoesNotContain("\u001b", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("\0", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("```html", markdown);
        Assert.DoesNotContain("fixture-only-1", markdown);
        Assert.DoesNotContain("declaration", markdown, StringComparison.OrdinalIgnoreCase);
        Assert.True(markdown.Length < 3500, markdown);
    }

    [Fact]
    public void RenderValidatesBeforeCreatingOutput()
    {
        WriteAuthored();
        var invalid = Read(document);
        invalid.AsObject().Remove("features");
        Save(document, invalid);
        var output = Path.Combine(directory, "new", "requirements.md");
        Invalid(Render(output));
        Assert.False(Directory.Exists(Path.GetDirectoryName(output)));
    }

    [Theory]
    [InlineData(@"Q:\artifacts\document.context.json", @"Q:\reports\requirements.md", "../artifacts/document.context.json")]
    [InlineData(@"Q:\artifacts\document.context.json", @"C:\reports\requirements.md", "file:///Q:/artifacts/document.context.json")]
    [InlineData(@"Q:\artifacts\ctx (draft)#1.json", @"C:\reports\requirements.md", "file:///Q:/artifacts/ctx%20%28draft%29%231.json")]
    public void RenderContextLinksAcrossWindowsDrives(string contextPath, string outputPath, string expectedLink)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Exercise lexical paths without requiring additional mounted drives.
        var assembly = System.Reflection.Assembly.LoadFrom(CollectorAssemblyPath);
        var renderer = assembly.GetType("Requirements.Collector.DocumentMarkdown", throwOnError: true)!;
        var method = renderer.GetMethod("Render")!;
        var markdown = Assert.IsType<string>(method.Invoke(null,
            [new FeatureDocument(), new DocumentContext(), contextPath, outputPath]));

        Assert.Contains($"]({expectedLink}); status:", markdown);
    }

    [Theory]
    [InlineData("property")]
    [InlineData("example")]
    [InlineData("optional-source")]
    public void InvalidUnicodeHasExplicitValidationDiagnostics(string location)
    {
        WriteAuthored();
        var json = File.ReadAllText(document);
        json = location switch
        {
            "property" => json.Replace("\"summary\":", "\"\\ud800\": \"invalid\", \"summary\":", StringComparison.Ordinal),
            "example" => json.Replace("\"value\": \"hello\"", "\"value\": \"\\ud800\"", StringComparison.Ordinal),
            "optional-source" => json.Replace("\"language\":", "\"name\": \"\\ud800\", \"language\":", StringComparison.Ordinal),
            _ => throw new ArgumentException(location)
        };
        File.WriteAllText(document, json);
        Invalid(Validate());
    }

    private JsonNode Authored()
    {
        // Deliberately test-authored; no fixture here represents real agent output.
        var result = JsonNode.Parse("""
            {
              "source":{"language":"csharp","kind":"library","root":"replaced"},
              "features":[{
                "id":"odd.do_sth_uncommon","name":"doSthUncommon",
                "signature":"string doSthUncommon(string value)","summary":"Prints and returns the supplied value.",
                "params":[{"name":"value","type":"string","constraints":[]}],
                "returns":{"type":"string","description":"The supplied value."},
                "behaviors":[{"id":"odd.do_sth_uncommon.b1","statement":"The value is printed and returned unchanged."}],
                "errors":[],"invariants":[],
                "examples":[{"id":"odd.do_sth_uncommon.x1","input":{"value":"hello"},"expected":{"return":"hello","printed":"hello\n"}}],
                "visibility":"public","source_refs":["Odd.cs#L1"]
              }]
            }
            """)!;
        result["source"]!["root"] = directory;
        return result;
    }

    private JsonNode DecimalDocument()
    {
        var authored = Authored();
        authored["features"]![0]!["params"]![0]!["type"] = "System.Decimal";
        authored["features"]![0]!["returns"]!["type"] = "decimal";
        authored["features"]![0]!["examples"]![0]!["input"]!["value"] = "0.1000000000000000000000000000";
        authored["features"]![0]!["examples"]![0]!["expected"] = "0.1000000000000000000000000000";
        return authored;
    }

    private JsonNode Metadata() => new JsonObject
    {
        ["schemaVersion"] = "2.0", ["taskId"] = "synthetic-test", ["extractionId"] = "fixture-only-1",
        ["status"] = "complete", ["upstreamStatus"] = "complete", ["documentPath"] = document,
        ["evidenceReferences"] = new JsonArray(input),
        ["featureEvidence"] = new JsonArray(new JsonObject
        {
            ["featureId"] = FeatureId, ["confidence"] = "confirmed", ["evidenceIds"] = new JsonArray(SymbolId)
        }),
        ["coverage"] = new JsonArray(new JsonObject { ["symbolId"] = SymbolId, ["disposition"] = "required", ["reason"] = "Public behavior." }),
        ["openQuestions"] = new JsonArray(), ["portingGuide"] = null
    };

    private void WriteAuthored(JsonNode? authored = null, JsonNode? metadata = null)
    {
        Save(document, authored ?? Authored());
        Save(context, metadata ?? Metadata());
    }

    private Result Render(string output) => Run("render", "--input", input, "--document", document, "--context", context, "--output", output);

    private ExtractionArtifact Extraction() => new()
    {
        TaskId = "synthetic-test", ExtractionId = "fixture-only-1",
        RootDirectory = directory, InputPath = "Odd.csproj", Status = "complete",
        Projects =
        [
            new()
            {
                Id = "p", Name = "Odd", File = "Odd.csproj", OutputKind = "DynamicallyLinkedLibrary",
                TargetFramework = "net8.0", LanguageVersion = "CSharp12", NullableContext = "Enable",
                Symbols =
                [
                    new()
                    {
                        Id = SymbolId, Kind = "Method", Name = "doSthUncommon",
                        DisplayName = "Odd.doSthUncommon(string)", Accessibility = "Public",
                        IsPublicApi = true, Type = "string", Parameters = [new("value", "string", "None", false, null)],
                        Source = new("Odd.cs", 1, 1),
                        Declaration = "public string doSthUncommon(string value) { Console.WriteLine(value); return value; }"
                    }
                ]
            }
        ],
        Limitations = ["Synthetic test evidence, not an agent-authored or compiler-produced artifact."]
    };

    private Result Validate(bool ready = false) => Run(
        ["validate", "--input", input, "--document", document, "--context", context, .. ready ? new[] { "--require-ready" } : []]);

    private static JsonNode Read(string path) => JsonNode.Parse(File.ReadAllText(path))!;
    private static JsonNode ReadSummary(Result result) => JsonNode.Parse(result.Stdout)!;
    private static void Save<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, ArtifactJson.Options));

    private string CollectorAssemblyPath => Path.Combine(root, "src", "Requirements.Collector", "bin",
        new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net8.0", "Requirements.Collector.dll");

    private Result Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(CollectorAssemblyPath);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30000), "Collector timed out.");
        return new(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static void Success(Result result) =>
        Assert.True(result.ExitCode == 0, $"Exit {result.ExitCode}: {result.Stderr}\n{result.Stdout}");

    private static void Invalid(Result result)
    {
        Assert.True(result.ExitCode == 2, $"Expected exit 2, got {result.ExitCode}: {result.Stderr}\n{result.Stdout}");
        Assert.Contains("error", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
    private sealed record Result(int ExitCode, string Stdout, string Stderr);
}
