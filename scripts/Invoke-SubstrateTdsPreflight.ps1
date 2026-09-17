[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $RunId,

    [Parameter(Mandatory = $true)]
    [string] $WorkspaceRoot,

    [Parameter(Mandatory = $true)]
    [string] $Code,

    [Parameter(Mandatory = $true)]
    [string] $ArtifactRoot,

    [Parameter(Mandatory = $true)]
    [string] $SourceManifest,

    [Parameter(Mandatory = $true)]
    [string] $SourceSha256,

    [Parameter(Mandatory = $true)]
    [string] $TdsMachine,

    [Parameter(Mandatory = $true)]
    [string] $EnvironmentConfig,

    [Parameter(Mandatory = $true)]
    [string] $DependencyManifest,

    [Parameter(Mandatory = $true)]
    [string] $AttestationKeyPath,

    [Parameter(Mandatory = $true)]
    [string] $AttestationKeyId
)

$ErrorActionPreference = 'Stop'

trap {
    $message = $_.Exception.Message
    if ($message.StartsWith('ENVIRONMENT_BLOCKED:', [System.StringComparison]::Ordinal)) {
        [Console]::Error.WriteLine($message)
        exit 4
    }

    if ($message.StartsWith('INVALID_INPUT:', [System.StringComparison]::Ordinal)) {
        [Console]::Error.WriteLine($message)
        exit 2
    }

    if ($message -match '^(ArtifactRoot|Trusted root|Trusted Git executable|Path escapes|Path contains|WorkspaceRoot|Git administrative|Git object|Git replacement|Git index|Git attributes|Sparse checkout|Repository-local Git|Untracked workspace|Required build input|Code path|Code file|EnvironmentConfig|DependencyManifest|Dependency manifest|RunId|TdsMachine|Substrate origin|Trusted instruction|Dependency entry|Attestation key|AttestationKeyId|Generated preflight|Source manifest|SourceSha256)') {
        [Console]::Error.WriteLine("INVALID_INPUT: $message")
        exit 2
    }

    [Console]::Error.WriteLine("PREFLIGHT_ERROR: $message")
    exit 5
}

Get-ChildItem Env: |
    Where-Object { $_.Name -in @('GIT_ALTERNATE_OBJECT_DIRECTORIES', 'GIT_ASKPASS', 'GIT_COMMON_DIR', 'GIT_CONFIG_COUNT', 'GIT_CONFIG_GLOBAL', 'GIT_CONFIG_PARAMETERS', 'GIT_CONFIG_SYSTEM', 'GIT_DIR', 'GIT_DIFF_OPTS', 'GIT_EXEC_PATH', 'GIT_EXTERNAL_DIFF', 'GIT_GLOB_PATHSPECS', 'GIT_ICASE_PATHSPECS', 'GIT_INDEX_FILE', 'GIT_LITERAL_PATHSPECS', 'GIT_NOGLOB_PATHSPECS', 'GIT_OBJECT_DIRECTORY', 'GIT_SSH', 'GIT_SSH_COMMAND', 'GIT_WORK_TREE', 'SSH_ASKPASS') -or $_.Name -match '^GIT_CONFIG_(KEY|VALUE)_\d+$' } |
    Remove-Item

function Resolve-ContainedPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [Parameter(Mandatory = $true)]
        [string] $Child
    )

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "Trusted root does not exist: $Root"
    }

    $resolvedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $resolvedChild = [System.IO.Path]::GetFullPath((Join-Path $resolvedRoot $Child))
    $isRoot = $resolvedChild.Equals($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)
    $isChild = $resolvedChild.StartsWith("$resolvedRoot$([System.IO.Path]::DirectorySeparatorChar)", [System.StringComparison]::OrdinalIgnoreCase)
    if (-not $isRoot -and -not $isChild) {
        throw "Path escapes its trusted root: $Child"
    }

    if (Test-Path -LiteralPath $resolvedChild) {
        $item = Get-Item -Force -LiteralPath $resolvedChild
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Path contains a symbolic link or junction: $($item.FullName)"
        }
        $current = if ($item -is [System.IO.FileInfo]) { $item.Directory } else { $item }
        $reachedRoot = $false
        while ($null -ne $current) {
            if (($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Path contains a symbolic link or junction: $($current.FullName)"
            }

            $currentPath = [System.IO.Path]::GetFullPath($current.FullName).TrimEnd('\', '/')
            if ($currentPath.Equals($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                $reachedRoot = $true
                break
            }

            $current = $current.Parent
        }
        if (-not $reachedRoot) {
            throw "Path escapes its trusted root through an unresolved ancestor: $Child"
        }
    }

    return $resolvedChild
}

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    $output = Invoke-GitBytes -Arguments $Arguments
    return [Text.Encoding]::UTF8.GetString($output).TrimEnd("`r", "`n").Split("`n") | ForEach-Object { $_.TrimEnd("`r") }
}

function Invoke-GitBytes {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $script:GitExecutable
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($name in @($startInfo.Environment.Keys | Where-Object { $_ -in @('GIT_ALTERNATE_OBJECT_DIRECTORIES', 'GIT_ASKPASS', 'GIT_COMMON_DIR', 'GIT_CONFIG_COUNT', 'GIT_CONFIG_GLOBAL', 'GIT_CONFIG_PARAMETERS', 'GIT_CONFIG_SYSTEM', 'GIT_DIR', 'GIT_DIFF_OPTS', 'GIT_EXEC_PATH', 'GIT_EXTERNAL_DIFF', 'GIT_GLOB_PATHSPECS', 'GIT_ICASE_PATHSPECS', 'GIT_INDEX_FILE', 'GIT_LITERAL_PATHSPECS', 'GIT_NOGLOB_PATHSPECS', 'GIT_OBJECT_DIRECTORY', 'GIT_SSH', 'GIT_SSH_COMMAND', 'GIT_WORK_TREE', 'SSH_ASKPASS') -or $_ -match '^GIT_CONFIG_(KEY|VALUE)_\d+$' })) {
        $startInfo.Environment.Remove($name)
    }
    $startInfo.Environment['GIT_CONFIG_NOSYSTEM'] = '1'
    $startInfo.Environment['GIT_CONFIG_GLOBAL'] = 'NUL'
    $startInfo.Environment['GIT_NO_LAZY_FETCH'] = '1'
    $startInfo.Environment['GIT_OPTIONAL_LOCKS'] = '0'

    $startInfo.ArgumentList.Add('--no-lazy-fetch')
    $startInfo.ArgumentList.Add('--no-replace-objects')
    $startInfo.ArgumentList.Add('-C')
    $startInfo.ArgumentList.Add($WorkspaceRoot)
    $startInfo.ArgumentList.Add('-c')
    $startInfo.ArgumentList.Add('core.fsmonitor=false')
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "Failed to start Git."
    }

    $output = [System.IO.MemoryStream]::new()
    $copyTask = $process.StandardOutput.BaseStream.CopyToAsync($output)
    $errorTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $copyTask.GetAwaiter().GetResult() | Out-Null
    $errorText = $errorTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) {
        throw $errorText
    }

    return ,$output.ToArray()
}

function Get-TextDigest {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string] $Text
    )

    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()
}

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
        return ([System.Convert]::ToString($Value, [Globalization.CultureInfo]::InvariantCulture))
    }

    throw "Unsupported canonical JSON value type: $($Value.GetType().FullName)"
}

function Test-FileEvidence {
    param(
        [AllowNull()]
        [object] $Evidence,

        [AllowNull()]
        [AllowEmptyCollection()]
        [string[]] $AdditionalProperties = @(),

        [Parameter(Mandatory = $true)]
        [string] $EvidenceField,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary] $TrustedProcedures
    )

    $requiredProperties = @('artifact', 'sha256', 'validation') + @($AdditionalProperties | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if (-not (Test-RequiredProperties -Value $Evidence -Properties $requiredProperties)) {
        return $false
    }

    if ($Evidence.sha256 -notmatch '^[0-9a-f]{64}$') {
        return $false
    }

    try {
        $artifactPath = Resolve-ContainedPath -Root $WorkspaceRoot -Child $Evidence.artifact
    }
    catch {
        return $false
    }

    if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
        return $false
    }

    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $artifactPath).Hash.ToLowerInvariant()
    if ($actualHash -ne $Evidence.sha256) {
        return $false
    }

    if (-not (Test-RequiredProperties -Value $Evidence.validation -Properties @('procedureId', 'instructionHash'))) {
        return $false
    }

    $procedureId = [string]$Evidence.validation.procedureId
    if (-not $TrustedProcedures.Contains($procedureId)) {
        return $false
    }

    $procedure = $TrustedProcedures[$procedureId]
    return $procedure.evidenceFields -contains $EvidenceField -and
        $Evidence.validation.instructionHash -eq $procedure.instructionHash
}

function Test-GitAdministrativeStorage {
    $gitDirectory = Resolve-ContainedPath -Root $WorkspaceRoot -Child '.git'
    if (-not (Test-Path -LiteralPath $gitDirectory -PathType Container)) {
        throw "WorkspaceRoot must use a local Git administrative directory."
    }

    foreach ($forbiddenPath in @('.git\commondir', '.git\objects\info\alternates')) {
        $resolvedForbiddenPath = Resolve-ContainedPath -Root $WorkspaceRoot -Child $forbiddenPath
        if (Test-Path -LiteralPath $resolvedForbiddenPath) {
            throw "Git administrative storage may not reference external object or common directories: $forbiddenPath"
        }
    }

    $objectDirectory = Resolve-ContainedPath -Root $WorkspaceRoot -Child '.git\objects'
    if (-not (Test-Path -LiteralPath $objectDirectory -PathType Container)) {
        throw "Git object storage is missing."
    }
    $administrativeReparsePoint = Get-ChildItem -LiteralPath $gitDirectory -Recurse -Force |
        Where-Object { ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 } |
        Select-Object -First 1
    if ($null -ne $administrativeReparsePoint) {
        throw "Git administrative storage contains a symbolic link or junction: $($administrativeReparsePoint.FullName)"
    }

    $localConfigPath = Resolve-ContainedPath -Root $WorkspaceRoot -Child '.git\config'
    if (-not (Test-Path -LiteralPath $localConfigPath -PathType Leaf)) {
        throw "Repository-local Git configuration is missing."
    }
    if ((Get-Content -Raw -LiteralPath $localConfigPath) -match '(?im)^\s*\[include(?:If)?(?:\s+[^\]]+)?\]\s*(?:[#;].*)?$') {
        throw "Repository-local Git configuration includes external configuration."
    }

    $worktreeConfigPath = Resolve-ContainedPath -Root $WorkspaceRoot -Child '.git\config.worktree'
    if (Test-Path -LiteralPath $worktreeConfigPath) {
        throw "Repository-local Git worktree configuration is not allowed."
    }
}

function Get-WorkspaceState {
    param(
        [Parameter(Mandatory = $true)]
        [string] $CodePath
    )

    try {
        $gitRoot = (Invoke-Git -Arguments @('rev-parse', '--show-toplevel')).Trim()
    }
    catch {
        throw "WorkspaceRoot must be a Git repository root."
    }
    $resolvedWorkspace = [System.IO.Path]::GetFullPath($WorkspaceRoot).TrimEnd('\', '/')
    $resolvedGitRoot = [System.IO.Path]::GetFullPath($gitRoot).TrimEnd('\', '/')
    if (-not $resolvedWorkspace.Equals($resolvedGitRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "WorkspaceRoot must be the Substrate Git repository root."
    }

    foreach ($replacementPath in @('.git\refs\replace', '.git\info\grafts')) {
        $resolvedReplacementPath = Resolve-ContainedPath -Root $WorkspaceRoot -Child $replacementPath
        if (Test-Path -LiteralPath $resolvedReplacementPath) {
            throw "Git replacement objects or grafts are not allowed: $replacementPath"
        }
    }

    $indexEntries = [Text.Encoding]::UTF8.GetString((Invoke-GitBytes -Arguments @('ls-files', '-v', '-z'))).Split([char] 0, [StringSplitOptions]::RemoveEmptyEntries)
    $specialIndexEntry = $indexEntries | Where-Object { $_[0] -ne 'H' } | Select-Object -First 1
    if ($null -ne $specialIndexEntry) {
        throw "Git index contains a skip-worktree, assume-unchanged, unmerged, or otherwise special entry: $specialIndexEntry"
    }

    $sparseCheckoutPath = Resolve-ContainedPath -Root $WorkspaceRoot -Child '.git\info\sparse-checkout'
    if (Test-Path -LiteralPath $sparseCheckoutPath) {
        throw "Sparse checkout is not allowed for an attested workspace."
    }

    $head = (Invoke-Git -Arguments @('rev-parse', 'HEAD')).Trim()
    $dangerousConfig = @(Invoke-Git -Arguments @('config', '--local', '--list')) |
        Where-Object { $_ -match '^(include\.path|includeif\..+\.path|extensions\.partialclone|remote\..+\.(promisor|partialclonefilter|proxy)|core\.(fsmonitor|sshcommand|gitproxy)|protocol\..+\.allow|url\..+\.(insteadof|pushinsteadof)|diff\..*\.(command|textconv)|filter\..*\.(clean|process|smudge)|credential(?:\..+)?\.helper)=' }
    if ($dangerousConfig.Count -gt 0) {
        throw "Repository-local Git configuration can execute external programs: $($dangerousConfig -join ', ')"
    }

    $attributeFiles = @(Get-ChildItem -LiteralPath $WorkspaceRoot -Filter '.gitattributes' -File -Recurse -Force | Select-Object -ExpandProperty FullName)
    $infoAttributesPath = Resolve-ContainedPath -Root $WorkspaceRoot -Child '.git\info\attributes'
    if (Test-Path -LiteralPath $infoAttributesPath -PathType Leaf) {
        $attributeFiles += $infoAttributesPath
    }
    foreach ($attributeFile in $attributeFiles) {
        $attributeRelativePath = [System.IO.Path]::GetRelativePath($resolvedWorkspace, $attributeFile)
        $attributePath = Resolve-ContainedPath -Root $WorkspaceRoot -Child $attributeRelativePath
        if ((Get-Content -Raw -LiteralPath $attributePath) -match '(?im)(^|\s)filter(?:=|\s|$)') {
            throw "Git attributes may not configure content filters: $attributeRelativePath"
        }
    }

    $codeEntries = if (Test-Path -LiteralPath $CodePath -PathType Leaf) {
        @((Get-Item -Force -LiteralPath $CodePath))
    }
    else {
        $descendants = @(Get-ChildItem -LiteralPath $CodePath -Recurse -Force)
        $reparsePoint = $descendants | Where-Object { ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 } | Select-Object -First 1
        if ($null -ne $reparsePoint) {
            throw "Code path contains a symbolic link or junction: $($reparsePoint.FullName)"
        }

        @($descendants | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName)
    }
    if ($codeEntries.Count -eq 0) {
        throw "Code path contains no files: $CodePath"
    }

    $codeHashes = [ordered]@{}
    foreach ($codeEntry in $codeEntries) {
        $relativeCodePath = [System.IO.Path]::GetRelativePath($resolvedWorkspace, $codeEntry.FullName)
        $validatedCodePath = Resolve-ContainedPath -Root $WorkspaceRoot -Child $relativeCodePath
        $codeHashes[$relativeCodePath] = (Get-FileHash -Algorithm SHA256 -LiteralPath $validatedCodePath).Hash.ToLowerInvariant()
    }

    $baselineHashes = [ordered]@{}
    $baselinePathSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($projectPath in @($manifest.sourceProject, $manifest.sourceTests)) {
        $resolvedProjectPath = Resolve-ContainedPath -Root $WorkspaceRoot -Child $projectPath
        if (-not (Test-Path -LiteralPath $resolvedProjectPath -PathType Leaf)) {
            throw "Code file is missing: $projectPath"
        }

        $projectEntries = @(Get-ChildItem -LiteralPath (Split-Path -Parent $resolvedProjectPath) -Recurse -Force)
        $projectReparsePoint = $projectEntries | Where-Object { ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 } | Select-Object -First 1
        if ($null -ne $projectReparsePoint) {
            throw "Code path contains a symbolic link or junction: $($projectReparsePoint.FullName)"
        }

        foreach ($projectEntry in @($projectEntries | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName)) {
            $relativeProjectPath = [System.IO.Path]::GetRelativePath($resolvedWorkspace, $projectEntry.FullName)
            if (-not $baselinePathSet.Add($relativeProjectPath)) {
                throw "Code path contains case-colliding or duplicate baseline files: $relativeProjectPath"
            }
            $validatedProjectPath = Resolve-ContainedPath -Root $WorkspaceRoot -Child $relativeProjectPath
            $baselineHashes[$relativeProjectPath] = (Get-FileHash -Algorithm SHA256 -LiteralPath $validatedProjectPath).Hash.ToLowerInvariant()
        }
    }

    $attestedWorkspaceInputs = [ordered]@{}
    $attestedInputPathSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($relativePath in $environment.attestedWorkspaceInputs) {
        $path = Resolve-ContainedPath -Root $WorkspaceRoot -Child $relativePath
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Required build input is missing: $relativePath"
        }

        $inputFiles = if (Test-Path -LiteralPath $path -PathType Container) {
            $descendants = @(Get-ChildItem -LiteralPath $path -Recurse -Force)
            $reparsePoint = $descendants | Where-Object { ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 } | Select-Object -First 1
            if ($null -ne $reparsePoint) {
                throw "Required build input contains a symbolic link or junction: $($reparsePoint.FullName)"
            }

            @($descendants | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName)
        }
        else {
            @((Get-Item -LiteralPath $path -Force))
        }
        if ($inputFiles.Count -eq 0) {
            throw "Required build input contains no files: $relativePath"
        }

        foreach ($inputFile in $inputFiles) {
            $inputRelativePath = [System.IO.Path]::GetRelativePath($resolvedWorkspace, $inputFile.FullName)
            if (-not $attestedInputPathSet.Add($inputRelativePath)) {
                throw "Required build input contains case-colliding or duplicate files: $inputRelativePath"
            }
            $validatedPath = Resolve-ContainedPath -Root $WorkspaceRoot -Child $inputRelativePath
            $attestedWorkspaceInputs[$inputRelativePath] = (Get-FileHash -Algorithm SHA256 -LiteralPath $validatedPath).Hash.ToLowerInvariant()
        }
    }

    return [ordered]@{
        head = $head
        code_files = $codeHashes
        baseline_files = $baselineHashes
        attested_workspace_inputs = $attestedWorkspaceInputs
    }
}

function Test-RequiredProperties {
    param(
        [AllowNull()]
        [object] $Value,

        [Parameter(Mandatory = $true)]
        [string[]] $Properties
    )

    if ($null -eq $Value -or $Value -isnot [System.Management.Automation.PSCustomObject]) {
        return $false
    }

    foreach ($property in $Properties) {
        if ($Value.PSObject.Properties.Name -notcontains $property) {
            return $false
        }

        $propertyValue = $Value.$property
        if ($null -eq $propertyValue -or ($propertyValue -is [string] -and [string]::IsNullOrWhiteSpace($propertyValue))) {
            return $false
        }
    }

    return $true
}

$verifierRoot = Split-Path -Parent $PSScriptRoot
$expectedEnvironmentConfig = '.github\verification-environments\substrate-tds.json'
$expectedDependencyManifest = 'targets\route-resolution-client.json'
$normalizedEnvironmentConfig = $EnvironmentConfig.Replace('/', '\')
$normalizedDependencyManifest = $DependencyManifest.Replace('/', '\')
if ($normalizedEnvironmentConfig.StartsWith('.\', [System.StringComparison]::Ordinal)) {
    $normalizedEnvironmentConfig = $normalizedEnvironmentConfig.Substring(2)
}
if ($normalizedDependencyManifest.StartsWith('.\', [System.StringComparison]::Ordinal)) {
    $normalizedDependencyManifest = $normalizedDependencyManifest.Substring(2)
}
if (-not $normalizedEnvironmentConfig.Equals($expectedEnvironmentConfig, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "EnvironmentConfig must be the reviewed Substrate TDS profile: $expectedEnvironmentConfig"
}

if (-not $normalizedDependencyManifest.Equals($expectedDependencyManifest, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "DependencyManifest must be the reviewed RouteResolutionClient target: $expectedDependencyManifest"
}

$environmentPath = Resolve-ContainedPath -Root $verifierRoot -Child $expectedEnvironmentConfig
$manifestPath = Resolve-ContainedPath -Root $verifierRoot -Child $expectedDependencyManifest
$sourceManifestPath = Resolve-ContainedPath -Root $ArtifactRoot -Child $SourceManifest
$codePath = Resolve-ContainedPath -Root $WorkspaceRoot -Child $Code
if (-not (Test-Path -LiteralPath $codePath)) {
    throw "Code path does not exist: $Code"
}

$environment = Get-Content -Raw $environmentPath | ConvertFrom-Json
$manifest = Get-Content -Raw $manifestPath | ConvertFrom-Json
$sourceManifestBytes = [System.IO.File]::ReadAllBytes($sourceManifestPath)
if ($SourceSha256 -notmatch '^[0-9a-f]{64}$' -or
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($sourceManifestBytes)).ToLowerInvariant() -ne $SourceSha256) {
    throw "SourceSha256 does not match the exact source manifest bytes."
}
try {
    $sourceManifestJson = [Text.UTF8Encoding]::new($false, $true).GetString($sourceManifestBytes)
}
catch {
    throw "Source manifest is not valid UTF-8."
}
if ($sourceManifestJson.StartsWith([char]0xFEFF) -or
    -not ($sourceManifestJson | Test-Json -SchemaFile (Resolve-ContainedPath -Root $verifierRoot -Child 'contracts\source-manifest.schema.json'))) {
    throw "Source manifest does not match its JSON schema or canonical encoding."
}
$sourceManifestObject = $sourceManifestJson | ConvertFrom-Json
if ((ConvertTo-CanonicalJson -Value $sourceManifestObject) -ne $sourceManifestJson) {
    throw "Source manifest bytes are not canonical sorted-json-integer-v1."
}
if ($manifest.environmentProfile.path -ne $expectedEnvironmentConfig) {
    throw "Dependency manifest does not reference the reviewed environment profile."
}

$script:GitExecutable = [System.IO.Path]::GetFullPath($environment.trustedGit.path)
foreach ($sourceRoot in @([System.IO.Path]::GetFullPath($WorkspaceRoot), [System.IO.Path]::GetFullPath($verifierRoot))) {
    $normalizedSourceRoot = $sourceRoot.TrimEnd('\', '/')
    if ($script:GitExecutable.Equals($normalizedSourceRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        $script:GitExecutable.StartsWith("$normalizedSourceRoot$([System.IO.Path]::DirectorySeparatorChar)", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Trusted Git executable must be outside source and verifier repositories."
    }
}
if (-not (Test-Path -LiteralPath $script:GitExecutable -PathType Leaf)) {
    throw "Trusted Git executable does not exist."
}
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $script:GitExecutable).Hash.ToLowerInvariant() -ne $environment.trustedGit.sha256) {
    throw "Trusted Git executable hash does not match the environment profile."
}
if ((Get-AuthenticodeSignature -LiteralPath $script:GitExecutable).Status.ToString() -ne $environment.trustedGit.signatureStatus) {
    throw "Trusted Git executable signature status does not match the environment profile."
}

Test-GitAdministrativeStorage

$adapterPath = Resolve-ContainedPath -Root $verifierRoot -Child $manifest.rustDeploymentAdapter.path
$adapter = Get-Content -Raw $adapterPath | ConvertFrom-Json
$codeRelativePath = [System.IO.Path]::GetRelativePath([System.IO.Path]::GetFullPath($WorkspaceRoot), $codePath)
$workspaceState = Get-WorkspaceState -CodePath $codePath
$expectedManifestWorkspace = [System.IO.Path]::GetFullPath($WorkspaceRoot).TrimEnd('\', '/')
if (-not $sourceManifestObject.workspace_root.TrimEnd('\', '/').Equals($expectedManifestWorkspace, [System.StringComparison]::OrdinalIgnoreCase) -or
    $sourceManifestObject.code.Replace('/', '\') -ne $codeRelativePath) {
    throw "Source manifest identifies a different workspace or code scope."
}
$manifestCodeFiles = [ordered]@{}
$manifestPathSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$previousManifestPath = $null
foreach ($file in $sourceManifestObject.files) {
    $normalizedFilePath = $file.path.Replace('/', '\')
    if ($normalizedFilePath -ne $file.path -or [System.IO.Path]::IsPathRooted($normalizedFilePath) -or ($normalizedFilePath -split '\\') -contains '..') {
        throw "Source manifest contains a non-canonical file path: $($file.path)"
    }
    if (-not $manifestPathSet.Add($normalizedFilePath) -or
        ($null -ne $previousManifestPath -and [string]::CompareOrdinal($previousManifestPath, $normalizedFilePath) -ge 0)) {
        throw "Source manifest paths must be unique and ordinally sorted."
    }
    $manifestCodeFiles[$normalizedFilePath] = $file.sha256
    $previousManifestPath = $normalizedFilePath
}
if ($manifestCodeFiles.Count -ne $sourceManifestObject.files.Count -or
    $manifestCodeFiles.Count -ne $workspaceState.code_files.Count -or
    (ConvertTo-CanonicalJson -Value $manifestCodeFiles) -ne (ConvertTo-CanonicalJson -Value $workspaceState.code_files)) {
    throw "Source manifest does not exactly match the selected code scope."
}

if ([string]::IsNullOrWhiteSpace($RunId)) {
    throw "RunId is required."
}

if ([string]::IsNullOrWhiteSpace($TdsMachine) -or $TdsMachine -eq 'auto' -or $TdsMachine.IndexOfAny([char[]]@('*', '?')) -ge 0) {
    throw "TdsMachine must identify one explicit machine."
}

try {
    $actualRemote = (Invoke-Git -Arguments @('remote', 'get-url', 'origin') | Out-String).Trim()
}
catch {
    throw "Substrate origin is missing or unreadable."
}
if ($actualRemote -ne $environment.trustedRepositoryUrl) {
    throw "Substrate origin does not match the trusted repository URL."
}

$commit = $environment.trustedInstructionCommit
$commitType = $null
try {
    $commitType = (Invoke-Git -Arguments @('cat-file', '-t', $commit) | Out-String).Trim()
}
catch {
    $commitType = $null
}
if ($commitType -ne 'commit') {
    throw "ENVIRONMENT_BLOCKED: The trusted instruction commit is absent. Provision it through the trusted workspace setup before preflight."
}

$instructionHashes = [ordered]@{}
foreach ($relativePath in $manifest.trustedInstructionPaths) {
    $workspacePath = Resolve-ContainedPath -Root $WorkspaceRoot -Child $relativePath
    if (-not (Test-Path -LiteralPath $workspacePath -PathType Leaf)) {
        throw "Trusted instruction file is missing: $relativePath"
    }

    $pinnedInstruction = Invoke-GitBytes -Arguments @('show', "$commit`:$($relativePath.Replace('\', '/'))")
    $pinnedInstructionHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($pinnedInstruction)).ToLowerInvariant()
    $workspaceInstructionHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $workspacePath).Hash.ToLowerInvariant()
    if ($workspaceInstructionHash -ne $pinnedInstructionHash) {
        throw "Trusted instruction file differs from pinned commit: $relativePath"
    }

    $instructionHashes[$relativePath] = $workspaceInstructionHash
}
$trustedProcedures = [ordered]@{}
if (-not (Test-RequiredProperties -Value $manifest -Properties @('trustedProcedures'))) {
    throw "Dependency manifest must define trustedProcedures."
}
foreach ($procedure in $manifest.trustedProcedures) {
    if (-not (Test-RequiredProperties -Value $procedure -Properties @('id', 'instructionPath', 'evidenceFields')) -or
        $procedure.id -notmatch '^[A-Za-z0-9._-]+$' -or
        $trustedProcedures.Contains($procedure.id) -or
        -not $instructionHashes.Contains($procedure.instructionPath) -or
        @($procedure.evidenceFields).Count -eq 0) {
        throw "Dependency manifest contains an invalid or duplicate trusted procedure."
    }

    $trustedProcedures[$procedure.id] = [pscustomobject]@{
        instructionHash = $instructionHashes[$procedure.instructionPath]
        evidenceFields = @($procedure.evidenceFields)
    }
}

$allowedStrategies = @('rust-native', 'generated', 'bridge', 'test-double', 'unavailable')
$blockingDependencies = @()
foreach ($dependency in $manifest.dependencies) {
    foreach ($requiredProperty in @('name', 'strategy', 'runtimeRequired', 'ready', 'evidence')) {
        if ($dependency.PSObject.Properties.Name -notcontains $requiredProperty) {
            throw "Dependency entry is missing '$requiredProperty'."
        }
    }

    if ($dependency.strategy -notin $allowedStrategies) {
        throw "Dependency '$($dependency.name)' has unsupported strategy '$($dependency.strategy)'."
    }

    if (-not $dependency.runtimeRequired) {
        continue
    }

    if ($dependency.strategy -eq 'generated' -and
        ($dependency.PSObject.Properties.Name -notcontains 'source' -or [string]::IsNullOrWhiteSpace($dependency.source))) {
        throw "Dependency entry '$($dependency.name)' must declare its authoritative generated source."
    }

    $requiredEvidence = switch ($dependency.strategy) {
        'rust-native' { @() }
        'generated' { @('source', 'sourceSha256') }
        'bridge' { @('boundary', 'owner') }
        default { @() }
    }
    $hasEvidence = $dependency.strategy -notin @('test-double', 'unavailable') -and
        (Test-FileEvidence -Evidence $dependency.evidence -AdditionalProperties $requiredEvidence `
            -EvidenceField 'dependency' -TrustedProcedures $trustedProcedures)
    if ($dependency.strategy -eq 'generated' -and $hasEvidence) {
        try {
            if ($dependency.evidence.source.Replace('/', '\') -ne $dependency.source.Replace('/', '\')) {
                throw "Generated evidence source does not match the dependency source."
            }
            $sourcePath = Resolve-ContainedPath -Root $WorkspaceRoot -Child $dependency.source
            $hasEvidence = (Test-Path -LiteralPath $sourcePath -PathType Leaf) -and
                $dependency.evidence.sourceSha256 -match '^[0-9a-f]{64}$' -and
                (Get-FileHash -Algorithm SHA256 -LiteralPath $sourcePath).Hash.ToLowerInvariant() -eq $dependency.evidence.sourceSha256
        }
        catch {
            $hasEvidence = $false
        }
    }

    if ($dependency.strategy -in @('test-double', 'unavailable') -or $dependency.ready -ne $true -or -not $hasEvidence) {
        $blockingDependencies += $dependency.name
    }
}

$requiredAdapterEvidence = [ordered]@{
    artifact = @('path', 'sha256')
    hostOrBridgeBoundary = @('description', 'owner')
    deploymentDestination = @('path')
    activation = @('procedureId', 'instructionHash')
    healthCheck = @('procedureId', 'instructionHash')
    rollback = @('procedureId', 'instructionHash')
    evidenceVerification = @('algorithm', 'trustedIdentity', 'keyId', 'verifier')
    toolchainManifest = @('path', 'sha256')
    restoreState = @('path', 'sha256')
    executionPlan = @('path', 'sha256')
    controlPlaneTestScript = @('path', 'sha256', 'repository', 'commit')
}
$blockingAdapterFields = @()
foreach ($field in $requiredAdapterEvidence.Keys) {
    $value = $adapter.candidate.$field
    $hasEvidence = $null -ne $value -and (Test-RequiredProperties -Value $value.evidence -Properties $requiredAdapterEvidence[$field])
    if ($hasEvidence -and $field -eq 'artifact') {
        $hasEvidence = Test-FileEvidence -Evidence ([pscustomobject]@{
            artifact = $value.evidence.path
            sha256 = $value.evidence.sha256
            validation = $value.evidence.validation
        }) -AdditionalProperties @() -EvidenceField 'artifact' -TrustedProcedures $trustedProcedures
    }
    elseif ($hasEvidence -and $field -in @('toolchainManifest', 'restoreState', 'executionPlan', 'controlPlaneTestScript')) {
        try {
            $evidencePath = Resolve-ContainedPath -Root $WorkspaceRoot -Child $value.evidence.path
            $hasEvidence = (Test-Path -LiteralPath $evidencePath -PathType Leaf) -and
                $value.evidence.sha256 -match '^[0-9a-f]{64}$' -and
                (Get-FileHash -Algorithm SHA256 -LiteralPath $evidencePath).Hash.ToLowerInvariant() -eq $value.evidence.sha256
        }
        catch {
            $hasEvidence = $false
        }
    }
    elseif ($hasEvidence -and $field -in @('activation', 'healthCheck', 'rollback')) {
        $procedureId = [string]$value.evidence.procedureId
        if ($trustedProcedures.Contains($procedureId)) {
            $procedure = $trustedProcedures[$procedureId]
            $hasEvidence = $procedure.evidenceFields -contains $field -and
                $value.evidence.instructionHash -eq $procedure.instructionHash
        }
        else {
            $hasEvidence = $false
        }
    }

    if ($null -eq $value -or $value.status -ne 'ready' -or -not $hasEvidence) {
        $blockingAdapterFields += $field
    }
}
$controlPlaneProvenanceValidationImplemented = $false
if (-not $controlPlaneProvenanceValidationImplemented -and $blockingAdapterFields -notcontains 'controlPlaneTestScript') {
    $blockingAdapterFields += 'controlPlaneTestScript'
}
$csharpBaselineGraphValidationImplemented = $false
if (-not $csharpBaselineGraphValidationImplemented) {
    $blockingAdapterFields += 'csharpBaselineGraphValidation'
}
$adapterReady = $adapter.status -eq 'ready' -and $blockingAdapterFields.Count -eq 0
$privilegedExecutorValidationImplemented = $false
if (-not $privilegedExecutorValidationImplemented) {
    $blockingAdapterFields += 'privilegedExecutorValidation'
    $adapterReady = $false
}
$preflightVerdict = if ($blockingDependencies.Count -gt 0 -or -not $adapterReady) { 'blocked' } else { 'ready' }
$preflightCategory = if ($preflightVerdict -eq 'blocked') { 'dependency-blocked' } else { 'preflight-passed' }
$workspaceStateJson = $workspaceState | ConvertTo-Json -Depth 10 -Compress
$attestationSource = [ordered]@{
    schema_version = '1.0'
    run_id = $RunId
    workspace = [System.IO.Path]::GetFullPath($WorkspaceRoot)
    repository = $actualRemote
    environment_config = (Get-FileHash -Algorithm SHA256 -LiteralPath $environmentPath).Hash.ToLowerInvariant()
    git_executable = [ordered]@{
        path = $script:GitExecutable
        sha256 = $environment.trustedGit.sha256
        signature_status = $environment.trustedGit.signatureStatus
    }
    workspace_state = $workspaceState
    workspace_state_hash = Get-TextDigest -Text $workspaceStateJson
    instruction_commit = $commit
    instructions = $instructionHashes
    target = $manifest.name
    code = $codeRelativePath
    source_manifest = $SourceSha256
    tds_machine = $TdsMachine
    dependency_manifest = (Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath).Hash.ToLowerInvariant()
    deployment_adapter = (Get-FileHash -Algorithm SHA256 -LiteralPath $adapterPath).Hash.ToLowerInvariant()
    readiness = [ordered]@{
        verdict = $preflightVerdict
        result_category = $preflightCategory
        blocking_dependencies = $blockingDependencies
        deployment_adapter_status = $adapter.status
        blocking_deployment_fields = $blockingAdapterFields
    }
}
$attestationJson = ConvertTo-CanonicalJson -Value $attestationSource
$attestationBytes = [Text.Encoding]::UTF8.GetBytes($attestationJson)
$attestationHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($attestationBytes)).ToLowerInvariant()

if ([string]::IsNullOrWhiteSpace($AttestationKeyId)) {
    throw "AttestationKeyId is required."
}

if ($AttestationKeyId -ne $environment.attestationKeyId) {
    throw "AttestationKeyId does not match the reviewed environment profile."
}

$normalizedAttestationKeyPath = $AttestationKeyPath.Replace('/', '\')
if ($normalizedAttestationKeyPath.StartsWith('\\?\', [System.StringComparison]::Ordinal) -or
    $normalizedAttestationKeyPath.StartsWith('\\.\', [System.StringComparison]::Ordinal) -or
    $normalizedAttestationKeyPath.StartsWith('\\', [System.StringComparison]::Ordinal) -or
    $normalizedAttestationKeyPath.IndexOf(':', 2) -ge 0) {
    throw "Attestation key device and UNC path namespaces are not allowed."
}
$resolvedKeyPath = [System.IO.Path]::GetFullPath($normalizedAttestationKeyPath)
foreach ($forbiddenRoot in @([System.IO.Path]::GetFullPath($WorkspaceRoot), [System.IO.Path]::GetFullPath($verifierRoot))) {
    $normalizedRoot = $forbiddenRoot.TrimEnd('\', '/')
    if ($resolvedKeyPath.Equals($normalizedRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        $resolvedKeyPath.StartsWith("$normalizedRoot$([System.IO.Path]::DirectorySeparatorChar)", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Attestation key must be stored outside source and verifier repositories."
    }
}

if (-not (Test-Path -LiteralPath $resolvedKeyPath -PathType Leaf)) {
    throw "Attestation key file does not exist."
}

$keyPathItem = Get-Item -Force -LiteralPath $resolvedKeyPath
while ($null -ne $keyPathItem) {
    if (($keyPathItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Attestation key path contains a symbolic link or junction."
    }

    $keyPathItem = if ($keyPathItem -is [System.IO.FileInfo]) { $keyPathItem.Directory } else { $keyPathItem.Parent }
}

$attestationKey = [System.IO.File]::ReadAllBytes($resolvedKeyPath)
if ($attestationKey.Length -lt 32) {
    throw "Attestation key must contain at least 32 bytes."
}

$hmac = [System.Security.Cryptography.HMACSHA256]::new($attestationKey)
try {
    $attestationMac = [Convert]::ToBase64String($hmac.ComputeHash($attestationBytes))
}
finally {
    $hmac.Dispose()
    [Array]::Clear($attestationKey, 0, $attestationKey.Length)
}

$result = [ordered]@{
    schema_version = '1.0'
    verdict = $preflightVerdict
    result_category = $preflightCategory
    attestation = $attestationHash
    attestation_authentication = [ordered]@{
        algorithm = 'HMAC-SHA256'
        key_id = $AttestationKeyId
        canonicalization = 'sorted-json-integer-v1'
        value = $attestationMac
    }
    attestation_payload_canonical = $attestationJson
    attestation_payload = $attestationSource
}

$resultJson = $result | ConvertTo-Json -Depth 10
$resultSchemaPath = Resolve-ContainedPath -Root $verifierRoot -Child 'contracts\tds-preflight-result.schema.json'
if (-not ($resultJson | Test-Json -SchemaFile $resultSchemaPath)) {
    throw "Generated preflight result does not match its JSON schema."
}

$resultJson
if ($result.verdict -eq 'blocked') {
    exit 3
}
