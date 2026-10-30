@echo off
rem Builds the Rampside installer: installer\bin\x64\Release\Rampside-<version>-x64.msi. Double-click to run.
rem Needs the MSFS 2024 SDK (MSFS2024_SDK or C:\MSFS 2024 SDK). Releases are built by GitHub Actions instead.
setlocal
cd /d "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\build.ps1"
if errorlevel 1 (
    echo.
    echo Building the installer failed.
    pause
    exit /b 1
)

explorer installer\bin\x64\Release
