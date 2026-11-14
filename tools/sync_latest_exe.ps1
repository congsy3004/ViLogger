<#
.SYNOPSIS
    Sync the latest ARVSerialTool.exe release (or CI branch build) onto a local Windows PC.
.DESCRIPTION
    Downloads the newest GitHub Release of ARVSerialTool.exe when a newer version is available,
    preserving any existing Logs directory. Optionally downloads the latest CI artifact from a
    feature branch via GitHub CLI, launches the app, or registers a background Windows Scheduled Task.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "",
    [string]$Repo = "congsy3004/ViLogger",
    [string]$Branch = "",
    [string]$Token = $env:GITHUB_TOKEN,
    [switch]$Force,
    [switch]$Launch,
    [switch]$RegisterTask,
    [switch]$UnregisterTask,
    [int]$IntervalMinutes = 15
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$TaskName = "ARVSerialTool-AutoSync"
$IsBackgroundRun = (-not [Environment]::UserInteractive) -or
    ([Environment]::CommandLine -match '(?i)-WindowStyle\s+Hidden') -or
    ([Environment]::CommandLine -match '(?i)-NonInteractive')

function Normalize-ParsedVersion {
    param([string]$RawVersion)
    if ([string]::IsNullOrWhiteSpace($RawVersion)) {
        return $null
    }
    $clean = $RawVersion.Trim()
    # Strip commit metadata (+sha) or prerelease suffix (-rc1) before parsing System.Version
    $plusIdx = $clean.IndexOf('+')
    if ($plusIdx -ge 0) { $clean = $clean.Substring(0, $plusIdx) }
    $dashIdx = $clean.IndexOf('-')
    if ($dashIdx -ge 0) { $clean = $clean.Substring(0, $dashIdx) }
    $clean = $clean.TrimStart('v', 'V')

    $parsed = $null
    if ([version]::TryParse($clean, [ref]$parsed)) {
        return New-Object System.Version(
            [Math]::Max($parsed.Major, 0),
            [Math]::Max($parsed.Minor, 0),
            [Math]::Max($parsed.Build, 0),
            [Math]::Max($parsed.Revision, 0)
        )
    }
    return $null
}

function Get-LocalExeVersionInfo {
    param([string]$ExePath)
    if (-not (Test-Path -LiteralPath $ExePath)) {
        return $null
    }
    $vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($ExePath)
    $raw = if (-not [string]::IsNullOrWhiteSpace($vi.ProductVersion)) { $vi.ProductVersion } else { $vi.FileVersion }
    $norm = Normalize-ParsedVersion -RawVersion $raw
    return [pscustomobject]@{
        Raw        = $raw
        Normalized = $norm
    }
}

function Test-ExeRunningAtPath {
    param([string]$ExePath)
    $fullTarget = [System.IO.Path]::GetFullPath($ExePath)
    $procs = Get-Process -Name "ARVSerialTool" -ErrorAction SilentlyContinue
    foreach ($p in $procs) {
        try {
            $procPath = $p.MainModule.FileName
            if (-not [string]::IsNullOrWhiteSpace($procPath)) {
                if ([string]::Equals([System.IO.Path]::GetFullPath($procPath), $fullTarget, [System.StringComparison]::OrdinalIgnoreCase)) {
                    return $true
                }
            }
        }
        catch {
            # Ignore access-denied processes from other sessions
        }
    }
    return $false
}

function Invoke-LaunchIfRequested {
    param([string]$ExePath)
    if ($Launch -and (Test-Path -LiteralPath $ExePath)) {
        Write-Host "Launching $ExePath ..."
        Start-Process -FilePath $ExePath -WorkingDirectory (Split-Path -Parent $ExePath)
    }
}

if ($UnregisterTask) {
    $existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host "Removed scheduled task '$TaskName'."
    }
    else {
        Write-Host "Scheduled task '$TaskName' is not registered."
    }
    exit 0
}

if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $parentDir = Split-Path -Parent $PSScriptRoot
    $slnCandidate = Join-Path $parentDir "EverLogger.sln"
    if (Test-Path -LiteralPath $slnCandidate) {
        $InstallDir = Join-Path $parentDir "dist"
    }
    else {
        $InstallDir = $PSScriptRoot
    }
}

if (-not (Test-Path -LiteralPath $InstallDir)) {
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
}
$InstallDir = (Resolve-Path -LiteralPath $InstallDir).Path
$targetExe = Join-Path $InstallDir "ARVSerialTool.exe"

if ($RegisterTask) {
    if ($IntervalMinutes -lt 1 -or $IntervalMinutes -gt 1439) {
        throw "IntervalMinutes must be between 1 and 1439."
    }
    $scriptPath = $MyInvocation.MyCommand.Path
    if ([string]::IsNullOrWhiteSpace($scriptPath)) {
        $scriptPath = $PSCommandPath
    }
    $taskArgs = "-WindowStyle Hidden -NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`" -InstallDir `"$InstallDir`" -Repo `"$Repo`""
    $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $taskArgs
    $triggerLogon = New-ScheduledTaskTrigger -AtLogOn
    $triggerInterval = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes)
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger @($triggerLogon, $triggerInterval) -Settings $settings -Force | Out-Null
    Write-Host "Registered scheduled task '$TaskName' (at logon and every $IntervalMinutes min -> $InstallDir)."
}

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if (-not [string]::IsNullOrWhiteSpace($Branch)) {
    $ghCmd = Get-Command gh -ErrorAction SilentlyContinue
    if ($null -eq $ghCmd) {
        throw "GitHub CLI ('gh') is required to download branch CI artifacts. Install from https://cli.github.com/ or omit -Branch to download official releases."
    }

    Write-Host "Checking latest successful CI build for branch '$Branch' in $Repo..."
    $runsJson = & gh run list --repo $Repo --branch $Branch --workflow "Build" --status success --limit 1 --json databaseId,headSha,createdAt,url
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to query CI runs via gh CLI."
    }
    $runs = @($runsJson | ConvertFrom-Json)
    if ($runs.Count -eq 0) {
        throw "No successful 'Build' workflow run found for branch '$Branch'."
    }

    $run = $runs[0]
    $runId = $run.databaseId
    $shortSha = if ($run.headSha.Length -ge 7) { $run.headSha.Substring(0, 7) } else { $run.headSha }
    Write-Host "Found run #$runId (commit $shortSha) from $($run.createdAt)."

    if (Test-ExeRunningAtPath -ExePath $targetExe) {
        Write-Warning "ARVSerialTool is currently running at $targetExe. Close it to apply build #$runId."
        if ($IsBackgroundRun) { exit 0 } else { exit 2 }
    }

    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("ARVSerialTool_ci_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
    try {
        & gh run download $runId --repo $Repo --name "ARVSerialTool-win-x64" --dir $tempDir
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to download artifact 'ARVSerialTool-win-x64' from run #$runId."
        }
        $downloadedExe = Join-Path $tempDir "ARVSerialTool.exe"
        if (-not (Test-Path -LiteralPath $downloadedExe)) {
            $found = Get-ChildItem -Path $tempDir -Filter "ARVSerialTool.exe" -Recurse | Select-Object -First 1
            if ($null -eq $found) {
                throw "Artifact did not contain ARVSerialTool.exe."
            }
            $downloadedExe = $found.FullName
        }

        $tempTarget = "$targetExe.download"
        Copy-Item -LiteralPath $downloadedExe -Destination $tempTarget -Force
        Move-Item -LiteralPath $tempTarget -Destination $targetExe -Force
        Unblock-File -LiteralPath $targetExe -ErrorAction SilentlyContinue
        Write-Host "Updated $targetExe from branch '$Branch' (run #$runId, commit $shortSha)."
    }
    finally {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    Invoke-LaunchIfRequested -ExePath $targetExe
    exit 0
}

$headers = @{
    "User-Agent" = "ARVSerialTool-Updater"
    "Accept"     = "application/vnd.github+json"
}
if (-not [string]::IsNullOrWhiteSpace($Token)) {
    $headers["Authorization"] = "Bearer $Token"
}

$releaseUrl = "https://api.github.com/repos/$Repo/releases/latest"
$release = Invoke-RestMethod -Uri $releaseUrl -Headers $headers -Method Get
$remoteTag = [string]$release.tag_name
$remoteVerNorm = Normalize-ParsedVersion -RawVersion $remoteTag
if ($null -eq $remoteVerNorm) {
    throw "Could not parse release tag version '$remoteTag'."
}

$asset = @($release.assets) | Where-Object { $_.name -eq "ARVSerialTool.exe" } | Select-Object -First 1
if ($null -eq $asset) {
    throw "Release '$remoteTag' does not contain an ARVSerialTool.exe asset."
}

$localInfo = Get-LocalExeVersionInfo -ExePath $targetExe
if (-not $Force -and $null -ne $localInfo -and $null -ne $localInfo.Normalized) {
    if ($localInfo.Normalized -ge $remoteVerNorm) {
        Write-Host "ARVSerialTool is already up to date ($($localInfo.Raw), latest $remoteTag) at $targetExe."
        Invoke-LaunchIfRequested -ExePath $targetExe
        exit 0
    }
}

if (Test-ExeRunningAtPath -ExePath $targetExe) {
    Write-Warning "ARVSerialTool is currently running at $targetExe. Close it to apply update $remoteTag."
    if ($IsBackgroundRun) { exit 0 } else { exit 2 }
}

$tempDownload = "$targetExe.download"
if (Test-Path -LiteralPath $tempDownload) {
    Remove-Item -LiteralPath $tempDownload -Force
}

Write-Host "Downloading ARVSerialTool $remoteTag ($($asset.size) bytes) to $targetExe..."
$dlHeaders = @{ "User-Agent" = "ARVSerialTool-Updater" }
if (-not [string]::IsNullOrWhiteSpace($Token)) {
    $dlHeaders["Authorization"] = "Bearer $Token"
    $dlHeaders["Accept"] = "application/octet-stream"
    Invoke-WebRequest -Uri $asset.url -Headers $dlHeaders -OutFile $tempDownload -UseBasicParsing
}
else {
    Invoke-WebRequest -Uri $asset.browser_download_url -Headers $dlHeaders -OutFile $tempDownload -UseBasicParsing
}

$actualSize = (Get-Item -LiteralPath $tempDownload).Length
if ($actualSize -ne [long]$asset.size) {
    Remove-Item -LiteralPath $tempDownload -Force -ErrorAction SilentlyContinue
    throw "Downloaded size mismatch: expected $($asset.size) bytes, got $actualSize bytes."
}

Move-Item -LiteralPath $tempDownload -Destination $targetExe -Force
Unblock-File -LiteralPath $targetExe -ErrorAction SilentlyContinue

$installedInfo = Get-LocalExeVersionInfo -ExePath $targetExe
$displayVer = if ($null -ne $installedInfo) { $installedInfo.Raw } else { $remoteTag }
Write-Host "Successfully updated $targetExe to $remoteTag (ProductVersion: $displayVer)."
Invoke-LaunchIfRequested -ExePath $targetExe
