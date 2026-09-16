<#
.SYNOPSIS
    Shared lexical analysis of Rust test sources.

.DESCRIPTION
    Both gates have to answer the same question -- "is this manifest entry a real,
    registered, enabled test?" -- and they used to answer it differently. The
    quality gate masked comments and strings; the coverage gate ran a raw regex
    over the file, so a function name appearing in a comment satisfied it. Worse,
    neither asked whether the function was a test at all: a plain `fn` with no
    #[test] attribute, or one switched off by #[cfg(any())], counted as covered
    and as substantive evidence.

    Discovery lives here so the two gates cannot drift apart again.
#>

Set-StrictMode -Version Latest
# Scan forward from an opening brace/paren to its match, skipping over string
# literals, raw strings, char literals and comments so that braces inside them
# don't count.
function Get-BalancedSpan([string]$Text, [int]$OpenIndex, [char]$Open, [char]$Close) {
    $depth = 0
    $i = $OpenIndex
    $n = $Text.Length
    $isIdent = { param([int]$k) $k -ge 0 -and $k -lt $n -and ($Text[$k] -match '[A-Za-z0-9_]') }

    while ($i -lt $n) {
        $c = $Text[$i]
        if ($c -eq '/' -and $i + 1 -lt $n) {
            if ($Text[$i + 1] -eq '/') {
                while ($i -lt $n -and $Text[$i] -ne "`n") { $i++ }
                continue
            }
            if ($Text[$i + 1] -eq '*') {
                # Rust block comments nest: /* a /* b */ still open */
                $cdepth = 0
                while ($i + 1 -lt $n) {
                    if ($Text[$i] -eq '/' -and $Text[$i + 1] -eq '*') { $cdepth++; $i += 2; continue }
                    if ($Text[$i] -eq '*' -and $Text[$i + 1] -eq '/') {
                        $cdepth--; $i += 2
                        if ($cdepth -le 0) { break }
                        continue
                    }
                    $i++
                }
                if ($i + 1 -ge $n) { $i = $n }
                continue
            }
        }

        # Raw / byte string prefixes: r"", r#""#, b"", br#""#. Only when the
        # prefix does not continue an identifier (so `for_r` is not a prefix).
        if (($c -eq 'r' -or $c -eq 'b') -and -not (& $isIdent ($i - 1))) {
            $j = $i
            if ($Text[$j] -eq 'b' -and $j + 1 -lt $n -and $Text[$j + 1] -eq 'r') { $j++ }
            if ($Text[$j] -eq 'r') {
                $j++
                $hashes = 0
                while ($j -lt $n -and $Text[$j] -eq '#') { $hashes++; $j++ }
                if ($j -lt $n -and $Text[$j] -eq '"') {
                    $terminator = '"' + ('#' * $hashes)
                    $end = $Text.IndexOf($terminator, $j + 1)
                    $i = if ($end -lt 0) { $n } else { $end + $terminator.Length }
                    continue
                }
            }
            elseif ($Text[$i] -eq 'b' -and $i + 1 -lt $n -and $Text[$i + 1] -eq '"') {
                $i++   # fall through to the ordinary string scanner below
                $c = $Text[$i]
            }
        }

        if ($c -eq '"') {
            $i++
            while ($i -lt $n) {
                if ($Text[$i] -eq '\') { $i += 2; continue }
                if ($Text[$i] -eq '"') { break }
                $i++
            }
            $i++
            continue
        }
        if ($c -eq "'") {
            # Could be a char literal or a lifetime. A char literal is 'x',
            # '\n', '\'' or '\u{1F600}'; a lifetime is 'a followed by a
            # non-quote. Measure the literal explicitly instead of guessing a
            # window, so '}' and ')' cannot leak into the balance count.
            $j = $i + 1
            if ($j -lt $n -and $Text[$j] -eq '\') {
                $j++
                if ($j -lt $n -and $Text[$j] -eq 'u') {
                    $close = $Text.IndexOf('}', $j)
                    $j = if ($close -lt 0) { $n } else { $close + 1 }
                }
                else { $j++ }
            }
            elseif ($j -lt $n) { $j++ }
            if ($j -lt $n -and $Text[$j] -eq "'") { $i = $j + 1; continue }
            $i++   # a lifetime: nothing to skip
            continue
        }
        if ($c -eq $Open) { $depth++ }
        elseif ($c -eq $Close) {
            $depth--
            if ($depth -eq 0) { return @{ Start = $OpenIndex; End = $i } }
        }
        $i++
    }
    return $null
}

# Blank out everything that is not executable Rust, preserving every index so
# offsets found in a mask address the same character in the original text.
# Without this, a `fn` inside a comment is discovered as a test and an
# `assert_eq!` inside a string literal counts as an assertion -- prose passes as
# evidence. Comments are always masked; -MaskStrings additionally blanks string
# and char *contents*, keeping the delimiters so brace/paren balance is intact.
function ConvertTo-MaskedRust([string]$Text, [switch]$MaskStrings) {
    $n = $Text.Length
    $buf = $Text.ToCharArray()
    $blank = {
        param([int]$From, [int]$To)
        for ($k = [Math]::Max($From, 0); $k -lt $To -and $k -lt $n; $k++) {
            if ($buf[$k] -ne "`n" -and $buf[$k] -ne "`r") { $buf[$k] = ' ' }
        }
    }
    $isIdent = { param([int]$k) $k -ge 0 -and $k -lt $n -and ($Text[$k] -match '[A-Za-z0-9_]') }

    $i = 0
    while ($i -lt $n) {
        $c = $Text[$i]

        if ($c -eq '/' -and $i + 1 -lt $n -and $Text[$i + 1] -eq '/') {
            $end = $Text.IndexOf("`n", $i)
            if ($end -lt 0) { $end = $n }
            & $blank $i $end
            $i = $end
            continue
        }
        if ($c -eq '/' -and $i + 1 -lt $n -and $Text[$i + 1] -eq '*') {
            $start = $i
            $cdepth = 0
            while ($i + 1 -lt $n) {
                if ($Text[$i] -eq '/' -and $Text[$i + 1] -eq '*') { $cdepth++; $i += 2; continue }
                if ($Text[$i] -eq '*' -and $Text[$i + 1] -eq '/') {
                    $cdepth--; $i += 2
                    if ($cdepth -le 0) { break }
                    continue
                }
                $i++
            }
            if ($i + 1 -ge $n) { $i = $n }
            & $blank $start $i
            continue
        }

        # Raw / byte strings: r"", r#".."#, b"", br#".."#. Only when the prefix
        # does not continue an identifier, so `for_r` is not read as a prefix.
        if (($c -eq 'r' -or $c -eq 'b') -and -not (& $isIdent ($i - 1))) {
            $j = $i
            if ($Text[$j] -eq 'b' -and $j + 1 -lt $n -and $Text[$j + 1] -eq 'r') { $j++ }
            if ($Text[$j] -eq 'r') {
                $j++
                $hashes = 0
                while ($j -lt $n -and $Text[$j] -eq '#') { $hashes++; $j++ }
                if ($j -lt $n -and $Text[$j] -eq '"') {
                    $terminator = '"' + ('#' * $hashes)
                    $end = $Text.IndexOf($terminator, $j + 1)
                    $contentEnd = if ($end -lt 0) { $n } else { $end }
                    if ($MaskStrings) { & $blank ($j + 1) $contentEnd }
                    $i = if ($end -lt 0) { $n } else { $end + $terminator.Length }
                    continue
                }
            }
            elseif ($Text[$i] -eq 'b' -and $i + 1 -lt $n -and $Text[$i + 1] -eq '"') {
                $i++
                $c = $Text[$i]
            }
        }

        if ($c -eq '"') {
            $start = $i + 1
            $i++
            while ($i -lt $n) {
                if ($Text[$i] -eq '\') { $i += 2; continue }
                if ($Text[$i] -eq '"') { break }
                $i++
            }
            if ($MaskStrings) { & $blank $start ([Math]::Min($i, $n)) }
            $i++
            continue
        }
        if ($c -eq "'") {
            # A char literal ('x', '\n', '\u{1F600}') or a lifetime ('a). Measure
            # it explicitly rather than guessing a window.
            $j = $i + 1
            if ($j -lt $n -and $Text[$j] -eq '\') {
                $j++
                if ($j -lt $n -and $Text[$j] -eq 'u') {
                    $close = $Text.IndexOf('}', $j)
                    $j = if ($close -lt 0) { $n } else { $close + 1 }
                }
                else { $j++ }
            }
            elseif ($j -lt $n) { $j++ }
            if ($j -lt $n -and $Text[$j] -eq "'") {
                if ($MaskStrings) { & $blank ($i + 1) $j }
                $i = $j + 1
                continue
            }
            $i++   # a lifetime: nothing to mask
            continue
        }
        $i++
    }
    return (-join $buf)
}

# $ScanText has comments and string contents blanked, so discovery cannot match
# prose; $CodeText has only comments blanked, so extracted bodies keep the string
# literals a test legitimately asserts against. Indices are identical in both.
function Get-TestSource([string]$ScanText, [string]$CodeText, [string]$Fn) {
    $Text = $ScanText
    $m = [regex]::Match($Text, "(?m)^[ \t]*(?:pub(?:\([^)]*\))?\s+)?(?:async\s+)?fn\s+" + [regex]::Escape($Fn) + "(?:\s*<[^>]*>)?\s*\(")
    if (-not $m.Success) { return $null }

    # Walk backwards over contiguous attribute lines so #[should_panic] is
    # visible. Comment lines are blank in the scan text, so a whitespace-only
    # line is treated as a continuation rather than a stop.
    $lineStart = $Text.LastIndexOf("`n", [Math]::Max($m.Index - 1, 0)) + 1
    $attrStart = $lineStart
    while ($attrStart -gt 0) {
        $prevEnd = $Text.LastIndexOf("`n", $attrStart - 2)
        $prevStart = $prevEnd + 1
        if ($prevStart -lt 0) { break }
        $line = $Text.Substring($prevStart, $attrStart - $prevStart).Trim()
        if ($line.StartsWith('#[') -or $line -eq '') { $attrStart = $prevStart } else { break }
    }

    $brace = $Text.IndexOf('{', $m.Index)
    if ($brace -lt 0) { return [pscustomobject]@{ Unparsable = $true } }
    $span = Get-BalancedSpan $Text $brace '{' '}'
    if (-not $span) { return [pscustomobject]@{ Unparsable = $true } }

    return [pscustomobject]@{
        Unparsable = $false
        Attributes = $Text.Substring($attrStart, $lineStart - $attrStart)
        Body       = $CodeText.Substring($span.Start + 1, $span.End - $span.Start - 1)
        ScanBody   = $Text.Substring($span.Start + 1, $span.End - $span.Start - 1)
        Line       = ($Text.Substring(0, $m.Index) -split "`n").Count
    }
}



# A manifest entry earns credit only if cargo would actually run it. Three
# distinct answers matter, and collapsing them is what let phantom coverage in:
#
#   registered   -- carries #[test] (or a recognised async-test attribute) and
#                   no attribute that switches it off.
#   unregistered -- an ordinary `fn`. cargo never runs it; it is not a test.
#   disabled     -- #[ignore], or a cfg predicate that is always false such as
#                   #[cfg(any())]. Definitely not run.
#   unknown      -- gated on a cfg this tool cannot evaluate (a feature flag, a
#                   target predicate). It may or may not run, and an unresolved
#                   maybe must never be reported as proof.
function Get-TestRegistration([string]$Attributes) {
    $attrs = if ($Attributes) { $Attributes } else { '' }

    $isTest = $attrs -match '#\[\s*(?:[A-Za-z_][A-Za-z0-9_]*\s*::\s*)*(?:test|tokio\s*::\s*test|async_std\s*::\s*test)\s*(?:\(|\])'
    if (-not $isTest) {
        return [pscustomobject]@{
            State  = 'unregistered'
            Reason = 'the function carries no #[test] attribute, so cargo never runs it'
        }
    }

    if ($attrs -match '#\[\s*ignore\s*[\](]') {
        return [pscustomobject]@{ State = 'disabled'; Reason = 'the test is marked #[ignore]' }
    }

    foreach ($m in [regex]::Matches($attrs, '#\[\s*cfg\s*\(')) {
        $open = $attrs.IndexOf('(', $m.Index + $m.Length - 1)
        $span = Get-BalancedSpan $attrs $open '(' ')'
        if (-not $span) {
            return [pscustomobject]@{ State = 'unknown'; Reason = 'a #[cfg(...)] attribute could not be parsed' }
        }
        $pred = $attrs.Substring($span.Start + 1, $span.End - $span.Start - 1)
        $norm = ($pred -replace '\s', '')

        # `any()` is the empty disjunction: false. `all()` is the empty
        # conjunction: true, and so is the ordinary `#[cfg(test)]` wrapper.
        if ($norm -eq 'any()') {
            return [pscustomobject]@{ State = 'disabled'; Reason = 'the test is gated on #[cfg(any())], which is always false' }
        }
        if ($norm -eq 'all()' -or $norm -eq 'test') { continue }

        return [pscustomobject]@{
            State  = 'unknown'
            Reason = "the test is gated on #[cfg($pred)], which this gate cannot evaluate"
        }
    }

    return [pscustomobject]@{ State = 'registered'; Reason = '' }
}
