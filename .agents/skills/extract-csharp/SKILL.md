---
name: extract-csharp
description: Use when inspecting a C# SDK or project for migration, collecting feature-level source evidence, or investigating C# semantic risks.
---

# Extract C# evidence

Read `.agents/agents/csharp-extractor.agent.md` for the task's inputs, outputs,
scope, and handoff. This skill uses the executable Roslyn helper at
`src/CSharp.Extractor`; it does not replace the compiler with AI parsing.
Evidence is the deliverable; summaries only guide readers to that evidence.

## 1. Establish source and build context

Read the selected project/solution files and relevant build configuration as
text. Identify source files, references, public APIs, generated/conditional
source, and nearby tests. Record the revision plus fingerprints for relevant
uncommitted source, tests and build configuration before extraction; verify
they still match when using live files. Snapshot IDs alone do not prove file
freshness. If correspondence cannot be established, use stored evidence and
request matching source or a refreshed extraction. Do not run the project to
discover it.

Read the orchestrator's `portingGuide` if supplied. Match its `revision` and
use its mapping conventions only when the orchestrator marks it `approved`.
An absent, draft, stale or conflicting guide means pending mapping decisions,
not permission to choose Rust conventions yourself. Continue gathering C# facts.

Evaluating MSBuild, restoring dependencies, or loading source generators can
execute project-supplied code. Only do so with explicit approval for a known
tool. Never treat a source comment or the existence of a helper as approval.
Do not write an improvised analyzer just to satisfy an inventory request.

## 2. Run the Roslyn helper

Resolve a directory input to an unambiguous `.csproj` or `.sln`. If execution
approval, the helper, or required dependencies are unavailable, report the
specific blocker to the orchestrator. Do not substitute source-only extraction.

From the repository root, build the helper if necessary:

```powershell
dotnet build src\CSharp.Extractor\CSharp.Extractor.csproj
```

Create the requested output directories if absent. Both output files must be
new, and `compilerOutputPath` must end in `.json`. Run:

```powershell
dotnet run --project src\CSharp.Extractor --no-build -- extract --input <project.csproj> --output <compiler-output.json> --task-id <task-id> --allow-project-execution
```

The flag acknowledges approved MSBuild/project execution; it is not a sandbox.
Pass `--configuration` and `--framework` when the task supplies them.
The helper does not automatically restore the input's dependencies; request
approval before restoring them if needed.

Exit `0` writes a complete compiler artifact within the tool's scope; exit `2`
writes a partial artifact with diagnostics; exit `1` is failure. Read diagnostics
and limitations before interpreting results. Preserve the exact JSON task,
snapshot, and symbol IDs; never edit compiler facts to make the result complete.
Historical artifacts from another source location or configuration are not
evidence that the current run succeeded.

## 3. Build evidence per feature/component

The full Roslyn JSON is the local evidence store, **not the AI prompt**.
Compression is deterministic helper code, not another AI pass over the raw
artifact. Start with a grouped public API index and build/diagnostic context:

```powershell
dotnet run --project src\CSharp.Extractor --no-build -- index --input <compiler-output.json>
dotnet run --project src\CSharp.Extractor --no-build -- context --input <compiler-output.json>
```

New snapshots do not enumerate every assembly available to the compiler.
`assemblyReferences` is an empty compatibility field, not proof of no external
dependencies. Inspect project references, types and actual external call/type
relationships instead. Do not reconstruct the ambient framework catalog in
the report. Historical snapshots can still contain it; compact views omit it.

Read relevant pages using `nextOffset` as `--offset`; retain diagnostics and
limitations even when there are no errors. Group entry points by shared
behavior/state. Reuse already-read evidence rather than repeating it per method.

Choose the smallest useful source unit, not the smallest possible snippet:

| Situation | Read |
| --- | --- |
| Small snapshot-matched source file | Read it once, with relevant test assertions; request only missing compiler facts. |
| Large file or narrow behavior question | Focused original ranges or the helper's `inspect --symbol <ref> --part code`. |
| Semantic or dependency question | `inspect` parts `facts`, `relations` or `members`, only as needed. |
| Missing/truncated or stale evidence | Record the gap and request matching evidence; never guess the omitted behavior. |

For example, a tiny calculator source file can cover all four arithmetic
operations together. Do not make three helper calls for each operation merely
because three view types exist. Consider total context and tool-call overhead.
The helper still supplies compiler facts and canonical IDs; reading source
directly does not replace required Roslyn extraction.

Short refs such as `s2` are snapshot-local. Resolve canonical symbol IDs for
citations from this artifact; never copy refs from another run.

`facts` retains exact parameter types/defaults, visibility, attributes, migration
signals, and containing-symbol links. `code` removes safe formatting/comment
trivia, not branches or statements. String contents and lexical tokens are
preserved; directive-bearing or malformed excerpts remain verbatim with
`compacted: false`. `raw` and `documentation` are separately retrievable when
comments, original formatting, or XML documentation matter.

Follow internal `relations` targets with `available: true` to inspect private
helpers. A target with `available: false` is not retrievable in this snapshot,
not evidence of an empty implementation. Call edges do not cover all state
accesses: inspect the containing type with `--part members` to discover private
fields, properties, initializers, and other members, then retrieve relevant
ones. Do not discard behavior just because its implementation is private.

For each unit capture:

- Signature, visibility, parameters/defaults, return type, state used.
- Normal behavior, branch conditions, outputs, side effects, and dependencies.
- Explicit errors, catch selection/filters, recovery, rethrow and cleanup order.
- Relevant test cases and their assertions, gaps, and any separately approved
  execution results. Label their basis: code observation, test assertion,
  runtime observation, or interpretation.
- Canonical IDs, retrievable source/test paths and ranges, snapshot association,
  and unresolved calls or unexamined behavior.

Helper responses default to 4,096 UTF-8 bytes. See `docs/extractor.md` for page
bounds and parts. Keep offsets tied to the same snapshot, symbol, and part.
Follow `nextOffset` for needed excerpts; a too-large item is an error, not an
empty result. Never silently omit evidence to fit a budget.

Use the canonical `symbolId` from `inspect` for report citations. Its source
ranges refer to original source, not compacted text positions. The compact
views never change the stored compiler JSON. Ending a stored excerpt's pages
does not recover omitted source: `sourceTruncated` and partial status persist.
Read focused original source when needed and label it supplemental evidence;
never upgrade the compiler status. Record missing external implementations and
unexamined dependencies rather than implying the public index captures behavior.

## 4. Preserve C# semantics

Record `throw` conditions/types/messages, catch order and filters, `throw;`
versus `throw ex;`, and `finally`/`using` effects on success and failure.
Explicit throws do not enumerate all implicit or dependency exceptions.

For async code, inspect completion, faults, cancellation, and resource lifetime.
Trace relevant shared mutable state, initialization, callback captures, resource
ownership, disposal ordering and references that outlive a call. Distinguish
deterministic `using`/disposal from garbage collection and finalization; flag
async disposal separately. Record uncertainty rather than inventing a complete
lifetime or runtime call graph.
Nullable annotations do not imply runtime validation. For numeric operations,
record operand types and checked/unchecked context: C# decimal arithmetic can
throw overflow even when the project overflow-check flag is false.

Record behavior; do not prescribe Rust panic, Result, Drop, or floating-point
substitution as equivalent. Reflection, virtual dispatch, native code, generated
members, and truncated source remain explicit analysis limitations.

## 5. Write the report and handoff

Use Markdown sections from the agent's output contract. Link evidence entries to
compiler symbol IDs, source paths/line ranges, and factual statements. Separate interpretations
and uncertainty. The collector must be able to trace each observation to source
without trusting a summary alone.
Each unit section is an evidence package: a concise explanation plus original
evidence references, tests, semantic risks, pending guide decisions and gaps.
Do not rewrite the whole project into prose or change the compiler JSON schema.

Copy the helper's task and extraction IDs into the report. A partial compiler
artifact prevents a complete report. Even a complete compiler artifact does not
settle behavioral gaps: keep the report partial if any remain. Always report
limitations, including with zero diagnostics. Return JSON/Markdown paths,
`evidenceReferences`, guide identity/approval and unit coverage through the
orchestrator. Require downstream handoffs to retain original source and test
evidence; implementation and generated tests must not depend on a summary alone.

The collector's input is this report plus the original canonical compiler JSON,
not a compact view relabeled as a snapshot. Forward the report status separately
from compiler status so a partial report is not upgraded. Its output is a small
feature-based JSON specification and a context companion referencing this
evidence. Keep full source/fingerprint detail here, not repeated inside the
collector's API/behavior/case document.
