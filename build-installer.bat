@echo off
rem Builds the Rampside installer: installer\bin\Release\Rampside-<version>-x64.msi. Double-click to run.
rem The app is published self-contained, so players need neither the .NET runtime nor the MSFS SDK.
setlocal
cd /d "%~dp0"

rem SimConnect.dll comes from the MSFS 2024 SDK: MSFS2024_SDK, or its default folder.
set SDK_ARG=
if not defined MSFS2024_SDK (
    if exist "C:\MSFS 2024 SDK\SimConnect SDK" (
        set SDK_ARG="-p:MsfsSdkRoot=C:\MSFS 2024 SDK"
    ) else (
        echo MSFS 2024 SDK not found. Set MSFS2024_SDK to its folder and try again.
        goto :failed
    )
)

set PUBLISH_DIR=%~dp0installer\obj\publish
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"

echo Publishing Rampside...
dotnet publish src\Rampside.Desktop -c Release -r win-x64 --self-contained -p:DebugType=none -o "%PUBLISH_DIR%" -nologo -v q %SDK_ARG%
if errorlevel 1 goto :failed

echo Building the installer...
rem --no-incremental: published files keep their old timestamps, so an incremental build could ship a stale MSI.
dotnet build installer -c Release --no-incremental -nologo -v q "-p:RampsideAppDir=%PUBLISH_DIR%\\"
if errorlevel 1 goto :failed

echo.
echo Done:
dir /b installer\bin\x64\Release\*.msi
explorer installer\bin\x64\Release
exit /b 0

:failed
echo.
echo Building the installer failed.
pause
exit /b 1
