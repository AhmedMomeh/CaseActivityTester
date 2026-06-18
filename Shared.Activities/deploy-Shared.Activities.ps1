#requires -RunAsAdministrator

<#
.SYNOPSIS
    Deploys Shared.Activities.dll into the Case Portal and patches
    Intalio.Case.Portal.deps.json so .NET 8's AssemblyLoadContext can
    resolve Shared.Activities by name.

.DESCRIPTION
    Idempotent and re-runnable. Designed to be executed after every Case
    Portal install/upgrade - Portal upgrades overwrite the bin folder
    (removing Shared.Activities.dll) and the deps.json (dropping the
    patch). Running this script restores both, plus an app-pool recycle.

    Steps:
        1. Pre-flight checks (paths exist, DLL is present).
        2. Stop the Case Portal app pool.
        3. Back up the current deps.json with a timestamped suffix.
        4. Patch deps.json (idempotent - skips if already patched).
        5. Validate the patched deps.json is still well-formed JSON;
           auto-rollback if not.
        6. Copy Shared.Activities.dll into the Portal bin folder.
        7. Start the app pool.
        8. Print the verification SQL command.

    Bundle ship list (what to give the deploy team):
        deploy-Shared.Activities.ps1   - this script
        Shared.Activities.dll          - the activity library

    Place them in the same folder and the script auto-detects the DLL.

.PARAMETER PortalPath
    Path to the Case Portal install. Default: C:\Program Files\Intalio\UC_CasePortal.

.PARAMETER DllPath
    Path to Shared.Activities.dll. Defaults to the script's own folder
    (so just dropping both files together and running the script works).

.PARAMETER AppPoolName
    IIS application-pool name hosting the Portal.
    Default: UC_CasePortal.

.PARAMETER SkipPatchVersion
    Override the Intalio.Case.Portal.Core version to anchor on (default
    auto-detects from existing deps.json). Use this only if auto-detect
    can't find the version in a future Portal release.

.EXAMPLE
    # Standard run from the bundle folder (script + DLL next to each other)
    PS> .\deploy-Shared.Activities.ps1

.EXAMPLE
    # Custom install location
    PS> .\deploy-Shared.Activities.ps1 -PortalPath "D:\Apps\CasePortal" -AppPoolName "Intalio.Case"
#>

[CmdletBinding()]
param(
    [string]$PortalPath  = "C:\Program Files\Intalio\UC_CasePortal",
    [string]$DllPath     = (Join-Path $PSScriptRoot "Shared.Activities.dll"),
    [string]$AppPoolName = "UC_CasePortal",
    [string]$SkipPatchVersion = $null
)

$ErrorActionPreference = 'Stop'

function Write-Step($m) { Write-Host ""; Write-Host "==> $m" -ForegroundColor Cyan }
function Write-OK  ($m) { Write-Host "  + $m" -ForegroundColor Green }
function Write-Warn($m) { Write-Host "  ! $m" -ForegroundColor Yellow }
function Write-Err ($m) { Write-Host "  - $m" -ForegroundColor Red }

# =============================================================================
# 1. Pre-flight
# =============================================================================
Write-Step "Pre-flight"

if (-not (Test-Path $DllPath)) {
    throw "Source DLL not found: $DllPath. Build with: dotnet build Shared.Activities.csproj -c Release"
}
Write-OK "Source DLL: $DllPath ($([math]::Round((Get-Item $DllPath).Length / 1KB, 1)) KB)"

if (-not (Test-Path $PortalPath)) {
    throw "Portal install not found: $PortalPath. Override with -PortalPath."
}
Write-OK "Portal install: $PortalPath"

$depsPath = Join-Path $PortalPath "Intalio.Case.Portal.deps.json"
if (-not (Test-Path $depsPath)) {
    throw "deps.json not found at: $depsPath. Wrong Portal path?"
}
Write-OK "deps.json: $depsPath"

Import-Module WebAdministration -ErrorAction Stop
$null = Get-WebAppPoolState -Name $AppPoolName     # throws if pool does not exist
Write-OK "App pool '$AppPoolName' found"

# Auto-detect Intalio.Case.Portal.Core version from existing deps.json so the
# script keeps working when Intalio bumps the Portal version.
$detected = $null
$origTxt = Get-Content $depsPath -Raw
if ($SkipPatchVersion) {
    $detected = $SkipPatchVersion
    Write-OK "Using override version: $detected"
} else {
    $m = [regex]::Match($origTxt, '"Intalio\.Case\.Portal\.Core/(\d+\.\d+\.\d+(?:\.\d+)?)":\s*\{\s*"type":\s*"project"')
    if (-not $m.Success) {
        throw "Could not find an Intalio.Case.Portal.Core/<version> entry in libraries section of deps.json. Pass -SkipPatchVersion manually."
    }
    $detected = $m.Groups[1].Value
    Write-OK "Detected Intalio.Case.Portal.Core version: $detected"
}

# =============================================================================
# 2. Stop the app pool
# =============================================================================
Write-Step "Stopping app pool '$AppPoolName'"

$state = (Get-WebAppPoolState -Name $AppPoolName).Value
if ($state -ne 'Stopped') {
    Stop-WebAppPool -Name $AppPoolName
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline -and (Get-WebAppPoolState -Name $AppPoolName).Value -ne 'Stopped') {
        Start-Sleep -Milliseconds 500
    }
    $state = (Get-WebAppPoolState -Name $AppPoolName).Value
}
if ($state -ne 'Stopped') {
    throw "App pool did not stop within 45s (current state: $state). Kill the w3wp process manually and retry."
}
Write-OK "Stopped"

# =============================================================================
# 3. Backup deps.json
# =============================================================================
Write-Step "Backing up deps.json"

$ts = (Get-Date).ToString('yyyyMMdd-HHmmss')
$bak = "$depsPath.bak-$ts"
Copy-Item $depsPath $bak -Force
Write-OK "Backup: $bak"

# =============================================================================
# 4. Patch deps.json (idempotent)
# =============================================================================
Write-Step "Patching deps.json"

$txt = $origTxt

# --- targets section ---------------------------------------------------------
$tgtAlready = $txt -match '"Shared\.Activities/1\.0\.0\.0":\s*\{\s*"runtime"\s*:'
if (-not $tgtAlready) {
    # Anchor on the END of the Intalio.Case.Portal.Core targets block. The
    # "compile" subsection + the .dll filename is stable across versions
    # (the version lives in the key, not the file name).
    $tgtAnchorRegex = '("Intalio\.Case\.Portal\.Core/' + [regex]::Escape($detected) + ')(":\s*\{[\s\S]*?"compile":\s*\{[\s\S]*?"Intalio\.Case\.Portal\.Core\.dll":\s*\{\}[\s\S]*?\}[\s\S]*?\},)'
    if ([regex]::IsMatch($txt, $tgtAnchorRegex) -eq $false) {
        throw "deps.json targets section: could not locate Intalio.Case.Portal.Core/$detected block. Schema may have changed - restore $bak and patch manually."
    }
    $tgtReplacement = '$1$2' + "`r`n      `"Shared.Activities/1.0.0.0`": {`r`n        `"runtime`": {`r`n          `"Shared.Activities.dll`": {`r`n            `"assemblyVersion`": `"1.0.0.0`",`r`n            `"fileVersion`": `"1.0.0.0`"`r`n          }`r`n        }`r`n      },"
    $txt = [regex]::Replace($txt, $tgtAnchorRegex, $tgtReplacement, 1)
    Write-OK "Targets entry inserted"
} else {
    Write-Warn "Targets entry already present - skipping"
}

# --- libraries section -------------------------------------------------------
$libAlready = $txt -match '"Shared\.Activities/1\.0\.0\.0":\s*\{\s*"type"\s*:\s*"project"'
if (-not $libAlready) {
    # Anchor on the FULL Intalio.Case.Portal.Core libraries entry - type=project,
    # serviceable=false, sha512="". Structurally unique to "project" types.
    $libAnchorRegex = '("Intalio\.Case\.Portal\.Core/' + [regex]::Escape($detected) + '":\s*\{\s*"type":\s*"project",\s*"serviceable":\s*false,\s*"sha512":\s*""\s*\},)'
    if ([regex]::IsMatch($txt, $libAnchorRegex) -eq $false) {
        throw "deps.json libraries section: could not locate Intalio.Case.Portal.Core/$detected block. Schema may have changed."
    }
    $libReplacement = '$1' + "`r`n    `"Shared.Activities/1.0.0.0`": {`r`n      `"type`": `"project`",`r`n      `"serviceable`": false,`r`n      `"sha512`": `"`"`r`n    },"
    $txt = [regex]::Replace($txt, $libAnchorRegex, $libReplacement, 1)
    Write-OK "Libraries entry inserted"
} else {
    Write-Warn "Libraries entry already present - skipping"
}

Set-Content -Path $depsPath -Value $txt -Encoding UTF8 -NoNewline

# =============================================================================
# 5. Validate JSON - auto-rollback if broken
# =============================================================================
Write-Step "Validating patched deps.json"

try {
    $j = Get-Content $depsPath -Raw | ConvertFrom-Json
    $tgtOK = [bool]$j.targets.'.NETCoreApp,Version=v8.0'.'Shared.Activities/1.0.0.0'
    $libOK = [bool]$j.libraries.'Shared.Activities/1.0.0.0'
    if (-not ($tgtOK -and $libOK)) {
        throw "JSON valid but Shared.Activities entries not detected after patch (tgt=$tgtOK, lib=$libOK)."
    }
    Write-OK "Valid JSON; both Shared.Activities entries present"
} catch {
    Write-Err "deps.json validation failed: $($_.Exception.Message)"
    Copy-Item $bak $depsPath -Force
    Write-Warn "Rolled back to $bak. Investigate the patch logic; original file restored."
    Start-WebAppPool -Name $AppPoolName
    throw "Patch aborted; deps.json rolled back."
}

# =============================================================================
# 6. Copy DLL
# =============================================================================
Write-Step "Copying Shared.Activities.dll"

$dest = Join-Path $PortalPath "Shared.Activities.dll"
Copy-Item $DllPath $dest -Force
Write-OK "Copied to $dest"

# =============================================================================
# 7. Start app pool
# =============================================================================
Write-Step "Starting app pool"

Start-WebAppPool -Name $AppPoolName
$deadline = (Get-Date).AddSeconds(45)
while ((Get-Date) -lt $deadline -and (Get-WebAppPoolState -Name $AppPoolName).Value -ne 'Started') {
    Start-Sleep -Milliseconds 500
}
$state = (Get-WebAppPoolState -Name $AppPoolName).Value
if ($state -ne 'Started') {
    throw "App pool did not reach Started state within 45s (current state: $state). Check Application Event Log."
}
Write-OK "App pool: $state"

# =============================================================================
# 8. Done - verification instructions
# =============================================================================
Write-Host ""
Write-Host "Deployment complete." -ForegroundColor Green
Write-Host ""
Write-Host 'Verify drain is firing (wait ~90s for one cron tick, then run):' -ForegroundColor Gray
Write-Host '  sqlcmd -S <prod-sql> -E -d <CaseDB> -I -Q "SELECT [Field], CAST([Value] AS NVARCHAR(MAX)) FROM Scheduler.Hash WHERE [Key] = ''recurring-job:case-tcp-overdue-drainer'' ORDER BY [Field];"' -ForegroundColor Gray
Write-Host ''
Write-Host "Expected: LastJobId + LastExecution present, NO 'Error' field." -ForegroundColor Gray
Write-Host 'Activity log: C:\Logs\Case\ScheduleHrNotificationActivity-<today>.log' -ForegroundColor Gray
