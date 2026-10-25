<#
.SYNOPSIS
    Build POMP's patched Windows x64 omp engine from the upstream v18.12.6 tag.
.DESCRIPTION
    Requires Git, Bun >= 1.4, Rust/Cargo (MSVC x64), and Visual Studio C++ tools
    (including the CMake/Ninja components used by upstream's native build).
    Clones the exact pinned commit into a fresh ignored work directory, applies
    engines/omp/pomp-v18.12.6.patch, installs the pinned Bun lockfile, builds
    the Windows native addon and the release-style Windows x64 executable.
    Only after a successful build copies the executable and the tag's verified
    THIRD-PARTY-NOTICES.txt to artifacts/engines/omp/18.12.6/ by default.
    Prints the SHA-256 and size to pin in engines/omp/engine.props before VSIX
    packaging. No binary hash is assumed before the first successful build.
.PARAMETER OutputPath
    Destination for omp.exe; defaults to artifacts/engines/omp/18.12.6/omp.exe.
    Notices always go beside the executable (VSIX packaging expects the default
    directory, even when build/build.ps1 -OmpExe uses another executable path).
.PARAMETER BunPath
    Optional full path to bun.exe when Bun is not on PATH.
.PARAMETER CargoBinPath
    Optional directory containing cargo.exe and rustc.exe when not on PATH.
    If rustup was installed in a custom location, also export CARGO_HOME and
    RUSTUP_HOME in this shell before calling the recipe.
.EXAMPLE
    .\build\build-omp.ps1
.EXAMPLE
    .\build\build-omp.ps1 -BunPath C:\tools\bun.exe -CargoBinPath C:\tools\cargo\bin
#>
[CmdletBinding()]
param(
    [string]$OutputPath,
    [string]$BunPath,
    [string]$CargoBinPath
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$version = '18.12.6'
$commit = '45c35e40c46166dc726ccc52f5d27f40f2449f99'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$engineDir = Join-Path $repo 'engines\omp'
$patch = Join-Path $engineDir "pomp-v$version.patch"
$props = [xml](Get-Content -LiteralPath (Join-Path $engineDir 'engine.props') -Raw)
if ($props.Project.PropertyGroup.PompOmpVersion -ne $version) {
    throw "engine.props must pin omp $version before building this source patch."
}
if (-not (Test-Path -LiteralPath $patch -PathType Leaf)) { throw "Missing source patch: $patch" }

if ($BunPath) {
    $BunPath = (Resolve-Path -LiteralPath $BunPath).ProviderPath
    if (-not (Test-Path -LiteralPath $BunPath -PathType Leaf)) { throw "Not an executable: $BunPath" }
    $env:PATH = "$(Split-Path $BunPath -Parent);$env:PATH"
}
if ($CargoBinPath) {
    $CargoBinPath = (Resolve-Path -LiteralPath $CargoBinPath).ProviderPath
    $env:PATH = "$CargoBinPath;$env:PATH"
}
foreach ($tool in @('git', 'bun', 'cargo', 'rustc')) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool is required on PATH." }
}
if (-not (Get-Command 'bun.exe' -ErrorAction SilentlyContinue)) { throw 'Windows x64 bun.exe is required.' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
    throw 'vswhere.exe not found. Install Visual Studio with Desktop development with C++.'
}
$vsInstall = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or -not $vsInstall) {
    throw 'Visual Studio C++ x64 build tools not found (Microsoft.VisualStudio.Component.VC.Tools.x86.x64).'
}

# An explicit full path avoids a different working directory changing the destination.
if (-not $OutputPath) { $OutputPath = Join-Path $repo "artifacts\engines\omp\$version\omp.exe" }
$OutputPath = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath))
$destDir = Split-Path $OutputPath -Parent
$workParent = Join-Path $repo 'artifacts\engines\omp'
New-Item -ItemType Directory -Path $workParent -Force | Out-Null
$work = Join-Path $workParent ('b-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))

function Invoke-Native([string]$name, [string[]]$arguments, [string]$directory) {
    Write-Host "[$directory] $name $($arguments -join ' ')"
    Push-Location $directory
    $priorPreference = $ErrorActionPreference
    # PowerShell 5.1 can turn native stderr into terminating ErrorRecords.
    $ErrorActionPreference = 'Continue'
    try {
        & $name @arguments
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $priorPreference
        Pop-Location
    }
    if ($code -ne 0) { throw "$name exited $code" }
}

# Keep the work directory on failure for diagnostics, never package partial output.
try {
    Invoke-Native 'git' @('-c', 'core.autocrlf=false', 'clone', '--depth', '1', '--single-branch', '--branch', "v$version", 'https://github.com/can1357/oh-my-pi.git', $work) $workParent
    $actual = (& git -C $work rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $actual -ne $commit) {
        throw "Unexpected upstream commit: $actual (expected $commit)"
    }
    Invoke-Native 'git' @('-C', $work, 'apply', '--check', $patch) $repo
    Invoke-Native 'git' @('-C', $work, 'apply', $patch) $repo

    # The upstream tag contains the release notice payload. Check against the
    # separately pinned notice digest, not the changed executable's digest.
    $notices = Join-Path $work 'THIRD-PARTY-NOTICES.txt'
    $noticeHash = (Get-FileHash -LiteralPath $notices -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($noticeHash -ne $props.Project.PropertyGroup.PompOmpNoticesSha256.ToLowerInvariant()) {
        throw "Upstream THIRD-PARTY-NOTICES.txt SHA-256 mismatch: $noticeHash"
    }

    Invoke-Native 'bun' @('install', '--frozen-lockfile') $work
    $nativeDir = Join-Path $work 'packages\natives\native'
    $baselineAddon = Join-Path $nativeDir 'pi_natives.win32-x64-baseline.node'
    $modernAddon = Join-Path $nativeDir 'pi_natives.win32-x64-modern.node'
    # The Cargo host build normally selects only the current CPU's ISA. The
    # distributed executable must also load on x64 hosts without AVX2: build
    # one baseline addon from the patched source and embed only that variant.
    Remove-Item -LiteralPath $baselineAddon, $modernAddon -Force -ErrorAction SilentlyContinue
    $previousProfile = $env:OMP_NATIVE_CARGO_PROFILE
    $previousRustFlags = $env:RUSTFLAGS
    $env:OMP_NATIVE_CARGO_PROFILE = 'ci'
    $env:RUSTFLAGS = '-C target-feature=+crt-static -C target-cpu=x86-64-v2'
    try {
        Invoke-Native 'bun' @('run', 'build:native') $work
    }
    finally {
        $env:OMP_NATIVE_CARGO_PROFILE = $previousProfile
        $env:RUSTFLAGS = $previousRustFlags
    }
    if (Test-Path -LiteralPath $modernAddon -PathType Leaf) {
        Move-Item -LiteralPath $modernAddon -Destination $baselineAddon -Force
    }
    if (-not (Test-Path -LiteralPath $baselineAddon -PathType Leaf)) {
        throw "Patched baseline native addon was not built: $baselineAddon"
    }
    Invoke-Native 'bun' @('scripts/ci-release-build-binaries.ts', '--targets', 'win32-x64') $work
    $built = Join-Path $work 'packages\coding-agent\binaries\omp-windows-x64.exe'
    if (-not (Test-Path -LiteralPath $built -PathType Leaf)) {
        throw "Release build did not produce $built"
    }
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    Copy-Item -LiteralPath $built -Destination $OutputPath -Force
    Copy-Item -LiteralPath $notices -Destination (Join-Path $destDir 'THIRD-PARTY-NOTICES.txt') -Force
    $output = Get-Item -LiteralPath $OutputPath
    $hash = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "Built $($output.FullName)"
    Write-Host "PompOmpSize=$($output.Length)"
    Write-Host "PompOmpSha256=$hash"
    Write-Host "PompOmpNoticesSha256=$noticeHash"
}
catch {
    Write-Warning "Build failed; source and intermediate files retained at $work"
    throw
}
