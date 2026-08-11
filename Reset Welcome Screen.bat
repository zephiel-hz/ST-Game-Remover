@echo off
REM ============================================
REM Reset Welcome Screen Preference
REM ============================================
REM This batch file resets the Welcome Screen preference
REM so it will be displayed again on next application start

echo.
echo Resetting Welcome Screen preference...
echo.

REM Add registry entry to show welcome screen (1 = true/show)
reg add "HKEY_CURRENT_USER\SOFTWARE\STGameRemover" /v "ShowWelcomeScreen" /t REG_DWORD /d 1 /f

if %errorlevel% equ 0 (
    echo.
    echo ✓ Welcome Screen preference has been reset successfully!
    echo The Welcome Screen will be displayed on the next application start.
    echo.
) else (
    echo.
    echo ✗ Failed to reset Welcome Screen preference.
    echo Please try running this file as Administrator.
    echo.
    pause
    exit /b 1
)

pause
