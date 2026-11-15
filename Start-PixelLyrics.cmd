@echo off
setlocal
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_CLI_HOME=%~dp0.tools\home"
start "" "%~dp0PixelLyrics.Probe\bin\Release\net10.0-windows10.0.19041.0\PixelLyrics.Probe.exe"
endlocal
