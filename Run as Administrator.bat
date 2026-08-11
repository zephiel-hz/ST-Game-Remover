@echo off
REM Run SGR (ST Game Remover) as Administrator
REM This script will elevate the application to run with admin privileges

REM Check if already running as admin
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting Administrator privileges...
    powershell -Command "Start-Process '%~dpnx0' -Verb RunAs"
    exit /b
)

REM If we get here, we're running as admin
echo Starting ST Game Remover as Administrator...
start "" "%~dp0bin\Release\net9.0-windows\SGR.exe"
exit /b
