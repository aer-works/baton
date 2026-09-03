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

# Standard Win32/CRT command-line quoting (the algorithm CommandLineToArgvW expects on the way in),
# so a forwarded argument round-trips through cmd's %* passthrough and the target process's own argv
# parser byte-for-byte instead of being reconstructed loosely by a naive space-join.
function ConvertTo-CommandLineArg([string]$arg) {
    if ($arg -eq "") { return '""' }
    if ($arg -notmatch '[\s"]') { return $arg }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    for ($i = 0; $i -lt $arg.Length; $i++) {
        $backslashes = 0
        while ($i -lt $arg.Length -and $arg[$i] -eq '\') { $backslashes++; $i++ }
        if ($i -eq $arg.Length) {
            [void]$sb.Append('\' * ($backslashes * 2))
            break
        } elseif ($arg[$i] -eq '"') {
            [void]$sb.Append('\' * ($backslashes * 2 + 1))
            [void]$sb.Append('"')
        } else {
            [void]$sb.Append('\' * $backslashes)
            [void]$sb.Append($arg[$i])
        }
    }
    [void]$sb.Append('"')
    return $sb.ToString()
}

# Builds a real mock `baton.exe` fixture in $outDir: echoes $label plus its forwarded argv, then
# exits with $exitCode. Compiled with the legacy csc.exe rather than `dotnet publish`/`dotnet build`
# -- a few hundred milliseconds, and it doesn't compete for the lock `dotnet build` takes (see
# tools/gates/gates.py's OVERLAP/BUILD_PHASE split for why that matters here).
function New-MockBatonExe([string]$outDir, [string]$label, [int]$exitCode) {
    [System.IO.Directory]::CreateDirectory($outDir) | Out-Null
    $cscCandidates = @(
        (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
        (Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe")
    )
    $csc = $cscCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $csc) {
        throw "csc.exe not found under $env:WINDIR\Microsoft.NET -- cannot build the launcher test's mock exe fixture"
    }

    $src = Join-Path $outDir "MockBaton.cs"
    $exe = Join-Path $outDir "baton.exe"
    $lines = @(
        'class MockBaton {',
        '    static int Main(string[] args) {',
        ('        System.Console.WriteLine("' + $label + ' " + string.Join(" ", args));'),
        ('        return ' + $exitCode + ';'),
        '    }',
        '}'
    )
    Set-Content -LiteralPath $src -Value $lines
    $cscOutput = & $csc /nologo "/out:$exe" $src 2>&1
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "csc.exe failed to build the mock baton.exe fixture: $cscOutput"
    }
    return $exe
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
    Write-Host "Test 4: Valid pointer launches target binary and forwards args verbatim..."
    $validSha = "v1_sha_abcd"
    $shaDir = Join-Path $toolsDir $validSha
    [System.IO.Directory]::CreateDirectory($shaDir) | Out-Null
    Set-Content -LiteralPath $currentFile -Value "$validSha`r`n"
    New-MockBatonExe -outDir $shaDir -label "mock-v1" -exitCode 42 | Out-Null

    # A space, a `!` next to a real variable name (would get eaten by delayed expansion left active
    # in baton.cmd's dispatch line), and an embedded double quote -- the three hazards F3's fix and
    # this test exist for. baton.cmd's manual `%*` forwarding is where all three matter; baton.ps1's
    # `& $exePath @args` forwarding is idiomatic and untested only for the embedded quote, which
    # Windows PowerShell 5.1's own native-command argument passing mangles regardless of what
    # baton.ps1 does with it (a host limitation, not a launcher defect) -- so the space and `!` cases
    # are asserted on both launchers, and the quote only through baton.cmd.
    $cmdTestArgs = @('has space', 'bang!TOOL_SHA!end', 'quo"te')
    $rawArgs = ($cmdTestArgs | ForEach-Object { ConvertTo-CommandLineArg $_ }) -join ' '

    # cmd.exe's own `/c "..."` handling strips only the very first and very last quote of the whole
    # command line when it begins AND ends with one, then treats everything between as a single
    # unparsed token -- an extra outer quote pair is required so cmd re-parses the inner content
    # (the launcher path plus forwarded args) normally instead of swallowing it as one blob.
    $cmdOut = Join-Path $tempDir "cmd_out4.txt"
    $proc = Start-Process -FilePath "cmd.exe" -ArgumentList "/c `"`"$cmdLauncher`" $rawArgs`"" -RedirectStandardOutput $cmdOut -RedirectStandardError (Join-Path $tempDir "cmd_err4.txt") -Wait -PassThru -NoNewWindow
    Assert-Equal 42 $proc.ExitCode "baton.cmd exit code propagation"
    $out = (Get-Content -LiteralPath $cmdOut -Raw)
    Assert-Contains $out "mock-v1" "baton.cmd launched the mock target"
    foreach ($a in $cmdTestArgs) { Assert-Contains $out $a "baton.cmd forwarded arg '$a' verbatim" }

    $ps1TestArgs = @('has space', 'bang!TOOL_SHA!end')
    $ps1RawArgs = ($ps1TestArgs | ForEach-Object { ConvertTo-CommandLineArg $_ }) -join ' '
    $ps1Out = Join-Path $tempDir "ps1_out4.txt"
    $proc = Start-Process -FilePath "powershell.exe" -ArgumentList "-NoProfile -File `"$ps1Launcher`" $ps1RawArgs" -RedirectStandardOutput $ps1Out -RedirectStandardError (Join-Path $tempDir "ps1_err4.txt") -Wait -PassThru -NoNewWindow
    Assert-Equal 42 $proc.ExitCode "baton.ps1 exit code propagation"
    $out = (Get-Content -LiteralPath $ps1Out -Raw)
    Assert-Contains $out "mock-v1" "baton.ps1 launched the mock target"
    foreach ($a in $ps1TestArgs) { Assert-Contains $out $a "baton.ps1 forwarded arg '$a' verbatim" }

    # 5. Atomic pointer flip actually redirects the launcher to the new target
    Write-Host "Test 5: Pointer flip to new version launches the NEW target..."
    $v2Sha = "v2_sha_ef01"
    $v2Dir = Join-Path $toolsDir $v2Sha
    [System.IO.Directory]::CreateDirectory($v2Dir) | Out-Null
    New-MockBatonExe -outDir $v2Dir -label "mock-v2" -exitCode 0 | Out-Null

    $tmpPointer = Join-Path $toolsDir "current.tmp.test"
    $bakPointer = Join-Path $toolsDir "current.bak.test"
    Set-Content -LiteralPath $tmpPointer -Value "$v2Sha`r`n"
    [System.IO.File]::Replace($tmpPointer, $currentFile, $bakPointer)
    if (Test-Path -LiteralPath $bakPointer) { Remove-Item -LiteralPath $bakPointer -Force }

    $readSha = (Get-Content -LiteralPath $currentFile -Raw).Trim()
    Assert-Equal $v2Sha $readSha "Pointer was updated to v2Sha"

    $cmdOut5 = Join-Path $tempDir "cmd_out5.txt"
    $proc = Start-Process -FilePath "cmd.exe" -ArgumentList "/c `"`"$cmdLauncher`" ping`"" -RedirectStandardOutput $cmdOut5 -RedirectStandardError (Join-Path $tempDir "cmd_err5.txt") -Wait -PassThru -NoNewWindow
    Assert-Equal 0 $proc.ExitCode "baton.cmd exit code after pointer flip"
    $out5 = (Get-Content -LiteralPath $cmdOut5 -Raw)
    Assert-Contains $out5 "mock-v2" "baton.cmd ran the NEW target after the pointer flip"
    if ($out5.Contains("mock-v1")) {
        throw "Assertion failed: baton.cmd still ran the OLD target after the pointer flip. Got:`n$out5"
    }

    # 6. register-daemon-task.ps1 never calls New-ScheduledTaskSettingsSet with a parameter name
    # that cmdlet doesn't actually have (#1770: -DisallowStartIfOnBatteries/-StopIfGoingOnBatteries
    # don't exist on it and blew up the register call with NamedParameterNotFound before either
    # script reached Register-ScheduledTask).
    Write-Host "Test 6: register-daemon-task.ps1 only passes real New-ScheduledTaskSettingsSet parameters..."
    $registerScript = [System.IO.Path]::Combine($repoRoot, "tools", "tool-refresh", "register-daemon-task.ps1")
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($registerScript, [ref]$null, [ref]$null)
    $realParams = (Get-Command New-ScheduledTaskSettingsSet).Parameters.Keys
    $calls = $ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -eq "New-ScheduledTaskSettingsSet"
    }, $true)
    if ($calls.Count -eq 0) {
        throw "Assertion failed: no New-ScheduledTaskSettingsSet call found in $registerScript"
    }
    foreach ($call in $calls) {
        $usedParams = $call.CommandElements | Where-Object { $_ -is [System.Management.Automation.Language.CommandParameterAst] } | ForEach-Object { $_.ParameterName }
        foreach ($p in $usedParams) {
            $match = $realParams | Where-Object { $_ -like "$p*" }
            if (-not $match) {
                throw "Assertion failed: New-ScheduledTaskSettingsSet call in $registerScript passes unknown parameter '-$p'"
            }
        }
    }

    Write-Host "All launcher tests PASSED!"
} finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    $env:BATON_HOME = $null
}
