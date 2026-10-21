@echo off
setlocal EnableExtensions
rem Builds the mod and installs it into the game. The game folder is found automatically
rem (scripts\Find-GameDir.ps1); to use another one, run: Build-and-Install.bat "X:\path\to\Date Everything"
rem or set the DATE_EVERYTHING_DIR environment variable.
cd /d "%~dp0"
set "LOG=%~dp0build-log.txt"
call :findgame "%~1"
if not defined GAMEDIR goto nogame
set "PLUGINS=%GAMEDIR%\BepInEx\plugins"

tasklist /FI "IMAGENAME eq Date Everything.exe" 2>nul | find /I "Date Everything.exe" >nul
if not errorlevel 1 goto gamerunning

if not exist "%~dp0backup\DateEverythingAccess-original.dll" call :backup

echo Build started %date% %time% > "%LOG%"
echo Game folder: %GAMEDIR% >> "%LOG%"
dotnet build "%~dp0DateEverythingAccess.csproj" -c Release -p:GameDir="%GAMEDIR%" -nologo >> "%LOG%" 2>&1
if errorlevel 1 goto failed

echo Build finished OK %date% %time% >> "%LOG%"
call :say "Build succeeded and the new mod is installed. You can start the game."
exit /b 0

:failed
echo Build FAILED %date% %time% >> "%LOG%"
call :say "The build failed. Nothing in the game was changed. build-log.txt in the project folder says why."
exit /b 1

:nogame
call :say "Date Everything could not be found. Run this file with the game folder as its argument, or set DATE_EVERYTHING_DIR to it."
exit /b 1

:gamerunning
call :say "Date Everything is still running. Close the game completely, then run this file again."
exit /b 1

:backup
mkdir "%~dp0backup" 2>nul
if exist "%PLUGINS%\DateEverythingAccess.dll" copy /y "%PLUGINS%\DateEverythingAccess.dll" "%~dp0backup\DateEverythingAccess-original.dll" >nul
exit /b 0

:findgame
set "GAMEDIR="
if not "%~1"=="" if exist "%~1\Date Everything.exe" set "GAMEDIR=%~1"
if defined GAMEDIR exit /b 0
for /f "usebackq delims=" %%G in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Find-GameDir.ps1"`) do set "GAMEDIR=%%G"
exit /b 0

:say
powershell -NoProfile -Command "Add-Type -AssemblyName PresentationFramework; [void][System.Windows.MessageBox]::Show('%~1','Date Everything Access')"
exit /b 0
