@echo off
setlocal
start "" "%~dp0.tools\dotnet\dotnet.exe" "%~dp0release\auto-live-test\PixelLyrics.Probe.dll" --gui
endlocal
