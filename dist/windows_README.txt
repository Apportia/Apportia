Apportia -- System Integration Scripts (Windows)
=================================================

Run these scripts once after placing Apportia in its final location.
Moving Apportia afterwards requires re-running windows-install.bat.


windows-install.bat
-------------------

Requires administrator privileges.
The script elevates itself automatically via UAC.

- Sets a system-wide environment variable ApportiaDir pointing to the
  Apportia directory. This allows shortcuts, scripts, or other integrations
  to reference %ApportiaDir%\Apportia.exe instead of a hardcoded path.
  If Apportia is ever moved, only ApportiaDir needs to be updated and all
  references resolve correctly automatically.

- Registers a context menu entry "Open with Apportia" for:
    - Files (all types)
    - Folders
    - Folder background (right-click inside a folder)

  The entries use %ApportiaDir%\Apportia.exe as an expandable path
  (REG_EXPAND_SZ), so they survive Apportia being moved as long as
  ApportiaDir is updated.

- Creates a shortcut on the Desktop.

- Creates a shortcut in SendTo (%APPDATA%\Microsoft\Windows\SendTo).

- Safe to re-run: already registered entries and existing shortcuts are
  skipped; only ApportiaDir is updated if the path has changed.


windows-uninstall.bat
---------------------

Requires administrator privileges.
The script elevates itself automatically via UAC.

- Removes the ApportiaDir system environment variable.
- Removes all context menu entries for files, folders, and folder backgrounds.
- Deletes the Desktop shortcut.
- Deletes the SendTo shortcut.
