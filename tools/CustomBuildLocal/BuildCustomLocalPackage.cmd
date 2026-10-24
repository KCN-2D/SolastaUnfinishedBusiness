@echo off
setlocal

chcp 65001 >nul

set "SCRIPT_DIR=%~dp0"
set "SCRIPT_PATH=%SCRIPT_DIR%internal\BuildCustomLocalPackage.ps1"

if not exist "%SCRIPT_PATH%" (
    echo エラー: ビルドスクリプトが見つかりません: "%SCRIPT_PATH%"
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PATH%" %*

set "EXIT_CODE=%errorlevel%"
exit /b %EXIT_CODE%
