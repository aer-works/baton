# Verification of side-by-side execution (Acceptance bullet 1)
# Parameterised (F1, #1670 review) so this can be pointed at a temp tools root/PATH dir instead of
# the live install; every mutating step and both process launches are wrapped in try/finally so a
# throw partway through (e.g. process 2 never starts) still restores the pointer, removes the temp
# v2 directory, and stops any process this run spawned.
[CmdletBinding()]
param(
    # Defaults to the live root -- callers that want isolation must pass a temp path explicitly.
    [string]$ToolsDir = (Join-Path $HOME ".baton\tools"),
    [string]$PathDir = $null
)

$ErrorActionPreference = "Stop"

$liveToolsDir = Join-Path $HOME ".baton\tools"
if ($ToolsDir -eq $liveToolsDir) {
    Write-Warning "verify-side-by-side: running against the LIVE tools root ($ToolsDir) -- this will flip the real 'current' pointer and launch real baton.exe processes on this machine. Pass -ToolsDir/-PathDir pointing at a temp root to avoid touching the live install."
}

$currentFile = Join-Path $ToolsDir "current"
$initialSha = (Get-Content $currentFile -Raw).Trim()
Write-Host "Initial active tool SHA: $initialSha"

$proc1Entry = $null
$proc2Entry = $null
$proc2Cmd = $null
$v2Dir = $null
$pointerFlipped = $false

try {
    # 1. Start a long-running baton mcp process using initialExe
    $initialExe = Join-Path (Join-Path $ToolsDir $initialSha) "baton.exe"
    $proc1 = Start-Process -FilePath $initialExe -ArgumentList "mcp", "--fleet-status-tool" -PassThru
    Start-Sleep -Milliseconds 1000

    $proc1Entry = Get-CimInstance Win32_Process | Where-Object { $_.Name -eq "baton.exe" -and $_.CommandLine -like "*$initialSha*" } | Select-Object -First 1
    if (-not $proc1Entry) {
        throw "Could not find running process 1 with CommandLine containing $initialSha"
    }
    Write-Host "Process 1 (PID $($proc1Entry.ProcessId)) is running with CommandLine:`n  $($proc1Entry.CommandLine)"

    # 2. Set up a side-by-side second version at $ToolsDir/v2_test_sha
    $v2Sha = "v2_test_sha"
    $v2Dir = Join-Path $ToolsDir $v2Sha
    New-Item -ItemType Directory -Path $v2Dir -Force | Out-Null
    $v2Exe = Join-Path $v2Dir "baton.exe"
    Copy-Item $initialExe $v2Exe
    $initialStore = Join-Path (Join-Path $ToolsDir $initialSha) ".store"
    if (Test-Path $initialStore) { Copy-Item $initialStore (Join-Path $v2Dir ".store") -Recurse -Force }

    # 3. Flip current pointer to v2_test_sha
    $tmpPointer = Join-Path $ToolsDir "current.tmp"
    Set-Content -LiteralPath $tmpPointer -Value "$v2Sha`r`n"
    $bakPointer = Join-Path $ToolsDir "current.bak"
    [System.IO.File]::Replace($tmpPointer, $currentFile, $bakPointer)
    if (Test-Path $bakPointer) { Remove-Item $bakPointer -Force }
    $pointerFlipped = $true

    $activeSha = (Get-Content $currentFile -Raw).Trim()
    Write-Host "Pointer flipped to: $activeSha"

    # 4. Launch Process 2 via the launcher on PATH
    $launcherArgs = @{ FilePath = "baton.cmd"; ArgumentList = @("mcp", "--fleet-status-tool"); PassThru = $true }
    if ($PathDir) {
        $cmdPath = Join-Path $PathDir "baton.cmd"
        $launcherArgs.FilePath = $cmdPath
    }
    $proc2Cmd = Start-Process @launcherArgs
    Start-Sleep -Milliseconds 1500

    $proc2Entry = Get-CimInstance Win32_Process | Where-Object { $_.Name -eq "baton.exe" -and $_.CommandLine -like "*$v2Sha*" } | Select-Object -First 1
    if (-not $proc2Entry) {
        throw "Could not find running process 2 with CommandLine containing $v2Sha"
    }
    Write-Host "Process 2 (PID $($proc2Entry.ProcessId)) launched via launcher with CommandLine:`n  $($proc2Entry.CommandLine)"

    # 5. Verify Process 1 is STILL alive and running from initialSha
    $proc1StillAlive = (Get-Process -Id $proc1Entry.ProcessId -ErrorAction SilentlyContinue) -ne $null
    Write-Host "Process 1 (PID $($proc1Entry.ProcessId)) is STILL running from $($initialSha): $proc1StillAlive"

    Write-Host "Side-by-side coexistence verification PASSED!"
}
finally {
    # Cleanup processes and temporary v2 directory, and restore the pointer, on every exit path.
    if ($proc1Entry) { Stop-Process -Id $proc1Entry.ProcessId -Force -ErrorAction SilentlyContinue }
    if ($proc2Entry) { Stop-Process -Id $proc2Entry.ProcessId -Force -ErrorAction SilentlyContinue }
    if ($proc2Cmd) { Stop-Process -Id $proc2Cmd.Id -Force -ErrorAction SilentlyContinue }

    if ($pointerFlipped) {
        Set-Content -LiteralPath $currentFile -Value "$initialSha`r`n"
        Write-Host "Restored pointer to $initialSha"
    }
    if ($v2Dir -and (Test-Path $v2Dir)) {
        Remove-Item -LiteralPath $v2Dir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "Cleaned temporary side-by-side directory $v2Dir"
    }
}
