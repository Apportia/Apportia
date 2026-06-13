@echo off
setlocal EnableDelayedExpansion

:: ── Customise ────────────────────────────────────────────────────────────────

set "MENU_LABEL=Open with Apportia"

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

for %%i in ("%~dp0..\Apportia.exe") do set "BINARY=%%~fi"

if not exist "%BINARY%" (
    echo [ERROR] Binary not found: %BINARY%
    pause
    exit /b 1
)

echo.
echo Installing Apportia system integration...
echo.

:: ── Context menu — files ─────────────────────────────────────────────────────

echo Registering context menu for files...
reg add "HKCU\Software\Classes\*\shell\Apportia" /ve /d "%MENU_LABEL%" /f >nul
reg add "HKCU\Software\Classes\*\shell\Apportia" /v "Icon" /d "\"%BINARY%\"" /f >nul
reg add "HKCU\Software\Classes\*\shell\Apportia\command" /ve /d "\"%BINARY%\" \"%%1\"" /f >nul

:: ── Context menu — folders ───────────────────────────────────────────────────

echo Registering context menu for folders...
reg add "HKCU\Software\Classes\Directory\shell\Apportia" /ve /d "%MENU_LABEL%" /f >nul
reg add "HKCU\Software\Classes\Directory\shell\Apportia" /v "Icon" /d "\"%BINARY%\"" /f >nul
reg add "HKCU\Software\Classes\Directory\shell\Apportia\command" /ve /d "\"%BINARY%\" \"%%1\"" /f >nul

:: ── Context menu — folder background ────────────────────────────────────────

echo Registering context menu for folder background...
reg add "HKCU\Software\Classes\Directory\Background\shell\Apportia" /ve /d "%MENU_LABEL%" /f >nul
reg add "HKCU\Software\Classes\Directory\Background\shell\Apportia" /v "Icon" /d "\"%BINARY%\"" /f >nul
reg add "HKCU\Software\Classes\Directory\Background\shell\Apportia\command" /ve /d "\"%BINARY%\" \"%%V\"" /f >nul

:: ── Shortcuts ────────────────────────────────────────────────────────────────

echo Creating shortcuts...
powershell -NoProfile -Command ^
    "$s = (New-Object -ComObject WScript.Shell).CreateShortcut([System.Environment]::GetFolderPath('Desktop') + '\Apportia.lnk');" ^
    "$s.TargetPath = '%BINARY:\=\\%';" ^
    "$s.IconLocation = '%BINARY:\=\\%';" ^
    "$s.Save()"

powershell -NoProfile -Command ^
    "$s = (New-Object -ComObject WScript.Shell).CreateShortcut($env:APPDATA + '\Microsoft\Windows\SendTo\Apportia.lnk');" ^
    "$s.TargetPath = '%BINARY:\=\\%';" ^
    "$s.IconLocation = '%BINARY:\=\\%';" ^
    "$s.Save()"

echo.
echo Done!
echo   Binary:  %BINARY%
echo   Context menu: "%MENU_LABEL%" added for files, folders and folder background
echo   Shortcuts created on Desktop and in SendTo
echo.
pause
