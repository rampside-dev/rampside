@echo off
rem Builds the current Rampside desktop app (Release) and starts it. Double-click to run.
rem Extra arguments are passed on, e.g.  start-rampside.bat --fake-pmdg
setlocal
cd /d "%~dp0"

rem A running Rampside locks its files, so the build could not replace them.
tasklist /fi "imagename eq Rampside.exe" 2>nul | find /i "Rampside.exe" >nul
if not errorlevel 1 (
    echo Rampside is still running.
    choice /c YN /m "Close it and rebuild"
    if errorlevel 2 exit /b 1
    taskkill /im Rampside.exe >nul 2>&1
    timeout /t 2 /nobreak >nul
)

rem Use the MSFS 2024 SDK from MSFS2024_SDK, or from its default folder.
set SDK_ARG=
if not defined MSFS2024_SDK if exist "C:\MSFS 2024 SDK\SimConnect SDK" set SDK_ARG="-p:MsfsSdkRoot=C:\MSFS 2024 SDK"

echo Building Rampside...
dotnet build src\Rampside.Desktop -c Release -nologo -v q %SDK_ARG%
if errorlevel 1 (
    echo.
    echo Build failed. If someone is changing the code right now, wait a moment and try again.
    pause
    exit /b 1
)

start "" "src\Rampside.Desktop\bin\Release\net10.0-windows\Rampside.exe" %*
