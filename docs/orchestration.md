# Yen verifier subgroup orchestration

## Scope

This document covers only the syntax/style, security, and end-to-end verifiers owned by Yen Nguyen. The upstream
migration agents and the feature-parity verifier are independent components owned by other team members.

## Topology

```text
Parent pipeline or user
  |
  v
verifier-orchestrator
  |-- syntax-style-verifier
  |-- security-verifier
  `-- end-to-end-verifier
```

`verifier-orchestrator` delegates to the three private profiles through the `agent` tool. Leaf agents cannot invoke
peers, edit code, run commands, or access TDS.

## Host and model responsibilities

The deterministic host is the security boundary. It validates the orchestration request, canonical bytes, hashes,
authenticated receipts, path containment, reparse-point policy, and immutable workspace state before the model starts.
It repeats the required checks after the model returns and accepts or rejects the candidate verdict.

The model orchestrator only:

1. copies the common Rust identity into three child requests, adding syntax
   receipt fields only to syntax/style and environment fields only to end-to-end;
2. invokes syntax/style and security independently;
3. checks child response structure and identity consistency;
4. invokes end-to-end only after both static gates pass; and
5. combines the three candidate verdicts.

It does not claim to authenticate HMACs or signatures with read/search tools.

## Decision rule

Any child failure produces `fail`. Otherwise, any blocked, invalid, unavailable, or prerequisite-skipped gate produces
`blocked`. Only three passes produce `pass`. End-to-end is `not-run` with category `prerequisite-not-passed` when either
static gate does not pass.

## Artifact identity

| Artifact | Root | Identity |
|---|---|---|
| Rust code | Rust workspace plus relative code path | Canonical source-manifest SHA-256 |
| Syntax receipt | Artifact root plus relative path | Exact file SHA-256 |
| `document.json` | Artifact root plus relative path | Exact file SHA-256 |
| Generated-tests manifest | Artifact root plus relative path | Exact file SHA-256 |
| Environment config | Adapter-defined root plus relative path | Exact file SHA-256 |
| Dependency manifest | Adapter-defined root plus relative path | Exact file SHA-256 |
| Adapter preflight | Artifact root plus relative path | Exact file SHA-256 and adapter validation |
| Runtime evidence | Artifact root plus relative path | Exact file SHA-256; adapter authentication when required |

Syntax/style and security are local code gates and never receive environment
fields. End-to-end selects behavior from its explicit environment identifier:
`local` consumes host-validated local test evidence, while other environments
use a tracked adapter config. `substrate-tds` is one optional adapter and its
current preflight blockers remain authoritative.
