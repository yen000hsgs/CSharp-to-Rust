---
name: collect-requirements
description: Use when producing compact feature requirements and cases for downstream agents, with separate evidence, coverage, and unresolved decisions.
---

# Collect requirements

Read `.agents/agents/requirements-collector.agent.md` for inputs and handoff,
and `docs/requirements-collector.md` for exact JSON fields and CLI commands.
JSON is the pipeline specification. Optional Markdown is a rendered view of
that specification. Detailed evidence stays in the extractor artifacts.

## 1. Resolve the evidence

Compare the assignment's task/extraction IDs with the original compiler JSON
and report. A missing or conflicting snapshot blocks collection. Resolve the
canonical JSON from `compilerArtifactPath` when the supplied extraction is
Markdown; do not serialize a narrative as compiler facts.

Read the report's scope, diagnostics, limitations and evidence index. Keep a
partial report partial even when the compiler JSON says complete. The helper
can validate compiler facts, but cannot recover an omitted report limitation.

New extractor snapshots leave the legacy `assemblyReferences` field empty
instead of enumerating ambient framework assemblies. Do not interpret that as
no dependencies, or rebuild that catalog in requirements. Use source types,
project references and observed external relationships when behavior depends
on another API.

Inspect actual implementation and relevant test assertions. Read small matching
files together; retrieve focused ranges for larger code. Check source/test
revision or fingerprints against the extractor evidence. When hashing, use
full non-truncated output, not a formatted table with ellipses. Stale or
inaccessible evidence is a question, not confirmation.

The optional read-only `inspect` command supplies exact compiler facts and
stored declaration pages. It does not read live files or run the project.
Use canonical symbol IDs, not snapshot-local `sN` aliases, in context evidence.

## 2. Prepare new outputs

Using the prepared collector helper:

```powershell
dotnet run --no-build --project src\Requirements.Collector -- prepare --input <extraction.json> --output <document.json> --context <document.context.json>
```

Both outputs must be new. The helper creates an empty feature list and a draft
context with pending API coverage. This is authoring scaffolding, not a
downstream-ready specification or an AI-generated result.

Read the supplied report before replacing context `upstreamStatus` with the
actual report status. Add references to the original report, compiler artifact,
and relevant source/tests. Reference existing fingerprint records; do not copy
large evidence packages into the document or companion.

## 3. Write the compact behavior contract

Choose features whose public API can be understood unambiguously. A class can
have an API-shape feature and separate operation features. Stable IDs are
semantic join keys, not compiler IDs or temporary array positions. Reuse IDs
when revising the same requirement.

For each feature record:

- Actual name, signature, parameter/return types, relevant constraints and a
  short summary. Do not silently replace C# `decimal` with `double`.
- Observable behaviors, error conditions/results, and genuine invariants.
  Each has its own scoped `.bN`, `.eN`, or `.iN` ID.
- Concrete examples with `.xN` IDs, JSON input objects and expected outcomes.
  Include normal, meaningful boundary, and known failure cases.
- Visibility and a few useful source references; no inline fingerprint tables
  or repeated compiler facts.

A name such as `doSthUncommon` establishes no behavior. If the body sums C#
`char` values modulo 3 and prints an animal, describe the numeric/encoding rule,
all three branches, and printing as a side effect rather than a return value.
Use strings and examples supported by the actual code. An empty string case,
null behavior, newline formatting, and overflow rules need evidence rather
than assumptions copied from a hypothetical example.

Use the exact PR-compatible property names `params`, `source_refs`, and
optional `source.target_crate`. Decimal input and expected-result values are
JSON strings, not floating-point JSON numbers. Preserve arbitrary string
contents, empty strings, object results and explicit null expected values
when they represent the actual behavior.

For calculator evidence, preserve supported cases such as `0.1m + 0.2m = 0.3m`
and `decimal.MaxValue + 1m` throwing. Do not generalize these to unlimited exact
arithmetic: decimal can round, reduce scale, and underflow. Product scale is
not always the sum of operand scales. An empty `errors` array does not prove
absence of implicit framework failures.

## 4. Keep evidence and uncertainty out of the main specification

The context's `featureEvidence` has exactly one entry per document feature:
`featureId`, `confidence`, and nonempty canonical `evidenceIds`. Confidence is
`confirmed`, `inferred`, or `uncertain`, not an execution/parity verdict.

Account for every public compiler symbol in context `coverage`. `required`
symbols must be linked by feature evidence; `excluded` symbols need a scope
rationale and must not be cited as feature evidence. Use `pending` for missing
in-scope work, never exclusions to manufacture completeness.

Keep focused `openQuestions` with canonical evidence IDs where applicable.
The referenced extraction distinguishes observations, assertions and runtime
evidence. Record new uncertainty in context questions with a concrete request
for the orchestrator. Do not put conjecture into a behavior merely to fill an
array. If there is no established behavior, leave work pending.

Read an orchestrator-supplied porting guide and check its revision/approval.
Absent or unapproved mapping rules do not erase known C# preservation duties.
For example, an explicit divide-by-zero message remains required while its
Rust error transport is unresolved. Do not independently choose a decimal
crate, `Result`, panic, async runtime, `Drop`, or a normalization policy.

Retain exact error types/messages and ordering, cleanup/finally effects,
async completion/cancellation, state/resource lifetimes and encoding behavior
when observable. Compiler nullability annotations are not runtime null checks;
explicit throws do not enumerate operator or dependency failures.

## 5. Validate, render, and return

```powershell
dotnet run --no-build --project src\Requirements.Collector -- validate --input <extraction.json> --document <document.json> --context <document.context.json>
dotnet run --no-build --project src\Requirements.Collector -- validate --input <extraction.json> --document <document.json> --context <document.context.json> --require-ready
```

The first command permits structurally valid unfinished work; read its status
and readiness fields. The second is the downstream readiness gate and fails
for unfinished requirements. Do not remove questions or change status just
to obtain a successful exit. Neither command validates behavioral truth.

Only when `reportPath` was requested:

```powershell
dotnet run --no-build --project src\Requirements.Collector -- render --input <extraction.json> --document <document.json> --context <document.context.json> --output <requirements.md>
```

Do not author a separate Markdown report or label a hand-written example as a
real collector run. Keep input artifacts unchanged.

Return the defined handoff through the orchestrator. GenTest receives the
compact document after readiness/scope approval; Code additionally receives
the generated tests and manifest. Original C# source/tests remain separately
available to both and to the verifier. C# tests need equivalent Rust cases or
an adapter; reading assertions is not running them. Harness generation,
execution, and parity comparison are separate tasks.

Legacy `prepare-legacy` and `validate-legacy` handle old schema 1.0 requirements
only. They do not convert them to feature documents and are not the new agent
workflow.
