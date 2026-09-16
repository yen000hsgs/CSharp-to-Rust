<#
.SYNOPSIS
  Validates a parity report against the evidence it cites.

.DESCRIPTION
  The parity verifier judges every other agent, and until now nothing judged it.
  Its self-check is a prose checklist, which is exactly the kind of mechanical
  bookkeeping an agent polices badly: counts that drift from array lengths, gaps
  emitted with no work order, and -- the costly one -- a `coverage_level` claimed
  beyond what the run actually established.

  An inflated verdict is worse than a wrong test. A wrong test fails loudly; an
  inflated verdict tells the orchestrator to stop looping while requirements are
  still unproven, and the run ends believing it succeeded.

  This gate recomputes those claims from the report's own contents, so the
  verifier can correct itself before returning, and so any other agent can check
  it afterwards.

  Checks:
    1. Required top-level fields exist and enums are legal.
    2. `summary` counts equal the actual array lengths.
    3. Every coverage gap carries a `required_tests` entry.
    4. Every gap and mismatch carries a `next_actions` entry.
    5. `coverage_level` is supported by evidence present in the report:
         proven         -> a killed mutation probe for that requirement
         parity-checked -> executed golden cases with recorded output
    6. Mutation probes carry the evidence fields that make them auditable.
    7. `verdict` is consistent with the findings.
    8. Every `failing_tests` entry in the code report was adjudicated.
    9. Every `disputes` entry in the manifest was ruled on.

.PARAMETER ReportPath
  reports/parity-report.json to validate.

.PARAMETER CodeReportPath
  Optional reports/code-report.json. When supplied, adjudication of the Code agent's blocked tests is
  enforced.

.PARAMETER ManifestPath
  Optional tests/manifest.json. When supplied, a ruling is required for every GenTest dispute.

.PARAMETER OutputPath
  Optional path for the machine-readable validation result.

.OUTPUTS
  Exit 0 valid | 1 violations found | 2 unreadable input
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReportPath,
    [string]$CodeReportPath,
    [string]$ManifestPath,
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-Json([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Host "ERROR: $Label not found: $Path" -ForegroundColor Red
        exit 2
    }
    try { return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch {
        Write-Host "ERROR: $Label is not valid JSON: $Path`n$($_.Exception.Message)" -ForegroundColor Red
        exit 2
    }
}

function Get-Prop($Object, [string]$Name) {
    # Two traps, both from ConvertFrom-Json output:
    #   * JSON `null` materialises as [DBNull]::Value, which is not $null.
    #   * A primitive such as Int64 exposes no adapted properties at all, so
    #     `.PSObject.Properties.Name` throws under Set-StrictMode.
    # Enumerating the property bag is safe for both.
    if ($null -eq $Object -or $Object -is [System.DBNull]) { return $null }
    foreach ($p in $Object.PSObject.Properties) {
        if ($p.Name -eq $Name) { return $p.Value }
    }
    return $null
}

function Get-ListProp($Object, [string]$Name) {
    # `@(Get-Prop $o 'missing')` is an array of one $null, not an empty array, so
    # every "the array is empty" test silently passed for an *absent* key. That is
    # how a report could omit golden_results entirely and still claim the level
    # those results exist to earn.
    $value = Get-Prop $Object $Name
    if ($null -eq $value -or $value -is [System.DBNull]) { return @() }
    return @(@($value) | Where-Object { $null -ne $_ -and $_ -isnot [System.DBNull] })
}

$report = Read-Json $ReportPath 'Parity report'


$codeReport = if ($CodeReportPath) { Read-Json $CodeReportPath 'Code report' } else { $null }
$manifest = if ($ManifestPath) { Read-Json $ManifestPath 'Manifest' } else { $null }

$violations = [System.Collections.Generic.List[object]]::new()
function Add-Violation([string]$Check, [string]$Severity, [string]$Detail, [string]$Remediation) {
    $violations.Add([pscustomobject]@{
        check = $Check; severity = $Severity; detail = $Detail; remediation = $Remediation
    })
}

# --- 1. Shape ----------------------------------------------------------------

$verdict = Get-Prop $report 'verdict'
$coverageLevel = Get-Prop $report 'coverage_level'

$validVerdicts = @('pass', 'pass_with_warnings', 'fail', 'blocked')
$validLevels = @('counted', 'substantive', 'proven', 'parity-checked')

if (-not $verdict) {
    Add-Violation 'shape' 'critical' "Report has no 'verdict'." "Emit one of: $($validVerdicts -join ', ')."
}
elseif ($verdict -notin $validVerdicts) {
    Add-Violation 'shape' 'critical' "verdict '$verdict' is not a legal value." "Use one of: $($validVerdicts -join ', ')."
}

if (-not $coverageLevel) {
    Add-Violation 'shape' 'critical' "Report has no 'coverage_level'." "Emit one of: $($validLevels -join ', ')."
}
elseif ($coverageLevel -notin $validLevels) {
    Add-Violation 'shape' 'critical' "coverage_level '$coverageLevel' is not a legal value." "Use one of: $($validLevels -join ', ')."
}

$gaps = @(Get-ListProp $report 'gaps')
$mismatches = @(Get-ListProp $report 'mismatches')
$probes = @(Get-ListProp $report 'mutation_probes')
$requiredTests = @(Get-ListProp $report 'required_tests')
$nextActions = @(Get-ListProp $report 'next_actions')
$adjudications = @(Get-ListProp $report 'adjudications')
$disputeRulings = @(Get-ListProp $report 'dispute_rulings')

# --- 2. Counts match reality -------------------------------------------------

$summary = Get-Prop $report 'summary'
if ($summary) {
    $pairs = @(
        @{ Field = 'gaps'; Actual = $gaps.Count },
        @{ Field = 'mismatches'; Actual = $mismatches.Count }
    )
    foreach ($p in $pairs) {
        $claimed = Get-Prop $summary $p.Field
        if ($null -ne $claimed -and [int]$claimed -ne $p.Actual) {
            Add-Violation 'counts' 'high' `
                ("summary.{0} says {1} but the array holds {2}." -f $p.Field, $claimed, $p.Actual) `
                "Recompute summary from the arrays; a summary that disagrees with the detail makes the whole report untrustworthy."
        }
    }
}

# --- 3 & 4. Every finding is actionable --------------------------------------
# A gap with no work order returns uncovered next round, because the agent
# fixing it does not have the verifier's context.

# These must be the exact kinds docs/contracts.md defines for gaps[].kind, or the
# check silently skips real gaps. `document_gap` routes to the requirements agent
# and `unimplemented_feature` to the Code agent; neither produces a GenTest work
# order, so they are deliberately absent.
$coverageGapKinds = @('untested_feature', 'untested_behavior', 'untested_error',
                      'weak_assertion', 'survived_mutation')
$requiredRefs = @{}
foreach ($rt in $requiredTests) {
    $rid = Get-Prop $rt 'ref_id'
    if ($rid) { $requiredRefs[[string]$rid] = $true }

    if (-not (Get-Prop $rt 'required_assertion')) {
        Add-Violation 'work_order' 'high' `
            "required_tests entry for '$rid' has no required_assertion." `
            "State the assertion the test must make. Without it GenTest re-derives your analysis and usually gets it wrong."
    }
}

foreach ($gap in $gaps) {
    $kind = [string](Get-Prop $gap 'kind')
    $refId = [string](Get-Prop $gap 'ref_id')
    if ($kind -in $coverageGapKinds -and -not $requiredRefs.ContainsKey($refId)) {
        Add-Violation 'work_order' 'high' `
            "Coverage gap '$refId' ($kind) has no required_tests entry." `
            "Add a required_tests entry naming the assertion that closes it."
    }
}

if (($gaps.Count + $mismatches.Count) -gt 0 -and $nextActions.Count -eq 0) {
    Add-Violation 'work_order' 'high' `
        "Report has $($gaps.Count) gap(s) and $($mismatches.Count) mismatch(es) but no next_actions." `
        "Recommend at least one action per finding so the orchestrator can route the next iteration."
}

# --- 5. The coverage level must be earned ------------------------------------
# This is the check that matters most. Everything else costs a round; an
# inflated level ends the run.

$killedRefs = @{}
foreach ($probe in $probes) {
    if ((Get-Prop $probe 'outcome') -eq 'killed') {
        $probeRef = [string](Get-Prop $probe 'ref_id')
        if ($probeRef) { $killedRefs[$probeRef] = $true }
    }
}

if ($coverageLevel -eq 'proven' -or $coverageLevel -eq 'parity-checked') {
    if ($killedRefs.Count -eq 0) {
        Add-Violation 'inflated_level' 'critical' `
            "coverage_level is '$coverageLevel' but no mutation probe in the report has outcome 'killed'." `
            "'proven' requires at least one requirement whose test demonstrably failed when the code was broken. Downgrade to 'substantive', or run pass 2b and record the probes."
    }
    $probedScope = @(Get-ListProp $report 'proven_scope')
    if ($killedRefs.Count -gt 0 -and $probedScope.Count -eq 0) {
        Add-Violation 'inflated_level' 'high' `
            "coverage_level is '$coverageLevel' but the report does not state which requirements were probed." `
            "Emit 'proven_scope' listing the probed ref_ids. 'proven' applies only to that subset, never to the suite."
    }

    # Without this, one killed probe licenses an arbitrarily long proven_scope --
    # the strongest claim in the pipeline resting on an unchecked assertion.
    foreach ($ref in $probedScope) {
        $refStr = [string]$ref
        if (-not $refStr) { continue }
        if (-not $killedRefs.ContainsKey($refStr)) {
            Add-Violation 'inflated_level' 'critical' `
                "proven_scope lists '$refStr' but no mutation probe with outcome 'killed' covers it." `
                "Only a requirement whose test demonstrably failed against a broken implementation may appear in proven_scope. Probe it in pass 2b, or remove it."
        }
    }
}

if ($coverageLevel -eq 'parity-checked') {
    # The level's whole meaning is "the two implementations were run against each
    # other". Claiming it while the pass that does the running is marked skipped
    # is the single most misleading state this report can be in, so the claim is
    # checked against the pass log, not only against the results array.
    $passes = Get-Prop $report 'passes_run'
    $differential = [string](Get-Prop $passes 'differential')
    if ($differential -ne 'ran') {
        Add-Violation 'inflated_level' 'critical' `
            ("coverage_level is 'parity-checked' but passes_run.differential is '{0}'." -f $(if ($differential) { $differential } else { 'absent' })) `
            "'parity-checked' means C# and Rust were executed and compared. Run the differential pass, or downgrade the level to 'substantive'."
    }

    $golden = @(Get-ListProp $report 'golden_results')
    if ($golden.Count -eq 0) {
        Add-Violation 'inflated_level' 'critical' `
            "coverage_level is 'parity-checked' but no golden case results are recorded." `
            "The top level requires executed C#-vs-Rust comparisons. Record golden_results, or downgrade the level."
    }

    # A result object missing the side it is supposed to compare proves nothing;
    # counting it made the array's length, not its content, carry the claim.
    $mismatchCases = @{}
    foreach ($mm in $mismatches) {
        $cid = [string](Get-Prop $mm 'case_id')
        if ($cid) { $mismatchCases[$cid] = $true }
    }
    foreach ($g in $golden) {
        $caseId = [string](Get-Prop $g 'case_id')
        $label = if ($caseId) { $caseId } else { '(unnamed case)' }
        foreach ($field in 'case_id', 'csharp', 'rust', 'status') {
            if ($null -eq (Get-Prop $g $field) -or (Get-Prop $g $field) -is [System.DBNull]) {
                Add-Violation 'inflated_level' 'critical' `
                    "Golden result '$label' has no '$field'." `
                    "A comparison needs both observed outputs and a verdict. Record case_id, csharp, rust and status (match | mismatch | error)."
            }
        }
        $status = [string](Get-Prop $g 'status')
        if ($status -and $status -notin @('match', 'mismatch', 'error')) {
            Add-Violation 'inflated_level' 'high' `
                "Golden result '$label' has status '$status'." 'Use match | mismatch | error.'
        }
        # Otherwise a run can record a differing pair, call it a match, and the
        # mismatch never reaches the summary the orchestrator reads.
        if ($status -eq 'mismatch' -and -not $mismatchCases.ContainsKey($caseId)) {
            Add-Violation 'inflated_level' 'critical' `
                "Golden result '$label' is a mismatch but no entry in 'mismatches' reports it." `
                'Every mismatching case must appear in mismatches, or it is invisible to the verdict and the counts.'
        }
    }
}

$survived = @($probes | Where-Object { (Get-Prop $_ 'outcome') -eq 'survived' })
if ($survived.Count -gt 0 -and $coverageLevel -in @('proven', 'parity-checked')) {
    Add-Violation 'inflated_level' 'high' `
        "$($survived.Count) mutation(s) survived, yet coverage_level is '$coverageLevel'." `
        "A survived mutation is a disproved coverage claim. Exclude those requirements from proven_scope and say so."
}

# --- 6. Probes must be auditable ---------------------------------------------

foreach ($probe in $probes) {
    $probeRef = [string](Get-Prop $probe 'ref_id')
    $outcome = [string](Get-Prop $probe 'outcome')
    if ($outcome -notin @('killed', 'survived', 'probe_error')) {
        Add-Violation 'probe_evidence' 'high' `
            "Probe for '$probeRef' has outcome '$outcome'." "Use killed | survived | probe_error."
    }
    if ($outcome -eq 'killed') {
        foreach ($field in 'mutation', 'command', 'baseline_output', 'mutant_output') {
            if (-not (Get-Prop $probe $field)) {
                Add-Violation 'probe_evidence' 'critical' `
                    "Probe for '$probeRef' claims 'killed' but has no '$field'." `
                    "An unevidenced probe is the self-report this pipeline exists to replace. Record the mutation, the command, and both outputs."
            }
        }
    }
}

# --- 7. Verdict consistency --------------------------------------------------
# `pass_with_warnings` is held to the same blocking bar as `pass`. Both tell the
# orchestrator the suite is good enough to stop on, so a verdict that tolerates a
# critical gap under a softer name is an inflated verdict by another name. The
# only thing a warning may represent is a medium/low finding.

$blockingGaps = @($gaps | Where-Object { (Get-Prop $_ 'severity') -in @('critical', 'high') })
if ($verdict -in @('pass', 'pass_with_warnings')) {
    if ($blockingGaps.Count -gt 0) {
        Add-Violation 'verdict' 'critical' `
            "verdict is '$verdict' with $($blockingGaps.Count) critical/high gap(s)." `
            "A critical or high gap is an unproven requirement and means 'fail'. Only medium/low findings may ride under 'pass_with_warnings'."
    }
    if ($mismatches.Count -gt 0) {
        Add-Violation 'verdict' 'critical' `
            "verdict is '$verdict' with $($mismatches.Count) mismatch(es)." "Any mismatch means 'fail'."
    }
    if ($survived.Count -gt 0) {
        Add-Violation 'verdict' 'critical' `
            "verdict is '$verdict' with $($survived.Count) survived mutation(s)." "A survived mutation means 'fail'."
    }
}

# --- 8. Blocked tests must be ruled on ---------------------------------------

# Validating adjudications only when a code report happens to be supplied left
# the content of every ruling unchecked. A ruling is what unblocks the Code
# agent, so it runs unconditionally.
$adjudicated = @{}
$incorrectTestOrders = @{}
foreach ($rt in $requiredTests) {
    if ([string](Get-Prop $rt 'reason') -ne 'incorrect_test') { continue }
    $rid = [string](Get-Prop $rt 'ref_id')
    if ($rid) { $incorrectTestOrders[$rid] = $true }
}

foreach ($a in $adjudications) {
    $tid = [string](Get-Prop $a 'test_id')
    if ($tid) { $adjudicated[$tid] = $true }
    else {
        Add-Violation 'adjudication' 'high' `
            'An adjudication has no test_id.' 'Name the test the ruling applies to; otherwise it unblocks nothing.'
    }

    $ruling = [string](Get-Prop $a 'ruling')
    if ($ruling -notin @('test_wrong', 'code_wrong', 'document_wrong', 'rejected')) {
        Add-Violation 'adjudication' 'high' `
            "Adjudication for '$tid' has ruling '$ruling'." `
            "Use test_wrong | code_wrong | document_wrong | rejected."
    }

    # Same standard as a dispute ruling: without the text it was read against,
    # a ruling is an assertion, and the agent it overrules cannot act on it.
    if (-not [string](Get-Prop $a 'evidence')) {
        Add-Violation 'adjudication' 'high' `
            "Adjudication for '$tid' cites no evidence." `
            'Quote the requirement, the test and the observed result the ruling was based on. An unevidenced ruling is the self-report this pipeline exists to replace.'
    }

    # A verdict alone changes nothing: the test is still wrong next round and the
    # Code agent is still blocked on it. The correction has to be commissioned,
    # and work orders are keyed by requirement, so the ruling must name one.
    if ($ruling -eq 'test_wrong') {
        $refId = [string](Get-Prop $a 'ref_id')
        if (-not $refId) {
            Add-Violation 'adjudication' 'critical' `
                "Adjudication rules '$tid' test_wrong but names no 'ref_id'." `
                'Name the requirement the discarded test was covering. Without it the requirement loses its only test and nothing records the loss.'
        }
        elseif (-not $incorrectTestOrders.ContainsKey($refId)) {
            Add-Violation 'adjudication' 'critical' `
                "Adjudication rules '$tid' test_wrong but no required_tests entry commissions a replacement for '$refId'." `
                "Emit a required_tests entry with ref_id '$refId' and reason 'incorrect_test'. The Code agent cannot edit tests, so a ruling with no work order leaves it blocked and the requirement untested."
        }
    }
    if ($ruling -eq 'document_wrong' -and -not [string](Get-Prop $a 'route')) {
        Add-Violation 'adjudication' 'high' `
            "Adjudication rules '$tid' document_wrong but names no route for the correction." `
            "Set route to the owner of the fix (requirements | gentest). A document defect nobody is asked to fix recurs every round."
    }
}

if ($codeReport) {
    foreach ($ft in @(Get-ListProp $codeReport 'failing_tests')) {
        $tid = [string](Get-Prop $ft 'test_id')
        if (-not $adjudicated.ContainsKey($tid)) {
            Add-Violation 'adjudication' 'critical' `
                "The Code agent stopped on '$tid' but the report contains no ruling for it." `
                "The Code agent cannot edit tests and GenTest is never told otherwise, so an unruled conflict deadlocks the loop. Rule on it in pass 1d."
        }
    }
}

# --- 9. GenTest disputes must be ruled on ------------------------------------

if ($manifest) {
    $ruledDisputes = @{}
    foreach ($dr in $disputeRulings) {
        $rid = [string](Get-Prop $dr 'ref_id')
        if ($rid) { $ruledDisputes[$rid] = $dr }

        $ruling = [string](Get-Prop $dr 'ruling')
        if ($ruling -notin @('upheld', 'rejected')) {
            Add-Violation 'dispute_ruling' 'high' `
                "Dispute ruling for '$rid' has ruling '$ruling'." 'Use upheld | rejected.'
        }
        if (-not [string](Get-Prop $dr 'evidence')) {
            Add-Violation 'dispute_ruling' 'high' `
                "Dispute ruling for '$rid' cites no evidence." `
                'A ruling without the document text it was read against is an opinion, and GenTest will dispute it again.'
        }

        $route = [string](Get-Prop $dr 'route')
        if ($route -and $route -notin @('requirements', 'gentest', 'none')) {
            Add-Violation 'dispute_ruling' 'high' `
                "Dispute ruling for '$rid' has route '$route'." 'Use requirements | gentest | none.'
        }
    }

    # A rejected dispute means the work order stands -- so it must actually be
    # re-issued. Dropping it is how a real requirement quietly leaves the suite.
    $requiredRefs = @{}
    foreach ($rt in $requiredTests) {
        $rid = [string](Get-Prop $rt 'ref_id')
        if ($rid) { $requiredRefs[$rid] = $true }
    }

    foreach ($d in @(Get-ListProp $manifest 'disputes')) {
        $rid = [string](Get-Prop $d 'ref_id')
        if (-not $ruledDisputes.ContainsKey($rid)) {
            Add-Violation 'dispute_ruling' 'critical' `
                "GenTest disputed '$rid' but the report contains no ruling for it." `
                'An unruled dispute livelocks: the same work order is re-issued and re-disputed every round. Rule on it in pass 1e.'
            continue
        }
        if ([string](Get-Prop $ruledDisputes[$rid] 'ruling') -eq 'rejected' -and -not $requiredRefs.ContainsKey($rid)) {
            Add-Violation 'dispute_ruling' 'high' `
                "Dispute for '$rid' was rejected but no required_tests entry re-issues it." `
                'Rejecting a dispute means the work order stands; without a re-issued entry the requirement is silently dropped.'
        }
    }
}

# --- Result ------------------------------------------------------------------

$bySeverity = @{ critical = 0; high = 0 }
foreach ($v in $violations) { $bySeverity[$v.severity]++ }
$failed = $violations.Count -gt 0

$result = [pscustomobject]@{
    verdict = $(if ($failed) { 'invalid' } else { 'valid' })
    summary = [pscustomobject]@{
        violations = $violations.Count
        critical   = $bySeverity.critical
        high       = $bySeverity.high
        reported_coverage_level = $coverageLevel
        reported_verdict = $verdict
    }
    violations = $violations
}

if ($OutputPath) {
    $dir = Split-Path -Parent $OutputPath
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
}

Write-Host ""
Write-Host "Parity report validation: $($result.verdict.ToUpper())" -ForegroundColor $(if ($failed) { 'Red' } else { 'Green' })
Write-Host ("  verdict '{0}', coverage_level '{1}', {2} violation(s)" -f $verdict, $coverageLevel, $violations.Count)

foreach ($sev in 'critical', 'high') {
    $group = @($violations | Where-Object { $_.severity -eq $sev })
    if (-not $group.Count) { continue }
    Write-Host "`n  $($sev.ToUpper()) ($($group.Count)):" -ForegroundColor Red
    foreach ($v in $group) {
        Write-Host ("    [{0}] {1}" -f $v.check, $v.detail)
        Write-Host ("      -> {0}" -f $v.remediation) -ForegroundColor DarkGray
    }
}
Write-Host ""

if ($failed) { exit 1 } else { exit 0 }
