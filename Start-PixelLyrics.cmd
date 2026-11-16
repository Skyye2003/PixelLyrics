@echo off
setlocal
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_CLI_HOME=%~dp0.tools\home"
if exist "%~dp0release\win-x64\PixelLyrics.exe" (
    start "" "%~dp0release\win-x64\PixelLyrics.exe"
    exit /b 0
)
set "PIXELLYRICS_DLL=%~dp0PixelLyrics.Probe\bin\Release\net10.0-windows10.0.19041.0\PixelLyrics.Probe.dll"
if not exist "%PIXELLYRICS_DLL%" (
    echo PixelLyrics build not found. Build the Release configuration first.
    pause
    exit /b 1
)
if exist "%~dp0.tools\dotnet\dotnet.exe" (
    start "" "%~dp0.tools\dotnet\dotnet.exe" "%PIXELLYRICS_DLL%" --gui
) else (
    start "" dotnet "%PIXELLYRICS_DLL%" --gui
)
endlocal
