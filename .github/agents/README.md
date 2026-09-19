# Yen's C# to Rust verifier team

This verifier subgroup owns three gates in the repository pipeline:

```text
Generated Rust
  |
  v
verifier-orchestrator
  |-- syntax-style-verifier
  |-- security-verifier
  `-- end-to-end-verifier
```

Feature parity remains a separate verifier owned by another team member. This orchestrator does not redefine or modify
`.github\agents\feature-parity-verifier.agent.md`; a higher-level pipeline orchestrator can combine that result with
this subgroup's aggregate verdict.

## Agents

| Agent | Role | Invocation |
|---|---|---|
| `verifier-orchestrator` | Coordinates and aggregates Yen's three verifier agents | User or parent orchestrator |
| `syntax-style-verifier` | Rust compiler, formatting, lint, and idiom gate | Subagent only |
| `security-verifier` | High-confidence static security gate | Subagent only |
| `end-to-end-verifier` | Authenticated runtime evidence gate | Subagent only |

The private verifiers expose only read/search tools. The orchestrator exposes `agent`, read, and search so it can invoke
the exact verifier profiles. It cannot edit code, execute commands, connect to TDS, or substitute a general-purpose
agent.

## Trust boundary

The deterministic host, not the model orchestrator, validates schemas, hashes, authentication, artifact containment,
and workspace immutability before launch and revalidates the model result afterward. The agent orchestrator performs
delegation and identity-consistency checks only. Its output is a candidate verdict until the host accepts it.

Every artifact path other than a declared root is root-relative and has no drive prefix, leading separator, or `..`
segment. Every externally produced artifact required by end-to-end verification has an exact SHA-256 identity.

## Prepare the request

Create a JSON file conforming to `contracts\verifier-orchestration-request.schema.json`. It contains:

- the artifact root;
- the generated Rust workspace, relative code path, source-manifest path, and source-manifest SHA-256;
- an optional local syntax-worker receipt and hash;
- the intermediate `document.json`, generated-tests manifest, and explicit end-to-end environment identifier;
- local runtime evidence and hash, or adapter-specific config, dependency, preflight, and authenticated evidence fields.

Syntax/style and security never receive environment fields. The syntax gate can
perform a local read-only review without a receipt; a supplied invalid receipt
still blocks verification. End-to-end defaults to `local`; deployment and
machine fields are required only by adapters that declare them.

## Run

From the repository root:

```powershell
agency copilot --agent verifier-orchestrator --source repo `
  --add-dir Q:\src\Substrate `
  --input Request=artifacts\migration-001\verifier-request.json `
  --prompt "Run Yen's verifier team and return the aggregate verdict." `
  --allow-tool=agent,read,search
```

Repeat `--add-dir` for each external root referenced by the request. The orchestrator invokes syntax/style and security
in parallel, then invokes end-to-end only when both static gates pass. Its response conforms to
`contracts\verifier-orchestration-result.schema.json`.

This agent can also be invoked as stage 6 of a full migration run by `migration-orchestrator`, which builds and
validates the request itself. Either launcher inherits the deterministic host's responsibilities, and in both cases the
aggregate is a candidate model verdict that a deterministic gate must validate before it releases anything.

For `environment: "substrate-tds"`, the end-to-end verifier consumes
`.github\skills\substrate-tds-verification\SKILL.md` as adapter context but never
receives TDS tools. A separate privileged executor may run only after
deterministic authenticated preflight succeeds.
