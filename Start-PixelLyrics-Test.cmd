@echo off
setlocal
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
start "" "%~dp0.tools\dotnet\dotnet.exe" "%~dp0release\netease-integration\PixelLyrics.Probe.dll" --gui
endlocal
