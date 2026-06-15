<#
.SYNOPSIS
    Build the Curfew installer locally, mirroring the GitHub release workflow.

.DESCRIPTION
    Publishes the service, overlay and app as self-contained win-x64, then compiles
    installer\setup.iss with Inno Setup 6 to produce curfew-setup-v<Version>.exe in
    the repository root. This is the same sequence .github/workflows/release.yml runs
    on a tag push, factored out so a release can be produced (and on-device tested)
    without going through CI. Signing is intentionally omitted — that stays in CI,
    which holds the certificate secret.

.PARAMETER Version
    Three-part version (MAJOR.MINOR.PATCH) stamped into the binaries and installer
    name. Defaults to 0.0.1 for local/auto-test builds.

.EXAMPLE
    pwsh installer\build-installer.ps1 -Version 1.6.0
#>
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.0.1'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    # Locate ISCC: the CI image installs it under Program Files (x86); a winget
    # install (JRSoftware.InnoSetup) lands under the user's local programs instead.
    $isccCandidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) {
        throw "ISCC.exe not found. Install Inno Setup 6 (winget install JRSoftware.InnoSetup)."
    }

    # Fresh publish dirs so a stale binary from a previous version never ships.
    foreach ($d in 'service', 'overlay', 'app') {
        $p = Join-Path $repo "installer\$d"
        if (Test-Path $p) { Remove-Item $p -Recurse -Force }
    }

    function Publish-Project($project, $outDir, [string[]]$extra) {
        Write-Host "==> publish $project -> $outDir"
        dotnet publish $project -c Release -r win-x64 --self-contained `
            -p:Version=$Version @extra -o (Join-Path $repo "installer\$outDir")
        if ($LASTEXITCODE -ne 0) { throw "publish failed: $project" }
    }

    Publish-Project 'src\Curfew.Service\Curfew.Service.csproj' 'service' @()
    Publish-Project 'src\Curfew.Overlay\Curfew.Overlay.csproj' 'overlay' @()
    # The WinUI app needs its platform pinned and the Windows App SDK bundled so the
    # installed copy runs without a separate runtime dependency.
    Publish-Project 'src\Curfew.App\Curfew.App.csproj' 'app' `
        @('-p:Platform=x64', '-p:WindowsAppSDKSelfContained=true')

    Write-Host "==> compiling installer with $iscc"
    & $iscc "/DMyAppVersion=$Version" (Join-Path $repo 'installer\setup.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed' }

    $installer = Join-Path $repo "curfew-setup-v$Version.exe"
    if (-not (Test-Path $installer)) { throw "installer not produced at $installer" }
    $sizeMb = [math]::Round((Get-Item $installer).Length / 1MB, 1)
    Write-Host "OK: $installer ($sizeMb MB)"
}
finally {
    Pop-Location
}
