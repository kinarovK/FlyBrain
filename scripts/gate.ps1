<#
.SYNOPSIS
  FlyBrain sprint gate: build, all tests, and (when the FlyWire data exists) the
  real-brain tests against a freshly started brain service. Writes a report.

.EXAMPLE
  .\scripts\gate.ps1 -Sprint G0
  .\scripts\gate.ps1 -Sprint G0 -RecordGolden   # first run only: record tests/golden/*
  .\scripts\gate.ps1 -NoReal                     # fast part only
  .\scripts\gate.ps1 -DryRun                     # list the steps
#>
[CmdletBinding()]
param(
    [string]$Sprint = "manual",
    [string]$Task = "0011_VIEWER-AND-LEARNING",
    [switch]$NoReal,
    [switch]$RecordGolden,
    [switch]$DryRun,
    [int]$Port = 8765,
    [int]$ReadyTimeoutSeconds = 300
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root = Split-Path -Parent $PSScriptRoot
$Python = Join-Path $Root ".venv\Scripts\python.exe"
$GraphMetadata = Join-Path $Root "data\flywire\derived\flytrader-sparse-v1\metadata.json"
$ReportDir = Join-Path $Root "docs\project\tasks\$Task\gates"
$BaseUrl = "http://127.0.0.1:$Port"
$RealAvailable = (Test-Path $GraphMetadata) -and (Test-Path $Python)
$RunReal = -not $NoReal -and $RealAvailable

$Steps = @(
    [pscustomobject]@{ Name = "build";        Command = "dotnet build FlyBrain.sln -c Debug -warnaserror (NuGet audit warnings excluded)" },
    [pscustomobject]@{ Name = "dotnet-test";  Command = "dotnet test FlyBrain.sln --no-build" },
    [pscustomobject]@{ Name = "pytest";       Command = ".venv\Scripts\python.exe -m pytest tests\python -q -p no:cacheprovider" },
    [pscustomobject]@{ Name = "brain-start";  Command = "python -m brain_service.server (port $Port), wait for /health/ready" },
    [pscustomobject]@{ Name = "real-tests";   Command = "dotnet test FlyBrain.sln --no-build -m:1 (sequential) with FLYBRAIN_REAL=1" },
    [pscustomobject]@{ Name = "brain-stop";   Command = "stop the service, assert no brain_service process and port $Port free" }
)

if ($DryRun) {
    Write-Output "FlyBrain gate (dry run) - sprint $Sprint"
    foreach ($step in $Steps) {
        $note = if ($step.Name -in @("brain-start", "real-tests", "brain-stop") -and -not $RunReal) { " [skipped: FlyWire data or .venv missing, or -NoReal]" } else { "" }
        Write-Output ("STEP {0}: {1}{2}" -f $step.Name, $step.Command, $note)
    }
    exit 0
}

$Results = New-Object System.Collections.Generic.List[object]
$LogDir = Join-Path ([System.IO.Path]::GetTempPath()) ("flybrain-gate-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

function Add-Result([string]$Name, [bool]$Ok, [double]$Seconds, [string]$Summary) {
    $Results.Add([pscustomobject]@{ Name = $Name; Ok = $Ok; Seconds = [math]::Round($Seconds, 1); Summary = $Summary })
    $mark = if ($Ok) { "PASS" } else { "FAIL" }
    Write-Host ("[{0}] {1} ({2:N1} s) {3}" -f $mark, $Name, $Seconds, $Summary) -ForegroundColor ($(if ($Ok) { "Green" } else { "Red" }))
}

function Invoke-Step([string]$Name, [scriptblock]$Body, [scriptblock]$Summarize) {
    $log = Join-Path $LogDir "$Name.log"
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $global:LASTEXITCODE = 0
    # Native tools write progress to stderr; do not turn that into terminating errors.
    $ErrorActionPreference = "Continue"
    try {
        & $Body 2>&1 | Tee-Object -FilePath $log | Out-Host
        $ok = ($LASTEXITCODE -eq 0)
    }
    catch {
        $_ | Out-File -Append $log
        $ok = $false
    }
    $watch.Stop()
    $text = if (Test-Path $log) { Get-Content $log -Raw } else { "" }
    $summary = if ($Summarize) { & $Summarize $text } else { "" }
    Add-Result $Name $ok $watch.Elapsed.TotalSeconds $summary
    return $ok
}

function Get-DotnetTestSummary([string]$Text) {
    $passed = 0; $failed = 0; $skipped = 0
    foreach ($m in [regex]::Matches($Text, "Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+)")) {
        $failed += [int]$m.Groups[1].Value; $passed += [int]$m.Groups[2].Value; $skipped += [int]$m.Groups[3].Value
    }
    return "passed $passed, failed $failed, skipped $skipped"
}

function Get-PytestSummary([string]$Text) {
    $m = [regex]::Matches($Text, "(\d+ (passed|failed|skipped|error)[^\r\n]*)")
    if ($m.Count -gt 0) { return $m[$m.Count - 1].Groups[1].Value }
    return ""
}

function Test-PortListening([int]$P) {
    $c = Get-NetTCPConnection -LocalPort $P -State Listen -ErrorAction SilentlyContinue
    return $null -ne $c
}

function Get-BrainProcesses {
    Get-CimInstance Win32_Process -Filter "Name='python.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine -match "brain_service" }
}

Push-Location $Root
try {
    $allOk = $true
    $allOk = (Invoke-Step "build" {
        dotnet build FlyBrain.sln -c Debug -warnaserror "-p:WarningsNotAsErrors=NU1901%3BNU1902%3BNU1903%3BNU1904"
    } { param($t) if ($t -match "(\d+) Warning\(s\)") { "$($Matches[1]) warnings" } else { "" } }) -and $allOk

    if ($allOk) {
        $allOk = (Invoke-Step "dotnet-test" { dotnet test FlyBrain.sln --no-build } { param($t) Get-DotnetTestSummary $t }) -and $allOk
    }

    if (Test-Path $Python) {
        $allOk = (Invoke-Step "pytest" { & $Python -m pytest tests\python -q -p no:cacheprovider } { param($t) Get-PytestSummary $t }) -and $allOk
    } else {
        Add-Result "pytest" $false 0 "missing .venv (run the app's setup or python -m venv .venv)"
        $allOk = $false
    }

    if ($RunReal -and $allOk) {
        $brain = $null
        $startWatch = [System.Diagnostics.Stopwatch]::StartNew()
        if (Test-PortListening $Port) {
            Add-Result "brain-start" $false 0 "port $Port is already in use - stop the running brain/app first"
            $allOk = $false
        } else {
            $env:FLYBRAIN_EAGER_WHOLE = "0"
            $brain = Start-Process -FilePath $Python -ArgumentList @("-u", "-m", "brain_service.server") `
                -WorkingDirectory $Root -WindowStyle Hidden -PassThru `
                -RedirectStandardOutput (Join-Path $LogDir "brain.out.log") -RedirectStandardError (Join-Path $LogDir "brain.err.log")
            $ready = $false
            while ($startWatch.Elapsed.TotalSeconds -lt $ReadyTimeoutSeconds -and -not $brain.HasExited) {
                try {
                    $r = Invoke-WebRequest -Uri "$BaseUrl/health/ready" -UseBasicParsing -TimeoutSec 3
                    if ($r.StatusCode -eq 200) { $ready = $true; break }
                } catch { }
                Start-Sleep -Milliseconds 500
            }
            Add-Result "brain-start" $ready $startWatch.Elapsed.TotalSeconds ($(if ($ready) { "ready (reference domain)" } else { "not ready; see $LogDir\brain.err.log" }))
            $allOk = $ready -and $allOk

            if ($ready) {
                $env:FLYBRAIN_REAL = "1"
                $env:FLYBRAIN_BRAIN_SERVICE_URL = $BaseUrl
                if ($RecordGolden) { $env:FLYBRAIN_RECORD_GOLDEN = "1" }
                try {
                    # Sequential: the brain allows only 4 sessions, so real-brain tests must not run in parallel.
                    $allOk = (Invoke-Step "real-tests" {
                        dotnet test FlyBrain.sln --no-build -m:1 -- RunConfiguration.DisableParallelization=true xUnit.ParallelizeTestCollections=false
                    } { param($t) Get-DotnetTestSummary $t }) -and $allOk
                } finally {
                    Remove-Item Env:FLYBRAIN_REAL, Env:FLYBRAIN_RECORD_GOLDEN -ErrorAction SilentlyContinue
                }
            }

            $stopWatch = [System.Diagnostics.Stopwatch]::StartNew()
            if ($brain -and -not $brain.HasExited) {
                Stop-Process -Id $brain.Id -Force -ErrorAction SilentlyContinue
                $brain.WaitForExit(10000) | Out-Null
            }
            Start-Sleep -Milliseconds 500
            $leftovers = @(Get-BrainProcesses)
            $portBusy = Test-PortListening $Port
            $stopOk = ($leftovers.Count -eq 0) -and -not $portBusy
            Add-Result "brain-stop" $stopOk $stopWatch.Elapsed.TotalSeconds ($(if ($stopOk) { "no brain_service process, port $Port free" } else { "leftover processes: $($leftovers.Count), port busy: $portBusy" }))
            $allOk = $stopOk -and $allOk
        }
    } elseif (-not $RunReal) {
        $reason = if ($NoReal) { "-NoReal" } else { "FlyWire data or .venv missing" }
        Add-Result "real-tests" $true 0 "skipped ($reason)"
    }

    New-Item -ItemType Directory -Force -Path $ReportDir | Out-Null
    $commit = (git rev-parse --short HEAD 2>$null)
    $branch = (git branch --show-current 2>$null)
    $dirty = if (git status --porcelain 2>$null) { " (uncommitted changes)" } else { "" }
    $verdict = if ($allOk) { "PASS" } else { "FAIL" }
    $lines = @(
        "# Gate report: $Sprint",
        "",
        "- Result: **$verdict**",
        "- Date: $(Get-Date -Format 'yyyy-MM-dd HH:mm')",
        "- Branch / commit: $branch / $commit$dirty",
        "- Real-brain tests: $(if ($RunReal) { 'run' } else { 'skipped' })$(if ($RecordGolden) { ' (golden files recorded)' } else { '' })",
        "- Logs: $LogDir",
        "",
        "| Step | Result | Seconds | Summary |",
        "|---|---|---:|---|"
    )
    foreach ($r in $Results) {
        $lines += "| $($r.Name) | $(if ($r.Ok) { 'pass' } else { 'FAIL' }) | $($r.Seconds) | $($r.Summary) |"
    }
    $reportPath = Join-Path $ReportDir "$Sprint.md"
    ($lines -join [Environment]::NewLine) + [Environment]::NewLine | Set-Content -Path $reportPath -Encoding UTF8
    Write-Host ""
    Write-Host "Gate $verdict - report: $reportPath" -ForegroundColor ($(if ($allOk) { "Green" } else { "Red" }))
    if (-not $allOk) { exit 1 }
    exit 0
}
finally {
    Remove-Item Env:FLYBRAIN_EAGER_WHOLE, Env:FLYBRAIN_BRAIN_SERVICE_URL, Env:FLYBRAIN_REAL, Env:FLYBRAIN_RECORD_GOLDEN -ErrorAction SilentlyContinue
    Pop-Location
}
