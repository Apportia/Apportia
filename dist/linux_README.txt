Apportia -- System Integration Scripts (Linux)
================================================

Run these scripts once after placing Apportia in its final location.
Moving Apportia afterwards requires re-running linux-install.sh.


linux-install.sh
----------------

No root required. All changes are user-local.

- Installs hicolor icons to ~/.local/share/icons/hicolor and refreshes
  the icon cache.

- Creates a .desktop entry at ~/.local/share/applications/Apportia.desktop
  which integrates Apportia into the application launcher and file manager.

- Registers MIME type associations so Apportia appears as an option for
  common file types (executables, archives, media, documents).


linux-uninstall.sh
------------------

No root required.

- Removes the .desktop entry.
- Removes all installed Apportia icons from the hicolor theme.
- Refreshes the icon cache.
