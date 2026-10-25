<#
.SYNOPSIS
    Builds the pomp Visual Studio extensions: optional WebUI bundle, then both VSIX projects, then copies the .vsix files
    to artifacts\.

.DESCRIPTION
    1. WebUI (optional): when src\Pomp.WebUI\package.json exists, runs "npm ci" (or "npm install" without a lockfile) and
       "npm run build" there. The resulting src\Pomp.WebUI\dist is packaged into the VSIX as WebUI\ by the VSIX projects
       (a placeholder page is packaged when dist does not exist). Then "npm ci --ignore-scripts" and "npm run build:vendor"
       in src\Pomp.OmpExtension generate its vendor\ bundle (not committed). Skip both with -SkipWebUI.
    2. VSIX: MSBuild (located with vswhere) restore + build of src\Pomp.Vsix.VS2017, src\Pomp.Vsix.VS2019
       and src\Pomp.Vsix.VS2022. They must be built with Visual Studio's MSBuild, not "dotnet build".
    3. Copies the packages to artifacts\ as Pomp.VS2017-<version>.vsix / Pomp.VS2019-<version>.vsix / Pomp.VS2022-<version>.vsix
       (<version> = the <Version> of Directory.Build.props, the same number the manifests carry).

    Every VSIX ships the locally patched omp engine pinned in engines\omp\engine.props (version, size, SHA-256).
    If the cache is missing or mismatched, this script builds the pinned upstream source plus engines\omp\pomp-v18.12.6.patch
    with build\build-omp.ps1. Git, Bun, Rust/Cargo and Visual Studio C++ build tools are required for a clean build.

    Works in Windows PowerShell 5.1 and PowerShell 7.

.PARAMETER Configuration
    Release (default) or Debug.

.PARAMETER Flavor
    Which VSIX to build: All (default), VS2017, VS2019 or VS2022.

.PARAMETER SkipWebUI
    Do not run npm: use an existing src\Pomp.WebUI\dist (or the placeholder page) and src\Pomp.OmpExtension\vendor.

.PARAMETER OmpExe
    Optional local copy of the pinned locally patched omp engine to package as omp\omp.exe instead of the build cache
    (MSBuild property PompOmpExe), e.g. for an offline build. It must match engines\omp\engine.props (same SHA-256);
    the build fails (POMP1011). A relative path is resolved against the current directory (not the project directory, as MSBuild
    would do) and the script fails when the file does not exist; the resolved absolute path is what MSBuild receives.

.PARAMETER MSBuildPath
    Explicit MSBuild.exe; by default the newest Visual Studio installation with MSBuild is used (vswhere).

.PARAMETER DryRun
    Print what would run without executing anything.

.EXAMPLE
    .\build\build.ps1 -SkipWebUI

.EXAMPLE
    .\build\build.ps1 -Configuration Debug -Flavor VS2022 -OmpExe C:\tools\omp.exe
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [ValidateSet('All', 'VS2017', 'VS2019', 'VS2022')]
    [string]$Flavor = 'All',

    [switch]$SkipWebUI,

    [string]$OmpExe,

    [string]$MSBuildPath,

    [switch]$DryRun
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$srcDir = Join-Path $repoRoot 'src'
$webUiDir = Join-Path $srcDir 'Pomp.WebUI'
$artifactsDir = Join-Path $repoRoot 'artifacts'
# The package version (Directory.Build.props <Version>) goes into the copied file names.
$versionMatch = [regex]::Match((Get-Content (Join-Path $repoRoot 'Directory.Build.props') -Raw), '<Version>\s*([0-9]+(?:\.[0-9]+){1,3})\s*</Version>')
if (-not $versionMatch.Success) { throw 'Directory.Build.props has no <Version>.' }
$packageVersion = $versionMatch.Groups[1].Value

function Write-Step([string]$message) {
    Write-Host ''
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Invoke-Tool([string]$file, [string[]]$arguments, [string]$workingDirectory) {
    $display = "$file $($arguments -join ' ')"
    if ($DryRun) {
        Write-Host "[dry-run] ($workingDirectory) $display"
        return
    }

    Write-Host "($workingDirectory) $display"
    Push-Location $workingDirectory

    # Native tools (npm, MSBuild) write notices to stderr. Windows PowerShell 5.1 turns those lines into error records when the caller
    # merges streams (2>&1, *>&1, the ISE, some CI wrappers) and $ErrorActionPreference = 'Stop' would abort the script on the first one.
    # Relax the preference around the call and judge the tool by its exit code only.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $file @arguments
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
        Pop-Location
    }

    if ($exitCode -ne 0) {
        throw "Command failed with exit code ${exitCode}: $display"
    }
}

# MSBuild splits "-p:" values on ';' and ',' and expands $(...), @(...) and %XX, so escape what can legally appear in a Windows path.
function ConvertTo-MSBuildPropertyValue([string]$value) {
    return $value.Replace('%', '%25').Replace(';', '%3B').Replace(',', '%2C').Replace('$', '%24').Replace('@', '%40').Replace("'", '%27')
}

function Find-MSBuild {
    if ($MSBuildPath) {
        if (-not (Test-Path $MSBuildPath)) {
            throw "MSBuild not found at '$MSBuildPath'."
        }

        return (Resolve-Path $MSBuildPath).Path
    }

    $programFilesX86 = ${env:ProgramFiles(x86)}
    if (-not $programFilesX86) {
        $programFilesX86 = $env:ProgramFiles
    }

    $vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        throw "vswhere.exe not found ($vswhere). Install Visual Studio (any edition with MSBuild) or pass -MSBuildPath."
    }

    # -latest: newest installation; the MSBuild component is required for building. Prerelease installs count.
    # Strings only: a merged stderr stream (see Invoke-Tool) must not leak error records into the result.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $found = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
            Where-Object { $_ -is [string] -and $_ -and (Test-Path -LiteralPath $_) } |
            Select-Object -First 1
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    if (-not $found) {
        throw 'vswhere did not find a Visual Studio installation with MSBuild. Install one or pass -MSBuildPath.'
    }

    return $found
}

# ---- 0. Arguments ---------------------------------------------------------------------------------------

# MSBuild would resolve a relative PompOmpExe against the project directory, so resolve it against the caller's directory here and
# fail early (before npm/MSBuild) with a clear message when the file is missing. The Pomp.Vsix targets re-check it (POMP1001/POMP1002).
if ($OmpExe) {
    if (-not (Test-Path -LiteralPath $OmpExe -PathType Leaf)) {
        throw "-OmpExe '$OmpExe' does not exist or is not a file."
    }

    $OmpExe = (Resolve-Path -LiteralPath $OmpExe).ProviderPath
    Write-Step "omp engine taken from: $OmpExe"
}

# ---- 1. WebUI -------------------------------------------------------------------------------------------

$webUiPackageJson = Join-Path $webUiDir 'package.json'
if ($SkipWebUI) {
    Write-Step 'WebUI: skipped (-SkipWebUI)'
}
elseif (-not (Test-Path $webUiPackageJson)) {
    Write-Step 'WebUI: src\Pomp.WebUI\package.json not found, packaging the placeholder page'
}
else {
    Write-Step 'WebUI: npm ci + npm run build'
    $npm = 'npm'
    if (-not $DryRun -and -not (Get-Command npm -ErrorAction SilentlyContinue)) {
        throw 'npm was not found on PATH. Install Node.js or use -SkipWebUI.'
    }

    $installCommand = if (Test-Path (Join-Path $webUiDir 'package-lock.json')) { 'ci' } else { 'install' }
    Invoke-Tool $npm @($installCommand) $webUiDir
    Invoke-Tool $npm @('run', 'build') $webUiDir

    if (-not $DryRun -and -not (Test-Path (Join-Path $webUiDir 'dist\index.html'))) {
        throw 'The WebUI build finished but src\Pomp.WebUI\dist\index.html does not exist.'
    }

    # The omp extension's bundled rule dependencies (vendor\) are generated from its pinned lockfile, not committed.
    $extensionDir = Join-Path $srcDir 'Pomp.OmpExtension'
    Write-Step 'omp extension: npm ci --ignore-scripts + npm run build:vendor'
    Invoke-Tool $npm @('ci', '--ignore-scripts') $extensionDir
    Invoke-Tool $npm @('run', 'build:vendor') $extensionDir
    if (-not $DryRun -and -not (Test-Path (Join-Path $extensionDir 'vendor\rule-deps.mjs'))) {
        throw 'The extension vendor build finished but src\Pomp.OmpExtension\vendor\rule-deps.mjs does not exist.'
    }
}

# ---- 2. Patched omp engine ------------------------------------------------------------------------------

if (-not $OmpExe) {
    $engineProps = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'engines\omp\engine.props') -Raw)
    $engineVersion = $engineProps.Project.PropertyGroup.PompOmpVersion
    $engineFile = Join-Path $artifactsDir "engines\omp\$engineVersion\omp.exe"
    $needsEngine = -not (Test-Path -LiteralPath $engineFile -PathType Leaf)
    if (-not $needsEngine) {
        $needsEngine = (Get-FileHash -LiteralPath $engineFile -Algorithm SHA256).Hash -ne
            $engineProps.Project.PropertyGroup.PompOmpSha256
    }
    if ($needsEngine) {
        Write-Step 'Building locally patched omp engine'
        if ($DryRun) { Write-Host '[dry-run] build\build-omp.ps1' }
        else { & (Join-Path $PSScriptRoot 'build-omp.ps1') }
    }
}

# ---- 3. VSIX --------------------------------------------------------------------------------------------

Write-Step 'Locating MSBuild'
$msbuild = if ($DryRun -and -not $MSBuildPath) { try { Find-MSBuild } catch { 'MSBuild.exe' } } else { Find-MSBuild }
Write-Host "MSBuild: $msbuild"

# Keep MSBuild's own messages in English regardless of the OS language (easier to search and to report).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:VSLANG = '1033'

$projects = @()
if ($Flavor -eq 'All' -or $Flavor -eq 'VS2017') {
    $projects += [pscustomobject]@{ Name = 'VS2017'; Project = Join-Path $srcDir 'Pomp.Vsix.VS2017\Pomp.Vsix.VS2017.csproj'; Vsix = 'Pomp.VS2017.vsix' }
}

if ($Flavor -eq 'All' -or $Flavor -eq 'VS2019') {
    $projects += [pscustomobject]@{ Name = 'VS2019'; Project = Join-Path $srcDir 'Pomp.Vsix.VS2019\Pomp.Vsix.VS2019.csproj'; Vsix = 'Pomp.VS2019.vsix' }
}

if ($Flavor -eq 'All' -or $Flavor -eq 'VS2022') {
    $projects += [pscustomobject]@{ Name = 'VS2022'; Project = Join-Path $srcDir 'Pomp.Vsix.VS2022\Pomp.Vsix.VS2022.csproj'; Vsix = 'Pomp.VS2022.vsix' }
}

if (-not $DryRun) {
    New-Item -ItemType Directory -Force $artifactsDir | Out-Null
}

$results = @()
foreach ($item in $projects) {
    Write-Step "VSIX $($item.Name) ($Configuration)"

    $arguments = @(
        $item.Project,
        '-restore',
        "-p:Configuration=$Configuration",
        '-nodeReuse:false',
        '-verbosity:minimal',
        '-nologo'
    )

    if ($OmpExe) {
        $arguments += "-p:PompOmpExe=$(ConvertTo-MSBuildPropertyValue $OmpExe)"
    }

    Invoke-Tool $msbuild $arguments $repoRoot

    $projectDir = Split-Path $item.Project -Parent
    $built = Join-Path $projectDir "bin\$Configuration\net472\$($item.Vsix)"
    $target = Join-Path $artifactsDir ([IO.Path]::GetFileNameWithoutExtension($item.Vsix) + '-' + $packageVersion + '.vsix')

    if ($DryRun) {
        Write-Host "[dry-run] copy $built -> $target"
        continue
    }

    if (-not (Test-Path $built)) {
        throw "Expected VSIX not found: $built"
    }

    Copy-Item $built $target -Force
    $results += Get-Item $target
}

# ---- 3. Summary -----------------------------------------------------------------------------------------

Write-Step 'Done'
foreach ($file in $results) {
    Write-Host ('{0,-20} {1,10:N0} bytes  {2}' -f $file.Name, $file.Length, $file.FullName)
}
