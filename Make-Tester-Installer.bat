@echo off
setlocal EnableExtensions
rem Builds DateEverythingAccess-Setup.exe for testers with the C# compiler built into Windows,
rem from the mod as installed in the game folder (run Build-and-Install.bat first).
rem The game folder is found the same way as in Build-and-Install.bat.
cd /d "%~dp0"
set "PKG=%~dp0tester-package"
set "OUT=%~dp0DateEverythingAccess-Setup.exe"
set "LOG=%~dp0tester-package\setup-build-log.txt"
set "GAMEDIR="
if not "%~1"=="" if exist "%~1\Date Everything.exe" set "GAMEDIR=%~1"
if not defined GAMEDIR for /f "usebackq delims=" %%G in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Find-GameDir.ps1"`) do set "GAMEDIR=%%G"
if not defined GAMEDIR goto nogame
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PKG%\build-setup.ps1" -Package "%PKG%" -Out "%OUT%" -Log "%LOG%" -GameDir "%GAMEDIR%"
if errorlevel 1 goto failed
if not exist "%OUT%" goto failed
call :say "DateEverythingAccess-Setup.exe is ready in the project folder. Give that one file to your testers."
exit /b 0
:nogame
call :say "Date Everything could not be found. Run this file with the game folder as its argument, or set DATE_EVERYTHING_DIR to it."
exit /b 1
:failed
call :say "Making the setup file failed. tester-package\setup-build-log.txt says why."
exit /b 1
:say
powershell -NoProfile -Command "Add-Type -AssemblyName PresentationFramework; [void][System.Windows.MessageBox]::Show('%~1','Date Everything Access')"
exit /b 0
