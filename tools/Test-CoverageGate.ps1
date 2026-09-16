<#
.SYNOPSIS
    Self-test for tools/Check-Coverage.ps1.

.DESCRIPTION
    A coverage gate that is itself untested is just a second thing to trust.
    This builds temporary fixtures and asserts the gate's exit code and findings
    for four scenarios:

      1. clean      - every requirement covered by a test that exists  -> exit 0
      2. dishonest  - missing, phantom, dangling and misclaimed        -> exit 1
      3. waived     - the only gap is waived                           -> exit 0,
                      and exit 1 under -FailOnWaived
      4. id-less    - document has an untraceable requirement          -> exit 2

    Run from anywhere: ./tools/Test-CoverageGate.ps1
#>
[CmdletBinding()]
param(
    [string]$DocumentPath = (Join-Path $PSScriptRoot 'testdata\calculator-document.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$gate = Join-Path $PSScriptRoot 'Check-Coverage.ps1'
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("covgate-" + [guid]::NewGuid().ToString('n').Substring(0, 8))
$failures = [System.Collections.Generic.List[string]]::new()

function New-Case([string]$Name) {
    $root = Join-Path $work $Name
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'tests\unit') | Out-Null
    return $root
}

function Get-RequirementIds($Document) {
    $ids = [System.Collections.Generic.List[string]]::new()
    foreach ($f in $Document.features) {
        $ids.Add($f.id)
        foreach ($kind in 'behaviors', 'errors', 'invariants', 'examples') {
            foreach ($e in $f.$kind) { if ($e -isnot [string]) { $ids.Add($e.id) } }
        }
    }
    return $ids
}

function Get-RequirementOwners($Document) {
    # Maps every requirement id to the feature that owns it. Deriving this by
    # slicing the id assumes a fixed id depth, which is a property of one
    # particular document rather than of the schema.
    $owners = @{}
    foreach ($f in $Document.features) {
        $owners[$f.id] = $f.id
        foreach ($kind in 'behaviors', 'errors', 'invariants', 'examples') {
            foreach ($e in $f.$kind) { if ($e -isnot [string]) { $owners[$e.id] = $f.id } }
        }
    }
    return $owners
}

function Invoke-Gate([string]$Root, [string]$Doc, [switch]$FailOnWaived) {
    $reportPath = Join-Path $Root 'coverage.json'
    $args = @{
        DocumentPath = $Doc
        ManifestPath = (Join-Path $Root 'tests\manifest.json')
        ReportPath   = $reportPath
    }
    if ($FailOnWaived) { $args['FailOnWaived'] = $true }

    & $gate @args *> (Join-Path $Root 'console.txt')
    $code = $LASTEXITCODE
    $report = if (Test-Path $reportPath) { Get-Content $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
    return [pscustomobject]@{ ExitCode = $code; Report = $report }
}

function Assert([string]$Case, [string]$What, $Expected, $Actual) {
    if ($Expected -eq $Actual) {
        Write-Host ("  PASS  {0}: {1} = {2}" -f $Case, $What, $Actual) -ForegroundColor Green
    }
    else {
        Write-Host ("  FAIL  {0}: {1} expected {2}, got {3}" -f $Case, $What, $Expected, $Actual) -ForegroundColor Red
        $failures.Add("$Case/$What")
    }
}

$document = Get-Content -LiteralPath $DocumentPath -Raw -Encoding UTF8 | ConvertFrom-Json
$allIds = Get-RequirementIds $document
$owners = Get-RequirementOwners $document
Write-Host "Coverage gate self-test ($($allIds.Count) requirements in $(Split-Path -Leaf $DocumentPath))`n"

# --- 1. clean ----------------------------------------------------------------

$root = New-Case 'clean'
$fns = @()
$tests = @()
$i = 0
foreach ($id in $allIds) {
    $i++
    $fn = "covers_req_$i"
    $fns += "#[test]`nfn $fn() { assert_eq!(1, 1); }`n"
    $tests += [pscustomobject]@{
        test_id = "t_$i"; feature_id = $owners[$id]
        covers = @($id); tier = 'unit'; assertion_kind = 'value'
        file = 'tests/unit/all.rs'; test_fn = $fn
        rationale = 'self-test'; status = 'expected_fail_until_implemented'
    }
}
Set-Content -LiteralPath (Join-Path $root 'tests\unit\all.rs') -Value ($fns -join "`n") -Encoding UTF8
[pscustomobject]@{
    run_id = 'selftest-clean'; generated_from = 'selftest'; tests = $tests
    coverage_claim = [pscustomobject]@{
        requirements_total = $allIds.Count; requirements_covered = $allIds.Count; waived = @()
    }
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'tests\manifest.json') -Encoding UTF8

$r = Invoke-Gate $root $DocumentPath
Assert 'clean' 'exit code' 0 $r.ExitCode
Assert 'clean' 'verdict' 'pass' $r.Report.verdict
Assert 'clean' 'covered' $allIds.Count $r.Report.summary.requirements_covered

# --- 1b. a function cargo never runs is not coverage --------------------------
# The gate used to regex the raw file for the function name: a plain `fn`, a
# #[cfg(any())] test and a name written in a comment all satisfied it. Discovery
# is now shared with the quality gate, which requires a live #[test].
$cleanRust = Get-Content -LiteralPath (Join-Path $root 'tests\unit\all.rs') -Raw -Encoding UTF8
$degraded = $cleanRust `
    -replace "#\[test\]\r?\nfn covers_req_1\(\)", "fn covers_req_1()" `
    -replace "#\[test\]\r?\nfn covers_req_2\(\)", "#[cfg(any())]`n#[test]`nfn covers_req_2()" `
    -replace "#\[test\]\r?\nfn covers_req_3\(\)", "// fn covers_req_3() lives in a comment`n#[test]`nfn other_name_3()"
Set-Content -LiteralPath (Join-Path $root 'tests\unit\all.rs') -Value $degraded -Encoding UTF8

$r = Invoke-Gate $root $DocumentPath
Assert 'unrunnable' 'exit code' 1 $r.ExitCode
Assert 'unrunnable' 'phantom' 3 $r.Report.summary.phantom
Assert 'unrunnable' 'reason names the registration defect' $true `
    (@($r.Report.phantom | Where-Object { $_.reason -match 'no #\[test\]|always false' }).Count -eq 2)

Set-Content -LiteralPath (Join-Path $root 'tests\unit\all.rs') -Value $cleanRust -Encoding UTF8

# --- 2. dishonest ------------------------------------------------------------
# Drop the last requirement (MISSING), point one test at a nonexistent fn
# (PHANTOM), add a typo'd id (DANGLING), and overstate the claim (MISCLAIM).

$root = New-Case 'dishonest'
Copy-Item (Join-Path $work 'clean\tests\unit\all.rs') (Join-Path $root 'tests\unit\all.rs')
$bad = $tests | Select-Object -SkipLast 1 | ForEach-Object { $_.PSObject.Copy() }
$bad[0] = $bad[0].PSObject.Copy(); $bad[0].test_fn = 'no_such_function'
$bad += [pscustomobject]@{
    test_id = 't_typo'; feature_id = 'x'; covers = @('not.a.real.requirement')
    tier = 'unit'; assertion_kind = 'value'; file = 'tests/unit/all.rs'
    test_fn = 'covers_req_2'; rationale = 'self-test'; status = 'expected_fail_until_implemented'
}
[pscustomobject]@{
    run_id = 'selftest-dishonest'; generated_from = 'selftest'; tests = $bad
    coverage_claim = [pscustomobject]@{
        requirements_total = $allIds.Count; requirements_covered = $allIds.Count; waived = @()
    }
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'tests\manifest.json') -Encoding UTF8

$r = Invoke-Gate $root $DocumentPath
Assert 'dishonest' 'exit code' 1 $r.ExitCode
# Only the dropped requirement is missing. The phantom'd test covered a bare
# feature id, and a feature is credited when its child requirements are covered
# -- the phantom is still reported separately as critical, so nothing is hidden.
Assert 'dishonest' 'missing' 1 $r.Report.summary.missing
Assert 'dishonest' 'phantom' 1 $r.Report.summary.phantom
# Two: the typo'd `covers` id and the same test's bogus feature_id 'x'. An
# unknown feature_id used to be dropped in silence, which reported the real
# feature as missing with no explanation.
Assert 'dishonest' 'dangling' 2 $r.Report.summary.dangling
Assert 'dishonest' 'dangling names the covers typo' $true `
    (@($r.Report.dangling | Where-Object { $_.field -eq 'covers' -and $_.ref_id -eq 'not.a.real.requirement' }).Count -eq 1)
Assert 'dishonest' 'dangling names the feature_id typo' $true `
    (@($r.Report.dangling | Where-Object { $_.field -eq 'feature_id' -and $_.ref_id -eq 'x' }).Count -eq 1)
Assert 'dishonest' 'misclaim detected' $true ($null -ne $r.Report.misclaim)

# Every requirement the phantom test claimed must come back as a work order
# keyed by REQUIREMENT id, not by test id -- the parity validator matches on it.
$phantomOrders = @($r.Report.required_tests | Where-Object { $_.reason -eq 'phantom' })
Assert 'dishonest' 'phantom work orders emitted' $true ($phantomOrders.Count -ge 1)
$testIds = @($bad | ForEach-Object { $_.test_id })
Assert 'dishonest' 'phantom order ref_id is a requirement id' $true `
    (@($phantomOrders | Where-Object { $_.ref_id -in $testIds }).Count -eq 0)

# --- 3. waived ---------------------------------------------------------------

$root = New-Case 'waived'
Copy-Item (Join-Path $work 'clean\tests\unit\all.rs') (Join-Path $root 'tests\unit\all.rs')
$dropped = $allIds[$allIds.Count - 1]
$partial = $tests | Select-Object -SkipLast 1
[pscustomobject]@{
    run_id = 'selftest-waived'; generated_from = 'selftest'; tests = $partial
    coverage_claim = [pscustomobject]@{
        requirements_total   = $allIds.Count
        requirements_covered = $allIds.Count - 1
        waived = @([pscustomobject]@{ ref_id = $dropped; reason = 'self-test waiver'; waived_by = 'selftest' })
    }
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'tests\manifest.json') -Encoding UTF8

$r = Invoke-Gate $root $DocumentPath
Assert 'waived' 'exit code' 0 $r.ExitCode
Assert 'waived' 'waived count' 1 $r.Report.summary.waived
Assert 'waived' 'missing' 0 $r.Report.summary.missing

$r = Invoke-Gate $root $DocumentPath -FailOnWaived
Assert 'waived+strict' 'exit code' 1 $r.ExitCode

# --- 4. id-less document -----------------------------------------------------

$root = New-Case 'idless'
Copy-Item (Join-Path $work 'clean\tests\unit\all.rs') (Join-Path $root 'tests\unit\all.rs')
Copy-Item (Join-Path $work 'clean\tests\manifest.json') (Join-Path $root 'tests\manifest.json')
$badDoc = Get-Content -LiteralPath $DocumentPath -Raw -Encoding UTF8 | ConvertFrom-Json
$badDoc.features[0].invariants = @('an invariant with no id, therefore untraceable')
$badDocPath = Join-Path $root 'document.json'
$badDoc | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $badDocPath -Encoding UTF8

$r = Invoke-Gate $root $badDocPath
Assert 'idless' 'exit code' 2 $r.ExitCode

# --- 4b. duplicate ids -------------------------------------------------------
# A duplicate id used to overwrite the earlier entry, shrinking the denominator.
# That converts an uncovered requirement into a silent pass, so it must be a
# hard schema failure like any other untraceable requirement.

$root = New-Case 'dupe-ids'
Copy-Item (Join-Path $work 'clean\tests\unit\all.rs') (Join-Path $root 'tests\unit\all.rs')
Copy-Item (Join-Path $work 'clean\tests\manifest.json') (Join-Path $root 'tests\manifest.json')
$dupeDoc = Get-Content -LiteralPath $DocumentPath -Raw -Encoding UTF8 | ConvertFrom-Json
$firstBehavior = $dupeDoc.features[0].behaviors[0]
$clone = $firstBehavior.PSObject.Copy()
$dupeDoc.features[0].behaviors = @($dupeDoc.features[0].behaviors) + @($clone)
$dupePath = Join-Path $root 'document.json'
$dupeDoc | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $dupePath -Encoding UTF8

$r = Invoke-Gate $root $dupePath
Assert 'dupe-ids' 'exit code' 2 $r.ExitCode

# --- 4c. a document of the wrong shape ---------------------------------------
# Zero requirements divided by zero requirements scored as 100%, so a file using
# a top-level `requirements` key earned the gate's strongest verdict against an
# empty manifest -- a full pass produced by reading nothing at all. Unreadable
# input must fail as input, before any percentage exists.

$root = New-Case 'wrong-schema'
'{ "run_id": "r", "tests": [], "coverage_claim": { "waived": [] } }' |
    Set-Content -LiteralPath (Join-Path $root 'tests\manifest.json') -Encoding UTF8
$wrongPath = Join-Path $root 'document.json'
'{ "source": { "language": "csharp" }, "requirements": [ { "id": "a.b1", "statement": "x" } ] }' |
    Set-Content -LiteralPath $wrongPath -Encoding UTF8

$r = Invoke-Gate $root $wrongPath
Assert 'wrong-schema' 'exit code' 2 $r.ExitCode

# An empty features array is the same defect wearing the right key.
$root = New-Case 'empty-features'
'{ "run_id": "r", "tests": [], "coverage_claim": { "waived": [] } }' |
    Set-Content -LiteralPath (Join-Path $root 'tests\manifest.json') -Encoding UTF8
$emptyPath = Join-Path $root 'document.json'
'{ "features": [] }' | Set-Content -LiteralPath $emptyPath -Encoding UTF8

$r = Invoke-Gate $root $emptyPath
Assert 'empty-features' 'exit code' 2 $r.ExitCode

# --- 5. schema-shaped manifest -----------------------------------------------
# The regression case for the bug this gate shipped with: docs/contracts.md puts
# the feature id in `feature_id` and only child ids in `covers`. The gate used to
# credit coverage from `covers` alone, so every schema-correct manifest reported
# each of its features as MISSING/critical. The other cases hid it by stuffing
# feature ids into `covers`, which no real GenTest output does.

$root = New-Case 'schema-shaped'
$childIds = @($allIds | Where-Object { $_ -match '\.[bexi]\d+$' })
$fns = @()
$tests = @()
$i = 0
foreach ($id in $childIds) {
    $i++
    $fn = "schema_req_$i"
    $fns += "#[test]`nfn $fn() { assert_eq!(1, 1); }`n"
    $tests += [pscustomobject]@{
        test_id = "s_$i"; feature_id = ($id -replace '\.[bexi]\d+$', '')
        covers = @($id); tier = 'unit'; assertion_kind = 'value'
        file = 'tests/unit/all.rs'; test_fn = $fn
        rationale = 'self-test'; status = 'expected_fail_until_implemented'
    }
}
Set-Content -LiteralPath (Join-Path $root 'tests\unit\all.rs') -Value ($fns -join "`n") -Encoding UTF8
[pscustomobject]@{
    run_id = 'selftest-schema'; generated_from = 'selftest'; tests = $tests
    coverage_claim = [pscustomobject]@{
        requirements_total = $allIds.Count; requirements_covered = $allIds.Count; waived = @()
    }
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'tests\manifest.json') -Encoding UTF8

$r = Invoke-Gate $root $DocumentPath
Assert 'schema-shaped' 'exit code' 0 $r.ExitCode
Assert 'schema-shaped' 'missing' 0 $r.Report.summary.missing
Assert 'schema-shaped' 'covered' $allIds.Count $r.Report.summary.requirements_covered

# --- 6. generic and pub(crate) test functions --------------------------------
# `fn foo<T>()` and `pub(crate) fn foo()` are legal Rust. The fn regex used to
# miss both and report a real test as PHANTOM, which the gate itself calls worse
# than a missing test.

$root = New-Case 'exotic-fns'
$exotic = @(
    "#[test]`nfn generic_test<T: Default>() { assert_eq!(1, 1); }`n",
    "#[test]`npub(crate) fn scoped_test() { assert_eq!(1, 1); }`n"
) -join "`n"
Set-Content -LiteralPath (Join-Path $root 'tests\unit\all.rs') -Value $exotic -Encoding UTF8
$twoIds = @($childIds | Select-Object -First 2)
[pscustomobject]@{
    run_id = 'selftest-exotic'; generated_from = 'selftest'
    tests = @(
        [pscustomobject]@{
            test_id = 'e_1'; feature_id = ($twoIds[0] -replace '\.[bexi]\d+$', '')
            covers = @($twoIds[0]); tier = 'unit'; assertion_kind = 'value'
            file = 'tests/unit/all.rs'; test_fn = 'generic_test'
            rationale = 'self-test'; status = 'expected_fail_until_implemented'
        },
        [pscustomobject]@{
            test_id = 'e_2'; feature_id = ($twoIds[1] -replace '\.[bexi]\d+$', '')
            covers = @($twoIds[1]); tier = 'unit'; assertion_kind = 'value'
            file = 'tests/unit/all.rs'; test_fn = 'scoped_test'
            rationale = 'self-test'; status = 'expected_fail_until_implemented'
        }
    )
    coverage_claim = [pscustomobject]@{ requirements_total = $allIds.Count; requirements_covered = 2; waived = @() }
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'tests\manifest.json') -Encoding UTF8

$r = Invoke-Gate $root $DocumentPath
Assert 'exotic-fns' 'phantom' 0 $r.Report.summary.phantom

# --- 7. tool-computed work order ---------------------------------------------
# The feedback that drives the next GenTest round must be produced by this gate,
# not by a reviewer agent. Every uncovered requirement must arrive with a
# required_assertion stating what the missing test has to assert -- otherwise a
# stalled loop is indistinguishable from a reviewer having an off round.
# The exotic-fns case covers 2 of the document's requirements, so it is the widest
# work order available and also exercises entries whose JSON values are null
# (ConvertFrom-Json yields [DBNull], which is not $null).

$required = @($r.Report.required_tests)
Assert 'work-order' 'required_tests emitted' $true ($required.Count -gt 0)
Assert 'work-order' 'count matches summary' $required.Count $r.Report.summary.required_tests

$blank = @($required | Where-Object {
    -not $_.required_assertion -or -not $_.ref_id -or -not $_.reason
})
Assert 'work-order' 'entries without a prescription' 0 $blank.Count

$missingHaveOrders = @($r.Report.missing | Where-Object {
    $_.ref_id -notin $required.ref_id
})
Assert 'work-order' 'uncovered requirements with no order' 0 $missingHaveOrders.Count

$placeholder = @($required | Where-Object { $_.required_assertion -match '^\s*(TODO|TBD)' })
Assert 'work-order' 'placeholder prescriptions' 0 $placeholder.Count

# --- summary -----------------------------------------------------------------

Write-Host ""
if ($failures.Count) {
    Write-Host "Self-test FAILED ($($failures.Count)): $($failures -join ', ')" -ForegroundColor Red
    Write-Host "Fixtures left at $work for inspection." -ForegroundColor Yellow
    exit 1
}
Write-Host "Self-test PASSED - the gate detects missing, phantom, dangling, misclaimed and untraceable requirements." -ForegroundColor Green
Remove-Item -Recurse -Force $work
exit 0
