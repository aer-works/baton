$ErrorActionPreference = "Stop"

$batonHome = if ($env:BATON_HOME) { $env:BATON_HOME } else { Join-Path $HOME ".baton" }
$toolsDir = Join-Path $batonHome "tools"
$currentFile = Join-Path $toolsDir "current"

if (-not (Test-Path -LiteralPath $currentFile)) {
    [Console]::Error.WriteLine("baton: no current tool pointer found at '$currentFile'. Run 'pixi run tool-refresh' to install.")
    exit 1
}

$rawSha = Get-Content -LiteralPath $currentFile -Raw -ErrorAction SilentlyContinue
$toolSha = if ($rawSha) { $rawSha.Trim() } else { "" }

if ([string]::IsNullOrWhiteSpace($toolSha)) {
    [Console]::Error.WriteLine("baton: invalid tool pointer in '$currentFile'. Run 'pixi run tool-refresh' to install.")
    exit 1
}

$exePath = Join-Path (Join-Path $toolsDir $toolSha) "baton.exe"

if (-not (Test-Path -LiteralPath $exePath)) {
    [Console]::Error.WriteLine("baton: tool binary not found at '$exePath'. Run 'pixi run tool-refresh' to install.")
    exit 1
}

# #1897: PowerShell 5.1 wraps a redirected native stderr line as a terminating NativeCommandError under Stop, so invoke the native target under Continue.
$ErrorActionPreference = "Continue"
& $exePath @args
exit $LASTEXITCODE
