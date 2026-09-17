---
name: distribute-code
description: Use when partitioning collected C# migration requirements into traceable feature work packages for an orchestrator.
---

# Distribute requirements into feature work packages

Read `.agents/agents/code-distributor.agent.md` for the role and handoff, and
`docs/code-distributor.md` for the distribution schema and exact commands.
The requirements remain authoritative. A distribution is a plan over them,
not another interpretation of what the original C# code does.

## 1. Establish the input boundary

Read the compact `document.json` and paired context. Compare the assigned task
and snapshot IDs with the context and helper results. Use the original compiler
artifact only through the helper unless a focused evidence question requires
more information.

The collector has already identified features and atomic requirements. Keep
those IDs unchanged: tests, implementation, and verification join on them.
Legacy flat requirements or an extractor report alone are not substitutes for
the document/context pair.

The collector's complete compiler snapshot does not make a partial report or
context complete. Preserve unresolved questions and porting-guide approval.
Uncertainty about observable behavior belongs back with the collector through
the orchestrator, not in a guessed package summary.

## 2. Prepare a new plan

Using the prepared artifact helper from the repository root:

```powershell
dotnet run --no-build --project src\Requirements.Collector -- prepare-distribution --input <extraction.json> --document <document.json> --context <document.context.json> --output <distribution.json>
```

This creates an empty draft, with all features unassigned. It does not group
features, infer dependencies, or run AI.

Keep the generated identity, path, and digest fields intact. The digests bind
the exact document/context bytes; whitespace edits also change those bindings.
If the input changes, request a new planning revision and prepare a new file
against that input rather than editing old bindings to conceal a stale plan.

## 3. Choose coherent package boundaries

Read every feature's public API, behavior, errors, invariants, and cases. Start
with one package per feature when that describes independent implementation
work. Combine features when their shared lifecycle or inseparable behavior
makes a single objective clearer. Do not split by a fixed line/file count.

A package contains one or more complete existing features. Its `name` and
`summary` explain the implementation objective without repeating every
requirement. `featureIds` carries the precise scope.

Examples of reasoning, not fixed templates:

- A calculator API-shape feature can be prerequisite work for operation
  packages when its requirements establish their shared public contract.
- Operations with a shared transaction or lifecycle may belong in one package
  instead of pretending that their state transitions are independent.
- Similar method names do not establish a dependency, and different C# files
  do not establish safe parallel implementation.

Do not create a package for an API or behavior missing from the document.
Requirements that need semantic splitting or correction require a collector
revision through the orchestrator; do not rename their IDs yourself.

## 4. Express dependencies and shared concerns

`dependsOn` refers to another package whose contract/work is a prerequisite.
Keep references unique, known, and acyclic. If real dependencies form a cycle,
consider grouping the participating features or record a question. Do not
delete an inconvenient edge merely to satisfy validation.

Use `sharedConcerns` for known coordination obligations such as a shared error
contract, state ownership, initialization, or the same approved numeric policy.
Reference the affected package IDs. A concern can apply inside one package
that groups multiple features.

Unsettled decisions belong in `openQuestions`, not hidden in a concern while
marking the plan complete. Do not independently select a decimal crate,
`Result`/panic mapping, async runtime, or Rust module layout.

Neither an empty dependency list nor a successful gate establishes parallel
safety. GenTest and Code share the crate and manifests, and Code owns global
scaffolding. The orchestrator must sequence or isolate writes; you only return
the plan.

## 5. Account for the whole document

Every source feature must appear exactly once: either in one package's
`featureIds` or in `unassignedFeatures` with a concrete reason. Never duplicate
a feature between packages or erase unfinished work.

Selecting a feature implicitly selects all its atomic requirement IDs. Do not
copy a subset of its examples or errors into a smaller substitute document.
The original document remains the downstream input.

Use `draft` while organizing the plan, `partial` for useful unfinished work,
or `blocked` when a needed decision prevents planning. Use `complete` only
with ready upstream requirements, complete assignment, and no open questions.

## 6. Validate and hand back

```powershell
dotnet run --no-build --project src\Requirements.Collector -- validate-distribution --input <extraction.json> --document <document.json> --context <document.context.json> --distribution <distribution.json>
dotnet run --no-build --project src\Requirements.Collector -- validate-distribution --input <extraction.json> --document <document.json> --context <document.context.json> --distribution <distribution.json> --require-ready
```

Ordinary validation may accept a valid partial plan. Read its readiness fields,
not just the exit code. `--require-ready` must fail for unfinished plans or
partial upstream evidence. Do not remove a real blocker to obtain success.

Return the agent's defined handoff. The orchestrator takes a package's
`featureIds` as a downstream `focus` list while retaining the original
document/context/evidence. It passes tests and the manifest to Code as well.
No package result can certify requirements outside that focus, and all
project-wide coverage/integration decisions remain with the orchestrator.
