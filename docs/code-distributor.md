# Code distributor

The [code-distributor agent](../.agents/agents/code-distributor.agent.md) turns
the collector's requirements into feature work packages. Its
[skill](../.agents/skills/distribute-code/SKILL.md) explains grouping, dependency
analysis, and conservative handoff.

The collector already defines `document.json.features`. The distributor does
not replace those definitions: it partitions whole features into bounded
implementation objectives while retaining every original ID. A work package
may contain one feature or several related features.

## Responsibilities

| Stage | Owns |
| --- | --- |
| Collector | Observable behavior, API requirements, errors, invariants, and cases. |
| Distributor | Package boundaries, prerequisite relationships, shared concerns, and unassigned work. |
| Orchestrator | Approval, dispatch, budgets, shared-file ownership, and sequencing. |
| GenTest / Code | The generated tests / Rust implementation for the selected feature IDs. |

Neither the distributor nor its helper implements Rust, generates tests, calls
peers, or decides which worker runs next. The helper only checks structure and
association; planning remains an AI task.

## Agent assignment

From the repository root:

```powershell
copilot --agent code-distributor
```

Supply:

```text
taskId: <same task as the requirements context>
expectedExtractionId: <exact source snapshot ID>
documentPath: <collector document.json>
contextPath: <paired document.context.json>
compilerArtifactPath: <original compiler JSON>
outputPath: <new distribution.json>
```

Missing or invalid inputs are blockers, not permission to substitute a sample.
Valid partial requirements may be planned, but the plan must remain unready.
Original requirements, context, compiler evidence, source, tests, and guide are
read-only. Only the new requested plan is authored.

## `distribution.json`

This is the normative distribution shape. It adds no fields to the existing
[feature-document contract](contracts.md#documentjson--the-intermediate-spec)
or [collector context](requirements-collector.md#context-companion).

| Root field | Meaning |
| --- | --- |
| `schemaVersion` | `1.0` for this plan, independent of the collector context's `2.0`. |
| `taskId`, `extractionId` | Exact upstream association. |
| `documentPath`, `contextPath` | The paired inputs. |
| `documentSha256`, `contextSha256` | Lowercase SHA-256 hex digests of the exact input file bytes. |
| `status` | `draft`, `partial`, `blocked`, or `complete`. |
| `packages` | Identified work packages selecting existing features. |
| `sharedConcerns` | Known coordination obligations referencing affected packages. |
| `unassignedFeatures` | Features not yet packaged, each with a reason. |
| `openQuestions` | Unresolved planning decisions and their feature references. |

All fields and arrays are required. Extra/duplicate/missing fields and invalid
types or null records fail strict parsing rather than defaulting to success.

Each package has `id`, `name`, `summary`, `featureIds`, and `dependsOn`.
Package IDs are lowercase slugs using letters/digits separated by `.`, `_`, or
`-`, starting with a letter. `name` and `summary` must be nonblank.
`featureIds` is nonempty and contains known, unique IDs from the input document.
`dependsOn` is an array of known package IDs, with no duplicates, self-links,
or cycles.

Every input feature occurs exactly once, either in one package or in
`unassignedFeatures`. An unassigned entry is `{featureId, reason}`, with a
nonblank explanation. Unknown, duplicated, or omitted features are errors.
Selecting a feature includes all its behaviors, errors, invariants, and
examples; the distributor cannot silently omit individual requirements.

A shared concern is `{id, statement, packageIds}`. Concern IDs follow the same
slug grammar and are unique within the concerns. Statements are nonblank;
package references are known, nonempty, and unique. A concern may reference
one package when multiple source features are grouped inside it.

A question is `{id, question, featureIds}`. IDs and questions are nonblank,
question IDs are unique, and feature references are known and unique. An empty
reference list is allowed for a global question.

This is an **illustrative package fragment**, not actual agent output or a
complete artifact:

```json
{
  "id": "calculator-add",
  "name": "Addition",
  "summary": "Implement the addition contract and all its documented outcomes.",
  "featureIds": ["calculator.add"],
  "dependsOn": ["calculator-api"]
}
```

`calculator-api` must actually exist as another package whose requirements
establish that prerequisite. The example does not prescribe a package layout
for every calculator or invent a dependency from a name.

## Identity and readiness

Relative `documentPath` and `contextPath` resolve against the distribution
file's directory. Preparation writes absolute paths. The original document,
context, and extraction keep their own existing resolution rules.

The digests bind the exact current document/context bytes, including
whitespace, and are checked with the existing task/snapshot and path
associations. They detect a stale plan, not authenticity, source freshness,
or semantic truth. Regenerating extraction in another checkout does not
authorize rebinding an old plan to it. Prepare and review a new plan revision
when its inputs change.

`complete` requires a nonempty document and package list, every feature
assigned exactly once, no unassigned features, no questions, and ready upstream
requirements. Valid draft/partial/blocked plans remain available for inspection
but cannot pass `--require-ready`. A complete claim with unresolved work is an
error, not a silently downgraded success.

Known shared concerns can remain in a complete plan as coordination
obligations. Unsettled decisions belong in questions and block readiness.
Dependency truth and safe concurrent writes require review; an acyclic graph
alone proves neither.

## Artifact helper

Distribution support reuses `src\Requirements.Collector`, the existing
artifact helper, so there is one implementation of strict requirement
validation and output protection. This is helper reuse, not one AI agent
calling another.

Build it from the repository root when needed:

```powershell
dotnet build src\Requirements.Collector\Requirements.Collector.csproj
```

Then use actual paths:

```powershell
dotnet run --no-build --project src\Requirements.Collector -- prepare-distribution --input artifacts\extraction.json --document artifacts\document.json --context artifacts\document.context.json --output artifacts\distribution.json
dotnet run --no-build --project src\Requirements.Collector -- validate-distribution --input artifacts\extraction.json --document artifacts\document.json --context artifacts\document.context.json --distribution artifacts\distribution.json
dotnet run --no-build --project src\Requirements.Collector -- validate-distribution --input artifacts\extraction.json --document artifacts\document.json --context artifacts\document.context.json --distribution artifacts\distribution.json --require-ready
```

Preparation creates a bound empty draft: no packages or inferred dependencies,
all source features unassigned, and an authoring question. The distributor
authors that plan before validating it.

Outputs are create-only JSON; there is no `--force`. The helper protects the
upstream artifacts and referenced evidence/guide/source paths and rejects
aliases, existing outputs, and symlink/junction/reparse-point destinations.
Validation never rewrites a plan or its inputs.

Exit `0` means the requested operation succeeded, possibly for unfinished
work. Exit `2` means invalid input, failed readiness, usage, or I/O. A
successful command returns one JSON summary:

| Summary field | Meaning |
| --- | --- |
| `taskId`, `extractionId`, `status`, `distributionPath` | Identity and the plan inspected or created. |
| `upstreamReadyForDownstream` | Readiness calculated by the collector's existing document/context validator. |
| `readyForDownstream` | Complete distribution over ready upstream requirements. |
| `structureAndTraceabilityValid` | `true` after successful structural validation; not semantic correctness. |
| `packageCount` | Number of packages. |
| `assignedFeatureCount`, `unassignedFeatureCount` | Feature assignment accounting. |
| `assignedRequirementCount` | Atomic requirements carried by assigned features, excluding the feature IDs themselves. |
| `openQuestionCount` | Unresolved planning questions. |
| `semanticParityVerified` | Always `false`. |

## Downstream handoff

The orchestrator validates the plan with `--require-ready`, retains the
original inputs/evidence, and supplies each approved package's `featureIds` as
the existing GenTest/Code `focus` argument. Both still consume the original
`document.json`; Code additionally needs the generated suite and manifest.
Package IDs must not be used as feature IDs in tests or reports.

There is still exactly one Rust crate. The orchestrator must coordinate global
scaffolding, shared module/test roots, manifests, and integration. Package
boundaries do not grant concurrent write permission, and the distributor does
not run workers or merge their outputs.

Partial per-package reports do not establish project-wide coverage. The
orchestrator and verifier retain the full document scope when reconciling
results. No downstream execution or Rust equivalence is implied by a valid
distribution.

## Development

```powershell
dotnet test tests\Requirements.Collector.Tests\Requirements.Collector.Tests.csproj --filter FullyQualifiedName~DistributionCliTests
dotnet test CSharpToRust.sln
```

The focused cases use isolated synthetic inputs. They exercise the helper,
not the AI's semantic grouping quality. An actual distributor run must remain
identified separately from test fixtures and hand-written examples.
