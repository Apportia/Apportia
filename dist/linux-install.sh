#!/usr/bin/env bash
# Creates a desktop entry for Apportia and installs hicolor icons for the current user.

set -euo pipefail

nc='\033[0m'
red='\033[0;31m'
green='\033[0;32m'
white='\033[1;37m'
ul='\033[4m'

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BINARY="$(realpath "$SCRIPT_DIR/../Apportia")"
ICONS_SRC="$SCRIPT_DIR/linux-hicolor"
ICONS_DST="$HOME/.local/share/icons/hicolor"
DESKTOP_DIR="$HOME/.local/share/applications"
DESKTOP_FILE="$DESKTOP_DIR/Apportia.desktop"

if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
    echo -e "
${ul}${white}Usage${nc}
  $(basename "$0")

Creates a ~/.local/share/applications/Apportia.desktop entry and installs
hicolor icons for the current user.
"
    exit 0
fi

if [[ ! -f "$BINARY" ]]; then
    echo -e "${red}[ERROR]${nc} Binary not found: $BINARY"
    exit 1
fi

echo -e "\n${ul}${white}Installing Apportia desktop entry${nc}\n"

# ── Icons ─────────────────────────────────────────────────────────────────────

echo -e "${white}Installing icons...${nc}"
mkdir -p "$ICONS_DST"
cp -r "$ICONS_SRC/." "$ICONS_DST/"

if command -v gtk-update-icon-cache &>/dev/null; then
    gtk-update-icon-cache -f -t "$ICONS_DST" 2>/dev/null || true
fi

# ── Desktop entry ─────────────────────────────────────────────────────────────

echo -e "${white}Writing desktop entry...${nc}"
mkdir -p "$DESKTOP_DIR"

cat > "$DESKTOP_FILE" <<EOF
[Desktop Entry]
Type=Application
Name=Apportia
Comment=Browse, install, and launch Windows portable apps on any platform
Path=$SCRIPT_DIR
Exec=env WINEPREFIX=$HOME/.wine $BINARY
Icon=Apportia
Categories=Utility;Network;FileTransfer;
Keywords=Windows;Wine;Portable;Launcher;Download;Sources;Repositories;Program;Software;App;Store;
MimeType=inode/directory;application/x-ms-dos-executable;application/zip;application/x-zip-compressed;application/x-7z-compressed;application/x-tar;application/x-bzip2;application/x-xz;application/gzip;application/pdf;audio/mpeg;audio/ogg;audio/flac;audio/wav;audio/aac;audio/x-m4a;video/mp4;video/x-matroska;video/x-msvideo;video/mpeg;video/webm;video/quicktime;image/jpeg;image/png;image/gif;image/webp;image/svg+xml;image/bmp;image/tiff;text/plain;text/html;text/css;text/javascript;text/xml;text/csv;text/markdown;
StartupNotify=true
TryExec=$BINARY
EOF

chmod +x "$DESKTOP_FILE"

echo -e "\n${green}Done!${nc}"
echo -e "  Binary:  ${green}$BINARY${nc}"
echo -e "  Desktop: ${green}$DESKTOP_FILE${nc}\n"
