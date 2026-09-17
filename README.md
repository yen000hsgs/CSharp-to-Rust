# CSharp-to-Rust

A multi-agent tool to migrate C# code to Rust.

## Pipeline

```
C# ──► Intermediate ──► Requirements ──┬──► GenTest ──► Tests ──┐
                       (document.json) │                        ├──► Code ──► Rust
                                       └────────────────────────┘      │
                                                                       ▼
                                          Verifiers: syntax/style · feature parity · security · e2e
```

An orchestrator agent (autopilot by default) drives the stages and routes on the
JSON reports each agent emits.

## Agents

Defined as [custom agents](https://docs.github.com/en/copilot/reference/custom-agents-configuration)
in `.github/agents/`.

| Agent | In | Out | Owner |
| --- | --- | --- | --- |
| `gentest` | document.json, C# source | unit / functional / e2e tests, test manifest, golden cases | hoangnguyen@ |
| `feature-parity-verifier` | document.json, C#, tests, Rust | parity report (gaps + behavioral mismatches) | hoangnguyen@ |
| `code-agent` | document.json, tests | Rust crate, code report | hoangnguyen@ |
| `verifier-orchestrator` | Rust and authenticated verifier artifacts | aggregate verdict for Yen's verifier subgroup | Yen Nguyen |
| `syntax-style-verifier` | Rust and authenticated compiler/lint receipt | syntax and style verdict | Yen Nguyen |
| `security-verifier` | Rust | security verdict | Yen Nguyen |
| `end-to-end-verifier` | Rust and authenticated runtime evidence | TDS runtime verdict | Yen Nguyen |

Upstream C# analysis and requirements are owned by the rest of the team. Yen's verifier subgroup is documented in
`.github/agents/README.md`.

**The orchestrator owns control flow.** It supplies each agent's inputs and output paths, and decides how each result is
used. Generation and specialist verifier agents hand back structured results rather than routing the pipeline
themselves.

## Contracts

All agent handoffs are JSON artifacts whose schemas are defined in
[`docs/contracts.md`](docs/contracts.md). Read it before changing any agent:
every stage joins on `feature.id`, so that field is load-bearing for the whole
pipeline.

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

The pipeline's agents do not read C#; they read a **document**. Upstream agents
produce it from the C# source. This repo ships one describing the sample SDK —
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

- .NET SDK 10 (verified with 10.0.401) — for the sample and the C# side of the
  differential parity pass. Everything under `samples/` targets `net8.0`, which
  an SDK 10 install builds via its roll-forward; a dedicated .NET 8 runtime is
  not required.
- Rust toolchain (`cargo`) — for the Code agent and the Rust side of parity

## Usage

```
copilot
/agent gentest
```

Or invoke them from the orchestrator agent via the `agent` tool.
