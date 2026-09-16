<#
.SYNOPSIS
  Self-test for tools/Check-ParityReport.ps1.

.DESCRIPTION
  Builds parity reports with known defects and asserts the validator catches
  each one, plus a clean report it must not flag. The validator exists to stop
  the verifier grading its own homework; a validator nobody tests has the same
  problem one level up.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$checker = Join-Path $here 'Check-ParityReport.ps1'
$workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("parityval-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $workRoot | Out-Null

$failures = 0
function Assert([string]$Label, [bool]$Condition, [string]$Detail = '') {
    if ($Condition) {
        Write-Host "  PASS  $Label" -ForegroundColor Green
    }
    else {
        Write-Host "  FAIL  $Label $Detail" -ForegroundColor Red
        $script:failures++
    }
}

function Invoke-Validator($Report, $CodeReport, $Manifest) {
    $dir = Join-Path $workRoot ([guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $reportPath = Join-Path $dir 'parity-report.json'
    $outPath = Join-Path $dir 'validation.json'
    $Report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding UTF8

    $codeArgs = @{}
    if ($CodeReport) {
        $codePath = Join-Path $dir 'code-report.json'
        $CodeReport | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $codePath -Encoding UTF8
        $codeArgs['CodeReportPath'] = $codePath
    }
    if ($Manifest) {
        $manifestPath = Join-Path $dir 'manifest.json'
        $Manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
        $codeArgs['ManifestPath'] = $manifestPath
    }

    & $checker -ReportPath $reportPath -OutputPath $outPath @codeArgs *> $null
    $exit = $LASTEXITCODE
    $result = Get-Content -LiteralPath $outPath -Raw -Encoding UTF8 | ConvertFrom-Json
    return [pscustomobject]@{ Exit = $exit; Result = $result }
}

function Has-Check($Result, [string]$Check) {
    return @($Result.violations | Where-Object { $_.check -eq $Check }).Count -gt 0
}

# A well-formed report: probed one requirement, killed the mutant, scoped the
# claim, and routed every finding.
function New-CleanReport {
    return [ordered]@{
        verdict = 'fail'
        coverage_level = 'proven'
        proven_scope = @('demo.add.b1')
        summary = [ordered]@{ gaps = 1; mismatches = 0 }
        gaps = @(
            [ordered]@{ ref_id = 'demo.add.b2'; kind = 'untested_feature'; severity = 'high' }
        )
        mismatches = @()
        mutation_probes = @(
            [ordered]@{
                ref_id = 'demo.add.b1'; outcome = 'killed'
                mutation = 'src/demo.rs:10 `+` -> `-`'
                command = 'cargo test add_works'
                baseline_output = 'test add_works ... ok'
                mutant_output = 'test add_works ... FAILED'
                killed_by = @('add_works')
            }
        )
        required_tests = @(
            [ordered]@{
                ref_id = 'demo.add.b2'; reason = 'missing'
                required_assertion = 'Assert that add(2,2) returns 4.'
            }
        )
        next_actions = @(
            [ordered]@{ agent = 'gentest'; instruction = 'Add a test for demo.add.b2.' }
        )
        adjudications = @()
    }
}

Write-Host "`nParity report validator self-test" -ForegroundColor Cyan

# --- Case 1: clean report passes ---------------------------------------------
$r = Invoke-Validator (New-CleanReport) $null
Assert "clean: exit code = 0" ($r.Exit -eq 0) "(got $($r.Exit); violations: $(($r.Result.violations | ForEach-Object { $_.detail }) -join ' | '))"
Assert "clean: verdict = valid" ($r.Result.verdict -eq 'valid')

# --- Case 2: inflated level, no killed probe ---------------------------------
$doc = New-CleanReport
$doc.mutation_probes = @()
$doc.proven_scope = @()
$r = Invoke-Validator $doc $null
Assert "no-probe: exit code = 1" ($r.Exit -eq 1)
Assert "no-probe: inflated_level raised" (Has-Check $r.Result 'inflated_level')

# --- Case 3: 'proven' while a mutation survived ------------------------------
$doc = New-CleanReport
$doc.mutation_probes += [ordered]@{ ref_id = 'demo.add.b3'; outcome = 'survived' }
$r = Invoke-Validator $doc $null
Assert "survived: inflated_level raised" (Has-Check $r.Result 'inflated_level')

# --- Case 4: killed probe with no evidence -----------------------------------
$doc = New-CleanReport
$doc.mutation_probes = @([ordered]@{ ref_id = 'demo.add.b1'; outcome = 'killed' })
$r = Invoke-Validator $doc $null
Assert "unevidenced: probe_evidence raised" (Has-Check $r.Result 'probe_evidence')

# --- Case 5: gap with no work order ------------------------------------------
$doc = New-CleanReport
$doc.required_tests = @()
$r = Invoke-Validator $doc $null
Assert "no-work-order: work_order raised" (Has-Check $r.Result 'work_order')

# --- Case 6: summary count disagrees with the array --------------------------
$doc = New-CleanReport
$doc.summary.gaps = 7
$r = Invoke-Validator $doc $null
Assert "bad-count: counts raised" (Has-Check $r.Result 'counts')

# --- Case 7: 'pass' while findings remain ------------------------------------
$doc = New-CleanReport
$doc.verdict = 'pass'
$doc.gaps = @([ordered]@{ ref_id = 'demo.add.b2'; kind = 'untested_feature'; severity = 'critical' })
$r = Invoke-Validator $doc $null
Assert "false-pass: verdict raised" (Has-Check $r.Result 'verdict')

# --- Case 8: unadjudicated Code agent escalation -----------------------------
# The deadlock case: Code cannot edit the test, GenTest is never told, and the
# same failure returns every round.
$codeReport = [ordered]@{
    verdict = 'partial'
    failing_tests = @(
        [ordered]@{
            test_id = 't_demo_add_b1'; error = 'assertion failed'
            test_expects = 'add(2,2) == 5'; document_says = 'demo.add.b1: returns the sum'
            csharp_does = 'Demo.cs#L10-L12 returns a + b'; suspect = 'test'
            analysis = 'The test contradicts both the document and the C# original.'
        }
    )
}
$r = Invoke-Validator (New-CleanReport) $codeReport
Assert "unadjudicated: exit code = 1" ($r.Exit -eq 1)
Assert "unadjudicated: adjudication raised" (Has-Check $r.Result 'adjudication')

# --- Case 9: escalation properly adjudicated ---------------------------------
$doc = New-CleanReport
$doc.adjudications = @(
    [ordered]@{
        test_id = 't_demo_add_b1'; ruling = 'test_wrong'; ref_id = 'demo.add.b1'
        evidence = 'Demo.cs#L10-L12 returns a + b; document demo.add.b1 agrees.'
    }
)
$doc.required_tests += [ordered]@{
    ref_id = 'demo.add.b1'; reason = 'incorrect_test'
    required_assertion = 'Correct t_demo_add_b1 to assert add(2,2) == 4.'
}
$r = Invoke-Validator $doc $codeReport
Assert "adjudicated: no adjudication violation" (-not (Has-Check $r.Result 'adjudication'))

# --- Case 9b: a ruling that discards the test without commissioning a fix -----
# `test_wrong` with no replacement order deletes the only test for a requirement
# and leaves the Code agent blocked on it, while the report still reads as
# resolved. The ruling must be checked, not merely counted.
$doc = New-CleanReport
$doc.adjudications = @(
    [ordered]@{ test_id = 't_demo_add_b1'; ruling = 'test_wrong' }
)
$r = Invoke-Validator $doc $codeReport
Assert "bare-ruling: exit code = 1" ($r.Exit -eq 1)
Assert "bare-ruling: adjudication raised" (Has-Check $r.Result 'adjudication')

# A ruling naming its requirement but commissioning nothing is still a dropped
# requirement, so the work order is checked separately from the ref_id.
$doc = New-CleanReport
$doc.adjudications = @(
    [ordered]@{
        test_id = 't_demo_add_b1'; ruling = 'test_wrong'; ref_id = 'demo.add.b1'
        evidence = 'Demo.cs#L10-L12 returns a + b.'
    }
)
$r = Invoke-Validator $doc $codeReport
Assert "uncommissioned-ruling: adjudication raised" (Has-Check $r.Result 'adjudication')

# --- Case 9c: 'parity-checked' without an executed differential pass ----------
# The level's entire meaning is that the two implementations were run against
# each other. A report that claims it while the pass is marked skipped is the
# most misleading state possible, so the claim is checked against the pass log.
$doc = New-CleanReport
$doc.coverage_level = 'parity-checked'
$doc.passes_run = [ordered]@{
    coverage = 'ran'; quality = 'ran'; differential = 'skipped: no crate supplied'
    mutation = 'ran'; semantic = 'ran'
}
$doc.golden_results = @(
    [ordered]@{ case_id = 'add_basic'; csharp = '4'; rust = '4'; status = 'match' }
)
$r = Invoke-Validator $doc
Assert "skipped-differential: exit code = 1" ($r.Exit -eq 1)
Assert "skipped-differential: inflated_level raised" (Has-Check $r.Result 'inflated_level')

# Omitting the results entirely must not read as "no failures found".
$doc = New-CleanReport
$doc.coverage_level = 'parity-checked'
$doc.passes_run = [ordered]@{ differential = 'ran' }
$r = Invoke-Validator $doc
Assert "absent-golden: inflated_level raised" (Has-Check $r.Result 'inflated_level')

# A result object missing the side it is meant to compare proves nothing; the
# array's length must not carry the claim on its own.
$doc = New-CleanReport
$doc.coverage_level = 'parity-checked'
$doc.passes_run = [ordered]@{ differential = 'ran' }
$doc.golden_results = @([ordered]@{ case_id = 'add_basic'; csharp = '4'; status = 'match' })
$r = Invoke-Validator $doc
Assert "incomplete-golden: inflated_level raised" (Has-Check $r.Result 'inflated_level')

# A mismatching case that never reaches `mismatches` is invisible to the verdict.
$doc = New-CleanReport
$doc.coverage_level = 'parity-checked'
$doc.passes_run = [ordered]@{ differential = 'ran' }
$doc.golden_results = @(
    [ordered]@{ case_id = 'add_basic'; csharp = '4'; rust = '5'; status = 'mismatch' }
)
$r = Invoke-Validator $doc
Assert "unreported-mismatch: inflated_level raised" (Has-Check $r.Result 'inflated_level')

# The complete, honest form of the same claim must pass.
$doc = New-CleanReport
$doc.coverage_level = 'parity-checked'
$doc.passes_run = [ordered]@{ differential = 'ran' }
$doc.golden_results = @(
    [ordered]@{ case_id = 'add_basic'; csharp = '4'; rust = '4'; status = 'match' }
)
$r = Invoke-Validator $doc
Assert "complete-golden: no inflated_level violation" (-not (Has-Check $r.Result 'inflated_level'))

# --- Case 10: illegal enum values --------------------------------------------
$doc = New-CleanReport
$doc.coverage_level = 'totally-proven'
$r = Invoke-Validator $doc $null
Assert "bad-enum: shape raised" (Has-Check $r.Result 'shape')

# --- Case 11: pass_with_warnings must not launder a blocking gap -------------
# Held to the same bar as 'pass': both tell the orchestrator it may stop.
$doc = New-CleanReport
$doc.verdict = 'pass_with_warnings'
$doc.gaps = @([ordered]@{ ref_id = 'demo.add.b2'; kind = 'untested_error'; severity = 'critical' })
$r = Invoke-Validator $doc $null
Assert "warn-launder: verdict raised" (Has-Check $r.Result 'verdict')

# --- Case 12: 'pass' with a high (not critical) gap --------------------------
$doc = New-CleanReport
$doc.verdict = 'pass'
$r = Invoke-Validator $doc $null
Assert "high-gap-pass: verdict raised" (Has-Check $r.Result 'verdict')

# --- Case 13: proven_scope must be backed by killed probes -------------------
# One killed probe must not license an arbitrarily wide claim.
$doc = New-CleanReport
$doc.proven_scope = @('demo.add.b1', 'demo.add.b9')
$r = Invoke-Validator $doc $null
Assert "fake-scope: inflated_level raised" (Has-Check $r.Result 'inflated_level')
Assert "fake-scope: names the unbacked ref" (
    @($r.Result.violations | Where-Object { $_.detail -like '*demo.add.b9*' }).Count -gt 0)

# --- Case 14: contract gap kinds are the ones enforced -----------------------
# untested_feature/untested_error must demand a work order; a kind the contract
# does not define must not be silently exempt from scrutiny.
foreach ($kind in 'untested_feature', 'untested_behavior', 'untested_error', 'weak_assertion', 'survived_mutation') {
    $doc = New-CleanReport
    $doc.gaps = @([ordered]@{ ref_id = 'demo.add.b2'; kind = $kind; severity = 'high' })
    $doc.required_tests = @()
    $r = Invoke-Validator $doc $null
    Assert "gap-kind '$kind': demands a work order" (Has-Check $r.Result 'work_order')
}

# --- Case 15: an unruled GenTest dispute livelocks the requirement -----------
$manifest = [ordered]@{
    disputes = @(
        [ordered]@{ ref_id = 'demo.add.b2'; position = 'undocumented'; evidence = 'No b2 in document.json.' }
    )
}
$doc = New-CleanReport
$r = Invoke-Validator $doc $null $manifest
Assert "unruled-dispute: exit code = 1" ($r.Exit -eq 1)
Assert "unruled-dispute: dispute_ruling raised" (Has-Check $r.Result 'dispute_ruling')
Assert "unruled-dispute: names the ref" (
    @($r.Result.violations | Where-Object { $_.detail -like '*demo.add.b2*' }).Count -gt 0)

# --- Case 16: a ruled dispute passes -----------------------------------------
# 'rejected' re-issues the order, and New-CleanReport already carries the
# matching required_tests entry for demo.add.b2.
$doc = New-CleanReport
$doc.dispute_rulings = @(
    [ordered]@{ ref_id = 'demo.add.b2'; ruling = 'rejected'
                evidence = 'document.json b2 states add(2,2)=4.'; route = 'gentest' }
)
$r = Invoke-Validator $doc $null $manifest
Assert "ruled-dispute: exit code = 0" ($r.Exit -eq 0) "(violations: $(($r.Result.violations | ForEach-Object { $_.detail }) -join ' | '))"

# --- Case 17: rejecting a dispute without re-issuing the order ---------------
# Rejection means the work order stands; dropping it deletes a real requirement.
$doc = New-CleanReport
$doc.gaps = @()
$doc.summary.gaps = 0
$doc.required_tests = @()
$doc.next_actions = @()
$doc.dispute_rulings = @(
    [ordered]@{ ref_id = 'demo.add.b2'; ruling = 'rejected'; evidence = 'b2 is documented.'; route = 'gentest' }
)
$r = Invoke-Validator $doc $null $manifest
Assert "dropped-order: dispute_ruling raised" (Has-Check $r.Result 'dispute_ruling')

# --- Case 18: a ruling with no evidence is an opinion ------------------------
$doc = New-CleanReport
$doc.dispute_rulings = @(
    [ordered]@{ ref_id = 'demo.add.b2'; ruling = 'rejected'; evidence = ''; route = 'gentest' }
)
$r = Invoke-Validator $doc $null $manifest
Assert "evidenceless-ruling: dispute_ruling raised" (Has-Check $r.Result 'dispute_ruling')

# --- Case 19: an illegal ruling value ----------------------------------------
$doc = New-CleanReport
$doc.dispute_rulings = @(
    [ordered]@{ ref_id = 'demo.add.b2'; ruling = 'noted'; evidence = 'b2 is documented.'; route = 'gentest' }
)
$r = Invoke-Validator $doc $null $manifest
Assert "bad-ruling: dispute_ruling raised" (Has-Check $r.Result 'dispute_ruling')

# --- Case 20: no manifest supplied means no dispute enforcement --------------
$r = Invoke-Validator (New-CleanReport) $null $null
Assert "no-manifest: exit code = 0" ($r.Exit -eq 0)

Remove-Item -Recurse -Force $workRoot -ErrorAction SilentlyContinue

Write-Host ""
if ($failures -eq 0) {
    Write-Host "Self-test PASSED - the validator catches inflated levels, unevidenced probes, missing work orders and unadjudicated escalations." -ForegroundColor Green
    exit 0
}
Write-Host "Self-test FAILED - $failures assertion(s) did not hold." -ForegroundColor Red
exit 1

