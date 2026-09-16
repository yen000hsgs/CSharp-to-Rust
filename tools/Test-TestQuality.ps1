<#
.SYNOPSIS
    Self-test for tools/Check-TestQuality.ps1.

.DESCRIPTION
    Builds a temporary document, manifest and Rust test file containing one
    deliberately defective test per finding type, plus good tests that must NOT
    be flagged. False accusations matter as much as misses here: a gate that
    flags healthy tests trains the agent to ignore it.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$gate = Join-Path $PSScriptRoot 'Check-TestQuality.ps1'
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("tqgate-" + [guid]::NewGuid().ToString('n').Substring(0, 8))
New-Item -ItemType Directory -Force -Path (Join-Path $work 'tests\unit') | Out-Null
$failures = [System.Collections.Generic.List[string]]::new()

# --- Document ----------------------------------------------------------------

$document = [pscustomobject]@{
    features = @(
        [pscustomobject]@{
            id = 'demo.add'; name = 'Add'; summary = 'Adds an item.'
            behaviors = @([pscustomobject]@{ id = 'demo.add.b1'; statement = 'Returns the item.' })
            errors    = @([pscustomobject]@{ id = 'demo.add.e1'; condition = 'sku is empty' })
            invariants = @([pscustomobject]@{ id = 'demo.add.i1'; statement = 'Total is exact.' })
            examples  = @(
                [pscustomobject]@{ id = 'demo.add.x1'; input = [pscustomobject]@{ sku = 'widget-1' }
                                   expected = [pscustomobject]@{ sku = 'WIDGET-1'; lineTotal = '59.97' } }
                # An example whose documented outcome is a C# exception type. A
                # correct Rust port asserts its own error variant and can never
                # contain the .NET type name, so this must not bind an oracle.
                [pscustomobject]@{ id = 'demo.add.x2'; input = [pscustomobject]@{ sku = '' }
                                   expected = [pscustomobject]@{ error = 'InvalidOperationException' } }
            )
        }
    )
}
$docPath = Join-Path $work 'document.json'
$document | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $docPath -Encoding UTF8

# --- Rust test file ----------------------------------------------------------

$rust = @'
#[test]
fn good_value_test() {
    let item = add("widget-1", 3, "19.99");
    assert_eq!(item.sku, "WIDGET-1");
    assert_eq!(item.line_total, "59.97");
}

#[test]
fn good_error_test() {
    let result = add("", 1, "1.00");
    assert!(result.is_err());
}

#[test]
fn good_should_panic_test() {
    let _ = add("", 1, "1.00").unwrap();
}

#[test]
fn good_invariant_test() {
    // A comment mentioning assert_eq!(1, 1) must not trip the tautology check.
    let total = compute_total();
    assert_eq!(total, "1.00");
}

#[test]
fn good_option_test() {
    // is_none() IS the assertion for a documented "returns null" behaviour.
    assert!(find("no-such-sku").is_none());
}

#[test]
fn good_empty_test() {
    // Likewise is_empty() for a documented "returns an empty collection".
    assert!(list_removed().is_empty());
}

#[test]
fn good_raw_string_test() {
    /* A nested /* block comment */ with a stray } and ) inside. */
    let delim = '}';
    assert_eq!(render(), r#"{"sku":"WIDGET-1","lineTotal":"59.97"}"#);
    assert_ne!(delim, ')');
}

#[test]
fn good_error_example_test() {
    let result = add("", 1, "1.00");
    assert!(matches!(result, Err(DemoError::EmptySku)));
}

#[test]
fn bad_unimplemented() {
    todo!()
}

#[test]
fn bad_no_assertion() {
    let item = add("widget-1", 3, "19.99");
    println!("{:?}", item);
}

#[test]
fn bad_tautology() {
    let item = add("widget-1", 3, "19.99");
    assert_eq!(item.sku, item.sku);
    assert!(true);
}

#[test]
fn bad_unchecked_error() {
    let result = add("", 1, "1.00");
    assert_eq!(format!("{:?}", result).len() > 0, true);
}

#[test]
fn bad_unbound_oracle() {
    let item = add("widget-1", 3, "19.99");
    assert_eq!(item.quantity, 3);
}

#[test]
fn bad_smoke_only() {
    let item = add("widget-1", 3, "19.99");
    assert!(item.is_ok());
}

#[test]
fn bad_matches_no_assert() {
    // `matches!` returns a bool. On its own it never panics, so this test
    // cannot fail -- it must not count as an assertion.
    let result = add("widget-1", 3, "19.99");
    matches!(result, Ok(_));
}

#[test]
fn bad_todo_with_message() {
    todo!("wire this up after the storage layer lands")
}

#[test]
fn bad_err_constructed_not_asserted() {
    // Constructs an Err but asserts success. Scanning the whole body for
    // `Err(` used to accept this as checking the error path.
    let fallback: Result<Item, DemoError> = Err(DemoError::EmptySku);
    let _ = fallback;
    let result = add("", 1, "1.00");
    assert_eq!(result.is_ok(), true);
}

#[test]
fn good_error_example_by_name() {
    // Harness-style: the port is driven through JSON, so the error is asserted
    // by its documented name rather than as a Rust Result. That is a real error
    // assertion and must not be reported as a missing one.
    let out = run_case("empty_sku");
    assert_eq!(out["ok"], false);
    assert_eq!(out["error"]["type"], "InvalidOperationException");
}

// #[test]
// fn commented_out_test() {
//     assert_eq!(add("widget-1", 3, "19.99").unwrap().line_total, "59.97");
// }

/*
#[test]
fn block_commented_test() {
    assert_eq!(compute_total(), "1.00");
}
*/

#[test]
fn bad_stringly_assertion() {
    // The only assertion is inside a string literal, so nothing executes it.
    let source = "assert_eq!(item.line_total, \"59.97\");";
    println!("{}", source);
}

#[test]
fn bad_error_example_asserts_ok() {
    // Covers an example documented to fail, yet asserts success.
    let result = add("", 1, "1.00");
    assert_eq!(result, Ok(5));
}

#[test]
fn bad_oracle_outside_assertion() {
    // The documented values appear in the body but never in an assertion.
    let expected_total = "59.97";
    let expected_sku = "WIDGET-1";
    let _ = (expected_total, expected_sku);
    let item = add("widget-1", 3, "19.99");
    assert_ne!(item.quantity, 0);
}

// An ordinary function. cargo never runs it, but it used to earn full coverage
// and count as substantive evidence purely by existing.
fn bad_unregistered_fn() {
    let result = add("widget-1", 3, "19.99");
    assert_eq!(result.total, "59.97");
}

#[cfg(any())]
#[test]
fn bad_cfg_disabled() {
    let result = add("widget-1", 3, "19.99");
    assert_eq!(result.total, "59.97");
}

#[test]
#[ignore]
fn bad_ignored_test() {
    let result = add("widget-1", 3, "19.99");
    assert_eq!(result.total, "59.97");
}

// Gated on a predicate this gate cannot evaluate: it may or may not run, which
// is an UNKNOWN and must not be promoted to proof.
#[cfg(feature = "slow")]
#[test]
fn unknown_cfg_test() {
    let result = add("widget-1", 3, "19.99");
    assert_eq!(result.total, "59.97");
}
'@
# `good_should_panic_test` needs its attribute injected without the here-string
# swallowing it as a comment line.
$rust = $rust -replace '(?m)^#\[test\]\r?\n(fn good_should_panic_test)', "#[test]`n#[should_panic]`n`$1"
Set-Content -LiteralPath (Join-Path $work 'tests\unit\demo.rs') -Value $rust -Encoding UTF8

# --- Manifest ----------------------------------------------------------------

function New-Test([string]$id, [string]$fn, [string[]]$covers) {
    [pscustomobject]@{
        test_id = $id; feature_id = 'demo.add'; covers = $covers; tier = 'unit'
        assertion_kind = 'value'; file = 'tests/unit/demo.rs'; test_fn = $fn
        rationale = 'self-test'; status = 'expected_fail_until_implemented'
    }
}
[pscustomobject]@{
    run_id = 'tq-selftest'; generated_from = 'selftest'
    tests = @(
        New-Test 't_good_value'      'good_value_test'         @('demo.add', 'demo.add.b1', 'demo.add.x1')
        New-Test 't_good_error'      'good_error_test'         @('demo.add.e1')
        New-Test 't_good_panic'      'good_should_panic_test'  @('demo.add.e1')
        New-Test 't_good_invariant'  'good_invariant_test'     @('demo.add.i1')
        New-Test 't_good_option'     'good_option_test'        @('demo.add.b1')
        New-Test 't_good_empty'      'good_empty_test'         @('demo.add.i1')
        New-Test 't_good_raw'        'good_raw_string_test'    @('demo.add.x1')
        New-Test 't_good_err_ex'     'good_error_example_test' @('demo.add.x2')
        New-Test 't_good_err_named'  'good_error_example_by_name' @('demo.add.x2')
        New-Test 't_commented'       'commented_out_test'      @('demo.add.x1')
        New-Test 't_block_commented' 'block_commented_test'    @('demo.add.i1')
        New-Test 't_stringly'        'bad_stringly_assertion'  @('demo.add.x1')
        New-Test 't_err_ex_ok'       'bad_error_example_asserts_ok' @('demo.add.x2')
        New-Test 't_unimplemented'   'bad_unimplemented'       @('demo.add.b1')
        New-Test 't_no_assertion'    'bad_no_assertion'        @('demo.add.b1')
        New-Test 't_tautology'       'bad_tautology'           @('demo.add.b1')
        New-Test 't_unchecked_error' 'bad_unchecked_error'     @('demo.add.e1')
        New-Test 't_unbound_oracle'  'bad_unbound_oracle'      @('demo.add.x1')
        New-Test 't_smoke_only'      'bad_smoke_only'          @('demo.add.b1')
        New-Test 't_matches_only'    'bad_matches_no_assert'   @('demo.add.b1')
        New-Test 't_todo_msg'        'bad_todo_with_message'   @('demo.add.b1')
        New-Test 't_err_constructed' 'bad_err_constructed_not_asserted' @('demo.add.e1')
        New-Test 't_oracle_outside'  'bad_oracle_outside_assertion'     @('demo.add.x1')
        New-Test 't_phantom'         'no_such_function'        @('demo.add.b1')
        New-Test 't_unregistered'    'bad_unregistered_fn'     @('demo.add.b1')
        New-Test 't_cfg_disabled'    'bad_cfg_disabled'        @('demo.add.b1')
        New-Test 't_ignored'         'bad_ignored_test'        @('demo.add.b1')
        New-Test 't_unknown_cfg'     'unknown_cfg_test'        @('demo.add.b1')
    )
    coverage_claim = [pscustomobject]@{ requirements_total = 5; requirements_covered = 5; waived = @() }
} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $work 'tests\manifest.json') -Encoding UTF8

# --- Run ---------------------------------------------------------------------

$reportPath = Join-Path $work 'quality.json'
& $gate -DocumentPath $docPath -ManifestPath (Join-Path $work 'tests\manifest.json') -ReportPath $reportPath *> (Join-Path $work 'console.txt')
$exit = $LASTEXITCODE
$report = Get-Content $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json

function Assert([string]$What, $Expected, $Actual) {
    if ($Expected -eq $Actual) { Write-Host ("  PASS  {0} = {1}" -f $What, $Actual) -ForegroundColor Green }
    else {
        Write-Host ("  FAIL  {0} expected {1}, got {2}" -f $What, $Expected, $Actual) -ForegroundColor Red
        $failures.Add($What)
    }
}

function Assert-Finding([string]$TestId, [string]$Kind) {
    $hit = @($report.findings | Where-Object { $_.test_id -eq $TestId -and $_.finding -eq $Kind })
    if ($hit.Count -ge 1) { Write-Host ("  PASS  {0} -> {1} ({2})" -f $TestId, $Kind, $hit.Count) -ForegroundColor Green }
    else {
        Write-Host ("  FAIL  {0} -> {1} not reported" -f $TestId, $Kind) -ForegroundColor Red
        $failures.Add("$TestId -> $Kind")
    }
}

function Assert-Not-Analysed([string]$TestId) {
    $hit = @($report.not_analysed | Where-Object { $_.test_id -eq $TestId })
    if ($hit.Count -ge 1) { Write-Host ("  PASS  {0} -> not analysed" -f $TestId) -ForegroundColor Green }
    else {
        Write-Host ("  FAIL  {0} was analysed; it should be undiscoverable" -f $TestId) -ForegroundColor Red
        $failures.Add("$TestId -> not analysed")
    }
}

function Assert-Clean([string]$TestId) {    $hit = @($report.findings | Where-Object { $_.test_id -eq $TestId })
    if ($hit.Count -eq 0) { Write-Host ("  PASS  {0} not flagged" -f $TestId) -ForegroundColor Green }
    else {
        Write-Host ("  FAIL  {0} wrongly flagged: {1}" -f $TestId, ($hit.finding -join ', ')) -ForegroundColor Red
        $failures.Add("$TestId falsely flagged")
    }
}

Write-Host "Test quality gate self-test`n"
Assert 'exit code' 1 $exit
Assert 'verdict' 'fail' $report.verdict
Assert 'tests analysed' 21 $report.summary.tests_analysed
Assert 'tests skipped (phantom)' 7 $report.summary.tests_not_analysed

Write-Host "`n  Defects that must be caught:"
Assert-Finding 't_unimplemented'   'unimplemented_test'
Assert-Finding 't_no_assertion'    'no_assertion'
Assert-Finding 't_tautology'       'tautological_assertion'
Assert-Finding 't_unchecked_error' 'unchecked_error_path'
Assert-Finding 't_unbound_oracle'  'unbound_oracle'
Assert-Finding 't_smoke_only'      'smoke_only'

# Adversarial cases: satisfy the letter of a check while defeating its purpose.
Write-Host "`n  Gate-evasion attempts that must still be caught:"
Assert-Finding 't_matches_only'    'no_assertion'
Assert-Finding 't_todo_msg'        'unimplemented_test'
Assert-Finding 't_err_constructed' 'unchecked_error_path'
Assert-Finding 't_oracle_outside'  'unbound_oracle'

# Non-executable text is not evidence: a test that exists only inside a comment
# is not a test, and an assertion inside a string literal never runs.
Assert-Not-Analysed 't_commented'
Assert-Not-Analysed 't_block_commented'

# A function cargo never runs is not evidence. Each of these used to earn full
# coverage and `substantive_eligible: true` purely by existing on disk.
Assert-Not-Analysed 't_unregistered'
Assert-Not-Analysed 't_cfg_disabled'
Assert-Not-Analysed 't_ignored'
Assert-Not-Analysed 't_unknown_cfg'
Assert-Finding 't_unregistered' 'unregistered_test'
Assert-Finding 't_cfg_disabled' 'disabled_test'
Assert-Finding 't_ignored' 'disabled_test'
Assert-Finding 't_stringly'        'no_assertion'

# An example documented to fail is an error requirement, whatever its kind says.
Assert-Finding 't_err_ex_ok'       'unchecked_error_path'

Write-Host "`n  Healthy tests that must NOT be flagged:"
Assert-Clean 't_good_value'
Assert-Clean 't_good_error'
Assert-Clean 't_good_panic'
Assert-Clean 't_good_invariant'
Assert-Clean 't_good_option'
Assert-Clean 't_good_empty'
Assert-Clean 't_good_raw'
Assert-Clean 't_good_err_ex'
Assert-Clean 't_good_err_named'

# --- Every finding must carry its own fix ------------------------------------
# A finding that only names a defect makes the next GenTest round a guessing
# game. The gate knows which rule fired, so it states the repair itself.

Write-Host "`n  Tool-computed remediation:"
$findings = @($report.findings)
$noFix = @($findings | Where-Object { -not $_.remediation })
if ($noFix.Count -eq 0) {
    Write-Host ("  PASS  all {0} findings carry a remediation" -f $findings.Count) -ForegroundColor Green
}
else {
    Write-Host ("  FAIL  {0} finding(s) with no remediation: {1}" -f $noFix.Count,
        (($noFix | ForEach-Object { "$($_.test_id)/$($_.finding)" }) -join ', ')) -ForegroundColor Red
    $failures.Add('remediation/missing')
}

$kinds = @($findings | ForEach-Object { $_.finding } | Sort-Object -Unique)
$vague = @($findings | Where-Object { $_.remediation -and $_.remediation.Length -lt 20 })
if ($vague.Count -eq 0) {
    Write-Host ("  PASS  remediation present for all {0} finding kinds" -f $kinds.Count) -ForegroundColor Green
}
else {
    Write-Host ("  FAIL  {0} remediation(s) too short to act on" -f $vague.Count) -ForegroundColor Red
    $failures.Add('remediation/vague')
}

Write-Host ""
if ($failures.Count) {
    Write-Host "Self-test FAILED ($($failures.Count)): $($failures -join '; ')" -ForegroundColor Red
    Write-Host "Fixtures left at $work for inspection." -ForegroundColor Yellow
    exit 1
}
Write-Host "Self-test PASSED - the quality gate catches nominal coverage without flagging healthy tests." -ForegroundColor Green
Remove-Item -Recurse -Force $work
exit 0
