# C# extractor agent

Start with [the agent definition](../.agents/agents/csharp-extractor.agent.md).
It declares the role, skills, inputs, outputs, workflow, and orchestrator handoff.
The linked [skill](../.agents/skills/extract-csharp/SKILL.md) supplies the inspection
procedure. The Roslyn helper is the required compiler-analysis tool, not the
agent itself.

## Use

Load the Markdown agent definition in your agent host and supply:

```text
taskId: calculator-inspection
projectPath: samples\Calculator\Calculator.csproj
outputPath: artifacts\calculator-extraction.md
compilerOutputPath: artifacts\calculator-current.json
scope: public calculator API and its implementation; inspect nearby tests as evidence
executionApproved: true
```

Use execution approval only for source you trust. The agent runs Roslyn to
produce structured compiler facts, inspects relevant source and tests, and
writes a Markdown evidence report grouped by feature/component and tied to the
same extraction ID. Behavior summaries navigate the original evidence; they do
not replace it. Without approved compiler analysis, it reports a blocker rather than falling back to
AI-only parsing.

Optional `testPaths` identifies approved tests to inspect. Optional
`portingGuide` contains `path`, `revision`, and `approved`, supplied by the
orchestrator. Match the actual guide to the reviewed revision/fingerprint.
Only approved guidance defines Rust conventions. Missing or conflicting
decisions are reported, not invented; they do not prevent gathering C# facts.
The orchestrator owns review of conventions for errors, numeric types, async,
ownership/disposal and unsupported dependencies. This agent does not author or
approve that policy. Guide approval does not authorize project/test execution.
These are agent inputs, not new Roslyn command-line flags.

For Copilot CLI, a small entry point is provided under `.github\agents`:

```powershell
copilot --agent csharp-extractor
```

Then provide the assignment above. The canonical definition remains under
`.agents\agents`. Its `skills` list declares dependencies for readers and hosts
that support that metadata; it is not a universal automatic-loading guarantee.
The entry point explicitly instructs the agent to read the definition and skills.

## Roslyn helper

`src\CSharp.Extractor` uses Roslyn's syntax and semantic APIs, MSBuildWorkspace,
and Microsoft.Build.Locator. It loads `.csproj` or `.sln` inputs and emits types,
members, parameters, relationships, source excerpts, migration signals, and
diagnostics. `src\Pipeline.Contracts` defines its versioned JSON shape.

File-local types and their members include relative source-file scope in their
canonical IDs. Identically named helpers in different files therefore remain
distinct, including their containing-symbol links and resolved call targets.

New extractions omit the ambient assembly-reference catalog at generation time,
not just from compact views. Roslyn still loads its references for type/call
resolution. Project references, parameter/return types and observed external
relationships remain available. Full identities on actual external symbol IDs
are retained for unambiguous binding, not repeated as an inventory of every
framework assembly.

The schema 1.0 `assemblyReferences` property remains an empty array for existing
readers; it means the catalog is not emitted, not that the project has no
dependencies. Historical snapshots may contain the old catalog and remain
readable. Do not rewrite their contents under an existing extraction ID; a new
extraction produces the smaller artifact and its own snapshot ID.

For the trusted calculator sample, from the repository root:

```powershell
dotnet build src\CSharp.Extractor\CSharp.Extractor.csproj
dotnet restore samples\Calculator\Calculator.csproj
New-Item -ItemType Directory -Force artifacts
dotnet run --project src\CSharp.Extractor --no-build -- extract --input samples\Calculator\Calculator.csproj --output artifacts\calculator-current.json --task-id calculator-inspection --allow-project-execution
```

The SDK and runtime must support .NET 8. Add `--configuration Release` or
`--framework net8.0` when needed. Output must be a new JSON file; an existing
file is never overwritten. Restore of an unfamiliar input requires explicit
approval. Project evaluation and source generators can execute code; the
approval flag does not isolate or sandbox it.

Exit `0` means compiler extraction completed within documented scope; `2`
means a partial artifact was written; `1` means a request/load/write failure.
Never treat `partial` as complete or use zero diagnostics to claim no limitations.

## Compact AI input

Keep the full Roslyn JSON as the local evidence store. The helper projects
bounded, minified JSON views for the agent without running AI, MSBuild, or the
input project. It never rewrites the input artifact or removes its private code.
Use a small snapshot-matched source file directly when it is the clearest unit;
retrieve only missing compiler facts. For larger code, use relevant source
ranges or the focused views below. Reading small files does not bypass required
Roslyn extraction. Do not require facts/code/relations calls for every method
or use JSON byte reduction alone as the measure of an efficient workflow.

```powershell
dotnet run --project src\CSharp.Extractor --no-build -- index --input artifacts\calculator-current.json
dotnet run --project src\CSharp.Extractor --no-build -- context --input artifacts\calculator-current.json
dotnet run --project src\CSharp.Extractor --no-build -- inspect --input artifacts\calculator-current.json --symbol s2 --part code
```

Choose `s2` only if the index for your snapshot identifies the desired member.
Full canonical symbol IDs also work with `--symbol`.

| View / inspect part | Contents |
| --- | --- |
| `index` | Public declarations grouped by project/file, signatures and short refs; no bodies or assembly catalog. |
| `context` | Build configuration, framework, nullable/overflow settings, defines, project references, diagnostics and limitation text. |
| `inspect --part code` (default) | One declaration with safe trivia removed; statements, branches and token text retained. |
| `inspect --part facts` | Exact parameter types/defaults, attributes, visibility, migration signals and containing-symbol ref. |
| `inspect --part relations` | Observed relationships; internal targets have short refs and `available: true`. |
| `inspect --part members` | Direct children, including private state/helpers; inspect a containing type to discover code not linked by calls. |
| `inspect --part raw` / `documentation` | Original stored declaration / XML documentation, independently paged. |

Namespaces, generic arguments, nullable types and parameter `ref` kinds are
not replaced with ambiguous short type names. Short `sN` and `pN` refs are
deterministic within a snapshot; `inspect` returns the canonical `symbolId`
for citations. Public signatures are navigation, not a behavior summary.
Follow relevant private implementations and supplemental source/tests.

All commands default to `--max-bytes 4096` (UTF-8, including the console newline;
allowed range 512..16384). Item pages support `--limit 25` (1..100). Text pages
support `--max-chars 2000` (1..8000 UTF-16 code units, never splitting a surrogate
pair). Pass the returned `nextOffset` to `--offset` for the same snapshot and
view/part until it is null. `facts` is one bounded object and only accepts offset
zero. Pages automatically shrink to fit the byte budget. A single item or
mandatory metadata that cannot fit fails explicitly; it is not silently
discarded. Do not treat that failure as an empty successful view.

Code compaction verifies the original lexical token sequence. Directives,
disabled source, or malformed lexical excerpts cause a verbatim fallback
(`compacted: false`). Source line ranges always refer to original source, not
compacted positions. `sourceTruncated` remains true on every page of an affected
stored excerpt; pagination cannot repair the full extractor's source cap.
Compiler status and snapshot identity remain visible in every view.

The read-only commands accept canonical schema 1.0 extraction JSON up to 64 MiB.
Missing/defaulted fields, unsupported schemas, malformed/null records, duplicate
IDs, invalid selectors, and invalid bounds are errors. Exit `0` means a view was
rendered, **not** that the compiler artifact is complete; inspect its `status`.
Exit `1` reports a failure on stderr without emitting a successful JSON view.
These projections are not replacement artifacts for the requirements collector.

## Evidence and handoff

The helper owns compiler facts and the `extractionId`; the agent owns the
Markdown evidence report. Each feature/component section includes entry points,
relevant original code/private dependencies, compiler facts, existing test
assertions, semantic risks and unexamined areas. Review shared state, resource
lifetimes, initialization, callbacks and async disposal where relevant, not
only explicit calls and throws.

The report's `evidenceReferences` identifies the compiler artifact and original
source/test paths, line ranges and revision/fingerprints. A Git revision alone
does not identify uncommitted files, and an extraction ID does not verify live
source freshness. Keep code observations, test assertions, approved execution
results and AI interpretations distinct. Stale or inaccessible evidence needs a
focused request, not an inferred implementation.

Pass both artifacts, original evidence references, unit coverage and guide
identity/approval through the orchestrator. The collector, implementer, tester
and verifier need this evidence alongside summaries and requirements. The
collector can use canonical JSON for its structured workflow; unit packages
and handoff metadata do not add fields to schema 1.0 or create a new helper.

The collector's pipeline output is a compact `document.json` containing public
APIs, behaviors, errors and concrete cases, plus `document.context.json` for
snapshot association, readiness and evidence links. Optional Markdown is a
rendered view of that document. Supply the extractor report as `extractionPath`,
the canonical JSON as `compilerArtifactPath`, and preserve both report and
compiler statuses. Detailed evidence stays in the extractor artifacts rather
than being copied into each requirement. Downstream consumers need access to
the evidence, not an unconditional dump of it into every prompt.

Generate a new artifact for each run. Historical snapshots retain their original
paths and IDs and are not evidence that the current source or build context has
been inspected. Artifacts are ignored by Git because they may contain source
evidence and machine-local paths.

## Analysis limits

This is source-declaration extraction, not a complete runtime behavior model.
Call edges are compile-time bindings. Dependency exceptions, virtual/dynamic
dispatch, reflection, async timing, and cleanup behavior need source evidence
and explicit caveats. Compiler facts must not be replaced with AI guesses.

The tool records one evaluated build context; run separately for other target
frameworks/configurations. Source excerpts are capped at 16,000 characters
with diagnostics. Missing references, unresolved calls, truncation, implicit
public member/primary constructor gaps, and top-level executable code can make
the extraction partial. Generated source may not have a physical file.

## Development tests

```powershell
dotnet test CSharpToRust.sln
```
