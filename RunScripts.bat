@echo off
setlocal enabledelayedexpansion
color 0A
title Steam Plugin Manager - Script Launcher

:menu
cls
echo.
echo ============================================================
echo      STEAM PLUGIN MANAGER - PYTHON SCRIPT LAUNCHER
echo                      Version 1.0
echo ============================================================
echo.
echo Pilih Kategori Script:
echo.
echo   [1] Debug Scripts          (Debugging - Troubleshooting)
echo   [2] Database Scripts       (Populate - Update)
echo   [3] Testing Scripts        (Verification - Testing)
echo   [4] Utility Scripts        (Helper Functions)
echo   [5] Token Management       (Device Token Generation)
echo   [6] View Documentation
echo   [0] Exit
echo.
set /p choice="Masukkan pilihan (0-6): "

if "%choice%"=="1" goto debug_menu
if "%choice%"=="2" goto database_menu
if "%choice%"=="3" goto testing_menu
if "%choice%"=="4" goto utility_menu
if "%choice%"=="5" goto token_menu
if "%choice%"=="6" goto documentation
if "%choice%"=="0" goto exit_program
cls
echo Invalid choice. Please try again.
timeout /t 2 /nobreak
goto menu

:debug_menu
cls
echo.
echo ============================================================
echo                   DEBUG SCRIPTS MENU
echo ============================================================
echo.
echo   [1] debug_supabase.py        - Debug Supabase RLS & Connectivity
echo   [2] debug_cards.py           - Debug Hz Manifest Card Display
echo   [3] extended_debug.py        - Extended Debugging with Logging
echo   [4] quick_debug.py           - Quick Troubleshooting
echo   [5] quick_test.py            - Quick Functionality Test
echo   [0] Back to Main Menu
echo.
set /p script_choice="Pilih script (0-5): "

if "%script_choice%"=="1" call :run_script "Scripts\Debug\debug_supabase.py"
if "%script_choice%"=="2" call :run_script "Scripts\Debug\debug_cards.py"
if "%script_choice%"=="3" call :run_script "Scripts\Debug\extended_debug.py"
if "%script_choice%"=="4" call :run_script "Scripts\Debug\quick_debug.py"
if "%script_choice%"=="5" call :run_script "Scripts\Debug\quick_test.py"
if "%script_choice%"=="0" goto menu
cls
echo Invalid choice. Try again.
timeout /t 2 /nobreak
goto debug_menu

:database_menu
cls
echo.
echo ============================================================
echo                  DATABASE SCRIPTS MENU
echo ============================================================
echo.
echo Population & Update:
echo   [1] populate_hzmanifest.py      - Populate Hz Manifest (Main)
echo   [2] populate_hzmanifest_v2.py   - Populate Hz Manifest (V2)
echo   [3] update_steam_names.py       - Update Game Names
echo   [4] update_steam_metadata.py    - Update Game Metadata
echo   [5] update_via_upsert.py        - Update via UPSERT
echo   [6] create_new_table_test.py    - Test Table Creation
echo   [7] update_folder_path_from_bucket.py - Update Folder Paths from Bucket
echo   [8] fetch_and_upsert_dlc_from_bucket.py - Fetch and Upsert DLC from Bucket
echo.
echo Genre Management:
echo   [9] refresh_game_genres.py      - Refresh/Fetch Game Genres from Steam
echo   [10] genre_refresh_config.py     - Genre Refresh Configuration Tool
echo.
echo   [0] Back to Main Menu
echo.
set /p script_choice="Pilih script (0-8): "

if "%script_choice%"=="1" call :run_script "Scripts\Database\populate_hzmanifest.py"
if "%script_choice%"=="2" call :run_script "Scripts\Database\populate_hzmanifest_v2.py"
if "%script_choice%"=="3" call :run_script "Scripts\Database\update_steam_names.py"
if "%script_choice%"=="4" call :run_script "Scripts\Database\update_steam_metadata.py"
if "%script_choice%"=="5" call :run_script "Scripts\Database\update_via_upsert.py"
if "%script_choice%"=="6" call :run_script "Scripts\Database\create_new_table_test.py"
if "%script_choice%"=="7" call :run_script "Scripts\Database\update_folder_path_from_bucket.py"
if "%script_choice%"=="8" call :run_script "Scripts\Database\fetch_and_upsert_dlc_from_bucket.py"
if "%script_choice%"=="9" call :run_script "Scripts\Database\refresh_game_genres.py"
if "%script_choice%"=="10" call :run_script "Scripts\Database\genre_refresh_config.py"
if "%script_choice%"=="0" goto menu
cls
echo Invalid choice. Try again.
timeout /t 2 /nobreak
goto database_menu

:testing_menu
cls
echo.
echo ============================================================
echo                  TESTING SCRIPTS MENU
echo ============================================================
echo.
echo Verification Scripts:
echo   [1] verify_hz_manifest.py     - Hz Manifest Verification
echo   [2] verify_hz_fix.py          - Hz Fix Verification
echo   [3] verify_metadata_update.py - Metadata Update Verify
echo   [4] verify_new_features.py    - New Features Verify
echo   [5] verify_table.py           - Table Verify
echo   [6] verify_updates.py         - Updates Verify
echo.
echo Feature Tests:
echo   [7] test_appid_steam.py       - Steam AppID Integration
echo   [8] test_cards_binding.py     - Cards Binding Test
echo   [9] test_correct_table.py     - Table Correctness
echo   [10] test_delete_repopulate.py - Delete & Repopulate
echo   [11] test_fetch_fix.py        - Fetch Fix Test
echo   [12] test_patch_variants.py   - Patch Variants
echo.
echo Final Tests:
echo   [13] final_hzmanifest_test.py     - Final Hz Test
echo   [14] final_test_connection.py     - Connection Test
echo   [15] final_update_game_names.py   - Update Names Test
echo.
echo   [0] Back to Main Menu
echo.
set /p script_choice="Pilih script (0-15): "

if "%script_choice%"=="1" call :run_script "Scripts\Testing\verify_hz_manifest.py"
if "%script_choice%"=="2" call :run_script "Scripts\Testing\verify_hz_fix.py"
if "%script_choice%"=="3" call :run_script "Scripts\Testing\verify_metadata_update.py"
if "%script_choice%"=="4" call :run_script "Scripts\Testing\verify_new_features.py"
if "%script_choice%"=="5" call :run_script "Scripts\Testing\verify_table.py"
if "%script_choice%"=="6" call :run_script "Scripts\Testing\verify_updates.py"
if "%script_choice%"=="7" call :run_script "Scripts\Testing\test_appid_steam.py"
if "%script_choice%"=="8" call :run_script "Scripts\Testing\test_cards_binding.py"
if "%script_choice%"=="9" call :run_script "Scripts\Testing\test_correct_table.py"
if "%script_choice%"=="10" call :run_script "Scripts\Testing\test_delete_repopulate.py"
if "%script_choice%"=="11" call :run_script "Scripts\Testing\test_fetch_fix.py"
if "%script_choice%"=="12" call :run_script "Scripts\Testing\test_patch_variants.py"
if "%script_choice%"=="13" call :run_script "Scripts\Testing\final_hzmanifest_test.py"
if "%script_choice%"=="14" call :run_script "Scripts\Testing\final_test_connection.py"
if "%script_choice%"=="15" call :run_script "Scripts\Testing\final_update_game_names.py"
if "%script_choice%"=="0" goto menu
cls
echo Invalid choice. Try again.
timeout /t 2 /nobreak
goto testing_menu

:utility_menu
cls
echo.
echo ============================================================
echo                  UTILITY SCRIPTS MENU
echo ============================================================
echo.
echo   [1] check_jwt.py                  - JWT Token Validation
echo   [2] check_steam_appids.py         - Steam AppID Validation
echo   [3] check_name_difference.py      - Compare Names
echo   [4] check_one_record.py           - Inspect Single Record
echo   [5] list_rpc_functions.py         - List RPC Functions
echo   [6] try_rpc_functions.py          - Test RPC Functions
echo   [7] build_fix_report.py           - Build Fix Report
echo   [8] status_report.py              - Status Report
echo   [9] run_and_log.py                - Run with Logging
echo   [10] thumbnail_fix_verification.py - Thumbnail Verification
echo   [0] Back to Main Menu
echo.
set /p script_choice="Pilih script (0-10): "

if "%script_choice%"=="1" call :run_script "Scripts\Utilities\check_jwt.py"
if "%script_choice%"=="2" call :run_script "Scripts\Utilities\check_steam_appids.py"
if "%script_choice%"=="3" call :run_script "Scripts\Utilities\check_name_difference.py"
if "%script_choice%"=="4" call :run_script "Scripts\Utilities\check_one_record.py"
if "%script_choice%"=="5" call :run_script "Scripts\Utilities\list_rpc_functions.py"
if "%script_choice%"=="6" call :run_script "Scripts\Utilities\try_rpc_functions.py"
if "%script_choice%"=="7" call :run_script "Scripts\Utilities\build_fix_report.py"
if "%script_choice%"=="8" call :run_script "Scripts\Utilities\status_report.py"
if "%script_choice%"=="9" call :run_script "Scripts\Utilities\run_and_log.py"
if "%script_choice%"=="10" call :run_script "Scripts\Utilities\thumbnail_fix_verification.py"
if "%script_choice%"=="0" goto menu
cls
echo Invalid choice. Try again.
timeout /t 2 /nobreak
goto utility_menu

:token_menu
cls
echo.
echo ============================================================
echo                TOKEN MANAGEMENT MENU
echo ============================================================
echo.
echo Device Token Generation and Management:
echo   [1] generate_device_tokens.py         - Generate Secure Tokens
echo   [2] generate_and_insert_tokens.py     - Generate and insert tokens to Supabase
echo   [3] insert_tokens_to_supabase.py      - Insert Tokens to DB
echo   [4] check_device_tokens.py            - List available / used tokens
echo   [5] revoke_device_token.py            - Revoke or delete token
echo.
echo Documentation:
echo   [6] View DEVICE_TOKEN_GUIDE.md      - Token Management Guide
echo   [7] View create_device_tokens_table.sql - SQL Setup Script
echo.
echo   [0] Back to Main Menu
echo.
set /p script_choice="Pilih script (0-7): "

if "%script_choice%"=="1" call :run_script_token "Scripts\generate_device_tokens.py"
if "%script_choice%"=="2" call :run_script_token "Scripts\generate_and_insert_tokens.py"
if "%script_choice%"=="3" call :run_script_token "Scripts\insert_tokens_to_supabase.py"
if "%script_choice%"=="4" call :run_script_token "Scripts\check_device_tokens.py"
if "%script_choice%"=="5" call :run_revoke
if "%script_choice%"=="6" start notepad "DEVICE_TOKEN_GUIDE.md"
if "%script_choice%"=="7" start notepad "create_device_tokens_table.sql"
if "%script_choice%"=="0" goto menu
cls
echo Invalid choice. Try again.
timeout /t 2 /nobreak
goto token_menu

:run_script_token
cls
echo.
echo ============================================================
echo Running: %~1
echo ============================================================
echo.
echo Token Management Script
echo Usage:
echo   generate_device_tokens.py [--count N] [--length N] [--output FILE]
echo   insert_tokens_to_supabase.py [tokens.json]
echo.
python "%~1"
echo.
echo ============================================================
echo Script execution completed!
echo ============================================================
echo.
pause
goto token_menu

:run_revoke
cls
echo.
echo ============================================================
echo Revoke / Delete Token
echo ============================================================
echo.
set /p revtoken="Enter token to revoke (or leave empty to cancel): "
if "%revtoken%"=="" goto token_menu
echo.
echo Revoking token: %revtoken% ...
python "Scripts\revoke_device_token.py" "%revtoken%"
echo.
pause
goto token_menu

:run_script
cls
echo.
echo ============================================================
echo Running: %~1
echo ============================================================
echo.
python "%~1"
echo.
echo ============================================================
echo Script execution completed!
echo ============================================================
echo.
pause
goto menu

:documentation
cls
echo.
echo ============================================================
echo                  DOCUMENTATION MENU
echo ============================================================
echo.
echo Script Documentation:
echo   [1] README.md             - Complete Script Documentation
echo   [2] QUICK_REFERENCE.md    - Quick Reference Guide
echo   [3] ORGANIZATION.md       - Scripts Organization
echo   [4] INDEX.md              - Quick Access Index
echo.
echo Genre Management:
echo   [5] QUICK_START.md                  - Genre Refresh Quick Start
echo   [6] GENRE_REFRESH_GUIDE.md          - Genre Refresh Full Guide
echo.
echo Token Management:
echo   [7] DEVICE_TOKEN_GUIDE.md           - Token Management Guide
echo   [8] create_device_tokens_table.sql  - SQL Setup Script
echo.
echo Tools:
echo   [9] Open Scripts Folder in Explorer
echo   [0] Back to Main Menu
echo.
set /p doc_choice="Pilih (0-9): "

if "%doc_choice%"=="1" start notepad "Scripts\README.md"
if "%doc_choice%"=="2" start notepad "Scripts\QUICK_REFERENCE.md"
if "%doc_choice%"=="3" start notepad "Scripts\ORGANIZATION.md"
if "%doc_choice%"=="4" start notepad "Scripts\INDEX.md"
if "%doc_choice%"=="5" start notepad "Scripts\Database\QUICK_START.md"
if "%doc_choice%"=="6" start notepad "Scripts\Database\GENRE_REFRESH_GUIDE.md"
if "%doc_choice%"=="7" start notepad "Documentation\DEVICE_TOKEN_GUIDE.md"
if "%doc_choice%"=="8" start notepad "Scripts\Database\create_device_tokens_table.sql"
if "%doc_choice%"=="9" start explorer "Scripts\"
if "%doc_choice%"=="0" goto menu
timeout /t 2 /nobreak
goto documentation

:exit_program
cls
echo.
echo Thank you for using Script Launcher!
echo Goodbye...
echo.
timeout /t 2 /nobreak
endlocal
exit
