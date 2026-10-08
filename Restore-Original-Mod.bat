@echo off
setlocal EnableExtensions
rem Puts back the mod DLL that was installed before Build-and-Install.bat first ran (kept in backup\).
rem The game folder is found the same way as in Build-and-Install.bat.
cd /d "%~dp0"
call :findgame "%~1"
if not defined GAMEDIR goto nogame
set "PLUGINS=%GAMEDIR%\BepInEx\plugins"

tasklist /FI "IMAGENAME eq Date Everything.exe" 2>nul | find /I "Date Everything.exe" >nul
if not errorlevel 1 goto gamerunning
if not exist "%~dp0backup\DateEverythingAccess-original.dll" goto nobackup

copy /y "%~dp0backup\DateEverythingAccess-original.dll" "%PLUGINS%\DateEverythingAccess.dll" >nul
if errorlevel 1 goto copyfailed
call :say "The mod you had installed before your first build has been put back."
exit /b 0

:nogame
call :say "Date Everything could not be found. Run this file with the game folder as its argument, or set DATE_EVERYTHING_DIR to it."
exit /b 1
:gamerunning
call :say "Date Everything is still running. Close the game completely, then run this file again."
exit /b 1
:nobackup
call :say "There is no backup yet, so there is nothing to restore. The original mod is still installed."
exit /b 1
:copyfailed
call :say "Restoring failed. The game folder may be write-protected."
exit /b 1

:findgame
set "GAMEDIR="
if not "%~1"=="" if exist "%~1\Date Everything.exe" set "GAMEDIR=%~1"
if defined GAMEDIR exit /b 0
for /f "usebackq delims=" %%G in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Find-GameDir.ps1"`) do set "GAMEDIR=%%G"
exit /b 0

:say
powershell -NoProfile -Command "Add-Type -AssemblyName PresentationFramework; [void][System.Windows.MessageBox]::Show('%~1','Date Everything Access')"
exit /b 0
