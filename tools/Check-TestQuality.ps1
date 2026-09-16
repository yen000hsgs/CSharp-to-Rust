<#
.SYNOPSIS
    Test quality gate: proves the tests that claim coverage actually assert something.

.DESCRIPTION
    The coverage gate (Check-Coverage.ps1) proves a test *exists* for every
    requirement. It cannot prove the test is any good -- `assert!(true)` counts.
    Since GenTest is best effort, the parity verifier needs a deterministic way
    to reject nominal coverage.

    This analyses each test body named in the manifest and reports:

      unimplemented_test    - body is empty, or only todo!()/unimplemented!()
      no_assertion          - no assertion macro anywhere in the body
      tautological_assertion- assert!(true), assert_eq!(1, 1), assert_eq!(x, x)
      unchecked_error_path  - covers an error requirement but never asserts an error
      unbound_oracle        - covers a documented example but the expected value
                              never appears in the test body
      smoke_only            - only presence checks (is_ok/is_some), no comparison

    These are all decidable from source, so they are findings, not opinions.
    What is left over -- a test that asserts a plausible but wrong value -- is
    the parity verifier's pass 2 (differential execution) and pass 3 (semantic
    review) problem.

.PARAMETER DocumentPath
    Path to document.json. Supplies the expected values used for oracle binding.

.PARAMETER ManifestPath
    Path to tests/manifest.json.

.PARAMETER TestsRoot
    Root the manifest's `file` values are relative to. Defaults to the parent of
    the manifest's directory (the run root).

.PARAMETER ReportPath
    Optional path to write the JSON findings report.

.OUTPUTS
    Exit 0 when no findings, 1 when findings exist, 2 on a usage error.

.EXAMPLE
    ./tools/Check-TestQuality.ps1 -DocumentPath artifacts/run1/document.json `
                                  -ManifestPath artifacts/run1/tests/manifest.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DocumentPath,
    [Parameter(Mandatory)][string]$ManifestPath,
    [string]$TestsRoot,
    [string]$ReportPath
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

# Discovery and lexing are shared with Check-Coverage.ps1 so the two gates agree
# on what counts as a real, registered, enabled test.
. (Join-Path $PSScriptRoot 'RustLex.ps1')

# Extract the argument text of every assertion macro call in a body. Macros are
# located in the scan body (so `"assert_eq!(1, 1)"` inside a string is not one),
# but the arguments are sliced out of the code body so real string literals
# survive for the oracle check. The two bodies are index-identical.
function Get-Assertions([string]$ScanBody, [string]$CodeBody) {
    $result = [System.Collections.Generic.List[object]]::new()
    $pattern = '\b(debug_assert_eq|debug_assert_ne|debug_assert|assert_matches|assert_eq|assert_ne|assert)\s*!\s*\('
    foreach ($m in [regex]::Matches($ScanBody, $pattern)) {
        $open = $ScanBody.IndexOf('(', $m.Index + $m.Length - 1)
        $span = Get-BalancedSpan $ScanBody $open '(' ')'
        $argText = if ($span) { $CodeBody.Substring($span.Start + 1, $span.End - $span.Start - 1) } else { '' }
        $result.Add([pscustomobject]@{ Macro = $m.Groups[1].Value; Args = $argText.Trim() })
    }
    return $result
}

# Split macro arguments on top-level commas only.
function Split-Args([string]$ArgText) {
    $parts = [System.Collections.Generic.List[string]]::new()
    $depth = 0; $start = 0; $i = 0
    while ($i -lt $ArgText.Length) {
        $c = $ArgText[$i]
        if ($c -eq '"') {
            $i++
            while ($i -lt $ArgText.Length) {
                if ($ArgText[$i] -eq '\') { $i += 2; continue }
                if ($ArgText[$i] -eq '"') { break }
                $i++
            }
        }
        elseif ($c -in '(', '[', '{') { $depth++ }
        elseif ($c -in ')', ']', '}') { $depth-- }
        elseif ($c -eq ',' -and $depth -eq 0) {
            $parts.Add($ArgText.Substring($start, $i - $start).Trim())
            $start = $i + 1
        }
        $i++
    }
    if ($start -lt $ArgText.Length) { $parts.Add($ArgText.Substring($start).Trim()) }
    return $parts
}

# Normalise a value for comparison. Whitespace, quotes and separators vary
# freely between JSON and Rust source, so they are removed -- but case, sign and
# decimal point are meaning-bearing and are kept, or "-1" would match "1".
function ConvertTo-Comparable([string]$Value) {
    return ($Value -replace '[^A-Za-z0-9\.\-]', '')
}

# Flatten a documented `expected` value to its scalar leaves.
function Get-ScalarLeaves($Value) {
    $out = [System.Collections.Generic.List[string]]::new()
    if ($null -eq $Value) { return $out }
    if ($Value -is [string] -or $Value -is [bool] -or $Value -is [int] -or
        $Value -is [long] -or $Value -is [double] -or $Value -is [decimal]) {
        $out.Add([string]$Value); return $out
    }
    if ($Value -is [System.Collections.IEnumerable]) {
        foreach ($v in $Value) { $out.AddRange([string[]]@(Get-ScalarLeaves $v)) }
        return $out
    }
    foreach ($p in $Value.PSObject.Properties) { $out.AddRange([string[]]@(Get-ScalarLeaves $p.Value)) }
    return $out
}

# --- Load inputs -------------------------------------------------------------

# A document whose shape is wrong yields zero requirements, so every test in the
# manifest maps to an unknown id and the gate has nothing to check. That is not a
# clean bill of health -- it is unreadable input, and it must fail as such.
function Assert-DocumentShape($Document, [string]$Path) {
    $raw = Get-Prop $Document 'features'
    # @($null) is an array of one $null, not an empty array -- the same trap the
    # gate is being hardened against, so the nulls are filtered explicitly.
    $features = @()
    if ($null -ne $raw -and $raw -isnot [System.DBNull]) {
        $features = @(@($raw) | Where-Object { $null -ne $_ -and $_ -isnot [System.DBNull] })
    }
    if ($features.Count -eq 0) {
        Write-Host ("ERROR: Document '{0}' declares no requirements: 'features' is missing or empty." -f $Path) -ForegroundColor Red
        if ($Document -is [System.Management.Automation.PSCustomObject]) {
            $present = @($Document.PSObject.Properties.Name)
            if ($present.Count -gt 0) {
                Write-Host ("  Top-level keys present: {0}" -f ($present -join ', ')) -ForegroundColor Red
            }
        }
        Write-Host "  Expected shape: { features: [ { id, behaviors, errors, invariants, examples } ] }." -ForegroundColor Red
        exit 2
    }
    foreach ($f in $features) {
        if ($null -eq $f -or $f -is [string] -or $f -is [System.ValueType]) {
            Write-Host ("ERROR: Document '{0}' has a non-object entry in 'features'; each feature must be an object with an 'id'." -f $Path) -ForegroundColor Red
            exit 2
        }
    }
}

$document = Read-Json $DocumentPath 'Document'
$manifest = Read-Json $ManifestPath 'Manifest'
Assert-DocumentShape $document $DocumentPath

if (-not $TestsRoot) {
    $TestsRoot = Split-Path -Parent (Split-Path -Parent (Resolve-Path -LiteralPath $ManifestPath))
}

$kindOf = @{}       # ref_id -> feature | behavior | error | invariant | example
$expectedOf = @{}   # example ref_id -> expected value
foreach ($feature in (Get-Prop $document 'features')) {
    $kindOf[[string](Get-Prop $feature 'id')] = 'feature'
    foreach ($kind in 'behaviors', 'errors', 'invariants', 'examples') {
        foreach ($entry in (Get-Prop $feature $kind)) {
            if ($entry -is [string]) { continue }
            $refId = [string](Get-Prop $entry 'id')
            if (-not $refId) { continue }
            $kindOf[$refId] = $kind.TrimEnd('s')
            if ($kind -eq 'examples') { $expectedOf[$refId] = (Get-Prop $entry 'expected') }
        }
    }
}

# --- Analyse -----------------------------------------------------------------

$findings = [System.Collections.Generic.List[object]]::new()
$analysed = 0
$notAnalysed = 0
$notAnalysedTests = [System.Collections.Generic.List[object]]::new()
$fileCache = @{}

# Resolve a manifest-declared test path against the tests root. An absolute path
# must NOT be re-rooted, and nothing may escape the root via `..`.
function Resolve-TestPath([string]$Root, [string]$File) {
    if (-not $File) { return $null }
    $candidate = if ([System.IO.Path]::IsPathRooted($File)) { $File } else { Join-Path $Root $File }
    try {
        $full = [System.IO.Path]::GetFullPath($candidate)
        $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        if (-not $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { return $null }
        return $full
    }
    catch { return $null }
}

function Add-NotAnalysed([string]$TestId, [string]$File, [string]$Reason) {
    $script:notAnalysed++
    $notAnalysedTests.Add([pscustomobject]@{ test_id = $TestId; file = $File; reason = $Reason })
}

function Add-Finding([string]$TestId, [string]$File, $Line, [string]$Kind, [string]$Severity, [string]$Detail, $Refs, [string]$Remediation) {
    if (-not $Remediation) { $Remediation = $script:remediationByKind[$Kind] }
    $findings.Add([pscustomobject]@{
        test_id = $TestId; file = $File; line = $Line
        finding = $Kind; severity = $Severity; detail = $Detail
        covers = @($Refs)
        remediation = $Remediation
    })
}

# A diagnosis the agent has to interpret is a weaker feedback signal than an
# instruction it can execute. Every finding carries the fix, generated here so
# the loop does not depend on a reviewing agent re-deriving it each round.
$script:remediationByKind = @{
    unimplemented_test     = 'Replace the placeholder body with a real test, or delete the entry and waive the requirement with a reason. An empty test inflates coverage while proving nothing.'
    no_assertion           = 'Add an assertion comparing an actual value against an expected one. A body that only calls the API passes whenever it does not panic.'
    tautological_assertion = 'Replace the constant assertion with one over a value the code under test produced. As written it holds no matter what the implementation does.'
    unchecked_error_path   = 'Assert the specific error: match the expected variant (assert!(matches!(r, Err(E::Variant)))) rather than asserting the call merely failed.'
    unbound_oracle         = 'Assert against the documented expected value listed in the detail. The test currently never mentions it, so it cannot be checking it.'
    smoke_only             = 'Compare the returned value, not just its presence. Replace assert!(x.is_ok()) with assert_eq! on the unwrapped value.'
}

foreach ($test in (Get-Prop $manifest 'tests')) {
    $testId = [string](Get-Prop $test 'test_id')
    $file = [string](Get-Prop $test 'file')
    $fn = [string](Get-Prop $test 'test_fn')
    $covers = @(Get-Prop $test 'covers')

    $resolved = Resolve-TestPath $TestsRoot $file
    if (-not $resolved -or -not (Test-Path -LiteralPath $resolved)) {
        Add-NotAnalysed $testId $file 'file not found, or path escapes the tests root'
        continue
    }
    if (-not $fileCache.ContainsKey($resolved)) {
        $raw = Get-Content -LiteralPath $resolved -Raw -Encoding UTF8
        $fileCache[$resolved] = [pscustomobject]@{
            Code = ConvertTo-MaskedRust $raw
            Scan = ConvertTo-MaskedRust $raw -MaskStrings
        }
    }
    $cached = $fileCache[$resolved]
    $src = Get-TestSource $cached.Scan $cached.Code $fn
    # A function that isn't there is phantom coverage; Check-Coverage.ps1 owns that.
    if (-not $src) {
        Add-NotAnalysed $testId $file "function '$fn' not found in file (phantom; see the coverage gate)"
        continue
    }
    # The function is there but this gate's Rust scanner could not delimit its
    # body. Say so instead of guessing: an unanalysed test is an UNKNOWN, and an
    # unknown must never be promoted to `substantive`.
    if ($src.Unparsable) {
        Add-NotAnalysed $testId $file "function '$fn' found but its body could not be parsed by this scanner"
        continue
    }

    # A function cargo never runs is not evidence of anything. This used to be
    # invisible: a bare `fn` or a #[cfg(any())] test earned full coverage and
    # `substantive_eligible: true` without ever executing.
    $reg = Get-TestRegistration $src.Attributes
    if ($reg.State -eq 'unregistered') {
        Add-Finding $testId $file $src.Line 'unregistered_test' 'critical' `
            "'$fn' is listed as a test but $($reg.Reason)." `
            $covers `
            "Add #[test] (inside a #[cfg(test)] mod for a unit test), or remove the manifest entry. Until then every requirement it claims is untested."
        Add-NotAnalysed $testId $file $reg.Reason
        continue
    }
    if ($reg.State -eq 'disabled') {
        Add-Finding $testId $file $src.Line 'disabled_test' 'critical' `
            "'$fn' is listed as a test but $($reg.Reason)." `
            $covers `
            'Enable the test, or remove the manifest entry and record the requirement as uncovered. A test that does not run proves nothing.'
        Add-NotAnalysed $testId $file $reg.Reason
        continue
    }
    if ($reg.State -eq 'unknown') {
        Add-NotAnalysed $testId $file $reg.Reason
        continue
    }

    $analysed++
    $body = $src.Body
    # Comments are already blanked by the mask; `$stripped` keeps its old name so
    # the checks below read the same, but it is now lexically correct rather than
    # a regex strip that also ate "http://" inside string literals.
    $stripped = $src.ScanBody
    $compact = $stripped.Trim()

    # 1. Unimplemented. Match todo!/unimplemented! with or without a message --
    # `todo!("wire this up later")` is exactly as inert as `todo!()`.
    if ($compact -eq '' -or $compact -match '(?s)^\s*(todo!|unimplemented!)\s*\([^()]*\)\s*;?\s*$') {
        Add-Finding $testId $file $src.Line 'unimplemented_test' 'critical' `
            'Test body is empty or only todo!()/unimplemented!(); it can never fail.' $covers
        continue
    }

    $assertions = @(Get-Assertions $src.ScanBody $src.Body)
    $shouldPanic = $src.Attributes -match '#\[\s*should_panic'

    # 2. No assertion at all. `matches!` is deliberately NOT an escape hatch: it
    # is an expression returning bool, so `matches!(r, Ok(_));` on its own never
    # panics and proves nothing. Only `assert!(matches!(..))` counts, and that is
    # already captured by Get-Assertions. `unwrap_err`/`expect_err` do stay --
    # they panic on the wrong variant, so they are a real failure mechanism.
    if ($assertions.Count -eq 0 -and -not $shouldPanic -and
        $stripped -notmatch '\.unwrap_err\s*\(|\.expect_err\s*\(') {
        Add-Finding $testId $file $src.Line 'no_assertion' 'critical' `
            'Test body contains no assertion; it passes as long as nothing panics.' $covers
        continue
    }

    # 3. Tautologies -- assertions that cannot fail.
    foreach ($a in $assertions) {
        $parts = @(Split-Args $a.Args)
        $isTautology = $false
        if (($a.Macro -in @('assert', 'debug_assert')) -and $parts.Count -ge 1 -and $parts[0].Trim() -eq 'true') {
            $isTautology = $true
        }
        if (($a.Macro -in @('assert_eq', 'debug_assert_eq')) -and $parts.Count -ge 2 -and $parts[0].Trim() -eq $parts[1].Trim()) {
            $isTautology = $true
        }
        if ($isTautology) {
            Add-Finding $testId $file $src.Line 'tautological_assertion' 'critical' `
                ("Assertion cannot fail: {0}!({1})" -f $a.Macro, $a.Args) $covers
        }
    }

    # 4. Error requirements must actually assert an error.
    # The check has to look inside assertion arguments. Searching the whole body
    # for `Err(` also matches a helper that merely constructs one, so a test that
    # asserts *success* on an error requirement used to pass.
    $errorRefs = @($covers | Where-Object { $kindOf[[string]$_] -eq 'error' })

    # An example whose documented outcome is an error is an error requirement
    # too. Keying only on `kind` let `assert_eq!(result, Ok(5))` satisfy an
    # example documenting OverflowException: check 5 skips error examples, so
    # nothing at all checked that the call failed.
    $errorNames = [System.Collections.Generic.List[string]]::new()
    foreach ($ref in @($covers | Where-Object { $kindOf[[string]$_] -eq 'example' })) {
        if (-not $expectedOf.ContainsKey([string]$ref)) { continue }
        $err = Get-Prop $expectedOf[[string]$ref] 'error'
        if (-not $err) { continue }
        $errorRefs += $ref
        $named = if ($err -is [string]) { $err } else { [string](Get-Prop $err 'type') }
        if ($named) { $errorNames.Add($named) }
    }
    $errorRefs = @($errorRefs | Select-Object -Unique)

    if ($errorRefs.Count -gt 0) {
        $errorPattern = '\.is_err\s*\(|\.unwrap_err\s*\(|\.expect_err\s*\(|\bErr\s*(\(|::)'
        $assertsError = @($assertions | Where-Object { $_.Args -match $errorPattern }).Count -gt 0
        # A test driving the port through the differential harness sees JSON, not
        # a Result, so it asserts the documented error *name*. That is a genuine
        # error assertion and must not be reported as a missing one.
        if (-not $assertsError -and $errorNames.Count -gt 0) {
            $argsJoined = ($assertions | ForEach-Object { $_.Args }) -join ' | '
            $assertsError = @($errorNames | Where-Object { $argsJoined.Contains($_) }).Count -gt 0
        }
        # Outside an assertion these still panic on the wrong variant.
        $panicsOnOk = $stripped -match '\.unwrap_err\s*\(|\.expect_err\s*\('
        $checksError = $shouldPanic -or $assertsError -or $panicsOnOk
        if (-not $checksError) {
            Add-Finding $testId $file $src.Line 'unchecked_error_path' 'high' `
                'Covers an error requirement but never asserts an error result or panic.' $errorRefs
        }
    }

    # 5. Documented examples must bind to their documented expected value.
    foreach ($ref in @($covers | Where-Object { $kindOf[[string]$_] -eq 'example' })) {
        if (-not $expectedOf.ContainsKey([string]$ref)) { continue }
        $expected = $expectedOf[[string]$ref]

        # An example whose expectation is a C# exception name cannot bind to a
        # Rust test body: the port asserts a typed error variant, not the .NET
        # type name. Check 4 now covers these -- it treats an error example as an
        # error requirement -- so skipping here leaves nothing unchecked.
        if ((Get-Prop $expected 'error')) { continue }

        # Normalisation is deliberately conservative. Lower-casing and stripping
        # signs made "-1" equal "1" and "ABC" equal "abc", so a test asserting
        # the opposite of the documented value passed. Case, sign and decimal
        # point are all semantically load-bearing and are preserved.
        $leaves = @(Get-ScalarLeaves $expected |
            ForEach-Object { ConvertTo-Comparable $_ } |
            Where-Object { $_.Length -ge 1 } | Select-Object -Unique)
        # Booleans have no literal form a correct test must contain:
        # `assert!(item.is_active)` is idiomatic and carries no "true".
        $leaves = @($leaves | Where-Object { $_ -notin @('true', 'false') })
        if (-not $leaves -or $leaves.Count -eq 0) { continue }

        # Look only inside assertion arguments. Scanning the whole body let an
        # input literal that the API echoes into its output satisfy the check
        # without the output ever being asserted.
        if ($assertions.Count -eq 0) { continue }   # check 2 already reported it
        $assertComparable = ConvertTo-Comparable (($assertions | ForEach-Object { $_.Args }) -join ' | ')
        $absent = @($leaves | Where-Object { -not $assertComparable.Contains($_) })
        if ($absent.Count -gt 0) {
            Add-Finding $testId $file $src.Line 'unbound_oracle' 'high' `
                ("Documented expected value(s) never appear in any assertion: {0}" -f ($absent -join ', ')) @($ref) `
                ("Assert that the result equals the documented expected value(s): {0}. Requirement '{1}' documents them, but no assertion in the test references them -- appearing elsewhere in the body (as an input, say) does not check the output." -f ($absent -join ', '), $ref)
        }
    }

    # 6. Presence-only checks for requirements that have a concrete value.
    if ($assertions.Count -gt 0 -and $errorRefs.Count -eq 0) {
        $hasComparison = @($assertions | Where-Object {
            $_.Macro -in 'assert_eq', 'assert_ne', 'debug_assert_eq', 'debug_assert_ne', 'assert_matches'
        })
        $presenceOnly = @($assertions | Where-Object {
            ($_.Macro -in @('assert', 'debug_assert')) -and $_.Args -match '\.is_ok\s*\(|\.is_some\s*\('
        })
        if ($hasComparison.Count -eq 0 -and $presenceOnly.Count -eq $assertions.Count) {
            Add-Finding $testId $file $src.Line 'smoke_only' 'medium' `
                'Only presence checks (is_ok/is_some); no value is compared.' $covers
        }
    }
}

# --- Report ------------------------------------------------------------------

$bySeverity = @{ critical = 0; high = 0; medium = 0 }
foreach ($f in $findings) { $bySeverity[$f.severity]++ }
$failed = $findings.Count -gt 0

# A test this gate could not read is an UNKNOWN, not a pass. The parity verifier
# may not promote a suite to `substantive` while any unknown remains -- that is
# the difference between "the scanner found nothing wrong" and "the scanner
# never looked".
$substantiveEligible = ($bySeverity.critical -eq 0) -and ($bySeverity.high -eq 0) -and ($notAnalysed -eq 0)

$report = [pscustomobject]@{
    verdict = $(if ($failed) { 'fail' } else { 'pass' })
    summary = [pscustomobject]@{
        tests_analysed       = $analysed
        tests_not_analysed   = $notAnalysed
        findings             = $findings.Count
        critical             = $bySeverity.critical
        high                 = $bySeverity.high
        medium               = $bySeverity.medium
        substantive_eligible = $substantiveEligible
    }
    findings     = $findings
    not_analysed = $notAnalysedTests
}

if ($ReportPath) {
    $dir = Split-Path -Parent $ReportPath
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
}

Write-Host ""
Write-Host "Test quality gate: $($report.verdict.ToUpper())" -ForegroundColor $(if ($failed) { 'Red' } else { 'Green' })
Write-Host ("  {0} tests analysed, {1} not analysed, {2} findings" -f
    $analysed, $notAnalysed, $findings.Count)
if (-not $substantiveEligible) {
    Write-Host "  coverage_level may NOT be promoted to 'substantive' from this run." -ForegroundColor Yellow
}
if ($notAnalysed -gt 0) {
    Write-Host "`n  NOT ANALYSED ($notAnalysed) - treat as unknown, never as pass:" -ForegroundColor Yellow
    foreach ($n in $notAnalysedTests) { Write-Host ("    {0,-26} {1}" -f $n.test_id, $n.reason) }
}

foreach ($sev in 'critical', 'high', 'medium') {
    $group = @($findings | Where-Object { $_.severity -eq $sev })
    if (-not $group.Count) { continue }
    Write-Host "`n  $($sev.ToUpper()) ($($group.Count)):" -ForegroundColor $(if ($sev -eq 'medium') { 'Yellow' } else { 'Red' })
    foreach ($f in $group) {
        Write-Host ("    {0,-26} {1,-24} {2}:{3}" -f $f.finding, $f.test_id, $f.file, $f.line)
        Write-Host ("      {0}" -f $f.detail) -ForegroundColor DarkGray
    }
}
Write-Host ""

exit $(if ($failed) { 1 } else { 0 })
