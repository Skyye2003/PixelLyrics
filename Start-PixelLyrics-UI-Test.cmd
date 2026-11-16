@echo off
setlocal
start "" "%~dp0.tools\dotnet\dotnet.exe" "%~dp0release\ui-rhythm\PixelLyrics.Probe.dll" --gui
endlocal
