# Pipeline Contracts

Single source of truth for the **artifact schemas** exchanged between agents in
the C# → Rust migration pipeline.

> **The orchestrator owns control flow and paths.** It decides which agent runs,
> what inputs each one receives, and how each output is consumed. Agents do not
> discover their inputs, do not call each other, and do not decide what happens
> next. This document defines the *shape* of what is passed, not the routing.

## Where things live

The orchestrator may place artifacts anywhere and passes concrete paths at
invocation time. When it does not specify a path, agents fall back to the layout
in [Run layout](#run-layout) below, which is the single authoritative
description — read it before writing any path, and do not infer the arrangement
from examples elsewhere in this file.

Ownership is narrow on purpose:

| Path | Written by | Read-only to |
| --- | --- | --- |
| `document.json` | requirements collector | distributor, GenTest, Code, parity verifier |
| `document.context.json` | requirements collector | distributor, orchestrator |
| `distribution.json` | code distributor | orchestrator; downstream consumers when supplied |
| `csharp/` | upstream | all three agents |
| `tests/manifest.json`, all test sources, `tests/golden/cases.json` | GenTest | code agent, parity verifier |
| `rust/` (the crate) | code agent | GenTest, parity verifier |
| `reports/parity-report.json` | parity verifier | — |
| `reports/code-report.json` | code agent | — |

Agents MUST NOT write outside the outputs they were asked to produce. Tests are
read-only to the Code agent. The Rust crate is read-only to GenTest.

The C# differential harness is **not** in this table because none of these three
agents owns it; see [the harness contract](#csharp-differential-harness).

## `document.json` — the intermediate spec

Produced upstream. Prose is not acceptable; the document is a list of features.

This section defines the shared feature wire shape. The
[requirements collector guide](requirements-collector.md#feature-document-shape)
adds the author's field constraints, decimal example encoding, and the separate
`document.context.json` readiness contract. Downstream coverage checks do not
replace the collector's `validate --require-ready` gate.

```jsonc
{
  "source": { "language": "csharp", "kind": "sdk", "root": "csharp/" },
  "features": [
    {
      "id": "storage.blob.upload",          // stable, dot-scoped, lowercase
      "name": "UploadBlob",
      "signature": "Task<BlobInfo> UploadAsync(string name, Stream data, CancellationToken ct)",
      "summary": "Uploads a blob and returns its metadata.",
      "params": [
        { "name": "name", "type": "string", "constraints": ["non-empty", "<= 1024 chars"] }
      ],
      "returns": { "type": "BlobInfo", "description": "Metadata of the stored blob." },
      "behaviors": [
        { "id": "storage.blob.upload.b1", "statement": "Overwrites an existing blob with the same name." },
        { "id": "storage.blob.upload.b2", "statement": "Streams data without buffering the whole payload." }
      ],
      "errors": [
        { "id": "storage.blob.upload.e1", "condition": "name is empty", "result": "ArgumentException" }
      ],
      "invariants": [
        { "id": "storage.blob.upload.i1", "statement": "Blob length equals the number of bytes read from `data`." }
      ],
      "examples": [
        { "id": "storage.blob.upload.x1", "input": { "name": "a.txt", "data": "aGk=" },
          "expected": { "length": 2, "name": "a.txt" } }
      ],
      "visibility": "public",
      "source_refs": ["csharp/Storage/BlobClient.cs#L40-L88"]
    }
  ]
}
```

`id` values are the join key for the entire pipeline. Every test, every report
entry, and every generated Rust item traces back to a `feature.id` and, where
applicable, a `behavior.id`, `error.id`, `invariant.id`, or `example.id`.

**Every requirement must carry an id.** Anything expressed as a bare string is
invisible to the coverage gate and will be silently dropped. Id suffix
conventions, scoped under the owning feature id:

| Suffix | Kind |
| --- | --- |
| `.bN` | behavior |
| `.eN` | error case |
| `.iN` | invariant |
| `.xN` | example |

The set of all such ids is the **requirement set**. Coverage is defined as a set
relation against it, not as a judgment.

## `distribution.json` - feature work packages

The [code distributor contract](code-distributor.md#distributionjson) defines
the plan over the existing feature document. Packages select whole
`feature.id` values and preserve every associated atomic requirement.
They do not replace `document.json` or create new downstream requirement IDs.

The orchestrator validates the plan and its original inputs with
`validate-distribution --require-ready` before using a package's `featureIds`
as GenTest/Code's existing `focus`. Dependencies and shared concerns guide
coordination; they do not prove concurrent writes to the single crate or
manifests are safe. Draft/partial plans and partial upstream evidence cannot
be dispatched as complete.

## Run layout

Every artifact for one migration attempt lives under a single run directory.
There is exactly one Rust crate, and **its root is `<run>/rust`** — the directory
holding `Cargo.toml`. Cargo will not discover a test that lives outside it.

```
artifacts/<run>/
  document.json            # requirements agent output
  document.context.json    # collector readiness and evidence association
  distribution.json        # distributor work packages, when this stage is used
  tests/
    manifest.json          # GenTest output -- metadata only, no Rust sources
  rust/                    # <- the crate root; cargo is invoked here
    Cargo.toml
    src/...
    tests/...
  reports/
    coverage.json  test-quality.json  parity-report.json  code-report.json
```

`manifest.json` sits beside the crate rather than inside it so that a test
inventory can exist before any crate does — GenTest runs before the code agent,
and the coverage gate must be runnable at that point. Because of that split,
**every `file` value in the manifest is relative to the crate root, never to the
manifest's own directory**: `src/tests_unit.rs`, `tests/e2e.rs`. A path such as
`../rust/src/...` or an absolute path is a schema violation.

Both gates that read test sources therefore need the crate root, which is not
derivable from the manifest path:

```powershell
./tools/Check-Coverage.ps1 -DocumentPath artifacts/<run>/document.json `
                           -ManifestPath artifacts/<run>/tests/manifest.json `
                           -TestsRoot    artifacts/<run>/rust
```

Omitting `-TestsRoot` makes it default to the manifest's parent directory, where
no Rust source exists; every entry then resolves to a missing file and is
reported as `phantom`. That is a loud failure by design — a suite that measures
nothing must never be mistaken for a suite that passes.

Where each tier lives inside the crate is fixed by what Rust permits, not by
preference:

| Tier | Location | Why |
| --- | --- | --- |
| `unit` | `src/`, e.g. `src/tests_unit.rs` | Needs private access. A file under `tests/` is a separate crate and only sees the public API. |
| `functional`, `e2e` | `tests/`, e.g. `tests/functional.rs` | Integration tests against the public API, which is exactly what these tiers assert. |

Cargo compiles only top-level `.rs` files under `tests/`, so a nested
`tests/functional/upload.rs` is never built on its own. Nested files must be
pulled in from a top-level wrapper:

```rust
// rust/tests/functional.rs
#[path = "functional/upload.rs"]
mod upload;
```

Unit test files are included from the module they exercise, with a path relative
to *that module's* file:

```rust
// rust/src/lib.rs
#[cfg(test)]
#[path = "tests_unit.rs"]
mod tests_unit;
```

## `tests/manifest.json` — GenTest output

```jsonc
{
  "run_id": "2026-09-15-001",
  "generated_from": "document.json@<sha256-prefix>",
  "tests": [
    {
      "test_id": "t_storage_blob_upload_b1_overwrite",
      "feature_id": "storage.blob.upload",
      "covers": ["storage.blob.upload.b1"],      // behavior/error/example ids
      "tier": "unit",                             // unit | functional | e2e
      "assertion_kind": "value",                  // value | error | invariant | side_effect | property
      "file": "src/tests_unit.rs",                // crate-relative; see Run layout
      "test_fn": "overwrites_existing_blob",
      "rationale": "b1 states overwrite semantics; asserts second upload wins.",
      "status": "expected_fail_until_implemented"
    }
  ],
  "coverage_claim": {
    "waived": [
      { "ref_id": "storage.blob.list.b7", "reason": "Requires a live service; deferred to e2e.",
        "waived_by": "gentest" }
    ]
  },
  "disputes": [
    {
      "ref_id": "storage.blob.list.b9",
      "position": "undocumented",        // undocumented | contradicts_document | not_testable | wrong_target
      "evidence": "document.json has no b9 under storage.blob.list; the required assertion invents a limit.",
      "proposed_route": "requirements"   // requirements | code | verifier
    }
  ]
}
```

`coverage_claim` carries **decisions, not arithmetic**. The only required field
is `waived[]`, because a waiver is a judgement no tool can derive: someone chose
to leave a requirement untested and must be named for it.

Totals (`features_total`, `features_covered`, `requirements_total`,
`requirements_covered`) are **optional and informational**. The coverage gate
recomputes all of them from the document and the manifest, and its numbers are
authoritative. Asking a best-effort generator to redo arithmetic the gate
already does only manufactures disagreements that say nothing about coverage; a
mismatch is reported as a note, not a failure.

A waiver **counts as uncovered**. It suppresses the gate's exit code so a
best-effort GenTest run can complete, but no report may claim full-scope
`counted` coverage while an in-scope requirement is waived. Use
`-FailOnWaived` for a release-strength run.

`disputes[]` is GenTest's channel for pushing back on a `required_tests` entry it
believes is wrong. The verifier can be mistaken, and complying silently with a
bad work order is how a wrong expectation gets locked into the suite. A dispute
must carry evidence and must be mirrored into `waived[]` — disputing a
requirement never removes it from the uncovered count.

A dispute is **not self-resolving**. Mirroring it into `waived[]` only keeps it
visible; if nothing ever rules on it, the verifier re-issues the same work order,
GenTest re-disputes it, and the requirement never converges. The next parity
report must therefore carry a `dispute_rulings[]` entry for every dispute in the
manifest it read, and `Check-ParityReport.ps1 -ManifestPath` fails the report if
one is missing. See `dispute_rulings[]` below.

## `tests/golden/cases.json` — differential cases

Drives C#-vs-Rust output comparison. Each case must be executable on both sides.

```jsonc
{
  "cases": [
    {
      "case_id": "g_storage_blob_upload_x1",
      "feature_id": "storage.blob.upload",
      "entry": { "csharp": "Harness.Storage.Upload", "rust": "harness::storage::upload" },
      "input": { "name": "a.txt", "data": "aGk=" },
      "normalization": ["sort_keys", "strip_timestamps", "round_floats:6"]
    }
  ]
}
```

**Entry-point naming is a contract, not a convention.** A differential case is
only executable if both names resolve, so they are derived mechanically from the
`feature_id` rather than invented per case:

| Side | Rule | `storage.blob.upload` becomes |
| --- | --- | --- |
| `csharp` | `Harness.` + each dot-segment in PascalCase | `Harness.Storage.Blob.Upload` |
| `rust` | `harness::` + each dot-segment in snake_case | `harness::storage::blob::upload` |

### C# differential harness

**Ownership.** The **Code agent** owns the Rust side of the differential harness
and must build it as part of the port. **No agent in this pipeline owns the C#
side.** It has to be written against the original C# source, which is read-only
input to every agent here, so it is an *input to the run*: the orchestrator
supplies a runner command, or the differential pass does not happen.

That produces exactly three legal states, and the parity gate enforces them:

| C# runner | `passes_run.differential` | Highest legal `coverage_level` |
| --- | --- | --- |
| Supplied and ran | `ran` | `parity-checked` |
| Supplied but failed to run | `error` (with the failure in `blockers[]`) | `substantive` |
| Not supplied | `skipped` (with "no C# runner supplied" in `blockers[]`) | `substantive` |

A `parity-checked` claim without `differential: "ran"` is a critical violation.
There is no path by which a verifier reasons its way to parity from reading the
C# — the pass exists precisely because reading is not running.

This document defines the protocol both sides implement. They are **batch
runners**, not per-case filters:

```
<harness> --cases <cases.json> [--out <results.json>]
```

Each side reads the whole `cases.json`, runs every case, and writes one document
to stdout (and to `--out` when given):

```jsonc
{
  "results": {
    "g_storage_blob_upload_x1": { "ok": true,  "value": { "etag": "..." } },
    "g_storage_blob_upload_x2": { "ok": false, "error": { "type": "BlobNotFound", "message": "..." } }
  }
}
```

Rules that make the two documents diffable:

- Exit 0 whenever the batch completed. A domain error is **data** (`ok: false`),
  not a process failure. Exit 2 for a usage error, on stderr.
- **No other stdout traffic** — no logging, banner, or progress. The verifier
  parses stdout as a whole document.
- The Rust `error.type` strings must match the C# ones. The C# side emits
  `ex.GetType().Name`, so the Rust error enum must map its variants onto those
  same names or every error case reads as a behavioral mismatch.
- An unknown entry name produces an `ok: false` result for that case; it never
  aborts the batch and loses the other results.

GenTest writes `cases.json` using the table above; it does not invent entry
names. Without the Rust side, pass 2a of the parity verifier cannot run at all —
the case names would resolve on the C# side and dangle on the Rust side.

## Test file layout — cargo discovery

The manifest's `file` paths are the addresses every gate resolves, so **no agent
relocates a test file**. Every one of those paths is relative to the crate root
`<run>/rust` (see [Run layout](#run-layout)). Cargo only compiles top-level `.rs`
files under `tests/`, so a nested layout needs an explicit inclusion mechanism:

| Tier | Where it runs | Who makes cargo see it |
| --- | --- | --- |
| `functional`, `e2e` | Integration crate (public API only) | **GenTest** also emits root wrappers `tests/functional.rs` and `tests/e2e.rs` |
| `unit` | Inside the crate (needs private access) | **Code agent** includes the file from `src/` |

A root wrapper is an ordinary Rust file containing nothing but includes:

```rust
// rust/tests/functional.rs
#[path = "functional/storage_blob_upload.rs"] mod storage_blob_upload;
```

The Code agent includes unit tests from the module under test, with the `#[path]`
relative to *that module's* file:

```rust
// rust/src/storage/blob.rs   (manifest file: src/storage/blob_tests.rs)
#[cfg(test)]
#[path = "blob_tests.rs"]
mod blob_tests;
```

Every included test function still needs its own `#[test]`. Both gates require a
live `#[test]` attribute: a bare `fn`, an `#[ignore]`d test, or one behind
`#[cfg(any())]` is reported as `phantom` by the coverage gate and as
`unregistered_test` / `disabled_test` by the quality gate, because cargo would
never run it.

Both mechanisms leave the file exactly where the manifest says it is, so
`Check-Coverage.ps1` still resolves it and the entry is not reported as a
`phantom`. Adding a `mod` line inside `src/` is the Code agent editing its own
source, not a test, so it does not breach the read-only rule.

## `reports/parity-report.json` — Feature parity verifier output

```jsonc
{
  "verdict": "fail",                    // pass | pass_with_warnings | fail | blocked
  "coverage_level": "substantive",      // counted | substantive | proven | parity-checked
  "summary": {
    "features": 12, "gaps": 3, "mismatches": 2, "weak_tests": 1,
    "requirements_probed": 18,          // mutation-probed in pass 2b
    "mutations_survived": 1,
    "probe_errors": 0,                  // probes that could not be completed
    "tests_not_analysed": 0             // unknowns; blocks `substantive`
  },
  "passes_run": {
    "coverage": "ran", "quality": "ran",
    "differential": "skipped: no Rust crate supplied",
    "mutation": "skipped: no Rust crate supplied",
    "semantic": "ran"
  },
  "gaps": [
    {
      "kind": "untested_behavior",      // untested_feature | untested_behavior | untested_error
                                        // | weak_assertion | unimplemented_feature
                                        // | survived_mutation | document_gap
      "feature_id": "storage.blob.list",
      "ref_id": "storage.blob.list.b7",
      "severity": "high",               // critical | high | medium | low
      "evidence": "No test in manifest.json declares b7 in `covers`.",
      "suggested_test": "Assert continuation token is returned when page is full."
    }
  ],
  "golden_results": [
    {
      "case_id": "g_storage_blob_upload_x1",
      "csharp": { "ok": true, "value": { "length": 2 } },
      "rust":   { "ok": true, "value": { "length": 0 } },
      "status": "mismatch"              // match | mismatch | error
    }
  ],
  "mismatches": [
    {
      "case_id": "g_storage_blob_upload_x1",
      "feature_id": "storage.blob.upload",
      "csharp_output": { "length": 2 },
      "rust_output": { "length": 0 },
      "diff": "length: 2 != 0",
      "severity": "critical",
      "likely_cause": "Rust impl does not read the stream to completion."
    }
  ],
  "mutation_probes": [
    {
      "ref_id": "storage.blob.upload.b1",
      "outcome": "killed",              // killed | survived | probe_error
      "mutation": "rust/src/storage/blob.rs:42  `if exists` -> `if !exists`",
      "command": "cargo test overwrites_existing_blob",
      "baseline_output": "test overwrites_existing_blob ... ok",
      "mutant_output": "test overwrites_existing_blob ... FAILED",
      "killed_by": ["overwrites_existing_blob"]
    }
  ],
  "required_tests": [
    {
      "ref_id": "storage.blob.list.b7",
      "reason": "missing",              // missing | phantom | inert | survived_mutation | weak_assertion | incorrect_test
      "required_assertion": "After listing a full page, the returned token must be non-empty and usable as the next request's marker.",
      "evidence": "Check-Coverage.ps1 reported b7 as missing."
    }
  ],
  "adjudications": [
    {
      "test_id": "t_storage_blob_upload_b2_streaming",
      "ruling": "code_wrong",           // test_wrong | code_wrong | document_wrong | rejected
      "evidence": "Document b2 says 'streamed'; csharp/Storage/BlobClient.cs#L40-L88 confirms. The test is correct."
    },
    {
      "test_id": "t_storage_blob_list_b7_token",
      "ruling": "test_wrong",
      "ref_id": "storage.blob.list.b7", // required on test_wrong: the requirement left uncovered
      "evidence": "The test asserts a page size of 100; document b7 and BlobClient.cs#L120 both say 50."
    }
  ],
  "dispute_rulings": [
    {
      "ref_id": "storage.blob.list.b9",
      "ruling": "upheld",               // upheld | rejected
      "evidence": "document.json has no b9 under storage.blob.list. GenTest is right; the work order invented a limit.",
      "route": "requirements"           // requirements | gentest | none
    }
  ],
  "proven_scope": ["storage.blob.upload.b1"],
  "next_actions": [
    { "agent": "gentest",      "instruction": "Add a test covering storage.blob.list.b7." },
    { "agent": "code",         "instruction": "Fix stream consumption in storage::blob::upload." },
    { "agent": "requirements", "instruction": "Document the invariant-culture upper-casing applied to sku." }
  ]
}
```

`verdict: blocked` means the verification could not be performed at all — a
required input was missing, or the document contains an untraceable requirement
(coverage gate exit 2). It is distinct from `fail`, which means verification ran
and the suite did not hold up.

`kind: document_gap` reports behavior that is real in the C# source but absent
from `document.json`. Its `ref_id` is the **nearest** documented requirement, or
the owning `feature_id` when nothing is close; `evidence` must cite the C# file
and line where the undocumented behavior lives. This is the only gap kind whose
`next_actions` agent is `requirements` — GenTest cannot write a test for a rule
that was never written down, and the Code agent must not invent one.

`mutation_probes[]` is the evidence record for pass 2b, and `coverage_level:
proven` may not be claimed without it. Each entry must carry the mutation, the
command, and the before/after output: a probe that cannot be evidenced did not
happen. `outcome: probe_error` covers a red baseline, a mutant that did not
compile, or a non-zero `cargo test` that was not an assertion failure in a
claiming test — none of those kill a mutant.

`coverage_level` is the strongest level the verifier actually established, and it
must never be inflated:

| Level | Established by | Claim |
| --- | --- | --- |
| `counted` | coverage gate | A test exists per requirement. |
| `substantive` | + quality gate | That test asserts something real. |
| `proven` | + mutation probe | The test fails when the behavior breaks. |
| `parity-checked` | + differential run | Its expectation matches the C# original. |

`parity-checked` additionally requires `passes_run.differential: "ran"` and a
`golden_results[]` table — one entry per golden case, each carrying `case_id`,
the `csharp` output, the `rust` output, and a `status` of `match`, `mismatch`, or
`error`. Every `status: mismatch` must have a corresponding `mismatches[]` entry.
A parity claim backed by no execution record is the failure mode the level
exists to rule out, so `tools/Check-ParityReport.ps1` treats it as critical
rather than as a note. The C# runner is an input the orchestrator supplies; when
it is absent the differential is `skipped` and `substantive` is the ceiling.

**The label is not the evidence; the outputs are.** The gate recomputes each
case's verdict from the two recorded outputs rather than trusting `status`, so
these rules are enforced, not merely requested:

- `csharp` and `rust` are the **harness envelopes verbatim** — `{ "ok": true,
  "value": ... }` or `{ "ok": false, "error": { "type", "message" } }`. A bare
  value cannot distinguish a returned `4` from a thrown exception, and a failed
  envelope with no `error.type` reduces parity to "both failed somehow".
- Empty is not equal. A blank, absent or empty `csharp`/`rust`/`case_id`/`status`
  is an absent observation and fails the level.
- `status: match` requires the two envelopes to be **equal** (key order is not a
  difference). A mislabelled match is a critical violation.
- `status: mismatch` on two identical outputs is a violation too: a false
  mismatch sends the Code agent chasing a divergence that does not exist.
- `status: error` is a case that never ran, so it is incompatible with
  `parity-checked`. Fix the harness and re-run, or drop the case and the level.
- Normalization is the one honest reason unequal outputs match — and it must be
  shown, not asserted. Record `normalization` (what rule was applied) plus
  `normalized_csharp` and `normalized_rust`; the gate checks that the normalized
  pair really is equal.

```jsonc
{
  "case_id": "g_calculator_add_x3",
  "csharp": { "ok": true, "value": "4.50" },
  "rust":   { "ok": true, "value": "4.5" },
  "status": "match",
  "normalization": "decimal scale normalised before comparison",
  "normalized_csharp": { "ok": true, "value": "4.5" },
  "normalized_rust":   { "ok": true, "value": "4.5" }
}
```

`required_tests` is the feedback channel that closes the loop. GenTest is best
effort, so the verifier owns the coverage verdict and must return a work order
precise enough to act on without re-deriving the analysis. A gap reported without
a `required_tests` entry usually returns uncovered next round.

`next_actions` is a **recommendation** to the orchestrator, not a dispatch. The
verifier never invokes another agent; the orchestrator decides whether to act on
these, reorder them, or stop the run.

`adjudications[]` closes the pipeline's one otherwise-broken correction path. The
Code agent is the only agent that executes the tests, so it is the only one that
can discover a test that is itself wrong — and it is forbidden from editing
tests. Its only correct move is to stop and report the conflict with evidence.
The orchestrator then hands that report here, because ruling on a test against
its source requirement is what this agent does.

Every adjudication carries `test_id`, a `ruling`, and non-empty `evidence` — a
ruling without evidence is an assertion of authority, and the agent it lands on
has no way to act on it. Two rulings carry an extra obligation, because they
discard work that something still has to do:

| `ruling` | Also required | Why |
| --- | --- | --- |
| `test_wrong` | `ref_id`, plus a `required_tests[]` entry for that same `ref_id` with `reason: incorrect_test` | Discarding the test leaves its requirement uncovered. Naming the requirement is what tells GenTest which test to correct. |
| `code_wrong` | a `next_actions[]` entry with `agent: code` | The test was right; somebody has to change the code it caught. |
| `document_wrong` | `route`, a `gaps[]` entry with `kind: document_gap`, and a `next_actions[]` entry with `agent: requirements` | The defect is upstream; without a destination the finding stops here and the Code agent stays blocked. Raising it as a gap is what makes it visible in the verdict rather than buried in the ruling. |
| `rejected` | a `next_actions[]` entry with `agent: code` | Rejecting an escalation for missing evidence has to *ask for* the evidence, or the Code agent re-escalates the same thing next round. |

**A ruling commissions work; it does not perform it.** At the moment the report
is written the test is still wrong, the code is still blocked, and nothing has
been re-run. So while any adjudication is outstanding the report may not claim
`verdict: pass` or `pass_with_warnings`, and may not claim a `coverage_level`
above `substantive`. Reporting success mid-repair is exactly how a loop
terminates without converging. Report `fail` or `blocked`, let the correction
land, and let the next round earn the level.

`tools/Check-ParityReport.ps1` enforces all of this, including that the
`required_tests` entry actually exists. Ruling a test wrong and emitting no work
order reports success while the Code agent remains blocked on the same test — the
precise shape of a loop that terminates without converging. An unruled entry
deadlocks the loop the other way, so the gate fails the report when one is left
unanswered.

`dispute_rulings[]` does the same job for the opposite direction. GenTest may
reject a `required_tests` entry via `manifest.disputes[]`, and the verifier is
the only component that can rule on it, because ruling means reading the work
order back against `document.json`. Without a ruling the requirement livelocks —
the verifier re-issues the order, GenTest re-disputes it, forever.

| `ruling` | Meaning | Consequence |
| --- | --- | --- |
| `upheld` | GenTest was right; the work order was wrong. | Withdraw it. `route: requirements` if the behavior is real but undocumented, `none` if it was simply invented. |
| `rejected` | The work order stands. | Re-issue it as a `required_tests` entry whose `required_assertion` answers the dispute's evidence, with `route: gentest`. |

An upheld dispute does **not** make the requirement covered. It either moves to
the requirements agent or disappears because it was never a requirement; either
way it stays out of `proven_scope`. When the verifier is given the manifest,
`tools/Check-ParityReport.ps1 -ManifestPath` fails the report if any dispute in
it lacks a ruling.

`proven_scope[]` names the requirements a mutation probe actually killed.
`coverage_level: proven` applies to that list and nothing else.

## Validating the parity report

The verifier grades every other agent, which leaves it the only component no
other agent is positioned to correct. `tools/Check-ParityReport.ps1` recomputes
the verifier's claims from the report's own contents so it can self-correct
before returning, and so anyone downstream can re-check it:

| Violation | Meaning |
| --- | --- |
| `inflated_level` | `proven`/`parity-checked` claimed without a killed probe, without a stated `proven_scope`, or while a mutation survived. The costliest error in the pipeline: it ends the loop while requirements are still unproven. |
| `probe_evidence` | A `killed` probe with no mutation, command, or before/after output — an unauditable self-report. |
| `work_order` | A coverage gap with no `required_tests` entry, or an entry with no `required_assertion`. |
| `adjudication` | A Code agent escalation that was never ruled on. |
| `dispute_ruling` | A GenTest dispute that was never ruled on (requires `-ManifestPath`). |
| `counts` | `summary` disagreeing with the array lengths. |
| `verdict` | `pass` reported alongside critical gaps, mismatches, or survived mutations. |
| `shape` | Missing or illegal `verdict` / `coverage_level`. |

Verified by `tools/Test-ParityReport.ps1` (30 assertions), which builds a report
with each defect and asserts it is caught, plus a clean report it must not flag.

## `reports/code-report.json` — Code agent output

```jsonc
{
  "verdict": "pass",                    // pass | partial | fail | blocked
  "iterations": 3,
  "build": { "status": "ok", "warnings": 4 },
  "tests": { "total": 47, "passed": 45, "failed": 2, "ignored": 0 },
  "implemented": [
    { "feature_id": "storage.blob.upload", "module": "rust/src/storage/blob.rs",
      "public_items": ["storage::blob::upload"], "status": "complete" }
  ],
  "unimplemented": [
    { "feature_id": "storage.blob.list", "reason": "Pagination contract ambiguous in document.json.",
      "blocker_for": ["t_storage_blob_list_b7"] }
  ],
  "failing_tests": [
    { "test_id": "t_storage_blob_upload_b2_streaming",
      "error": "assertion failed: !buffered",
      "test_expects": "The uploader streams without buffering the whole payload.",
      "document_says": "storage.blob.upload.b2: 'uploads are streamed'",
      "csharp_does": "csharp/Storage/BlobClient.cs#L40-L88 wraps the stream, never reads to end",
      "suspect": "code",              // test | code | document
      "analysis": "Requires an async reader; current impl uses read_to_end." }
  ],
  "assumptions": [
    { "feature_id": "storage.blob.upload", "assumption": "Empty name maps to InvalidInput, not panic." }
  ]
}
```

The Code agent implements against the tests GenTest produced and against the
document, and treats both as authoritative. Where they agree — the normal case —
satisfying one satisfies the other.

Where they genuinely disagree the agent cannot satisfy both, and iterating
against a contradiction cannot converge. It therefore **stops and returns the
report**; it does not edit the test, does not implement the behavior it believes
is wrong, and does not route the finding to another agent. Stopping is a
successful outcome for this agent.

| `verdict` | Meaning |
| --- | --- |
| `pass` | Builds; every test passes. |
| `partial` | Builds; failures remain, all with `suspect: code`. More iterations could help. |
| `fail` | Does not build, or no meaningful implementation was produced. |
| `blocked` | At least one `failing_tests` entry has `suspect: test` or `suspect: document` — unresolvable inside this agent. |

`blocked` is what distinguishes "needs another round" from "needs a decision".
The orchestrator reads the report and dispatches: `suspect: code` back to the
Code agent, anything else to the parity verifier, which rules on it in pass 1d
and turns the ruling into a `required_tests` entry for GenTest or a
`document_gap` for the requirements stage.

`document_says` and `csharp_does` are mandatory on every entry. They are what
let the next agent rule on the conflict from the evidence instead of repeating
the investigation, and `tools/Check-ParityReport.ps1` rejects a parity report
that leaves any entry unruled.

## `reports/run-report.json` — Migration orchestrator output

Written by `migration-orchestrator` once per run. Every other report describes
one stage; this one describes the run, and it is the artifact a human reads to
decide whether the migration is trustworthy.

```jsonc
{
  "schema_version": "1.0",
  "run_id": "migration-001",
  "task_id": "task-42",
  "extraction_id": "12d809cc...",
  "verdict": "blocked",                // pass | pass_with_warnings | partial | fail | blocked
  "rounds_used": 2,
  "iteration_budget": 3,
  "stages": [
    { "stage": "gentest",
      "agent": "GenTest",
      "status": "pass",                // pass | partial | fail | blocked | not-run
      "reported": { "covered": 47, "total": 47 },   // what the agent claimed
      "measured": { "covered": 47, "total": 47 },   // what the orchestrator's gate found
      "gate": "Check-Coverage.ps1",
      "gate_exit": 0 }
  ],
  "coverage_level": "substantive",     // strongest level actually established
  "limitations": [
    "No C# differential runner supplied; parity differential skipped.",
    "Verifier subgroup returned blocked; adapter validation is unimplemented."
  ],
  "blockers": [],
  "paths": { "document": "document.json", "manifest": "tests/manifest.json" }
}
```

`reported` and `measured` are separate fields on purpose. Agents self-report and
self-reports inflate, so the orchestrator re-runs each deterministic gate and
records both numbers. When they disagree the gate is authoritative and the
divergence is itself a finding about that agent.

`limitations` lists every check that did **not** execute. A run whose
differential was skipped, whose end-to-end was `not-run`, or whose verifier
subgroup was never invoked cannot report a bare `pass`, because the word would
not distinguish "verified" from "never checked". That distinction is the whole
purpose of this file.

## Coverage gate

`tools/Check-Coverage.ps1` is the only real guarantee that the generated tests
cover the document. Everything else in this pipeline — including GenTest's own
`coverage_claim` — is self-report, and a model grading its own homework inflates.

The gate reads `document.json` and `tests/manifest.json`, recomputes coverage by
set arithmetic, and reports:

| Finding | Meaning | Why it matters |
| --- | --- | --- |
| `missing` | A requirement id no test declares in `covers`. | The plain gap. |
| `phantom` | A manifest entry whose `file` or `test_fn` is not on disk. | Worse than missing — it reports a requirement as tested while testing nothing. Grants zero coverage. |
| `dangling` | A `covers` id absent from the document. | Usually a typo, which silently leaves the real requirement untested. |
| `misclaim` | Optional `coverage_claim` totals disagree with the recomputed numbers. | **Informational.** The gate's own numbers are authoritative, so this says nothing about coverage and does not fail the run. |
| `waived` | A gap with an explicit recorded reason. | Suppresses failure but is always reported, so it stays a visible decision. Counts as **uncovered**. |

A **feature** is an envelope, not a requirement a test names directly. It is
credited as covered when a test carries it in `feature_id`, or when any of its
child requirements is covered. Requiring a feature id to appear in `covers`
would fail every manifest written to this contract.

Manifest `file` paths are resolved against the tests root — the crate root, see
[Run layout](#run-layout) — and may not escape it; an absolute path is used as-is
rather than re-rooted. A path that escapes is reported as `phantom`, not silently
followed.

```powershell
./tools/Check-Coverage.ps1 -DocumentPath artifacts/<run>/document.json `
                           -ManifestPath artifacts/<run>/tests/manifest.json `
                           -TestsRoot    artifacts/<run>/rust `
                           -ReportPath   artifacts/<run>/reports/coverage.json
```

Exit codes: `0` pass, `1` gaps found, `2` usage or schema error. Add
`-FailOnWaived` for a release gate where no waiver is acceptable.

**Exit 2 means the document is untraceable** — it is not in this contract's shape
(no `features` array, or an empty one), or a behavior, error, invariant, or
example carries no `id`, so it cannot be counted or pointed at. This is a defect
in the upstream requirement document; it must not be worked around by inventing
ids downstream, because the invented id will not match the next run's.

The shape check comes before any arithmetic on purpose. A file with a top-level
`requirements` key instead of `features` yields an empty requirement set, and
0 of 0 scores as 100% — the gate's strongest verdict, produced by reading
nothing. Input that cannot be read is a usage error, never a pass.

`tools/Test-CoverageGate.ps1` self-tests the gate against synthetic clean,
dishonest, waived, id-less, schema-shaped, and exotic-signature fixtures. Run it
after changing the gate. The *schema-shaped* case is deliberately the manifest
exactly as this document specifies it; an earlier revision of the self-test
stuffed feature ids into `covers`, which no real GenTest output does, and that
masked a bug that failed every contract-conformant manifest.

### What the coverage gate does not prove

It proves a test *exists* for each requirement. It cannot prove the test is any
good — `assert!(true)` counts.

## Test quality gate

`tools/Check-TestQuality.ps1` closes that hole deterministically. It parses each
test body named in the manifest and reports:

| Finding | Severity | Meaning |
| --- | --- | --- |
| `unimplemented_test` | critical | Body is empty or only `todo!()`/`unimplemented!()`. |
| `unregistered_test` | critical | The function carries no `#[test]` attribute. cargo never runs it, so it is not a test at all. |
| `disabled_test` | critical | Marked `#[ignore]`, or gated on an always-false predicate such as `#[cfg(any())]`. Definitely not run. |
| `no_assertion` | critical | No assertion macro; passes as long as nothing panics. |
| `tautological_assertion` | critical | `assert!(true)`, `assert_eq!(x, x)` — cannot fail. |
| `unchecked_error_path` | high | Covers an error requirement but never asserts an error or panic. An example whose documented `expected` is an `error` counts as an error requirement, whatever the manifest's `assertion_kind` says. |
| `unbound_oracle` | high | Covers a documented example but the expected value never appears in the test. Skipped when the example's `expected` is an `error` (a Rust port asserts its own error variant, never the C# exception name) and for booleans (`assert!(x.is_active)` carries no literal `true`). |
| `smoke_only` | medium | Only `is_ok`/`is_some` presence checks; no value compared. `is_none`/`is_empty` are *not* flagged — they are the exact assertion for a documented null or empty result. |

The gate also reports `not_analysed[]`: tests it could not read because the file
was absent, the function was phantom, or its Rust scanner could not delimit the
body. **These are unknowns, not passes.** While any remain, the summary carries
`substantive_eligible: false` and the verifier may not promote `coverage_level`
to `substantive`. The scanner is a conservative Rust lexer, not a parser — it
understands raw and byte strings, nested block comments, char literals and
lifetimes, and it says so when it is out of its depth instead of guessing.

**Only executable text is evidence.** Comments and string contents are blanked
before anything is detected, so a `fn` that exists only inside a comment is
undiscoverable (`not_analysed`, never a pass) and an `assert_eq!` that appears
only inside a string literal does not count as an assertion. Blanking preserves
every byte offset, so the literals an assertion genuinely passes — the expected
values the `unbound_oracle` check reads — survive intact.

**Only a test cargo would run is evidence.** Existing on disk is not enough: a
bare `fn`, an `#[ignore]`d test and a `#[cfg(any())]` test all used to earn full
coverage and count as substantive. Discovery now classifies every manifest entry
as `registered`, `unregistered`, `disabled` or `unknown`, and only `registered`
is analysed. A test gated on a predicate this gate cannot evaluate — a feature
flag, a target predicate — is `unknown`: it lands in `not_analysed` and, like
every other unknown, forces `substantive_eligible: false` rather than being
resolved in the suite's favour. The coverage gate reports the same entries as
`phantom` with the registration defect named in `reason`.

**Carrying `#[test]` is not enough — the signature has to be one libtest
accepts.** These are hard compile errors, not silent skips, so crediting them is
worse than missing them: the crate does not build and the requirement is reported
as covered anyway.

| Signature | Verdict |
| --- | --- |
| `fn t()` | runs |
| `fn t<'a>()` | runs — lifetimes are erased, and are the one generic libtest tolerates |
| `#[tokio::test] async fn t()` | runs — a *path-qualified* test attribute is a proc macro that builds the real test around the body, which is what makes `async` legal |
| `#[test] async fn t()` | rejected — plain `#[test]` cannot drive a future |
| `fn t(x: i32)` | rejected — libtest only calls zero-argument functions |
| `fn t<T>()`, `fn t<const N: usize>()` | rejected — no non-lifetime generic parameters |
| `const fn t()` | rejected |

`#[ignore]`, `#[ignore = "reason"]` and `#[ignore(...)]` are all the same
instruction and all count as `disabled`. An unusual signature is not the same as
an unrunnable one, and the gates must not reject `pub(crate) fn` or a lifetime
generic merely for looking exotic.

Both gates share `tools/RustLex.ps1` for this. They used to disagree — the
quality gate masked comments and strings while the coverage gate ran a raw regex
over the file, so a function name written in a comment satisfied one and not the
other. One implementation is the only way they stay agreed.

```powershell
./tools/Check-TestQuality.ps1 -DocumentPath artifacts/<run>/document.json `
                              -ManifestPath artifacts/<run>/tests/manifest.json `
                              -TestsRoot    artifacts/<run>/rust `
                              -ReportPath   artifacts/<run>/reports/quality.json
```

**A requirement whose only test carries a critical finding is not covered.** The
verifier subtracts it from the covered set rather than reporting a percentage
that counts inert tests.

`tools/Test-TestQuality.ps1` self-tests the analyser against one deliberately
defective test per finding type *and* against healthy tests that must not be
flagged. False accusations matter as much as misses: a gate that flags good tests
trains the agent to ignore it.

## Falsifiability is the only real proof

Both gates are static. Neither can tell you a test would actually fail if the
behavior were wrong — the only definition of coverage that means anything.

That is established by the verifier's mutation probe (pass 2b): on a **scratch
copy** of the crate, break the Rust implementation of one requirement, confirm
the mutant still compiles, run only the tests claiming to cover it, and confirm
at least one of them fails on an assertion. A requirement whose tests all stay
green is not covered, whatever the manifest says. The verifier reports these as
`survived_mutation`.

Three rules keep the probe honest:

- **Scratch copy, not the workspace.** An earlier revision gated the probe on a
  clean `git status`, which meant it never ran: a freshly generated crate is
  always uncommitted.
- **A non-zero exit code is not a kill.** `cargo test` exits non-zero for
  compile errors and harness panics too. Only an assertion failure naming a
  claiming test kills a mutant; everything else is `probe_error`.
- **Evidence or it did not happen.** `coverage_level: proven` requires a
  `mutation_probes[]` entry with the mutation, the command, and the before/after
  output. An unevidenced probe is exactly the self-report this pipeline exists to
  replace, and `proven` is scoped to the probed subset only.

This is why `parity-report.json` carries `coverage_level` rather than a single
percentage.

## Severity rubric

| Severity | Meaning |
| --- | --- |
| `critical` | Public API missing, wrong output for a documented example, or a security-relevant divergence. |
| `high` | Documented behavior or error case not implemented, not tested, or tested only by a test that survived mutation. |
| `medium` | Invariant untested, weak assertion, or divergence only in edge inputs. |
| `low` | Cosmetic divergence (formatting, message wording) with no semantic impact. |

## Status conventions

- An agent that cannot complete its job MUST still write its report file with the
  failure recorded, rather than exiting silently. The orchestrator routes on the
  report, not on the exit code.
- An agent missing a required input MUST stop and report the missing input. It
  must not search the repository for a substitute, and must not fabricate one.
- Fields are additive. Never remove a field; add and deprecate.

## Invocation

The orchestrator invokes an agent with a prompt that supplies:

| Key | Meaning |
| --- | --- |
| `run_id` | Identifier for the run, used in reports. |
| `inputs` | Concrete paths (or inline content) for every required input. |
| `outputs` | Concrete paths the agent must write. |
| `mode` | Agent-specific, e.g. `full` or `gap-fill` / `repair`. |
| `focus` | Optional subset of `feature.id` values to restrict work to. |

Every agent ends its turn with a short structured summary for the orchestrator:
verdict, counts, paths written, and blockers. The orchestrator routes on that
summary plus the report file.
