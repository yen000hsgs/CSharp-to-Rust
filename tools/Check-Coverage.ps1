<#
.SYNOPSIS
    Deterministic coverage gate for the C# -> Rust migration pipeline.

.DESCRIPTION
    Recomputes test coverage of the requirement document by set arithmetic.
    No model, no judgment, no trust in what GenTest claimed about itself.

    Four checks:
      1. MISSING   - a requirement id that no test declares in `covers`.
      2. PHANTOM   - a manifest entry whose file or test function does not
                     exist on disk. This is worse than missing: it inflates
                     coverage while testing nothing.
      3. DANGLING  - a `covers` entry naming a requirement id that is not in
                     the document. Usually a typo, which silently means the
                     real requirement is untested.
      4. MISCLAIM  - `coverage_claim` disagrees with the recomputed numbers.

    Waivers (`coverage_claim.waived`) suppress a MISSING failure but are always
    reported, so a waiver is a visible decision rather than a silent gap.

.PARAMETER DocumentPath
    Path to document.json.

.PARAMETER ManifestPath
    Path to tests/manifest.json.

.PARAMETER TestsRoot
    Root the manifest's `file` values are relative to. Defaults to the parent
    of the manifest's directory (the run root), since manifest paths are
    written as `tests/unit/foo.rs`.

.PARAMETER ReportPath
    Optional path to write the JSON coverage report.

.PARAMETER FailOnWaived
    Treat waived requirements as failures. Use for a final release gate.

.OUTPUTS
    Exit code 0 when the gate passes, 1 when it fails, 2 on a usage error.

.EXAMPLE
    ./tools/Check-Coverage.ps1 -DocumentPath artifacts/run1/document.json `
                               -ManifestPath artifacts/run1/tests/manifest.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DocumentPath,
    [Parameter(Mandatory)][string]$ManifestPath,
    [string]$TestsRoot,
    [string]$ReportPath,
    [switch]$FailOnWaived
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:schemaViolation = $false

function Read-Json([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Host "ERROR: $Label not found: $Path" -ForegroundColor Red
        exit 2
    }
    try {
        return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    }
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

# --- Remediation -------------------------------------------------------------
# The feedback channel must not depend on an agent to be useful. This gate
# already knows the requirement's kind, its prose, and (for examples) the exact
# input and expected value -- everything needed to state what the missing test
# must assert. Emitting that mechanically means a stalled loop is a real gap in
# the document, not a reviewer agent having an off round.

function New-RequiredAssertion($Requirement) {
    $stmt = $Requirement.Statement
    $entry = $Requirement.Entry

    switch ($Requirement.Kind) {
        'example' {
            $inputValue = Get-Prop $entry 'input'
            $expected = Get-Prop $entry 'expected'
            $inJson = if ($null -ne $inputValue) { $inputValue | ConvertTo-Json -Depth 6 -Compress } else { '<see document>' }
            $err = if ($null -ne $expected) { Get-Prop $expected 'error' } else { $null }
            if ($err) {
                return "Call the feature with input $inJson and assert it returns an error corresponding to '$err'. Assert on the error variant, not merely that the call failed."
            }
            $exJson = if ($null -ne $expected) { $expected | ConvertTo-Json -Depth 6 -Compress } else { '<see document>' }
            return "Call the feature with input $inJson and assert the result equals $exJson. Compare the value; a presence check (is_ok/is_some) does not satisfy this."
        }
        'error' {
            $cond = Get-Prop $entry 'condition'
            $result = Get-Prop $entry 'result'
            $when = if ($cond) { $cond } else { $stmt }
            $then = if ($result) { "returns the error corresponding to '$result'" } else { 'returns an error' }
            return "Construct the condition '$when' and assert the call $then. Assert on the specific error variant."
        }
        'invariant' {
            return "Assert that the invariant holds after the operation: $stmt. Exercise a sequence that would violate it if unguarded, then assert it still holds."
        }
        'behavior' {
            return "Assert the observable outcome of: $stmt. Compare a concrete returned value or state change, not just that the call succeeded."
        }
        'feature' {
            return "Add at least one test declaring feature_id '$($Requirement.RefId)' that exercises: $stmt."
        }
    }
    return "Add a test asserting: $stmt"
}

# --- Build the requirement set from the document -----------------------------

# A document whose shape is wrong yields zero requirements, and zero out of zero
# scores as 100%. A file with a top-level `requirements` key where `features` was
# expected therefore used to report full coverage against an empty manifest --
# the gate's strongest possible verdict, produced by reading nothing at all.
# Invalid input has to fail before any percentage is computed.
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
        Write-Host "  Refusing to score an empty requirement set -- 0 of 0 is not full coverage." -ForegroundColor Red
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
    # Manifest `file` values are run-root relative ("tests/unit/foo.rs"), and the
    # manifest itself lives at "<run root>/tests/manifest.json".
    $TestsRoot = Split-Path -Parent (Split-Path -Parent (Resolve-Path -LiteralPath $ManifestPath))
}

$requirements = [ordered]@{}   # ref_id -> descriptor
$featureIds = [System.Collections.Generic.List[string]]::new()

foreach ($feature in (Get-Prop $document 'features')) {
    $featureId = Get-Prop $feature 'id'
    if (-not $featureId) {
        Write-Host "ERROR: Document contains a feature with no 'id'." -ForegroundColor Red
        exit 2
    }
    $featureIds.Add($featureId)
    # Set arithmetic is only sound once the ids are known to be unique. A
    # duplicate would silently overwrite the earlier entry, shrinking the
    # denominator and turning an uncovered requirement into a pass.
    if ($requirements.Contains($featureId)) {
        Write-Host ("ERROR: duplicate id '{0}' -- a feature reuses an id already defined." -f $featureId) -ForegroundColor Red
        $script:schemaViolation = $true
    }
    $requirements[$featureId] = [pscustomobject]@{
        RefId = $featureId; FeatureId = $featureId; Kind = 'feature'
        Statement = (Get-Prop $feature 'summary')
        Entry = $feature
    }

    foreach ($kind in 'behaviors', 'errors', 'invariants', 'examples') {
        foreach ($entry in (Get-Prop $feature $kind)) {
            # A bare string has no id, so it cannot be traced. Reject loudly
            # rather than quietly excluding it from the denominator.
            if ($entry -is [string]) {
                Write-Host ("ERROR: feature '{0}' has an untraceable {1} entry (plain string, no id): '{2}'" -f
                    $featureId, $kind, $entry) -ForegroundColor Red
                $script:schemaViolation = $true
                continue
            }
            $refId = Get-Prop $entry 'id'
            if (-not $refId) {
                Write-Host ("ERROR: feature '{0}' has a {1} entry with no 'id'." -f $featureId, $kind) -ForegroundColor Red
                $script:schemaViolation = $true
                continue
            }
            $statement = @((Get-Prop $entry 'statement'), (Get-Prop $entry 'condition'), (Get-Prop $entry 'note')) |
                Where-Object { $_ } | Select-Object -First 1
            if (-not $statement -and $kind -eq 'examples') {
                # Examples carry input/expected rather than prose. Render a compact
                # form so a missing example is still identifiable in the report.
                $statement = 'example: ' + ((Get-Prop $entry 'input') | ConvertTo-Json -Depth 4 -Compress)
            }
            if ($requirements.Contains($refId)) {
                Write-Host ("ERROR: duplicate id '{0}' in feature '{1}' ({2}) -- ids must be unique for coverage to be computable." -f
                    $refId, $featureId, $kind) -ForegroundColor Red
                $script:schemaViolation = $true
                continue
            }
            $requirements[$refId] = [pscustomobject]@{
                RefId = $refId; FeatureId = $featureId
                Kind = $kind.TrimEnd('s')
                Statement = $statement
                Entry = $entry
            }
        }
    }
}

if ($script:schemaViolation) {
    Write-Host "`nCoverage gate: FAIL (document schema) - ids are required for coverage to be computable." -ForegroundColor Red
    exit 2
}

# --- Collect what the tests claim to cover -----------------------------------

$covered = @{}                 # ref_id -> list of test_id
$phantom = [System.Collections.Generic.List[object]]::new()
$dangling = [System.Collections.Generic.List[object]]::new()
$testFileCache = @{}

# Resolve a manifest-declared test path against the tests root. An absolute path
# must NOT be re-rooted (Join-Path would produce a corrupt path), and nothing may
# escape the tests root via `..` -- a manifest is agent-authored input.
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

foreach ($test in (Get-Prop $manifest 'tests')) {
    $testId = Get-Prop $test 'test_id'
    $file = Get-Prop $test 'file'
    $fn = Get-Prop $test 'test_fn'

    # PHANTOM: the manifest claims a test that is not actually on disk.
    $resolved = Resolve-TestPath $TestsRoot $file
    $exists = [bool]($resolved -and (Test-Path -LiteralPath $resolved))
    $fnFound = $false

    if ($exists -and $fn) {
        if (-not $testFileCache.ContainsKey($resolved)) {
            $testFileCache[$resolved] = Get-Content -LiteralPath $resolved -Raw -Encoding UTF8
        }
        $fnFound = $testFileCache[$resolved] -match ("(?m)^[ \t]*(?:pub(?:\([^)]*\))?\s+)?(?:async\s+)?fn\s+" + [regex]::Escape($fn) + "(?:\s*<[^>]*>)?\s*\(")
    }

    if (-not $exists -or -not $fnFound) {
        $phantom.Add([pscustomobject]@{
            test_id = $testId
            file    = $file
            test_fn = $fn
            reason  = if (-not $exists) { if ($resolved) { 'file does not exist' } else { 'file path is missing or escapes the tests root' } } else { 'function not found in file' }
            covers  = @(Get-Prop $test 'covers')
            required_assertion = if (-not $exists) {
                "Manifest entry '$testId' points at '$file', which does not resolve under the tests root. Either create the file or correct the path; until then every requirement it claims is untested."
            } else {
                "File '$file' exists but contains no function '$fn'. Implement it, or correct the manifest's test_fn to the real function name."
            }
        })
        # A phantom test grants no coverage.
        continue
    }

    # A feature is an envelope, not a requirement a test names in `covers`.
    # Per docs/contracts.md the test carries it in `feature_id`, so credit it
    # from there -- otherwise every schema-correct manifest reports its features
    # as missing.
    $testFeatureId = Get-Prop $test 'feature_id'
    if ($testFeatureId) {
        if ($requirements.Contains($testFeatureId)) {
            if (-not $covered.ContainsKey($testFeatureId)) {
                $covered[$testFeatureId] = [System.Collections.Generic.List[string]]::new()
            }
            $covered[$testFeatureId].Add($testId)
        }
        else {
            # Silently ignoring an unknown feature_id reports the real feature as
            # missing with no hint why. Surface the typo instead.
            $dangling.Add([pscustomobject]@{ test_id = $testId; ref_id = $testFeatureId; field = 'feature_id' })
        }
    }

    foreach ($refId in (Get-Prop $test 'covers')) {
        if (-not $requirements.Contains($refId)) {
            $dangling.Add([pscustomobject]@{ test_id = $testId; ref_id = $refId; field = 'covers' })
            continue
        }
        if (-not $covered.ContainsKey($refId)) {
            $covered[$refId] = [System.Collections.Generic.List[string]]::new()
        }
        $covered[$refId].Add($testId)
    }
}

# A feature whose child requirements are covered is itself exercised, even if no
# test names the feature in `feature_id`. Credit it before the set difference so
# the feature-level entry reports a real gap only when nothing touches it at all.
foreach ($refId in @($requirements.Keys)) {
    $req = $requirements[$refId]
    if ($req.Kind -ne 'feature' -or $covered.ContainsKey($refId)) { continue }
    $childTests = [System.Collections.Generic.List[string]]::new()
    foreach ($other in $requirements.Values) {
        if ($other.Kind -eq 'feature' -or $other.FeatureId -ne $refId) { continue }
        if ($covered.ContainsKey($other.RefId)) { $childTests.AddRange($covered[$other.RefId]) }
    }
    if ($childTests.Count -gt 0) {
        $covered[$refId] = [System.Collections.Generic.List[string]]::new()
        foreach ($t in ($childTests | Select-Object -Unique)) { $covered[$refId].Add($t) }
    }
}

# --- Set difference ----------------------------------------------------------

$claim = Get-Prop $manifest 'coverage_claim'
$waivers = @{}
foreach ($w in (Get-Prop $claim 'waived')) {
    $waivers[[string](Get-Prop $w 'ref_id')] = (Get-Prop $w 'reason')
}

$missing = [System.Collections.Generic.List[object]]::new()
$waived = [System.Collections.Generic.List[object]]::new()

foreach ($refId in $requirements.Keys) {
    if ($covered.ContainsKey($refId)) { continue }

    $req = $requirements[$refId]
    $severity = switch ($req.Kind) {
        'feature'   { 'critical' }
        'example'   { 'high' }
        'error'     { 'high' }
        'behavior'  { 'high' }
        'invariant' { 'medium' }
        default     { 'medium' }
    }
    $record = [pscustomobject]@{
        ref_id             = $refId
        feature_id         = $req.FeatureId
        kind               = $req.Kind
        statement          = $req.Statement
        severity           = $severity
        reason             = $waivers[[string]$refId]
        required_assertion = (New-RequiredAssertion $req)
    }

    if ($waivers.ContainsKey([string]$refId)) { $waived.Add($record) } else { $missing.Add($record) }
}

# --- MISCLAIM: does the self-reported claim match reality? -------------------
# Informational only. The gate computes the counts itself, so a disagreement
# means the generator's arithmetic was off -- not that coverage is wrong. Making
# it fail the build only punishes an agent for bookkeeping the gate already did.

$total = $requirements.Count
$actualCovered = $covered.Keys.Count
$misclaim = $null

if ($claim) {
    $claimedTotal = Get-Prop $claim 'requirements_total'
    $claimedCovered = Get-Prop $claim 'requirements_covered'
    if (($null -ne $claimedTotal -and $claimedTotal -ne $total) -or
        ($null -ne $claimedCovered -and $claimedCovered -ne $actualCovered)) {
        $misclaim = [pscustomobject]@{
            claimed_total   = $claimedTotal
            claimed_covered = $claimedCovered
            actual_total    = $total
            actual_covered  = $actualCovered
        }
    }
}

# --- Verdict -----------------------------------------------------------------

$failed = ($missing.Count -gt 0) -or ($phantom.Count -gt 0) -or
          ($dangling.Count -gt 0) -or
          ($FailOnWaived -and $waived.Count -gt 0)

$pct = if ($total -gt 0) { [math]::Round(100.0 * $actualCovered / $total, 1) } else { 100.0 }

# --- Work order --------------------------------------------------------------
# Emitted by the gate, not by a reviewing agent, so gap-fill gets the same
# instruction every run. Severity orders the queue; `required_assertion` is the
# spec GenTest must satisfy.

$requiredTests = [System.Collections.Generic.List[object]]::new()
foreach ($m in $missing) {
    $requiredTests.Add([pscustomobject]@{
        ref_id             = $m.ref_id
        reason             = 'missing'
        severity           = $m.severity
        required_assertion = $m.required_assertion
        evidence           = "Check-Coverage.ps1: no manifest entry declares '$($m.ref_id)' in covers."
    })
}
foreach ($p in $phantom) {
    # `ref_id` must be a requirement id -- the parity validator keys its
    # work-order check on it. Emitting the test_id here put a test id in a
    # requirement field and left the requirements the phantom claimed with no
    # work order at all. Emit one entry per requirement it failed to deliver.
    $claimed = @($p.covers | Where-Object { $_ })
    if ($claimed.Count -eq 0) { $claimed = @($p.test_id) }
    foreach ($c in $claimed) {
        $requiredTests.Add([pscustomobject]@{
            ref_id             = $c
            test_id            = $p.test_id
            reason             = 'phantom'
            severity           = 'critical'
            required_assertion = $p.required_assertion
            evidence           = "Check-Coverage.ps1: $($p.reason) ($($p.file) :: $($p.test_fn))."
        })
    }
}
foreach ($d in $dangling) {
    $requiredTests.Add([pscustomobject]@{
        ref_id             = $d.ref_id
        reason             = 'dangling'
        severity           = 'high'
        required_assertion = "Test '$($d.test_id)' claims to cover '$($d.ref_id)', which is not in the document. Correct the id to the requirement actually tested -- the intended requirement is currently counted as untested."
        evidence           = "Check-Coverage.ps1: '$($d.ref_id)' is not a known requirement id."
    })
}

$report = [pscustomobject]@{
    verdict = $(if ($failed) { 'fail' } else { 'pass' })
    summary = [pscustomobject]@{
        features             = $featureIds.Count
        requirements_total   = $total
        requirements_covered = $actualCovered
        coverage_pct         = $pct
        missing              = $missing.Count
        waived               = $waived.Count
        phantom              = $phantom.Count
        dangling             = $dangling.Count
        required_tests       = $requiredTests.Count
    }
    missing  = $missing
    waived   = $waived
    phantom  = $phantom
    dangling = $dangling
    misclaim = $misclaim
    required_tests = $requiredTests
}

if ($ReportPath) {
    $dir = Split-Path -Parent $ReportPath
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
}

# --- Human-readable output ---------------------------------------------------

Write-Host ""
Write-Host "Coverage gate: $($report.verdict.ToUpper())" -ForegroundColor $(if ($failed) { 'Red' } else { 'Green' })
Write-Host ("  {0}/{1} requirements covered ({2}%) across {3} features" -f
    $actualCovered, $total, $pct, $featureIds.Count)

if ($missing.Count) {
    Write-Host "`n  MISSING ($($missing.Count)) - no test declares these:" -ForegroundColor Red
    $missing | Sort-Object severity, ref_id | ForEach-Object {
        Write-Host ("    [{0,-8}] {1,-46} {2}" -f $_.severity, $_.ref_id, $_.statement)
    }
}
if ($phantom.Count) {
    Write-Host "`n  PHANTOM ($($phantom.Count)) - claimed in manifest, absent on disk:" -ForegroundColor Red
    $phantom | ForEach-Object { Write-Host ("    {0,-46} {1} ({2})" -f $_.test_id, $_.file, $_.reason) }
}
if ($dangling.Count) {
    Write-Host "`n  DANGLING ($($dangling.Count)) - covers an id not in the document:" -ForegroundColor Red
    $dangling | ForEach-Object { Write-Host ("    {0,-46} -> {1}" -f $_.test_id, $_.ref_id) }
}
if ($misclaim) {
    Write-Host "`n  NOTE - coverage_claim disagrees with recomputed values (informational):" -ForegroundColor DarkYellow
    Write-Host ("    claimed {0}/{1}, actual {2}/{3} - the recomputed values are authoritative." -f
        $misclaim.claimed_covered, $misclaim.claimed_total, $misclaim.actual_covered, $misclaim.actual_total)
}
if ($waived.Count) {
    Write-Host "`n  WAIVED ($($waived.Count)) - counts as UNCOVERED for certification:" -ForegroundColor Yellow
    $waived | ForEach-Object { Write-Host ("    {0,-46} {1}" -f $_.ref_id, $_.reason) }
}
Write-Host ""

exit $(if ($failed) { 1 } else { 0 })
