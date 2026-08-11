@echo off
REM Batch File yang dipercepat untuk launch Script GUI
REM Letakkan di Desktop untuk akses cepat

REM Get current directory
cd /d "%~dp0"

REM Check if RunScripts.bat exists
if not exist "RunScripts.bat" (
    echo.
    echo ERROR: RunScripts.bat not found!
    echo Please make sure this file is in the same directory as RunScripts.bat
    echo.
    pause
    exit /b 1
)

REM Clear screen and show startup message
cls
echo.
echo ╔════════════════════════════════════════════════════════════════╗
echo ║                                                                ║
echo ║        🚀 STEAM PLUGIN MANAGER - SCRIPT LAUNCHER              ║
echo ║                                                                ║
echo ║              Loading GUI Menu... Please Wait...               ║
echo ║                                                                ║
echo ╚════════════════════════════════════════════════════════════════╝
echo.
echo.

REM Set window title and run main menu
title Steam Plugin Manager - Script Launcher
color 0A

REM Run the main script menu
call RunScripts.bat

exit /b 0
