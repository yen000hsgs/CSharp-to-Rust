#requires -Version 7
# Uses [System.IO.Path]::GetRelativePath, which is absent in Windows PowerShell 5.1.
# Run with pwsh; under powershell.exe this fails with a MethodNotFound error that
# gives no hint the shell is the cause.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $WorkspaceRoot,

    [Parameter(Mandatory = $true)]
    [string] $Code,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
$sourceExclusionPolicy = 'generated-output-v1'

function ConvertTo-CanonicalJson {
    param(
        [AllowNull()]
        [object] $Value
    )

    if ($null -eq $Value) {
        return 'null'
    }

    if ($Value -is [string]) {
        $options = [System.Text.Json.JsonSerializerOptions]::new()
        $options.Encoder = [System.Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping
        return [System.Text.Json.JsonSerializer]::Serialize([string] $Value, $options)
    }

    if ($Value -is [bool]) {
        return $Value.ToString().ToLowerInvariant()
    }

    if ($Value -is [System.Collections.IDictionary]) {
        $keys = [string[]] @($Value.Keys)
        [Array]::Sort($keys, [System.StringComparer]::Ordinal)
        $entries = foreach ($key in $keys) {
            "$(ConvertTo-CanonicalJson -Value ([string] $key)):$(ConvertTo-CanonicalJson -Value $Value[$key])"
        }
        return "{$($entries -join ',')}"
    }

    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        $propertyNames = [string[]] @($Value.PSObject.Properties.Name)
        [Array]::Sort($propertyNames, [System.StringComparer]::Ordinal)
        $entries = foreach ($propertyName in $propertyNames) {
            $property = $Value.PSObject.Properties[$propertyName]
            "$(ConvertTo-CanonicalJson -Value $property.Name):$(ConvertTo-CanonicalJson -Value $property.Value)"
        }
        return "{$($entries -join ',')}"
    }

    if ($Value -is [System.Collections.IEnumerable]) {
        $items = foreach ($item in $Value) {
            ConvertTo-CanonicalJson -Value $item
        }
        return "[$($items -join ',')]"
    }

    if ($Value -is [byte] -or $Value -is [sbyte] -or $Value -is [short] -or $Value -is [ushort] -or $Value -is [int] -or $Value -is [uint] -or $Value -is [long] -or $Value -is [ulong]) {
        return [Convert]::ToString($Value, [Globalization.CultureInfo]::InvariantCulture)
    }

    throw "Unsupported canonical JSON value type: $($Value.GetType().FullName)"
}

function Get-SourceFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Directory
    )

    foreach ($entry in Get-ChildItem -LiteralPath $Directory -Force) {
        if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Source scope contains a symbolic link or junction: $($entry.FullName)"
        }

        if ($entry.PSIsContainer) {
            if (Test-IsGeneratedSourceDirectory -Directory $entry) {
                continue
            }

            Get-SourceFiles -Directory $entry.FullName
            continue
        }

        $entry
    }
}

function Test-IsGeneratedSourceDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.DirectoryInfo] $Directory
    )

    if ($Directory.Name -in @('.git', '.vs')) {
        return $true
    }
    if ($null -eq $Directory.Parent) {
        return $false
    }
    if ($Directory.Name -in @('bin', 'obj')) {
        $projectFile = Get-ChildItem -LiteralPath $Directory.Parent.FullName -File -Force |
            Where-Object { $_.Extension -in @('.csproj', '.fsproj', '.vbproj') } |
            Select-Object -First 1
        return $null -ne $projectFile
    }
    if ($Directory.Name -eq 'target') {
        return Test-Path -LiteralPath (Join-Path $Directory.Parent.FullName 'Cargo.toml') -PathType Leaf
    }

    return $false
}

function Assert-SourceScopeAllowed {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileSystemInfo] $Item,

        [Parameter(Mandatory = $true)]
        [string] $Workspace
    )

    $current = if ($Item -is [System.IO.FileInfo]) { $Item.Directory } else { $Item }
    while ($null -ne $current -and
        -not $current.FullName.TrimEnd('\', '/').Equals($Workspace, [System.StringComparison]::OrdinalIgnoreCase)) {
        if (Test-IsGeneratedSourceDirectory -Directory $current) {
            throw "Code must not select generated output: $($current.FullName)"
        }
        $current = $current.Parent
    }
}

foreach ($pathValue in @($WorkspaceRoot, $OutputPath)) {
    $normalizedPathValue = $pathValue.Replace('/', '\')
    if ($normalizedPathValue.StartsWith('\\?\', [System.StringComparison]::Ordinal) -or
        $normalizedPathValue.StartsWith('\\.\', [System.StringComparison]::Ordinal) -or
        $normalizedPathValue.StartsWith('\\', [System.StringComparison]::Ordinal) -or
        $normalizedPathValue.IndexOf(':', 2) -ge 0) {
        throw "Device and UNC path namespaces are not allowed."
    }
}

$workspace = [System.IO.Path]::GetFullPath($WorkspaceRoot.Replace('/', '\')).TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $workspace -PathType Container)) {
    throw "WorkspaceRoot does not exist."
}
$normalizedCodeInput = $Code.Replace('/', '\')
$codeSegments = $normalizedCodeInput.Split([char] '\')
if ([string]::IsNullOrWhiteSpace($normalizedCodeInput) -or
    [System.IO.Path]::IsPathRooted($normalizedCodeInput) -or
    $normalizedCodeInput.StartsWith('\\?\', [System.StringComparison]::Ordinal) -or
    $normalizedCodeInput.StartsWith('\\.\', [System.StringComparison]::Ordinal) -or
    $normalizedCodeInput.Contains(':') -or
    $normalizedCodeInput.IndexOfAny([char[]] '<>"|?*') -ge 0 -or
    @($normalizedCodeInput.ToCharArray() | Where-Object { [char]::IsControl($_) }).Count -gt 0 -or
    @($codeSegments | Where-Object {
        [string]::IsNullOrEmpty($_) -or
        $_ -eq '..' -or
        ($_ -eq '.' -and $normalizedCodeInput -ne '.') -or
        ($_ -ne '.' -and ($_.EndsWith('.') -or $_.EndsWith(' ')))
    }).Count -gt 0) {
    throw "Code must be a canonical relative path."
}
$codePath = [System.IO.Path]::GetFullPath((Join-Path $workspace $normalizedCodeInput))
$output = [System.IO.Path]::GetFullPath($OutputPath.Replace('/', '\'))
$codeIsWorkspace = $codePath.TrimEnd('\', '/').Equals($workspace, [System.StringComparison]::OrdinalIgnoreCase)
if ((-not $codeIsWorkspace -and -not $codePath.StartsWith("$workspace$([System.IO.Path]::DirectorySeparatorChar)", [System.StringComparison]::OrdinalIgnoreCase)) -or
    -not (Test-Path -LiteralPath $codePath)) {
    throw "Code must resolve inside WorkspaceRoot."
}
$relativeCodeScope = [System.IO.Path]::GetRelativePath($workspace, $codePath)
$normalizedCodePath = $codePath.TrimEnd('\', '/')
if ($output.Equals($normalizedCodePath, [System.StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith("$normalizedCodePath$([System.IO.Path]::DirectorySeparatorChar)", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputPath must be outside the selected source scope."
}
$outputAncestorPath = if (Test-Path -LiteralPath $output) { $output } else { Split-Path -Parent $output }
while (-not (Test-Path -LiteralPath $outputAncestorPath)) {
    $parentPath = Split-Path -Parent $outputAncestorPath
    if ([string]::IsNullOrWhiteSpace($parentPath) -or $parentPath -eq $outputAncestorPath) {
        throw "OutputPath has no existing trusted ancestor."
    }
    $outputAncestorPath = $parentPath
}
$outputAncestor = Get-Item -Force -LiteralPath $outputAncestorPath
if (($outputAncestor.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "OutputPath contains a symbolic link or junction: $($outputAncestor.FullName)"
}
if ($outputAncestor -is [System.IO.FileInfo]) {
    $outputAncestor = $outputAncestor.Directory
}
while ($null -ne $outputAncestor) {
    if (($outputAncestor.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "OutputPath contains a symbolic link or junction: $($outputAncestor.FullName)"
    }
    $outputAncestor = $outputAncestor.Parent
}

$item = Get-Item -Force -LiteralPath $codePath
if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Source scope contains a symbolic link or junction: $($item.FullName)"
}
Assert-SourceScopeAllowed -Item $item -Workspace $workspace
$current = if ($item -is [System.IO.FileInfo]) { $item.Directory } else { $item }
$reachedRoot = $false
while ($null -ne $current) {
    if (($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Source scope contains a symbolic link or junction: $($current.FullName)"
    }
    if ($current.FullName.TrimEnd('\', '/').Equals($workspace, [System.StringComparison]::OrdinalIgnoreCase)) {
        $reachedRoot = $true
        break
    }
    $current = $current.Parent
}
if (-not $reachedRoot) {
    throw "Source scope escapes WorkspaceRoot through an unresolved ancestor."
}

$entries = if (Test-Path -LiteralPath $codePath -PathType Leaf) {
    @((Get-Item -Force -LiteralPath $codePath))
}
else {
    @(Get-SourceFiles -Directory $codePath)
}
if ($entries.Count -eq 0) {
    throw "Source scope contains no files."
}

$paths = [string[]] @($entries | ForEach-Object { [System.IO.Path]::GetRelativePath($workspace, $_.FullName) })
[Array]::Sort($paths, [System.StringComparer]::Ordinal)
$pathSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($path in $paths) {
    if (-not $pathSet.Add($path)) {
        throw "Source scope contains case-ambiguous paths."
    }
}

$files = foreach ($path in $paths) {
    [ordered]@{
        path = $path
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $workspace $path)).Hash.ToLowerInvariant()
    }
}
$manifest = [ordered]@{
    schema_version = '1.1'
    canonicalization = 'sorted-json-integer-v1'
    code = $relativeCodeScope
    exclusion_policy = $sourceExclusionPolicy
    files = @($files)
}
$canonical = ConvertTo-CanonicalJson $manifest
$schemaPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'contracts\source-manifest.schema.json'
try {
    $schemaValid = $canonical | Test-Json -SchemaFile $schemaPath -ErrorAction Stop
}
catch {
    throw "Generated source manifest could not be validated: $($_.Exception.Message)"
}
if (-not $schemaValid) {
    throw "Generated source manifest does not match its JSON schema."
}

$parent = Split-Path -Parent $output
if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}
[System.IO.File]::WriteAllText($output, $canonical, [Text.UTF8Encoding]::new($false))
[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))).ToLowerInvariant()
