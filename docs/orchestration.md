# Verifier orchestration

## Scope

The verifier team starts after the migration pipeline has produced five immutable inputs: original C# code,
`document.json`, requirements, generated tests, and generated Rust code. Upstream analysis and generation agents are
outside this repository's ownership.

## Agent topology

```text
User
  |
  v
verifier-orchestrator
  |-- syntax-style-verifier
  |-- feature-parity-verifier
  |-- security-verifier
  `-- end-to-end-verifier
```

`verifier-orchestrator` is the only user-invocable profile. Its `agent` tool invokes the four private custom agents in
isolated subagent contexts. Leaf agents cannot invoke peers, edit code, run commands, or access TDS.

## Flow

1. A deterministic producer creates a unique `run_id`, immutable snapshots, and the source manifest.
2. The caller writes a request conforming to `contracts\verifier-orchestration-request.schema.json`.
3. The orchestrator validates the request and creates one child request per verifier using
   `contracts\verification-request.schema.json`.
4. Syntax/style, feature parity, and security run independently and may run in parallel.
5. The orchestrator validates each child response with `contracts\verification-result.schema.json` and requires exact
   run, agent, workspace, code, and source-hash identity.
6. End-to-end runs only when all three static gates pass. A failed or blocked static gate prevents runtime work.
7. The orchestrator returns one aggregate result conforming to
   `contracts\verifier-orchestration-result.schema.json`.

The aggregate decision is deterministic: any failure produces `fail`; otherwise any blocked, invalid, unavailable, or
not-run gate produces `blocked`; only four passes produce `pass`.

## Artifact responsibilities

| Artifact | Producer | Consumer |
|---|---|---|
| C# source | Migration input | Feature parity, security |
| Intermediate `document.json` | Upstream analyzer | Feature parity, security |
| Requirements | Upstream requirements agent | Feature parity, security, end-to-end |
| Generated-tests manifest | Upstream test generator | Feature parity, end-to-end |
| Rust source manifest | Deterministic source-manifest worker | All verifiers |
| Syntax validation receipt | Constrained compiler/lint worker | Syntax/style |
| TDS preflight | Deterministic preflight worker | End-to-end |
| Runtime evidence | Separate privileged executor | End-to-end |

No verifier treats conversational history as evidence. Every handoff uses a schema-validated artifact or an immutable
identifier.

## Privilege boundary

The orchestrator and all verifier agents are read-only. Compiler, lint, scanner, deployment, and runtime execution occur
in constrained workers or executors and return authenticated receipts. The end-to-end agent cannot authorize or perform
TDS mutation. Current unconditional preflight blockers remain authoritative until reviewed executor and complete C#
baseline graph validation exist.
