# Requirements collector

Start with [the agent](../.agents/agents/requirements-collector.agent.md) and
[its skill](../.agents/skills/collect-requirements/SKILL.md). The AI reads actual
C# behavior and writes requirements. The .NET helper prepares authoring drafts,
validates their structure and traceability, and renders a readable view. It
does not infer behavior, generate tests, or implement Rust.

## Pipeline contract

The default is **compact JSON, with context kept separately**:

| File | Purpose |
| --- | --- |
| `document.json` | Public API, parameters/returns, identified behaviors/errors/invariants and concrete examples. Feature shape consumed by GenTest and Code. |
| `document.context.json` | Version 2.0 association, readiness, evidence links, coverage and unresolved questions. For the orchestrator, not copied into every requirement. |
| `requirements.md` (optional) | Deterministic readable view of the same document, not a separately authored report. |

Full compiler facts, source excerpts and fingerprints stay in the original
extractor artifacts. A few `source_refs` in the document provide navigation;
the companion links features to canonical compiler symbols and to the original
evidence. Later agents can retrieve that evidence without loading all of it
into every prompt.

The feature document intentionally has the `source`/`features` shape in PR #2,
not the old collector's flat `requirements` shape. It has no metadata envelope
or embedded extraction report. Version `2.0` belongs to its companion; canonical
compiler extraction and historical requirements schema `1.0` are unchanged.

## Agent assignment

From the repository root, use the `.github\agents` entry point:

```powershell
copilot --agent requirements-collector
```

Supply actual paths and IDs, for example:

```text
taskId: <task ID from the extraction>
extractionPath: <original extractor Markdown report>
compilerArtifactPath: <original compiler JSON>
expectedExtractionId: <exact extraction ID>
outputPath: artifacts\run\document.json
contextPath: artifacts\run\document.context.json
reportPath: artifacts\run\requirements.md
scope: the supplied SDK public API
executionApproved: false
portingGuide: null
```

`reportPath` is optional. When `extractionPath` is already canonical compiler
JSON, it also supplies `compilerArtifactPath`. When it is Markdown, resolve
the companion compiler path from the supplied input or its handoff; missing
compiler evidence is a blocker, not permission to invent compiler facts.

Preserve the report's status separately from compiler status. The original
report can be partial even when its compiler snapshot is complete. Source and
test paths resolve against their supplied roots, not the agent's worktree.
Old `artifactFormat: markdown` assignments must use the JSON workflow and an
optional rendered `reportPath`; there is no independent Markdown-only spec.

## Feature document shape

Root fields are `source` and `features`. `source` requires `language` (`csharp`),
`kind` (`library`, `sdk`, `application`, or `solution`) and `root`. Optional
`name` and `target_crate` accommodate downstream naming.

Each feature has these fields:

| Field | Content |
| --- | --- |
| `id` | Stable dot-scoped lowercase ID; underscore is allowed. |
| `name`, `signature`, `summary` | Actual public API and a concise description of its behavior. |
| `params` | Objects with `name`, C# `type`, and string-array `constraints`. |
| `returns` | `type` and `description`; distinguish `void` side effects from returned data. |
| `behaviors` | `{id, statement}` objects, IDs `<feature>.bN`. |
| `errors` | `{id, condition, result}` objects, IDs `<feature>.eN`. |
| `invariants` | `{id, statement}` objects, IDs `<feature>.iN`. |
| `examples` | `{id, input, expected}` objects, IDs `<feature>.xN`; input is an object and expected is a JSON value. |
| `visibility` | Source API visibility. |
| `source_refs` | Short retrievable source references; no copied source or fingerprint tables. |

All arrays must be present, even when empty. Every atomic requirement must be
an identified object, not a bare string. IDs must be unique and belong to their
feature and category. Signatures and summaries alone do not establish behavioral
coverage. Requirements remain descriptive, not a prescription of Rust types.
Only a draft may have no features or a feature with no atomic requirements;
partial/blocked/complete documents must contain authored requirements.

For example, this is an **illustrative feature fragment**, not actual agent
output or an artifact to pass without its enclosing document and context:

```json
{
  "id": "calculator.add",
  "name": "Add",
  "signature": "decimal Add(decimal left, decimal right)",
  "summary": "Return the result of C# decimal addition.",
  "params": [
    { "name": "left", "type": "decimal", "constraints": [] },
    { "name": "right", "type": "decimal", "constraints": [] }
  ],
  "returns": { "type": "decimal", "description": "The decimal result." },
  "behaviors": [
    { "id": "calculator.add.b1", "statement": "Return left + right using System.Decimal semantics." }
  ],
  "errors": [
    { "id": "calculator.add.e1", "condition": "decimal.MaxValue plus 1", "result": "OverflowException" }
  ],
  "invariants": [],
  "examples": [
    { "id": "calculator.add.x1", "input": { "left": "0.1", "right": "0.2" }, "expected": "0.3" }
  ],
  "visibility": "public",
  "source_refs": ["Calculator.cs#L5"]
}
```

JSON numbers must not carry decimal-typed operands/results: preserve their
values as strings. Error examples may use objects such as
`{"error":"OverflowException"}`. Other APIs can use object, boolean, string,
array or null expected values where appropriate. A printed-output example
describes captured output; it must not silently turn a `void` API into one
returning that string.

Only assert rules backed by implementation/tests. C# decimal has finite
precision, rounding, scale reduction and underflow; product scale is not
universally the sum of input scales. Missing null/newline/overflow information
is an open question, not a fabricated behavior. An empty error array is not
proof that framework/runtime failures cannot happen.

## Context companion

The companion requires:

| Field | Meaning |
| --- | --- |
| `schemaVersion` | `2.0`, distinct from extraction schema `1.0`. |
| `taskId`, `extractionId` | Exact canonical extraction association. |
| `documentPath` | Path identifying the paired document. |
| `status` | `draft`, `partial`, `blocked`, or `complete`. |
| `upstreamStatus` | `complete` or `partial`; preserve a partial narrative even with complete compiler JSON. |
| `evidenceReferences` | Original compiler/report/source/test paths, without duplicating their contents. |
| `featureEvidence` | Exactly one `{featureId, confidence, evidenceIds}` per feature; canonical compiler IDs, not short aliases. |
| `coverage` | `{symbolId, disposition, reason}` for each public compiler symbol. |
| `openQuestions` | `{id, question, evidenceIds}`; empty evidence IDs allowed for global questions. |
| `portingGuide` | `null` or `{path, revision, approved}` supplied by the orchestrator. |

Confidence is `confirmed`, `inferred`, or `uncertain`. It describes an
interpretation of evidence, not execution or Rust parity. Required symbols
must occur in feature evidence, excluded symbols must not, and pending symbols
remain unfinished. Unknown/duplicate symbols and dangling feature links fail.

The helper validates association and structure, not source freshness or the
truth of the requirement. The agent must check live evidence against the
extractor snapshot and preserve limitations in status/questions. A context
path or matching ID is not cryptographic authentication.

Relative `source.root` paths resolve against the document directory.
`documentPath`, `evidenceReferences`, and the guide path resolve against the
context directory. Feature `source_refs` resolve against the extraction source
root, with an optional `#Lstart-Lend` location. Preparation writes absolute
source-root, document and extraction-reference paths to avoid worktree ambiguity.

## Helper commands

Use .NET 8 or a newer SDK supporting the .NET 8 runtime. Build the helper from
this checkout when needed:

```powershell
dotnet build src\Requirements.Collector\Requirements.Collector.csproj
```

The helper reads JSON only; it does not restore or evaluate the input SDK.
Run from the repository root with actual artifact paths:

```powershell
dotnet run --no-build --project src\Requirements.Collector -- prepare --input artifacts\extraction.json --output artifacts\document.json --context artifacts\document.context.json
dotnet run --no-build --project src\Requirements.Collector -- inspect --input artifacts\extraction.json --symbol <canonical-symbol-id> --offset 0 --max-chars 4096
dotnet run --no-build --project src\Requirements.Collector -- validate --input artifacts\extraction.json --document artifacts\document.json --context artifacts\document.context.json
dotnet run --no-build --project src\Requirements.Collector -- validate --input artifacts\extraction.json --document artifacts\document.json --context artifacts\document.context.json --require-ready
dotnet run --no-build --project src\Requirements.Collector -- render --input artifacts\extraction.json --document artifacts\document.json --context artifacts\document.context.json --output artifacts\requirements.md
```

`prepare` creates an empty authoring draft and pending coverage, not inferred
behavior. The agent authors both new files before validation. `render` reads
that pair and writes a new Markdown view; it does not run AI. New outputs are
create-only, must not alias each other or overwrite evidence, and reject
symlinks/junctions/reparse points. The new workflow has no `--force`.

Exit `0` means the requested operation succeeded. Ordinary validation may
accept a valid draft/partial package: inspect `readyForDownstream`, not just
the exit code. Exit `2` reports invalid input, usage, I/O, or failed readiness.
`semanticParityVerified` is always false.

The readiness flag requires complete, nonempty requirements with no pending
coverage, questions, uncertain feature evidence, partial upstream status, or
supplied unapproved guide. A null guide does not itself settle a mapping
decision: actual unresolved choices must remain questions. Do not remove them
to make a document dispatchable.

Strict readers reject unknown/duplicate/missing required properties, invalid
types, null records and malformed/wrong-shaped JSON instead of treating it as
an empty successful specification. The new document commands also reject
compiler snapshots marked complete when their diagnostics indicate analysis
gaps, using the same rule as the extractor. Ordinary project warnings do not
automatically indicate missing analysis, and honestly partial evidence remains
usable without being eligible for downstream dispatch.
Input size is limited to 64 MiB. `inspect`
still uses exact IDs, UTF-16 offsets without splitting surrogate pairs, a
maximum excerpt length of 8192, and a 64 KiB packet bound.

## Downstream handoff

Before dispatch, the orchestrator runs validation with `--require-ready` and
retains the document/context pair with original evidence. GenTest consumes
the document to create tests, manifest and differential cases. Code consumes
the same document plus that generated suite and manifest. The verifier uses
the same requirement IDs and independent original evidence.

PR #2's feature shape is compatible with the document, but its profiles/gates
do not automatically enforce this new context's readiness. The orchestrator
must enforce the collector gate; PR coverage success is not a substitute.
There is no orchestrator implementation in this repository yet. Fixing the PR's
coverage/parity defects and supplying its C# reference harness are separate
work. No real downstream-agent execution is implied by schema compatibility.

## Historical schema 1.0

Compiler extraction schema remains `1.0`. New snapshots leave its legacy
`assemblyReferences` array empty; this suppresses the ambient compiler catalog,
not actual dependency relationships or the references Roslyn needs to bind code.
Historical snapshots with populated catalogs remain readable.

Old flat requirements
(`requirements`, `coverage`, `openQuestions`) remain readable only through
the explicit legacy commands:

```powershell
dotnet run --no-build --project src\Requirements.Collector -- prepare-legacy --input artifacts\extraction.json --output artifacts\legacy.draft.json
dotnet run --no-build --project src\Requirements.Collector -- validate-legacy --input artifacts\extraction.json --requirements artifacts\legacy.requirements.json
```

Legacy `prepare-legacy --force` retains the original restriction to a valid
same-snapshot requirements artifact. These commands do not convert legacy
requirements into feature documents. Historical real reports are evidence of
their original runs, not output from the new contract; never rewrite them or
rebind snapshot IDs in place.

## Development

The root solution includes both helpers, their shared contracts and tests,
and the calculator sample:

```powershell
dotnet test CSharpToRust.sln
```

For focused collector changes after restoring the projects:

```powershell
dotnet test tests\Requirements.Collector.Tests\Requirements.Collector.Tests.csproj --no-restore --nologo --verbosity minimal
dotnet test tests\Pipeline.Contracts.Tests\Pipeline.Contracts.Tests.csproj --no-restore --nologo --verbosity minimal
```

The existing xUnit infrastructure uses synthetic portable evidence and isolated
scratch files. Such fixtures are not an extractor or actual agent output.
No input-project execution is needed to exercise the collector contract.
