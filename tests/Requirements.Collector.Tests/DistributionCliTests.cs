using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSharpToRust.Contracts;

namespace Requirements.Collector.Tests;

public sealed class DistributionCliTests : IDisposable
{
    private const string SymbolId = "p:M:Example.Echo(System.String)";
    private static readonly string[] FeatureIds = ["example.first", "example.second", "example.third"];
    private readonly string root;
    private readonly string directory;
    private readonly string input;
    private readonly string document;
    private readonly string context;
    private readonly string distribution;

    public DistributionCliTests()
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
        distribution = Path.Combine(directory, "distribution.json");
        Save(input, Extraction());
        Save(document, AuthoredDocument());
        Save(context, Metadata());
    }

    [Fact]
    public void PrepareCreatesDeterministicDraftWithoutInventingPackagesOrChangingInputs()
    {
        Success(Run("validate", "--input", input, "--document", document, "--context", context, "--require-ready"));
        var before = Snapshot();
        var result = Prepare();
        Success(result);
        var plan = Read(distribution);
        Assert.Equal(new[]
        {
            "schemaVersion", "taskId", "extractionId", "documentPath", "contextPath", "documentSha256",
            "contextSha256", "status", "packages", "sharedConcerns", "unassignedFeatures", "openQuestions"
        }, plan.AsObject().Select(property => property.Key));
        Assert.Equal("1.0", plan["schemaVersion"]!.GetValue<string>());
        Assert.Equal("draft", plan["status"]!.GetValue<string>());
        Assert.Equal(Path.GetFileName(document), plan["documentPath"]!.GetValue<string>());
        Assert.Equal(Path.GetFileName(context), plan["contextPath"]!.GetValue<string>());
        Assert.Equal(document, Path.GetFullPath(plan["documentPath"]!.GetValue<string>(), Path.GetDirectoryName(distribution)!));
        Assert.Equal(context, Path.GetFullPath(plan["contextPath"]!.GetValue<string>(), Path.GetDirectoryName(distribution)!));
        Assert.Equal(Hash(document), plan["documentSha256"]!.GetValue<string>());
        Assert.Equal(Hash(context), plan["contextSha256"]!.GetValue<string>());
        Assert.Empty(plan["packages"]!.AsArray());
        Assert.Empty(plan["sharedConcerns"]!.AsArray());
        Assert.Equal(FeatureIds, plan["unassignedFeatures"]!.AsArray().Select(entry => entry!["featureId"]!.GetValue<string>()));
        Assert.All(plan["unassignedFeatures"]!.AsArray(), entry =>
            Assert.False(string.IsNullOrWhiteSpace(entry!["reason"]!.GetValue<string>())));
        Assert.NotEmpty(plan["openQuestions"]!.AsArray());
        AssertSummary(result, "draft", upstreamReady: true, ready: false, 0, 0, 3, 0, 1);
        Success(Validate());
        Invalid(Validate(ready: true), "not ready");
        var bytes = File.ReadAllBytes(distribution);
        File.Delete(distribution);
        Success(Prepare());
        Assert.Equal(bytes, File.ReadAllBytes(distribution));
        Unchanged(before);
    }

    [Fact]
    public void CompleteDependencyChainIsReadyAndCountsEveryOriginalAtomicRequirement()
    {
        Save(distribution, Plan());
        var before = Snapshot(includePlan: true);
        var result = Validate(ready: true);
        Success(result);
        AssertSummary(result, "complete", upstreamReady: true, ready: true, 3, 3, 0, 12, 0);
        Unchanged(before);
    }

    [Fact]
    public void GroupedFeaturesRetainAllRequirementsAndSinglePackageConcernsAreAllowed()
    {
        var plan = Plan();
        plan["packages"] = new JsonArray(Package("shared", FeatureIds));
        plan["sharedConcerns"] = new JsonArray(Concern("shared", "shared"));
        Save(distribution, plan);
        var result = Validate(ready: true);
        Success(result);
        AssertSummary(result, "complete", upstreamReady: true, ready: true, 1, 3, 0, 12, 0);
    }

    [Theory]
    [InlineData("draft", false)]
    [InlineData("partial", false)]
    [InlineData("blocked", false)]
    [InlineData("draft", true)]
    [InlineData("partial", true)]
    [InlineData("blocked", true)]
    public void UnfinishedPlansAreValidButNeverDispatchable(string status, bool unresolved)
    {
        var plan = Plan();
        plan["status"] = status;
        if (unresolved)
        {
            plan["packages"]!.AsArray().RemoveAt(2);
            plan["unassignedFeatures"] = new JsonArray(Unassigned(FeatureIds[2]));
            plan["openQuestions"] = new JsonArray(Question("Q-1", FeatureIds[0]), Question("global question"));
        }
        Save(distribution, plan);
        var result = Validate();
        Success(result);
        AssertSummary(result, status, upstreamReady: true, ready: false, unresolved ? 2 : 3,
            unresolved ? 2 : 3, unresolved ? 1 : 0, unresolved ? 8 : 12, unresolved ? 2 : 0);
        Invalid(Validate(ready: true), "not ready");
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("partial")]
    [InlineData("blocked")]
    [InlineData("partial-extraction")]
    [InlineData("question")]
    [InlineData("pending-coverage")]
    [InlineData("uncertain")]
    [InlineData("unapproved-guide")]
    public void UnreadyUpstreamCanBePlannedButCannotBePromoted(string condition)
    {
        var metadata = Metadata();
        metadata.Status = condition is "draft" or "blocked" ? condition : "partial";
        switch (condition)
        {
            case "partial-extraction":
                var extraction = Extraction();
                extraction.Status = "partial";
                Save(input, extraction);
                metadata.UpstreamStatus = "partial";
                break;
            case "question": metadata.OpenQuestions.Add(new("Q-UPSTREAM", "Resolve the behavior.", [])); break;
            case "pending-coverage": metadata.Coverage[0] = new(SymbolId, "pending", "Review required."); break;
            case "uncertain": metadata.FeatureEvidence[0] = new(FeatureIds[0], "uncertain", [SymbolId]); break;
            case "unapproved-guide": metadata.PortingGuide = new("guide.md", "r1", false); break;
        }
        Save(context, metadata);
        Success(Prepare());
        Assert.False(Summary(Validate())["upstreamReadyForDownstream"]!.GetValue<bool>());
        var plan = Plan();
        plan["status"] = "partial";
        Save(distribution, plan);
        var result = Validate();
        Success(result);
        AssertSummary(result, "partial", upstreamReady: false, ready: false, 3, 3, 0, 12, 0);
        Invalid(Validate(ready: true), "not ready");
        plan["status"] = "complete";
        Save(distribution, plan);
        Invalid(Validate(), "Complete");
    }

    [Fact]
    public void EmptyDraftDocumentCanBePreparedButNeverCompleted()
    {
        var authored = AuthoredDocument();
        authored.Features.Clear();
        var metadata = Metadata();
        metadata.Status = "draft";
        metadata.FeatureEvidence.Clear();
        metadata.Coverage[0] = new(SymbolId, "pending", "Review required.");
        Save(document, authored);
        Save(context, metadata);
        Success(Prepare());
        Assert.Empty(Read(distribution)["unassignedFeatures"]!.AsArray());
        var plan = Read(distribution);
        plan["status"] = "complete";
        plan["openQuestions"] = new JsonArray();
        Save(distribution, plan);
        Invalid(Validate(), "Complete");
    }

    [Theory]
    [InlineData("omitted", "Every feature")]
    [InlineData("duplicate-in-package", "duplicate")]
    [InlineData("duplicate-across-packages", "assigned")]
    [InlineData("assigned-and-unassigned", "assigned")]
    [InlineData("duplicate-unassigned", "assigned")]
    [InlineData("unknown-assigned", "Unknown")]
    [InlineData("atomic-instead-of-feature", "Unknown")]
    [InlineData("unknown-unassigned", "Unknown")]
    [InlineData("empty-package", "featureIds")]
    [InlineData("duplicate-package", "Duplicate package")]
    [InlineData("unknown-dependency", "unknown")]
    [InlineData("duplicate-dependency", "duplicate")]
    [InlineData("self-dependency", "itself")]
    [InlineData("cycle", "cycle")]
    [InlineData("duplicate-concern", "Duplicate")]
    [InlineData("unknown-concern-package", "unknown")]
    [InlineData("duplicate-concern-package", "duplicate")]
    [InlineData("empty-concern-packages", "packageIds")]
    [InlineData("unknown-question-feature", "unknown")]
    [InlineData("duplicate-question-feature", "duplicate")]
    [InlineData("duplicate-question", "Duplicate")]
    [InlineData("blank-question-id", "nonempty")]
    [InlineData("blank-question", "nonempty")]
    [InlineData("blank-package-name", "nonempty")]
    [InlineData("blank-package-summary", "nonempty")]
    [InlineData("blank-concern-statement", "nonempty")]
    [InlineData("blank-unassigned-reason", "nonempty")]
    [InlineData("complete-unassigned", "Complete")]
    [InlineData("complete-question", "Complete")]
    [InlineData("bad-status", "status")]
    [InlineData("version", "schemaVersion")]
    [InlineData("task", "taskId")]
    [InlineData("extraction", "extractionId")]
    [InlineData("document-path", "documentPath")]
    [InlineData("context-path", "contextPath")]
    [InlineData("document-hash", "documentSha256")]
    [InlineData("context-hash", "contextSha256")]
    [InlineData("uppercase-hash", "documentSha256")]
    [InlineData("short-hash", "documentSha256")]
    [InlineData("newline-hash", "documentSha256")]
    [InlineData("nonhex-hash", "documentSha256")]
    public void InvalidPlanCannotCertifySuccess(string mutation, string diagnostic)
    {
        var plan = Plan();
        var packages = plan["packages"]!.AsArray();
        switch (mutation)
        {
            case "omitted": packages.RemoveAt(2); break;
            case "duplicate-in-package": packages[0]!["featureIds"]!.AsArray().Add(FeatureIds[0]); break;
            case "duplicate-across-packages": packages[1]!["featureIds"]!.AsArray().Add(FeatureIds[0]); break;
            case "assigned-and-unassigned": plan["unassignedFeatures"] = new JsonArray(Unassigned(FeatureIds[0])); break;
            case "duplicate-unassigned":
                packages.RemoveAt(2);
                plan["unassignedFeatures"] = new JsonArray(Unassigned(FeatureIds[2]), Unassigned(FeatureIds[2]));
                break;
            case "unknown-assigned": packages[0]!["featureIds"] = new JsonArray("unknown.feature"); break;
            case "atomic-instead-of-feature": packages[0]!["featureIds"] = new JsonArray(FeatureIds[0] + ".b1"); break;
            case "unknown-unassigned": plan["unassignedFeatures"] = new JsonArray(Unassigned("unknown.feature")); break;
            case "empty-package": packages[0]!["featureIds"] = new JsonArray(); break;
            case "duplicate-package": packages.Add(packages[0]!.DeepClone()); break;
            case "unknown-dependency": packages[0]!["dependsOn"] = new JsonArray("missing"); break;
            case "duplicate-dependency": packages[1]!["dependsOn"] = new JsonArray("first", "first"); break;
            case "self-dependency": packages[0]!["dependsOn"] = new JsonArray("first"); break;
            case "cycle": packages[0]!["dependsOn"] = new JsonArray("third"); break;
            case "duplicate-concern": plan["sharedConcerns"]!.AsArray().Add(plan["sharedConcerns"]![0]!.DeepClone()); break;
            case "unknown-concern-package": plan["sharedConcerns"]![0]!["packageIds"] = new JsonArray("unknown"); break;
            case "duplicate-concern-package": plan["sharedConcerns"]![0]!["packageIds"] = new JsonArray("first", "first"); break;
            case "empty-concern-packages": plan["sharedConcerns"]![0]!["packageIds"] = new JsonArray(); break;
            case "unknown-question-feature": plan["openQuestions"] = new JsonArray(Question("Q-1", "unknown.feature")); break;
            case "duplicate-question-feature": plan["openQuestions"] = new JsonArray(Question("Q-1", FeatureIds[0], FeatureIds[0])); break;
            case "duplicate-question": plan["openQuestions"] = new JsonArray(Question("Q-1"), Question("Q-1")); break;
            case "blank-question-id": plan["openQuestions"] = new JsonArray(Question(" ")); break;
            case "blank-question":
                plan["openQuestions"] = new JsonArray(Question("Q-1"));
                plan["openQuestions"]![0]!["question"] = " ";
                break;
            case "blank-package-name": packages[0]!["name"] = " "; break;
            case "blank-package-summary": packages[0]!["summary"] = " "; break;
            case "blank-concern-statement": plan["sharedConcerns"]![0]!["statement"] = " "; break;
            case "blank-unassigned-reason":
                packages.RemoveAt(2);
                plan["unassignedFeatures"] = new JsonArray(Unassigned(FeatureIds[2]));
                plan["unassignedFeatures"]![0]!["reason"] = " ";
                break;
            case "complete-unassigned":
                packages.RemoveAt(2);
                plan["unassignedFeatures"] = new JsonArray(Unassigned(FeatureIds[2]));
                break;
            case "complete-question": plan["openQuestions"] = new JsonArray(Question("Q-1")); break;
            case "bad-status": plan["status"] = "ready"; break;
            case "version": plan["schemaVersion"] = "2.0"; break;
            case "task": plan["taskId"] = "other-task"; break;
            case "extraction": plan["extractionId"] = "other-extraction"; break;
            case "document-path": plan["documentPath"] = "other-document.json"; break;
            case "context-path": plan["contextPath"] = "other-context.json"; break;
            case "document-hash": plan["documentSha256"] = new string('0', 64); break;
            case "context-hash": plan["contextSha256"] = new string('0', 64); break;
            case "uppercase-hash": plan["documentSha256"] = Hash(document).ToUpperInvariant(); break;
            case "short-hash": plan["documentSha256"] = "abc"; break;
            case "newline-hash": plan["documentSha256"] = Hash(document) + "\n"; break;
            case "nonhex-hash": plan["documentSha256"] = new string('z', 64); break;
            default: throw new ArgumentException(mutation);
        }
        Save(distribution, plan);
        Invalid(Validate(), diagnostic);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("First")]
    [InlineData("_first")]
    [InlineData("1first")]
    [InlineData("first/second")]
    [InlineData("first..second")]
    [InlineData("first--second")]
    [InlineData("first_")]
    [InlineData("first\n")]
    public void PackageAndConcernIdsMustUseLowercaseSlugGrammar(string id)
    {
        var plan = Plan();
        plan["packages"]![0]!["id"] = id;
        Save(distribution, plan);
        Invalid(Validate(), "ID");
        plan = Plan();
        plan["sharedConcerns"]![0]!["id"] = id;
        Save(distribution, plan);
        Invalid(Validate(), "ID");
    }

    [Fact]
    public void SlugSeparatorsAndSeparatePackageConcernNamespacesAreAccepted()
    {
        var plan = Plan();
        const string id = "a1.b2-c3_d4";
        plan["packages"] = new JsonArray(Package(id, FeatureIds));
        plan["sharedConcerns"] = new JsonArray(Concern(id, id));
        Save(distribution, plan);
        Success(Validate(ready: true));
    }

    public static IEnumerable<object[]> RequiredProperties()
    {
        var fields = new Dictionary<string, string[]>
        {
            ["root"] = ["schemaVersion", "taskId", "extractionId", "documentPath", "contextPath", "documentSha256",
                "contextSha256", "status", "packages", "sharedConcerns", "unassignedFeatures", "openQuestions"],
            ["packages"] = ["id", "name", "summary", "featureIds", "dependsOn"],
            ["sharedConcerns"] = ["id", "statement", "packageIds"],
            ["unassignedFeatures"] = ["featureId", "reason"],
            ["openQuestions"] = ["id", "question", "featureIds"]
        };
        foreach (var (location, properties) in fields)
            foreach (var property in properties)
                foreach (var mutation in new[] { "missing", "null", "wrong-type" })
                    yield return [location, property, mutation];
        foreach (var location in fields.Keys)
            yield return [location, "unexpected", "unknown"];
    }

    [Theory]
    [MemberData(nameof(RequiredProperties))]
    public void StrictReaderRejectsMissingNullUnknownAndMalformedProperties(string location, string property, string mutation)
    {
        var plan = UnfinishedPlan();
        var target = (location == "root" ? plan : plan[location]![0]!).AsObject();
        switch (mutation)
        {
            case "missing": target.Remove(property); break;
            case "null": target[property] = null; break;
            case "wrong-type": target[property] = 42; break;
            case "unknown": target[property] = true; break;
        }
        Save(distribution, plan);
        Invalid(Validate());
    }

    [Theory]
    [InlineData("packages")]
    [InlineData("sharedConcerns")]
    [InlineData("unassignedFeatures")]
    [InlineData("openQuestions")]
    public void NullArrayRecordsAreRejected(string property)
    {
        var plan = Plan();
        plan[property] = new JsonArray((JsonNode?)null);
        Save(distribution, plan);
        Invalid(Validate(), property);
    }

    [Theory]
    [InlineData("duplicate-root")]
    [InlineData("duplicate-nested")]
    [InlineData("wrong-case")]
    [InlineData("root-null")]
    [InlineData("root-array")]
    [InlineData("invalid-json")]
    [InlineData("trailing-json")]
    [InlineData("invalid-unicode")]
    public void StrictReaderRejectsInvalidJsonTokens(string mutation)
    {
        var json = Plan().ToJsonString(ArtifactJson.Options);
        json = mutation switch
        {
            "duplicate-root" => json.Replace("\"schemaVersion\":", "\"schemaVersion\":\"1.0\",\"schemaVersion\":", StringComparison.Ordinal),
            "duplicate-nested" => json.Replace("\"summary\":", "\"summary\":\"duplicate\",\"summary\":", StringComparison.Ordinal),
            "wrong-case" => json.Replace("\"taskId\":", "\"TaskId\":", StringComparison.Ordinal),
            "root-null" => "null",
            "root-array" => "[]",
            "invalid-json" => "{",
            "trailing-json" => json + "{}",
            "invalid-unicode" => json.Replace("\"summary\":", "\"\\ud800\":\"bad\",\"summary\":", StringComparison.Ordinal),
            _ => throw new ArgumentException(mutation)
        };
        File.WriteAllText(distribution, json);
        Invalid(Validate());
    }

    [Theory]
    [InlineData("document")]
    [InlineData("context")]
    public void ExactByteChangesInvalidateBindingsEvenWhenJsonMeaningIsUnchanged(string changed)
    {
        Save(distribution, Plan());
        File.AppendAllText(changed == "document" ? document : context, "\n");
        Invalid(Validate(), changed + "Sha256");
    }

    [Theory]
    [InlineData("document")]
    [InlineData("context")]
    public void CapturedInputsRetainStrictSizeAndDepthLimits(string artifact)
    {
        Save(distribution, Plan());
        var path = artifact == "document" ? document : context;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            stream.SetLength(64L * 1024 * 1024 + 1);
        Invalid(Validate(), "input limit");
        Invalid(Prepare(Path.Combine(directory, "new.json")), "input limit");
        File.WriteAllText(path, new string('[', 70) + "null" + new string(']', 70));
        Invalid(Validate());
        Invalid(Prepare(Path.Combine(directory, "new.json")));
        Assert.False(File.Exists(Path.Combine(directory, "new.json")));
    }

    [Theory]
    [InlineData("document")]
    [InlineData("context")]
    public void CapturedInputHashesIncludeAcceptedUtf8BomBytes(string artifact)
    {
        var path = artifact == "document" ? document : context;
        File.WriteAllBytes(path, [0xef, 0xbb, 0xbf, .. File.ReadAllBytes(path)]);
        Success(Prepare());
        Assert.Equal(Hash(path), Read(distribution)[artifact + "Sha256"]!.GetValue<string>());
        Save(distribution, Plan());
        Success(Validate(ready: true));
    }

    [Fact]
    public void CapturedPartialReadinessCannotBeCombinedWithReplacementCompleteContext()
    {
        var metadata = Metadata();
        metadata.Status = "partial";
        Save(context, metadata);
        var plan = JsonSerializer.Deserialize<DistributionPlan>(Plan(), ArtifactJson.Options)!;
        var capturedDocument = StrictJson.ReadSnapshot<FeatureDocument>(document);
        var capturedContext = StrictJson.ReadSnapshot<DocumentContext>(context);
        var retained = Path.Combine(directory, "retained-context.json");
        File.Move(context, retained);
        Save(context, Metadata());

        var extraction = Extraction();
        var symbols = ArtifactValidation.Extraction(extraction);
        var ready = DocumentValidation.Validate(capturedDocument.Value, capturedContext.Value, extraction, symbols,
            input, document, context);
        Assert.False(ready);
        Assert.Equal("complete", StrictJson.Read<DocumentContext>(context).Status);
        File.Move(retained, context, overwrite: true);

        Assert.Equal(Hash(context), capturedContext.Sha256);
        var error = Assert.Throws<InvalidDataException>(() =>
        {
            DistributionValidation.Validate(plan, capturedDocument.Value, capturedContext.Value, document, context, distribution,
                capturedDocument.Sha256, capturedContext.Sha256, ready);
        });
        Assert.Contains("Complete", error.Message);
    }

    [Fact]
    public void PlanRelativePathsResolveAgainstItsOwnDirectoryAndInputRulesAreUnchanged()
    {
        var authored = AuthoredDocument();
        authored.Source.Root = ".";
        var metadata = Metadata();
        metadata.DocumentPath = ".\\document.json";
        Save(document, authored);
        Save(context, metadata);
        var nested = Path.Combine(directory, "nested");
        Directory.CreateDirectory(nested);
        var path = Path.Combine(nested, "distribution.json");
        var plan = Plan();
        plan["documentPath"] = "..\\document.json";
        plan["contextPath"] = "..\\document.context.json";
        Save(path, plan);
        Success(Validate(ready: true, path: path));
        plan["documentPath"] = ".\\document.json";
        Save(path, plan);
        Invalid(Validate(path: path), "documentPath");
    }

    [Theory]
    [InlineData("extraction")]
    [InlineData("document")]
    [InlineData("context")]
    public void BothCommandsValidateUpstreamBeforeReportingSuccess(string artifact)
    {
        Save(distribution, Plan());
        var path = artifact == "extraction" ? input : artifact == "document" ? document : context;
        var value = Read(path);
        value["unexpected"] = true;
        Save(path, value);
        var output = Path.Combine(directory, "new.json");
        Invalid(Prepare(output));
        Assert.False(File.Exists(output));
        Invalid(Validate());
    }

    [Theory]
    [InlineData("input")]
    [InlineData("document")]
    [InlineData("context")]
    [InlineData("project")]
    [InlineData("entry-project")]
    [InlineData("source")]
    [InlineData("additional-source")]
    [InlineData("relationship")]
    [InlineData("diagnostic")]
    [InlineData("project-diagnostic")]
    [InlineData("context-evidence")]
    [InlineData("feature-source")]
    [InlineData("guide")]
    public void PrepareProtectsAllReferencedEvidenceIncludingAbsentPathsAndAliases(string location)
    {
        var extraction = Extraction();
        var authored = AuthoredDocument();
        var metadata = Metadata();
        var output = Path.Combine(directory, "evidence.json");
        switch (location)
        {
            case "input": output = input; break;
            case "document": output = document; break;
            case "context": output = context; break;
            case "project": extraction.Projects[0].File = "evidence.json"; break;
            case "entry-project": extraction.InputPath = "evidence.json"; break;
            case "source": extraction.Projects[0].Symbols[0].Source = new("evidence.json", 1, 1); break;
            case "additional-source": extraction.Projects[0].Symbols[0].AdditionalSources.Add(new("evidence.json", 1, 1)); break;
            case "relationship": extraction.Projects[0].Symbols[0].Relationships.Add(new("calls", "external:M:Other", "Other", new("evidence.json", 1, 1))); break;
            case "diagnostic":
                extraction.Status = "partial";
                metadata.Status = "partial";
                metadata.UpstreamStatus = "partial";
                extraction.Diagnostics.Add(new("D1", "info", "Evidence", new("evidence.json", 1, 1)));
                break;
            case "project-diagnostic": extraction.Projects[0].Diagnostics.Add(new("D1", "info", "Evidence", new("evidence.json", 1, 1))); break;
            case "context-evidence": metadata.EvidenceReferences.Add("evidence.json"); break;
            case "feature-source": authored.Features[0].SourceRefs.Add("evidence.json#L1"); break;
            case "guide": metadata.PortingGuide = new("evidence.json", "r1", true); break;
        }
        Save(input, extraction);
        Save(document, authored);
        Save(context, metadata);
        var before = Snapshot();
        output = Path.Combine(directory, ".", Path.GetFileName(output));
        if (OperatingSystem.IsWindows())
            output = output.ToUpperInvariant();
        Invalid(Prepare(output), "cannot overwrite");
        Unchanged(before);
        Assert.False(File.Exists(distribution));
        Assert.False(File.Exists(Path.Combine(directory, "evidence.json")));
        Assert.Empty(Directory.GetFiles(directory, "*.pending", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("existing")]
    [InlineData("extension")]
    [InlineData("directory")]
    [InlineData("parent-file")]
    public void PrepareIsCreateOnlyAndRejectsUnsafeDestinations(string condition)
    {
        var output = distribution;
        switch (condition)
        {
            case "existing": File.WriteAllText(distribution, "authored plan"); break;
            case "extension": output = Path.Combine(directory, "bad.md"); break;
            case "directory": Directory.CreateDirectory(distribution); break;
            case "parent-file":
                var parent = Path.Combine(directory, "parent");
                File.WriteAllText(parent, "keep");
                output = Path.Combine(parent, "plan.json");
                break;
        }
        var before = Snapshot();
        Invalid(Prepare(output));
        Unchanged(before);
        if (condition == "existing")
            Assert.Equal("authored plan", File.ReadAllText(distribution));
        Assert.Empty(Directory.GetFiles(directory, "*.pending", SearchOption.AllDirectories));
    }

    [Fact]
    public void PrepareRejectsOutputThroughDirectoryReparsePoint()
    {
        var target = Path.Combine(directory, "target");
        var alias = Path.Combine(directory, "alias");
        Directory.CreateDirectory(target);
        if (OperatingSystem.IsWindows())
        {
            using var process = Process.Start(new ProcessStartInfo("powershell")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command",
                    $"New-Item -ItemType Junction -Path '{alias}' -Target '{target}' -ErrorAction Stop | Out-Null" }
            })!;
            Assert.True(process.WaitForExit(30000));
            Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        }
        else
            Directory.CreateSymbolicLink(alias, target);
        try
        {
            Invalid(Prepare(Path.Combine(alias, "plan.json")), "reparse");
            Assert.Empty(Directory.GetFiles(target));
        }
        finally
        {
            Directory.Delete(alias);
        }
    }

    [Theory]
    [InlineData("prepare-distribution", "--force")]
    [InlineData("prepare-distribution", "--require-ready")]
    [InlineData("validate-distribution", "--force")]
    [InlineData("validate-distribution", "--output")]
    public void CommandsRejectUnknownFlags(string command, string flag) =>
        Invalid(Run(command, "--input", input, flag), "Unknown option");

    [Fact]
    public void MissingInputsAndRequiredFlagsHaveExplicitErrors()
    {
        Invalid(Run("prepare-distribution", "--input", input, "--document", document, "--context", context), "--output");
        Invalid(Run("validate-distribution", "--input", input, "--document", document, "--context", context), "--distribution");
        Invalid(Validate());
        File.Delete(context);
        Invalid(Prepare());
        Assert.False(File.Exists(distribution));
    }

    [Fact]
    public void HelpListsBothDistributionCommands()
    {
        var result = Run("--help");
        Success(result);
        Assert.Contains("prepare-distribution --input", result.Stdout);
        Assert.Contains("validate-distribution --input", result.Stdout);
    }

    private FeatureDocument AuthoredDocument() => new()
    {
        Source = new() { Kind = "library", Root = directory },
        Features = FeatureIds.Select(id => new DocumentFeature
        {
            Id = id, Name = id, Signature = "string Echo(string value)", Summary = "Synthetic fixture objective.",
            Parameters = [new("value", "string", [])], Returns = new("string", "The supplied value."),
            Behaviors = [new(id + ".b1", "Returns the supplied value.")],
            Errors = [new(id + ".e1", "Synthetic failure is requested.", "FixtureError")],
            Invariants = [new(id + ".i1", "The value remains unchanged.")],
            Examples = [new(id + ".x1", JsonSerializer.SerializeToElement(new { value = "hello" }), JsonSerializer.SerializeToElement("hello"))],
            Visibility = "public", SourceRefs = ["Example.cs#L1"]
        }).ToList()
    };

    private DocumentContext Metadata() => new()
    {
        TaskId = "synthetic-distribution", ExtractionId = "synthetic-snapshot", Status = "complete", UpstreamStatus = "complete",
        DocumentPath = document, EvidenceReferences = [input],
        FeatureEvidence = FeatureIds.Select(id => new FeatureEvidence(id, "confirmed", [SymbolId])).ToList(),
        Coverage = [new(SymbolId, "required", "Synthetic public behavior.")]
    };

    private ExtractionArtifact Extraction() => new()
    {
        TaskId = "synthetic-distribution", ExtractionId = "synthetic-snapshot", RootDirectory = directory,
        InputPath = "Example.csproj", Status = "complete",
        Projects =
        [
            new()
            {
                Id = "p", Name = "Example", File = "Example.csproj", OutputKind = "DynamicallyLinkedLibrary",
                TargetFramework = "net8.0", LanguageVersion = "CSharp12", NullableContext = "Enable",
                Symbols =
                [
                    new()
                    {
                        Id = SymbolId, Kind = "Method", Name = "Echo", DisplayName = "Example.Echo(string)",
                        Accessibility = "Public", IsPublicApi = true, Type = "string",
                        Source = new("Example.cs", 1, 1), Declaration = "public string Echo(string value) => value;"
                    }
                ]
            }
        ],
        Limitations = ["Synthetic test evidence, not compiler or agent output."]
    };

    private JsonNode Plan() => new JsonObject
    {
        ["schemaVersion"] = "1.0", ["taskId"] = "synthetic-distribution", ["extractionId"] = "synthetic-snapshot",
        ["documentPath"] = document, ["contextPath"] = context,
        ["documentSha256"] = Hash(document), ["contextSha256"] = Hash(context), ["status"] = "complete",
        ["packages"] = new JsonArray(Package("first", [FeatureIds[0]]), Package("second", [FeatureIds[1]], "first"),
            Package("third", [FeatureIds[2]], "second")),
        ["sharedConcerns"] = new JsonArray(Concern("shared", "first", "second")),
        ["unassignedFeatures"] = new JsonArray(), ["openQuestions"] = new JsonArray()
    };

    private JsonNode UnfinishedPlan()
    {
        var plan = Plan();
        plan["status"] = "partial";
        plan["packages"]!.AsArray().RemoveAt(2);
        plan["unassignedFeatures"] = new JsonArray(Unassigned(FeatureIds[2]));
        plan["openQuestions"] = new JsonArray(Question("Q-1", FeatureIds[0]));
        return plan;
    }

    private static JsonObject Package(string id, string[] features, params string[] dependencies) => new()
    {
        ["id"] = id, ["name"] = "Synthetic package", ["summary"] = "Implement the selected existing features.",
        ["featureIds"] = Strings(features), ["dependsOn"] = Strings(dependencies)
    };

    private static JsonObject Concern(string id, params string[] packages) => new()
    {
        ["id"] = id, ["statement"] = "Coordinate the shared convention.", ["packageIds"] = Strings(packages)
    };

    private static JsonObject Unassigned(string id) => new() { ["featureId"] = id, ["reason"] = "Review needed." };

    private static JsonObject Question(string id, params string[] features) => new()
    {
        ["id"] = id, ["question"] = "What coordination is required?", ["featureIds"] = Strings(features)
    };

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray());
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static JsonNode Read(string path) => JsonNode.Parse(File.ReadAllText(path))!;
    private static JsonNode Summary(Result result) => JsonNode.Parse(result.Stdout)!;
    private static void Save<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, ArtifactJson.Options));

    private Dictionary<string, byte[]> Snapshot(bool includePlan = false) =>
        (includePlan ? new[] { input, document, context, distribution } : [input, document, context])
            .ToDictionary(path => path, File.ReadAllBytes);

    private static void Unchanged(Dictionary<string, byte[]> before)
    {
        foreach (var (path, bytes) in before)
            Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    private Result Prepare(string? output = null) => Run("prepare-distribution", "--input", input,
        "--document", document, "--context", context, "--output", output ?? distribution);

    private Result Validate(bool ready = false, string? path = null) => Run(
        ["validate-distribution", "--input", input, "--document", document, "--context", context,
            "--distribution", path ?? distribution, .. ready ? new[] { "--require-ready" } : []]);

    private Result Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(root, "src", "Requirements.Collector", "bin",
            new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net8.0", "Requirements.Collector.dll"));
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30000), "Collector timed out.");
        return new(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static void AssertSummary(Result result, string status, bool upstreamReady, bool ready,
        int packages, int assigned, int unassigned, int requirements, int questions)
    {
        var summary = Summary(result);
        Assert.Equal(new[]
        {
            "taskId", "extractionId", "status", "distributionPath", "upstreamReadyForDownstream", "readyForDownstream",
            "structureAndTraceabilityValid", "packageCount", "assignedFeatureCount", "unassignedFeatureCount",
            "assignedRequirementCount", "openQuestionCount", "semanticParityVerified"
        }, summary.AsObject().Select(property => property.Key));
        Assert.Equal("synthetic-distribution", summary["taskId"]!.GetValue<string>());
        Assert.Equal("synthetic-snapshot", summary["extractionId"]!.GetValue<string>());
        Assert.Equal(status, summary["status"]!.GetValue<string>());
        Assert.True(Path.IsPathFullyQualified(summary["distributionPath"]!.GetValue<string>()));
        Assert.Equal(upstreamReady, summary["upstreamReadyForDownstream"]!.GetValue<bool>());
        Assert.Equal(ready, summary["readyForDownstream"]!.GetValue<bool>());
        Assert.True(summary["structureAndTraceabilityValid"]!.GetValue<bool>());
        Assert.Equal(packages, summary["packageCount"]!.GetValue<int>());
        Assert.Equal(assigned, summary["assignedFeatureCount"]!.GetValue<int>());
        Assert.Equal(unassigned, summary["unassignedFeatureCount"]!.GetValue<int>());
        Assert.Equal(requirements, summary["assignedRequirementCount"]!.GetValue<int>());
        Assert.Equal(questions, summary["openQuestionCount"]!.GetValue<int>());
        Assert.False(summary["semanticParityVerified"]!.GetValue<bool>());
    }

    private static void Success(Result result)
    {
        Assert.True(result.ExitCode == 0, $"Exit {result.ExitCode}: {result.Stderr}\n{result.Stdout}");
        Assert.Empty(result.Stderr);
    }

    private static void Invalid(Result result, string? diagnostic = null)
    {
        Assert.True(result.ExitCode == 2, $"Expected exit 2, got {result.ExitCode}: {result.Stderr}\n{result.Stdout}");
        Assert.Contains("error", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unknown command", result.Stderr, StringComparison.OrdinalIgnoreCase);
        if (diagnostic is not null)
            Assert.Contains(diagnostic, result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Stdout);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
    private sealed record Result(int ExitCode, string Stdout, string Stderr);
}
