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

:: ── Resolve paths ────────────────────────────────────────────────────────────

for %%i in ("%~dp0..") do set "APPORTIA_DIR=%%~fi"
set "BINARY=%APPORTIA_DIR%\Apportia.exe"

if not exist "%BINARY%" (
    echo [ERROR] Binary not found: %BINARY%
    pause
    exit /b 1
)

echo.
echo Updating Apportia system integration...
echo.

:: ── Environment variable ─────────────────────────────────────────────────────

set "NEEDS_EXPLORER_RESTART=0"
set "CURRENT_DIR="
for /f "tokens=2,*" %%a in ('reg query "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment" /v ApportiaDir 2^>nul ^| findstr "REG_"') do set "CURRENT_DIR=%%b"

if /i "!CURRENT_DIR!" == "%APPORTIA_DIR%" (
    echo ApportiaDir is already up to date, skipping.
    set "ApportiaDir=%APPORTIA_DIR%"
) else (
    echo Setting ApportiaDir environment variable...
    setx /M ApportiaDir "%APPORTIA_DIR%" >nul
    set "ApportiaDir=%APPORTIA_DIR%"
    set "NEEDS_EXPLORER_RESTART=1"
)

:: ── Context menu — files ─────────────────────────────────────────────────────

reg query "HKCU\Software\Classes\*\shell\Apportia" >nul 2>&1
if errorlevel 1 (
    echo Registering context menu for files...
    reg add "HKCU\Software\Classes\*\shell\Apportia" /ve /d "%MENU_LABEL%" /f >nul
    reg add "HKCU\Software\Classes\*\shell\Apportia" /v "Icon" /t REG_EXPAND_SZ /d "\"%%ApportiaDir%%\Apportia.exe\"" /f >nul
    reg add "HKCU\Software\Classes\*\shell\Apportia\command" /ve /t REG_EXPAND_SZ /d "\"%%ApportiaDir%%\Apportia.exe\" \"%%1\"" /f >nul
) else (
    echo Context menu for files already registered, skipping.
)

:: ── Context menu — folders ───────────────────────────────────────────────────

reg query "HKCU\Software\Classes\Directory\shell\Apportia" >nul 2>&1
if errorlevel 1 (
    echo Registering context menu for folders...
    reg add "HKCU\Software\Classes\Directory\shell\Apportia" /ve /d "%MENU_LABEL%" /f >nul
    reg add "HKCU\Software\Classes\Directory\shell\Apportia" /v "Icon" /t REG_EXPAND_SZ /d "\"%%ApportiaDir%%\Apportia.exe\"" /f >nul
    reg add "HKCU\Software\Classes\Directory\shell\Apportia\command" /ve /t REG_EXPAND_SZ /d "\"%%ApportiaDir%%\Apportia.exe\" \"%%1\"" /f >nul
) else (
    echo Context menu for folders already registered, skipping.
)

:: ── Context menu — folder background ────────────────────────────────────────

reg query "HKCU\Software\Classes\Directory\Background\shell\Apportia" >nul 2>&1
if errorlevel 1 (
    echo Registering context menu for folder background...
    reg add "HKCU\Software\Classes\Directory\Background\shell\Apportia" /ve /d "%MENU_LABEL%" /f >nul
    reg add "HKCU\Software\Classes\Directory\Background\shell\Apportia" /v "Icon" /t REG_EXPAND_SZ /d "\"%%ApportiaDir%%\Apportia.exe\"" /f >nul
    reg add "HKCU\Software\Classes\Directory\Background\shell\Apportia\command" /ve /t REG_EXPAND_SZ /d "\"%%ApportiaDir%%\Apportia.exe\" \"%%V\"" /f >nul
) else (
    echo Context menu for folder background already registered, skipping.
)

:: ── Restart Explorer if env var changed ──────────────────────────────────────

if "!NEEDS_EXPLORER_RESTART!" == "1" (
    echo Restarting Explorer to apply new ApportiaDir...
    taskkill /f /im explorer.exe >nul 2>&1
    start explorer.exe
    ping -n 2 127.0.0.1 >nul
)

:: ── Shortcuts ────────────────────────────────────────────────────────────────

if exist "%USERPROFILE%\Desktop\Apportia.lnk" (
    echo Desktop shortcut already exists, skipping.
) else (
    echo Creating Desktop shortcut...
    powershell -NoProfile -Command ^
        "$s = (New-Object -ComObject WScript.Shell).CreateShortcut([System.Environment]::GetFolderPath('Desktop') + '\Apportia.lnk');" ^
        "$s.TargetPath = '%%ApportiaDir%%\Apportia.exe';" ^
        "$s.IconLocation = '%%ApportiaDir%%\Apportia.exe';" ^
        "$s.Save()"
)

if exist "%APPDATA%\Microsoft\Windows\SendTo\Apportia.lnk" (
    echo SendTo shortcut already exists, skipping.
) else (
    echo Creating SendTo shortcut...
    powershell -NoProfile -Command ^
        "$s = (New-Object -ComObject WScript.Shell).CreateShortcut($env:APPDATA + '\Microsoft\Windows\SendTo\Apportia.lnk');" ^
        "$s.TargetPath = '%%ApportiaDir%%\Apportia.exe';" ^
        "$s.IconLocation = '%%ApportiaDir%%\Apportia.exe';" ^
        "$s.Save()"
)

echo.
echo Done!
echo   ApportiaDir: %APPORTIA_DIR%
echo.
pause
