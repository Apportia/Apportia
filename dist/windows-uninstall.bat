@echo off
setlocal EnableDelayedExpansion

:: ── Elevate if needed ────────────────────────────────────────────────────────

>nul 2>&1 "%SystemRoot%\System32\cacls.exe" "%SystemRoot%\System32\config\SYSTEM"
if not %errorlevel% == 0 (
    set "vbs=%temp%\elevate_%~n0.vbs"
    echo Set UAC = CreateObject^("Shell.Application"^) > "!vbs!"
    echo UAC.ShellExecute "cmd.exe", "/C call ""%~f0""", "", "runas", 1 >> "!vbs!"
    "!vbs!"
    del /q "!vbs!"
    exit /b 0
)

echo.
echo Uninstalling Apportia system integration...
echo.

:: ── Context menu ─────────────────────────────────────────────────────────────

echo Removing context menu entries...
reg delete "HKCU\Software\Classes\*\shell\Apportia" /f >nul 2>&1
reg delete "HKCU\Software\Classes\Directory\shell\Apportia" /f >nul 2>&1
reg delete "HKCU\Software\Classes\Directory\Background\shell\Apportia" /f >nul 2>&1

:: ── Environment variable ─────────────────────────────────────────────────────

echo Removing ApportiaDir environment variable...
reg delete "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment" /v ApportiaDir /f >nul 2>&1

:: ── Shortcuts ────────────────────────────────────────────────────────────────

echo Removing shortcuts...
if exist "%USERPROFILE%\Desktop\Apportia.lnk" del /q "%USERPROFILE%\Desktop\Apportia.lnk"
if exist "%APPDATA%\Microsoft\Windows\SendTo\Apportia.lnk" del /q "%APPDATA%\Microsoft\Windows\SendTo\Apportia.lnk"

echo.
echo Done!
echo.
pause
