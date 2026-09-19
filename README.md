# CSharp-to-Rust

A multi-agent tool to migrate C# code to Rust.

## Pipeline

```
C# -> Extractor -> Collector -> Distributor
                     |             |
               document.json  distribution.json
                     +------ Orchestrator ------+
                                |               |
                             GenTest --tests--> Code -> Rust
                                                        |
                             Verifiers: syntax/style, feature parity, security, e2e
```

An orchestrator drives the stages and routes on each agent's handoff. See
[`migration-orchestrator`](.github/agents/migration-orchestrator.agent.md) and
[Run a migration](#run-a-migration) below.

## Agents

Defined as [custom agents](https://docs.github.com/en/copilot/reference/custom-agents-configuration)
in `.github/agents/`.

| Agent | In | Out | Owner |
| --- | --- | --- | --- |
| `migration-orchestrator` | run request (`run_id`, `task_id`, `csharp_source_root`) | run-report.json and a single run verdict | hoangnguyen@ |
| `csharp-extractor` | C# project, solution, or SDK | compiler JSON and source-backed Markdown report | - |
| `requirements-collector` | extractor report and compiler JSON | document.json, document.context.json, optional rendered Markdown | - |
| `code-distributor` | requirements document, context, compiler JSON | distribution.json with feature work packages and dependencies | - |
| `gentest` | document.json, C# source | unit / functional / e2e tests, test manifest, golden cases | hoangnguyen@ |
| `feature-parity-verifier` | document.json, C#, tests, Rust | parity report (gaps + behavioral mismatches) | hoangnguyen@ |
| `code-agent` | document.json, tests | Rust crate, code report | hoangnguyen@ |

The remaining verifiers (syntax/style, security, end-to-end) are owned by the
rest of the team.

**The orchestrator owns control flow.** It supplies each agent's inputs and
output paths, and decides how each result is used. These agents do their
job and hand back a structured summary — they never invoke each other and never
decide what runs next.

## Run a migration

From the repository root:

```powershell
agency copilot --agent migration-orchestrator --source repo `
  --input run_id=migration-001 `
  --input task_id=task-42 `
  --input csharp_source_root=Q:\src\MyProject `
  --input execution_approved=true `
  --input porting_guide=Q:\src\porting-guide.md `
  --add-dir Q:\src\MyProject `
  --prompt "Run the full migration and return the run verdict." `
  --allow-all-tools
```

`run_id`, `task_id` and `csharp_source_root` are mandatory. The orchestrator
will not guess a project: omit either of the last two and it stops.

`porting_guide` is optional but usually decisive. The collector records Rust
mapping choices it cannot derive from C# evidence — decimal representation,
`Result` versus panic, whether formatting is contractual, the shape of the public
surface — as open questions, and a document carrying unresolved mapping questions
never reaches ready. Supply an approved guide answering them or the run blocks at
stage 2 no matter how clean the source is.

`--add-dir` the C# source, since it lives outside the repository and is
read-only to every agent including the orchestrator.

`--allow-all-tools` is required because the orchestrator re-runs the gates
itself — `dotnet`, `cargo` and the scripts in `tools/`. The verifier subgroup's
narrower `--allow-tool=agent,read,search` is not sufficient here.

`execution_approved=true` authorises **building the target C# project** during
extraction. It defaults to false and stage 1 blocks without it, deliberately:
extraction runs the project's own build, so it is opt-in rather than implied by
requesting a run. Omit it if you have not approved that.

Stages 1-5 run with the above. Stage 6 defaults to local verification of the
generated crate and stage-4 test evidence. Set
`verification_environment=substrate-tds` only for code integrated into a
Substrate checkout; that add-on additionally needs `tds_machine`,
`attestation_key_path`, and `attestation_key_id`.

Everything lands in `artifacts/<run_id>/`, with the verdict in
`artifacts/<run_id>/reports/run-report.json`.

## Extraction, requirements, and distribution agents

The [C# extractor](.agents/agents/csharp-extractor.agent.md) uses a required
Roslyn helper to collect compiler facts, then writes a source-backed report
for the orchestrator. Full compiler JSON stays on disk; bounded views keep
agent input focused. New snapshots omit the unused assembly catalog without
changing compiler binding or actual source dependency relationships.

The [requirements collector](.agents/agents/requirements-collector.agent.md)
uses that evidence to author compact behavioral requirements in `document.json`.
Its `document.context.json` companion retains snapshot association, evidence
links, coverage, and unresolved decisions. The orchestrator must enforce
`--require-ready` before dispatching downstream work; structural validation
alone does not establish semantic parity.

The collector helper prepares authoring drafts, validates the pair, and renders
optional Markdown; it does not infer behavior. Code also needs GenTest's
generated suite and manifest. Original source/tests remain available through
the orchestrator, and historical flat requirements use explicit legacy commands.

Both agents have Copilot discovery entry points under `.github\agents` and
canonical definitions and skills under `.agents`. See
[extractor usage](docs/extractor.md) and
[collector usage](docs/requirements-collector.md) for assignments and helper
commands.

The [code distributor](.agents/agents/code-distributor.agent.md) groups the
collected requirements into feature work packages without rewriting their
IDs or behavior. Its `distribution.json` records prerequisites, shared concerns,
and unassigned work. The orchestrator does **not** currently consume it: it
rejects a `focus` input, because `Check-Coverage.ps1` has no scope parameter and
always scores the complete `document.json`, so a focused run could never reach
exit 0. Package-at-a-time migration needs scope support in the coverage gate
first. See [distributor usage](docs/code-distributor.md).

Build and exercise both helpers and the calculator sample from the repository
root:

```powershell
dotnet build CSharpToRust.sln
dotnet test CSharpToRust.sln
```

## Contracts

Downstream JSON handoff schemas are defined in
[`docs/contracts.md`](docs/contracts.md). Extraction evidence and the collector's
context companion are described in their workflow documents linked above.
Downstream stages join on `feature.id`, so preserve those IDs across handoffs.

## Proving coverage

GenTest works on best effort. The parity verifier owns the coverage verdict, and
it earns it in five escalating steps rather than trusting anyone's self-report.

**1. Existence** — `tools/Check-Coverage.ps1` recomputes coverage from
`document.json` and `tests/manifest.json` by set arithmetic and fails on missing,
phantom (claimed but not on disk), dangling (typo'd ref), or misclaimed coverage.

**2. Substance** — `tools/Check-TestQuality.ps1` reads the test bodies and flags
tests that cannot fail: empty/`todo!()`, no assertion, `assert!(true)`, an error
requirement with nothing asserting an error, a documented example whose expected
value never appears, presence-only smoke checks. A requirement whose only test
has a critical finding is **not covered**.

**3. Falsifiability** — the verifier breaks the Rust implementation of one
requirement and runs only the tests claiming to cover it. If none fail, the
coverage is fake (`survived_mutation`). This is the only step that actually
proves anything.

**4. Correctness** — differential execution against the C# original, then
semantic review.

**5. The judge is judged** — `tools/Check-ParityReport.ps1` validates the
verifier's own report. The verifier grades every other agent, which leaves it the
one component no other agent is positioned to correct. The tool recomputes its
claims from the report: `proven` without a killed mutation probe is
`inflated_level`, a `killed` probe with no recorded command or output is
`probe_evidence`, a coverage gap with no work order is `work_order`, and a Code
agent escalation nobody ruled on is `adjudication`. An inflated level is the
costliest failure in the pipeline — it tells the orchestrator to stop looping
while requirements are still unproven, and the run ends believing it succeeded.

```powershell
# Manifest `file` paths are crate-relative, so -TestsRoot is the crate root.
# Omit it and correctly-placed test files are reported as phantoms.
./tools/Check-Coverage.ps1     -DocumentPath tools/testdata/calculator-document.json `
                               -ManifestPath artifacts/<run>/tests/manifest.json `
                               -TestsRoot artifacts/<run>/rust
./tools/Check-TestQuality.ps1  -DocumentPath tools/testdata/calculator-document.json `
                               -ManifestPath artifacts/<run>/tests/manifest.json `
                               -TestsRoot artifacts/<run>/rust
./tools/Check-ParityReport.ps1 -ReportPath artifacts/<run>/reports/parity-report.json `
                               -CodeReportPath artifacts/<run>/reports/code-report.json `
                               -ManifestPath artifacts/<run>/tests/manifest.json

./tools/Test-CoverageGate.ps1   # self-test the gates themselves
./tools/Test-TestQuality.ps1
./tools/Test-ParityReport.ps1
```

See [`docs/contracts.md`](docs/contracts.md#run-layout) for the run layout the
paths above assume, and for the C# differential harness contract — the harness is
an optional orchestrator-supplied input, not something any of these three agents
builds.

The verifier reports `coverage_level` (`counted` → `substantive` → `proven` →
`parity-checked`) so a weak claim can never read as a strong one, and emits
`required_tests` — a precise work order back to GenTest — so the orchestrator can
loop gap-fill until the set is empty.

## Correction channels

The loop belongs to the orchestrator. What these three agents owe it is the
ability to be corrected — by themselves, or by each other — with the feedback
computed by a tool rather than asserted by an agent.

| From → To | Channel |
| --- | --- |
| GenTest → itself | Runs both gates on its own output, fixes what they report, re-runs. |
| Code → itself | `cargo build` / `cargo test` repair loop, bounded by the orchestrator's budget. |
| Verifier → GenTest | `required_tests[]`, each carrying a `required_assertion`. |
| Verifier → Code | `next_actions[]`, consumed in repair mode. |
| Verifier → itself | `Check-ParityReport.ps1`, run on its own report before returning. |
| Code → orchestrator | `verdict: blocked` + evidence-bearing `failing_tests[]`; the orchestrator routes it to the verifier's pass 1d. |
| GenTest → Verifier | `disputes[]`, with evidence, when a work order looks wrong. |
| Verifier → GenTest | `dispute_rulings[]` — every dispute is upheld or rejected, so a contested requirement cannot livelock. |

The Code agent implements against the tests GenTest produced *and* against the
document, treating both as authoritative. Where they disagree it cannot satisfy
both, and iterating against a contradiction cannot converge — so it stops and
returns a report rather than editing the test or implementing what it believes is
wrong. **Stopping is a successful outcome for that agent.** It does not decide
who was wrong and does not route the finding; the orchestrator does, and `blocked`
is what tells it the difference between "needs another round" and "needs a
decision".

That matters because the Code agent is the only agent that executes the tests, so
it is the only one that can discover a test that is itself wrong. Without pass 1d
that discovery dead-ends and the loop deadlocks: the Code agent cannot satisfy the
test, cannot change it, and nobody reads its report.

**Coverage and quality work orders are produced by a gate, not by a reviewing
agent.** `Check-Coverage` knows each requirement's kind and, for documented
examples, its exact input and expected value, so it states what the missing test
must assert; `Check-TestQuality` knows which rule fired, so it states the repair.
A stalled loop on those is then a real gap in the document rather than a reviewer
having an off round.

The rest is agent judgement, and the tooling checks its *form*, not its
correctness. Mismatch causes, adjudications, dispute rulings and
`kind: document_gap` findings are written by the verifier; `Check-ParityReport`
enforces that each one exists, is routed, and cites evidence — it cannot tell you
the evidence is right. That is a real limit, not a rounding error: a verifier that
rules confidently and wrongly produces a report this gate passes.

Details in [`docs/contracts.md`](docs/contracts.md#coverage-gate).

## Known limitations

Stated plainly, because a verification pipeline that oversells itself is the
thing it exists to prevent:

- **The gates read Rust with a scanner, not a parser.** `Check-TestQuality.ps1`
  handles raw and byte strings, nested block comments, and char literals, but it
  is deliberately conservative: anything it cannot delimit is reported in
  `not_analysed[]` rather than guessed at. Those entries are unknowns, not
  passes, and they block promotion beyond `counted`.
- **Mutation probing covers a risk-prioritised subset.** The verifier probes what
  it can afford to, and `coverage_level: proven` applies only to the requirements
  actually listed in `mutation_probes[]` — never to the suite as a whole.
- **Waivers let a run finish; they do not certify it.** A waived requirement
  counts as *uncovered* and caps the verdict. `-FailOnWaived` turns them back
  into a hard failure when you want full-scope certification.
- **Pass 2a (`parity-checked`) is not wired end to end for the demo.** The C#
  harness exists; the Rust side is the Code agent's deliverable, so the top rung
  is reachable by design but unexercised here.
- **The gates check test *substance*, not test *correctness*.** A test can assert
  something real and still assert the wrong thing; that is what the differential
  pass and human review are for.
- **The optional Substrate TDS adapter cannot currently return a ready receipt
  on any host.** Three
  validation flags in `scripts/Invoke-SubstrateTdsPreflight.ps1` —
  `controlPlaneProvenanceValidationImplemented`,
  `csharpBaselineGraphValidationImplemented` and
  `privilegedExecutorValidationImplemented` — are hardcoded `$false`, and the
  last forces `adapterReady = $false`. A correctly parameterised run on a fully
  provisioned machine still returns `verdict: blocked` and exit 3. That is an
  unimplemented adapter validation, not a defect in the generated Rust or in the
  invocation, and the orchestrator records it as such.

## Inputs

Two C# sources ship with the repo, and they are for different jobs.

### `samples/Calculator` — migration target

A reusable .NET 8 class library intended as **actual conversion input**: no
console entry point, no external package dependencies. It supports addition,
subtraction, multiplication, and division over C# `decimal`, including negative
and fractional values.

```csharp
using CalculatorSample;

decimal sum        = Calculator.Add(2m, 3m);       //  5
decimal difference = Calculator.Subtract(7m, 10m); // -3
decimal product    = Calculator.Multiply(-2m, 3m); // -6
decimal quotient   = Calculator.Divide(5m, 2m);    //  2.5
```

Division by zero throws `DivideByZeroException` (`Cannot divide by zero.`);
arithmetic outside the `decimal` range throws `OverflowException`. The SDK never
prints or terminates the process — errors reach the caller. Precision and range
follow C# `decimal`, which is the first thing the Rust port has to reckon with:
there is no `decimal` in std, so the Code agent must choose a crate and the
parity verifier must prove the choice round-trips.

| Path | Contents |
| --- | --- |
| `samples/Calculator` | The SDK — public `Calculator` API. |
| `samples/Calculator.Tests` | xUnit tests: arithmetic, division by zero, overflow. |
| `samples/Calculator.sln` | Solution containing both. |

```powershell
dotnet build samples\Calculator\Calculator.csproj
dotnet test  samples\Calculator.sln
dotnet pack  samples\Calculator\Calculator.csproj --configuration Release
```

`dotnet pack` produces `CSharpToRust.Sample.Calculator` in
`samples\Calculator\bin\Release`; it does not publish to a feed.

### `tools/testdata/calculator-document.json` — the requirements document

Downstream agents consume a **document**, with original C# evidence available
separately. The extractor and collector produce it from the C# source. This repo
ships a hand-authored fixture describing the sample SDK —
5 features in 47 traceable requirements (behaviors, errors, invariants and
worked examples, each with an id the tests must cite) — so the three agents and
their gates can be exercised without running the whole pipeline.

It is also what the gates' self-tests run against, so a schema change that would
break a real run breaks the self-tests first.

### The differential parity harness

The parity verifier proves C# and Rust agree by running the *same* cases through
both and diffing the results. `docs/contracts.md` defines the protocol; the Code
agent owns the Rust side and must mirror it exactly.

Both sides are batch runners — `--cases <file> [--out <file>]` — that read every
case and emit one envelope keyed by `case_id`. Decimals cross the wire as
**strings**: a JSON number would be parsed back as a double on at least one
side, and that rounding is exactly the divergence this pass exists to catch.
`1 / 3` in C# `decimal` returns 28 significant digits — a Rust port backed by
`f64` fails that case immediately, which is the point.

## Prerequisites

- .NET 8 SDK, or a newer SDK with the .NET 8 runtime, for the extractor,
  collector, and calculator sample. These projects target `net8.0`.
  The extractor automatically uses the latest installed stable .NET runtime
  so it can load a newer SDK's MSBuild even when runtime 8 is also installed;
  no roll-forward environment override is needed. The collector, tests, and
  calculator still require runtime 8. See [extractor runtime selection](docs/extractor.md#runtime-and-sdk-selection).
- Rust toolchain (`cargo`) — for the Code agent and the Rust side of parity

## Usage

Select either upstream profile and supply an assignment from its workflow:

```powershell
copilot --agent csharp-extractor
copilot --agent requirements-collector
copilot --agent code-distributor
```

For a downstream profile:

```
copilot
/agent gentest
```

Or invoke them from the orchestrator agent via the `agent` tool.
