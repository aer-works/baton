[CmdletBinding()]
param(
    [switch]$DryRun,
    [switch]$Abort
)

$argsList = @()
if ($DryRun) { $argsList += "--dry-run" }
if ($Abort) { $argsList += "--abort" }

$script = Join-Path $PSScriptRoot "tool-refresh" "refresh.py"
python -u $script @argsList
exit $LASTEXITCODE
