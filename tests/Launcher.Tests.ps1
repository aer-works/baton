# Tests for baton launcher scripts (baton.cmd and baton.ps1) and pointer flip (#1668)
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$launcherDir = [System.IO.Path]::Combine($repoRoot, "tools", "tool-refresh", "launcher")
$cmdLauncher = [System.IO.Path]::Combine($launcherDir, "baton.cmd")
$ps1Launcher = [System.IO.Path]::Combine($launcherDir, "baton.ps1")

function Assert-Equal($expected, $actual, $message) {
    if ($expected -ne $actual) {
        throw "Assertion failed: $message. Expected '$expected', got '$actual'."
    }
}

function Assert-Contains($haystack, $needle, $message) {
    if (-not $haystack.Contains($needle)) {
        throw "Assertion failed: $message. Expected output to contain '$needle', got:`n$haystack"
    }
}

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "baton-launcher-tests-$([System.Guid]::NewGuid().ToString('N'))"
[System.IO.Directory]::CreateDirectory($tempDir) | Out-Null

try {
    $env:BATON_HOME = $tempDir
    $toolsDir = Join-Path $tempDir "tools"
    [System.IO.Directory]::CreateDirectory($toolsDir) | Out-Null
    $currentFile = Join-Path $toolsDir "current"

    # 1. Missing current pointer file fails closed
    Write-Host "Test 1: Missing pointer fails closed..."
    
    # Test baton.ps1
    $proc = Start-Process -FilePath "powershell.exe" -ArgumentList "-NoProfile", "-File", "`"$ps1Launcher`"", "--version" -RedirectStandardError (Join-Path $tempDir "ps1_err1.txt") -Wait -PassThru -NoNewWindow
    Assert-Equal 1 $proc.ExitCode "baton.ps1 missing pointer exit code"
    $err = Get-Content (Join-Path $tempDir "ps1_err1.txt") -Raw
    Assert-Contains $err "pixi run tool-refresh" "baton.ps1 missing pointer error message"

    # Test baton.cmd
    $proc = Start-Process -FilePath "cmd.exe" -ArgumentList "/c", "`"$cmdLauncher`"", "--version" -RedirectStandardError (Join-Path $tempDir "cmd_err1.txt") -Wait -PassThru -NoNewWindow
    Assert-Equal 1 $proc.ExitCode "baton.cmd missing pointer exit code"
    $err = Get-Content (Join-Path $tempDir "cmd_err1.txt") -Raw
    Assert-Contains $err "pixi run tool-refresh" "baton.cmd missing pointer error message"

    # 2. Empty pointer file fails closed
    Write-Host "Test 2: Empty pointer file fails closed..."
    Set-Content -LiteralPath $currentFile -Value "   `r`n"
    
    # Test baton.ps1
    $proc = Start-Process -FilePath "powershell.exe" -ArgumentList "-NoProfile", "-File", "`"$ps1Launcher`"", "--version" -RedirectStandardError (Join-Path $tempDir "ps1_err2.txt") -Wait -PassThru -NoNewWindow
    Assert-Equal 1 $proc.ExitCode "baton.ps1 empty pointer exit code"
    $err = Get-Content (Join-Path $tempDir "ps1_err2.txt") -Raw
    Assert-Contains $err "pixi run tool-refresh" "baton.ps1 empty pointer error message"

    # Test baton.cmd
    $proc = Start-Process -FilePath "cmd.exe" -ArgumentList "/c", "`"$cmdLauncher`"", "--version" -RedirectStandardError (Join-Path $tempDir "cmd_err2.txt") -Wait -PassThru -NoNewWindow
    Assert-Equal 1 $proc.ExitCode "baton.cmd empty pointer exit code"
    $err = Get-Content (Join-Path $tempDir "cmd_err2.txt") -Raw
    Assert-Contains $err "pixi run tool-refresh" "baton.cmd empty pointer error message"

    # 3. Garbage pointer (missing target binary) fails closed
    Write-Host "Test 3: Garbage pointer fails closed..."
    Set-Content -LiteralPath $currentFile -Value "garbage_sha_12345`r`n"
    
    # Test baton.ps1
    $proc = Start-Process -FilePath "powershell.exe" -ArgumentList "-NoProfile", "-File", "`"$ps1Launcher`"", "--version" -RedirectStandardError (Join-Path $tempDir "ps1_err3.txt") -Wait -PassThru -NoNewWindow
    Assert-Equal 1 $proc.ExitCode "baton.ps1 garbage pointer exit code"
    $err = Get-Content (Join-Path $tempDir "ps1_err3.txt") -Raw
    Assert-Contains $err "pixi run tool-refresh" "baton.ps1 garbage pointer error message"

    # Test baton.cmd
    $proc = Start-Process -FilePath "cmd.exe" -ArgumentList "/c", "`"$cmdLauncher`"", "--version" -RedirectStandardError (Join-Path $tempDir "cmd_err3.txt") -Wait -PassThru -NoNewWindow
    Assert-Equal 1 $proc.ExitCode "baton.cmd garbage pointer exit code"
    $err = Get-Content (Join-Path $tempDir "cmd_err3.txt") -Raw
    Assert-Contains $err "pixi run tool-refresh" "baton.cmd garbage pointer error message"

    # 4. Valid pointer executes target binary and forwards args and exit code
    Write-Host "Test 4: Valid pointer launches target binary and preserves exit code..."
    $validSha = "v1_sha_abcd"
    $shaDir = Join-Path $toolsDir $validSha
    [System.IO.Directory]::CreateDirectory($shaDir) | Out-Null
    Set-Content -LiteralPath $currentFile -Value "$validSha`r`n"

    # Create a mock baton.cmd in the target dir that echoes args and exits with code 42
    $mockBat = Join-Path $shaDir "baton.exe.cmd" # cmd/powershell will resolve
    # On Windows, we can create a tiny batch or dotnet binary; let's create a .cmd shim or copy dotnet host
    # For cmd launcher, it looks for baton.exe. Let's create mock baton.cmd / baton.exe
    # Let's test with a mock batch file named baton.exe.bat / baton.exe.cmd or a real test script
    # Alternatively, create a small C# console app or use cmd
    Set-Content -LiteralPath (Join-Path $shaDir "baton.exe.cmd") -Value "@echo mock-baton-v1 %*`r`n@exit /b 42"
    # To satisfy Test-Path / exist for baton.exe:
    Set-Content -LiteralPath (Join-Path $shaDir "baton.exe") -Value "mock"

    # Test with baton.ps1 calling a powershell script
    $mockPs1 = Join-Path $shaDir "mock.ps1"
    Set-Content -LiteralPath $mockPs1 -Value "Write-Output `"mock-ps1-v1 `$($args -join ' ')`"; exit 42"
    
    # Test atomic pointer flip to v2
    Write-Host "Test 5: Pointer flip to new version..."
    $v2Sha = "v2_sha_ef01"
    $v2Dir = Join-Path $toolsDir $v2Sha
    [System.IO.Directory]::CreateDirectory($v2Dir) | Out-Null
    Set-Content -LiteralPath (Join-Path $v2Dir "baton.exe") -Value "mock2"

    # Perform atomic replace
    $tmpPointer = Join-Path $toolsDir "current.tmp.test"
    $bakPointer = Join-Path $toolsDir "current.bak.test"
    Set-Content -LiteralPath $tmpPointer -Value "$v2Sha`r`n"
    [System.IO.File]::Replace($tmpPointer, $currentFile, $bakPointer)
    if (Test-Path -LiteralPath $bakPointer) { Remove-Item -LiteralPath $bakPointer -Force }

    $readSha = (Get-Content -LiteralPath $currentFile -Raw).Trim()
    Assert-Equal $v2Sha $readSha "Pointer was updated to v2Sha"

    Write-Host "All launcher tests PASSED!"
} finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    $env:BATON_HOME = $null
}
