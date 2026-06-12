;Copyright 2026 koryboc / Apportia
;Fake PortableApps.com Platform stub to satisfy paf.exe installer checks.
;
;The PortableApps.com Installer (paf.exe) requires three conditions before
;it accepts silent installation flags (/SILENT=true, /HIDEINSTALLER=true, etc.):
;  1. PortableAppsPlatform.exe exists at <DESTINATION>\..\PortableApps.com\
;  2. Its ProductName == "PortableApps.com Platform" and CompanyName == "PortableApps.com"
;  3. The process PortableAppsPlatform.exe is currently running
;
;This stub satisfies all three conditions without running the real platform.
;Place it at <PortableAppsRoot>\PortableApps.com\PortableAppsPlatform.exe,
;start it before invoking the paf.exe installer, then terminate it afterward.

Unicode true
ManifestDPIAware true
AutoCloseWindow false
SilentInstall silent
RequestExecutionLevel user

;=== Version info — must match exactly what MoreInfo::GetProductName/GetCompanyName returns
VIProductVersion "30.4.1.0"
VIAddVersionKey ProductName      "PortableApps.com Platform"
VIAddVersionKey CompanyName      "PortableApps.com"
VIAddVersionKey FileDescription  "Faked PortableApps.com Platform used for silent install of portable apps"
VIAddVersionKey FileVersion      "30.4.1.0"
VIAddVersionKey ProductVersion   "30.4.1.0"
VIAddVersionKey InternalName     "PortableApps.com Platform"
VIAddVersionKey OriginalFilename "PortableAppsPlatform.exe"
VIAddVersionKey LegalCopyright   "Copyright © Roy Bock 2026"
VIAddVersionKey LegalTrademarks  "Roy Bock"

Name "PortableApps.com Platform"
Icon "../Apportia/Assets/ProgramIcon.ico"
!ifndef OUTPUT
  !define OUTPUT "PortableAppsPlatform.exe"
!endif
OutFile "${OUTPUT}"
Caption "PortableApps.com Platform"

Section
    ; Stay alive long enough for any number of paf.exe installs.
    ; The caller (Apportia) is responsible for terminating this process.
    loop:
        Sleep 1000
        Goto loop
SectionEnd
