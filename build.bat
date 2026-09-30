@echo off
rem Baut NetSpeedMonitor.exe mit dem C#-Compiler, der in Windows (.NET Framework 4.x) enthalten ist.
rem Laeuft NetSpeedMonitor gerade, vorher ueber Rechtsklick > Beenden schliessen.
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo C#-Compiler nicht gefunden - .NET Framework 4.x wird benoetigt.
  exit /b 1
)

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 ^
  /win32icon:NetSpeedMonitor.ico /win32manifest:app.manifest ^
  /out:NetSpeedMonitor.exe NetSpeedMonitor.cs
if errorlevel 1 (
  echo Build fehlgeschlagen.
  exit /b 1
)
echo NetSpeedMonitor.exe wurde erstellt.
