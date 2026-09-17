# C# to Rust verifier team

The verifier team is the final gate in the migration pipeline:

```text
C# -> Intermediate document -> Requirements -> Generated tests -> Rust
                                                           |
                                                           v
                 syntax/style + feature parity + security + end-to-end
```

The upstream migration team owns C#, the intermediate document, requirements, generated tests, and Rust output. This
repository owns only verification.

## Agents

| Agent | Role | Invocation |
|---|---|---|
| `verifier-orchestrator` | User-facing coordinator and verdict aggregator | User selects this agent |
| `syntax-style-verifier` | Rust compiler, formatting, lint, and idiom gate | Orchestrator subagent only |
| `feature-parity-verifier` | C#-to-Rust behavior and requirement traceability gate | Orchestrator subagent only |
| `security-verifier` | Migration-aware security gate | Orchestrator subagent only |
| `end-to-end-verifier` | Authenticated runtime evidence gate | Orchestrator subagent only |

The private verifiers use `user-invocable: false` and expose only read/search tools. The orchestrator exposes the
`agent` tool so it can invoke the exact verifier profiles as isolated subagents. It cannot edit code, execute commands,
connect to TDS, or substitute a general-purpose agent.

## Prepare the request

Create a JSON file conforming to `contracts\verifier-orchestration-request.schema.json`. It references immutable
artifacts produced by the upstream pipeline and deterministic workers:

- original C# workspace and code scope;
- generated Rust workspace, code scope, source manifest, and manifest SHA-256;
- intermediate document, requirements, and generated-tests manifest;
- optional authenticated syntax-worker receipt;
- authenticated TDS preflight and, when ready, runtime evidence and its validation receipt.

The syntax gate returns `verification-blocked` until the constrained worker receipt exists. The end-to-end gate returns
`dependency-blocked` while the deployment adapter or runtime dependencies remain unavailable.

## Run

From the repository root:

```powershell
agency copilot --agent verifier-orchestrator --source repo `
  --add-dir Q:\src\Substrate `
  --input Request=artifacts\migration-001\verifier-request.json `
  --prompt "Run the verifier team and return the aggregate verdict." `
  --allow-tool=agent,read,search
```

Repeat `--add-dir` for each external workspace referenced by the request.

Only the orchestrator is selected by the user. It invokes syntax/style, feature parity, and security in parallel. It
invokes end-to-end only when all three static gates pass. The final response conforms to
`contracts\verifier-orchestration-result.schema.json`.

## Trust boundary

The Orchestrator validates child requests against `contracts\verification-request.schema.json`, sends each subagent
exactly one JSON object, validates every response against `contracts\verification-result.schema.json`, and rejects
identity drift. Compiler, lint, scanner, deployment, and runtime commands remain outside model agents and must produce
authenticated artifacts.

The end-to-end verifier consumes `.github\skills\substrate-tds-verification\SKILL.md` as procedural context but never
receives TDS tools. A separate privileged executor may run only after deterministic authenticated preflight succeeds.
