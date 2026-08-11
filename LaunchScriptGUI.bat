@echo off
REM Create VBS file to run batch in a more user-friendly window
setlocal enabledelayedexpansion

REM Get the directory where this script is located
for /f "delims=" %%a in ('cd') do set "SCRIPT_DIR=%%a"

REM Run the RunScripts.bat with better window properties
start "Python Script Launcher" cmd /k "cd /d "%SCRIPT_DIR%" && RunScripts.bat"

exit /b 0
