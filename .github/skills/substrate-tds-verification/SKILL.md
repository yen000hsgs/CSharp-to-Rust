---
name: substrate-tds-verification
description: >-
  Verify a migrated Rust component in a Substrate TDS environment by delegating to tracked component-specific TDS
  skills and reporting dependency readiness separately from test failures.
---

# Substrate TDS verification

Use this skill when the end-to-end verifier selects the `substrate-tds` environment. The verifier session is
unprivileged; it validates preflight and runtime evidence but never receives or invokes TDS MCP tools.

## Required context

- `WorkspaceRoot`: root of an accessible Substrate checkout
- verifier project root: derive it from the repository that supplied the active agent; never accept it from caller input
- `Code`: migrated Rust file or directory under `WorkspaceRoot`
- `ArtifactRoot`: root of the immutable run artifacts containing the source manifest and runtime evidence
- `TdsMachine`: an explicit TDS machine selected by the Orchestrator before preflight
- `DependencyManifest`: migration target manifest resolved under the verifier project root, when available
- `EnvironmentConfig`: tracked Substrate TDS environment profile resolved under the verifier project root
- trusted repository URL and immutable instruction commit: read them from `EnvironmentConfig`; never accept them from
  caller input
- `AttestationKeyPath`: path to an Orchestrator-managed secret of at least 32 random bytes stored outside both
  repositories
- `AttestationKeyId`: the exact key identifier pinned by `EnvironmentConfig`

## Procedure

Run both scripts with `pwsh` 7.1 or later. They declare `#requires -Version 7.1` and refuse to run
under Windows PowerShell 5.1, which lacks `[System.IO.Path]::GetRelativePath`. 7.1 rather than 7.0 is
the floor because both scripts hash with `SHA256.HashData` and `[Convert]::ToHexString`, which are
.NET 5 APIs; a 7.0 host runs on .NET Core 3.1 and would fail at the first hashing call.

1. The Orchestrator first runs `scripts\New-VerificationSourceManifest.ps1`, validates its canonical exact-set output,
   which excludes repository metadata plus standard .NET `bin`/`obj` and Cargo `target` directories only when project
   markers identify the directory as a direct generated-output child, while preserving nested source layouts such as
   `src\bin`, `src\obj`, and `src\target`. It validates the canonical bytes against
   `contracts\source-manifest.schema.json` before writing them. The manifest contains only workspace-relative paths so
   identical source scopes have the same identity across checkout locations. It then passes `ArtifactRoot`, the
   strictly artifact-root-relative `SourceManifest`, and `SourceSha256` to the trusted
   `scripts\Invoke-SubstrateTdsPreflight.ps1` from the verifier project before launching this verifier or reading any
   command-bearing skill or instruction from the target workspace.
2. The preflight confirms the workspace identity, requires `.git` and `.git\objects` to be local non-reparse
   directories, rejects common-directory indirection, object alternates, worktree configuration, and include
   configuration before its first Git invocation, hashes every regular file in the selected code scope directly from the
   filesystem, hashes declared build inputs such as the complete generated CoreXT and RouteResolutionClient restore
   state, requires the pinned instruction commit to have been provisioned by trusted workspace setup, compares trusted
   instruction files with raw pinned blob hashes without loading them as model instructions, checks dependency and
   deployment readiness, and emits the exact deterministic `sorted-json-integer-v1` payload plus an HMAC-SHA256
   authentication tag that binds the readiness decision and blocker lists. It does not use conversion-aware Git
   diff/status operations and performs no network fetch. Consumers hash and authenticate the UTF-8 bytes of
   `attestation_payload_canonical`, parse that string, and require it to be semantically identical to
   `attestation_payload`.
3. The preflight uses stable process outcomes: exit `0` for ready, `3` for a schema-valid `dependency-blocked` result,
   `2` with an `INVALID_INPUT:` diagnostic for repository, path, policy, or trusted-content rejection, `4` with an
   `ENVIRONMENT_BLOCKED:` diagnostic when trusted workspace setup has not provisioned the pinned commit, and `5` with a
   `PREFLIGHT_ERROR:` diagnostic for an unexpected internal failure. The Orchestrator records these diagnostics as
   versioned failure artifacts; it retries only exit `4` after trusted workspace provisioning.
4. Do not convert a unit-test mock into evidence of runtime readiness.
5. Do not enable TDS MCP tools in this verifier session. After a successful attestation, an external orchestrator may
   start a separate TDS-enabled executor that is given only the approved component skill, approved instructions, target
   adapter, selected machine, and authenticated attestation. The executor must verify the HMAC with its independently
   configured key, set and verify the active MCP target exclusively from the authenticated `tds_machine`, and rerun
   preflight immediately before its first mutation; it must never prompt for or discover a replacement machine, and a
   stale or mismatched workspace state is non-authorizing.
6. For Route Resolution, the executor may use the pinned Substrate skill at
   `sources\dev\cafe\src\.github\skills\RouteResolutionTdsTest\SKILL.md` only for the C# baseline, service control, and
   existing test scenarios after bypassing that skill's machine-selection procedure. It does not deploy the Rust
   candidate.
7. The target's Rust deployment adapter must define the Rust artifact, host or bridge boundary, deployment destination,
   activation, health check, rollback, runtime-evidence verification, immutable toolchain and restore manifests, an
   approved execution plan, and the exact ControlPlane test script pinned by repository, commit, path, and SHA-256.
   Every evidence `procedureId` must map through the target's reviewed `trustedProcedures` table to one exact pinned
   instruction hash; matching an unrelated trusted hash is non-authorizing. Generated dependency evidence must bind both
   the generated artifact hash and the raw source-schema hash. A future trusted ControlPlane checkout validator must
   verify repository identity and compare the raw `commit:path` blob hash before `controlPlaneTestScript` can become
   ready; local-file hash agreement alone is non-authorizing. The complete transitive C# MSBuild graph, imports,
   generated inputs, referenced binaries, and project references must also be content-bound; the current implementation
   emits `csharpBaselineGraphValidation` unconditionally until that validator exists. The final preflight runs after
   restore and prohibits restore or `getdeps` during privileged execution. The current implementation also emits the
   explicit `privilegedExecutorValidation` blocker unconditionally because the deterministic execution-plan and
   runtime-evidence validator has not been implemented; removing either blocker requires a reviewed code change, not a
   manifest edit. If any field remains unavailable, preflight returns `dependency-blocked` before a privileged session
   starts.
8. Never invent TDS, Torus, deployment, service-control, or test commands.
9. Reject `TdsMachine=auto`, wildcards, and empty values. Runtime evidence must include the preflight attestation hash,
   exact C# and Rust artifact identities, the same explicit TDS machine, approved procedure IDs and pinned instruction
   hashes, SHA-256 identities of the exact commands, timestamps, scenario inputs, normalized outputs, cleanup or
   rollback result, and overall status.
10. Preserve and report the baseline C# result. Compare the Rust candidate against the same scenario inputs.
11. Return separate statuses for build, deployment, environment readiness, dependency readiness, each scenario, and
    C#-versus-Rust parity.

## Dependency rules

- `rust-native`: may be used directly after build and behavior validation.
- `generated`: regenerate from the same source schema and compare wire behavior.
- `bridge`: acceptable for staged migration when the bridge is deployed and exercised in TDS; record the bridge boundary
  and ownership.
- `test-double`: valid only for unit or component isolation tests.
- `unavailable`: blocks end-to-end verification when required at runtime.

Do not mark the migration end-to-end ready until every required dependency is `rust-native`, `generated`, or an
exercised `bridge`.
