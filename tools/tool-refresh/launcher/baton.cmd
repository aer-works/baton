@echo off
setlocal enabledelayedexpansion

if defined BATON_HOME (
    set "BH=%BATON_HOME%"
) else (
    set "BH=%USERPROFILE%\.baton"
)

set "TOOLS_DIR=%BH%\tools"
set "CURRENT_FILE=%TOOLS_DIR%\current"

if not exist "%CURRENT_FILE%" (
    >&2 echo baton: no current tool pointer found at "%CURRENT_FILE%". Run 'pixi run tool-refresh' to install.
    exit /b 1
)

set "TOOL_SHA="
for /f "usebackq delims=" %%i in ("%CURRENT_FILE%") do (
    if not defined TOOL_SHA set "TOOL_SHA=%%i"
)

if not defined TOOL_SHA (
    >&2 echo baton: invalid tool pointer in "%CURRENT_FILE%". Run 'pixi run tool-refresh' to install.
    exit /b 1
)

for /f "tokens=* delims= " %%a in ("!TOOL_SHA!") do set "TOOL_SHA=%%a"

if "!TOOL_SHA!"=="" (
    >&2 echo baton: invalid tool pointer in "%CURRENT_FILE%". Run 'pixi run tool-refresh' to install.
    exit /b 1
)

set "EXE_PATH=%TOOLS_DIR%\!TOOL_SHA!\baton.exe"

if not exist "%EXE_PATH%" (
    >&2 echo baton: tool binary not found at "%EXE_PATH%". Run 'pixi run tool-refresh' to install.
    exit /b 1
)

set "FINAL_EXE_PATH=%EXE_PATH%"
endlocal & set "EXE_PATH=%FINAL_EXE_PATH%"

"%EXE_PATH%" %*
exit /b %ERRORLEVEL%
