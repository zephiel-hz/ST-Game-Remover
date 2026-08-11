@echo off
setlocal enabledelayedexpansion

echo ==============================
echo 🚀 Build Portable .NET 9 EXE
echo ==============================
echo Executable akan berjalan TANPA memerlukan admin privileges
echo.

REM Clean old build
echo Cleaning previous build...
rmdir /s /q bin\Release\net9.0-windows\win-x64\publish 2>nul

REM Ganti "SteamPluginManager.csproj" dengan nama file project kamu kalau beda
REM Manifest sudah di-embed via app.manifest dengan asInvoker privilege level
echo Building and publishing...
dotnet publish SteamPluginManager.csproj -r win-x64 -c Release --self-contained false ^
 /p:PublishSingleFile=false ^
 /p:PublishTrimmed=false ^
 /p:DebugType=embedded ^
 /p:IncludeAllContentForSelfExtract=true

if !ERRORLEVEL! EQU 0 (
    echo.
    echo ==============================
    echo ✅ Build Selesai!
    echo ==============================
    echo File hasil ada di:
    echo bin\Release\net9.0-windows\win-x64\publish\
    echo.
    echo SGR.exe telah di-publish dengan konfigurasi:
    echo - Manifest dengan asInvoker privilege level (tidak perlu admin)
    echo - Drag-drop OLE fully kompatibel tanpa privilege conflicts
    echo - DPI Awareness enabled untuk modern Windows
    echo - Long path support enabled
    echo.
    echo Catatan: 
    echo - Executable (SGR.exe) berjalan dengan user privileges normal
    echo - File operations yang memerlukan admin akan request UAC saat diperlukan
    echo - Drag-drop file import berfungsi sempurna di mode normal
    echo.
) else (
    echo.
    echo ❌ Build FAILED!
    echo.
)

pause
