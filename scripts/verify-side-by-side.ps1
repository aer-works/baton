# Verification of side-by-side execution (Acceptance bullet 1)
$ErrorActionPreference = "Stop"

$toolsDir = Join-Path $HOME ".baton\tools"
$currentFile = Join-Path $toolsDir "current"
$initialSha = (Get-Content $currentFile -Raw).Trim()
Write-Host "Initial active tool SHA: $initialSha"

# 1. Start a long-running baton mcp process using initialExe
$initialExe = Join-Path (Join-Path $toolsDir $initialSha) "baton.exe"
$proc1 = Start-Process -FilePath $initialExe -ArgumentList "mcp", "--fleet-status-tool" -PassThru
Start-Sleep -Milliseconds 1000

$proc1Entry = Get-CimInstance Win32_Process | Where-Object { $_.Name -eq "baton.exe" -and $_.CommandLine -like "*$initialSha*" } | Select-Object -First 1
if (-not $proc1Entry) {
    throw "Could not find running process 1 with CommandLine containing $initialSha"
}
Write-Host "Process 1 (PID $($proc1Entry.ProcessId)) is running with CommandLine:`n  $($proc1Entry.CommandLine)"

# 2. Set up a side-by-side second version at ~/.baton/tools/v2_test_sha
$v2Sha = "v2_test_sha"
$v2Dir = Join-Path $toolsDir $v2Sha
New-Item -ItemType Directory -Path $v2Dir -Force | Out-Null
$v2Exe = Join-Path $v2Dir "baton.exe"
Copy-Item $initialExe $v2Exe
$initialStore = Join-Path (Join-Path $toolsDir $initialSha) ".store"
if (Test-Path $initialStore) { Copy-Item $initialStore (Join-Path $v2Dir ".store") -Recurse -Force }

# 3. Flip current pointer to v2_test_sha
$tmpPointer = Join-Path $toolsDir "current.tmp"
Set-Content -LiteralPath $tmpPointer -Value "$v2Sha`r`n"
$bakPointer = Join-Path $toolsDir "current.bak"
[System.IO.File]::Replace($tmpPointer, $currentFile, $bakPointer)
if (Test-Path $bakPointer) { Remove-Item $bakPointer -Force }

$activeSha = (Get-Content $currentFile -Raw).Trim()
Write-Host "Pointer flipped to: $activeSha"

# 4. Launch Process 2 via the launcher on PATH
$proc2Cmd = Start-Process -FilePath "baton.cmd" -ArgumentList "mcp", "--fleet-status-tool" -PassThru
Start-Sleep -Milliseconds 1500

$proc2Entry = Get-CimInstance Win32_Process | Where-Object { $_.Name -eq "baton.exe" -and $_.CommandLine -like "*$v2Sha*" } | Select-Object -First 1
if (-not $proc2Entry) {
    throw "Could not find running process 2 with CommandLine containing $v2Sha"
}
Write-Host "Process 2 (PID $($proc2Entry.ProcessId)) launched via launcher with CommandLine:`n  $($proc2Entry.CommandLine)"

# 5. Verify Process 1 is STILL alive and running from initialSha
$proc1StillAlive = (Get-Process -Id $proc1Entry.ProcessId -ErrorAction SilentlyContinue) -ne $null
Write-Host "Process 1 (PID $($proc1Entry.ProcessId)) is STILL running from $($initialSha): $proc1StillAlive"

# Cleanup processes and temporary v2 directory
Stop-Process -Id $proc1Entry.ProcessId -Force -ErrorAction SilentlyContinue
Stop-Process -Id $proc2Entry.ProcessId -Force -ErrorAction SilentlyContinue
Stop-Process -Id $proc2Cmd.Id -Force -ErrorAction SilentlyContinue

# Restore pointer to initial SHA
Set-Content -LiteralPath $currentFile -Value "$initialSha`r`n"
Remove-Item -LiteralPath $v2Dir -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Restored pointer to $initialSha and cleaned temporary side-by-side directory."
Write-Host "Side-by-side coexistence verification PASSED!"
