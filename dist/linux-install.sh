#!/usr/bin/env bash
# Creates a desktop entry for Apportia and installs hicolor icons for the current user.

set -euo pipefail

nc='\033[0m'
red='\033[0;31m'
green='\033[0;32m'
yellow='\033[0;33m'
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

NEEDS_ICON_CACHE_REFRESH=0

# ── Icons ─────────────────────────────────────────────────────────────────────

SENTINEL_ICON="$ICONS_DST/256x256/apps/Apportia.png"

if [[ -f "$SENTINEL_ICON" ]]; then
    echo -e "${yellow}Icons already installed, skipping.${nc}"
else
    echo -e "${white}Installing icons...${nc}"
    mkdir -p "$ICONS_DST"
    cp -r "$ICONS_SRC/." "$ICONS_DST/"
    NEEDS_ICON_CACHE_REFRESH=1
fi

if [[ "$NEEDS_ICON_CACHE_REFRESH" == "1" ]] && command -v gtk-update-icon-cache &>/dev/null; then
    gtk-update-icon-cache -f -t "$ICONS_DST" 2>/dev/null || true
fi

# ── Desktop entry ─────────────────────────────────────────────────────────────

# Escape a value for the Desktop Entry Exec key: backslash-escape reserved
# characters, double the % sign (field-code marker), then wrap in double quotes.
desktop_exec_quote() {
    local s=$1
    s=${s//\\/\\\\}
    s=${s//\"/\\\"}
    s=${s//\`/\\\`}
    s=${s//\$/\\\$}
    s=${s//%/%%}
    printf '"%s"' "$s"
}

EXEC_WINEPREFIX="$(desktop_exec_quote "WINEPREFIX=$HOME/.wine")"
EXEC_BINARY="$(desktop_exec_quote "$BINARY")"
DESIRED_EXEC="env $EXEC_WINEPREFIX $EXEC_BINARY"
DESIRED_PATH="$SCRIPT_DIR"

FALLBACK_MIME_TYPES="inode/directory;application/x-ms-dos-executable;application/zip;application/x-zip-compressed;application/x-7z-compressed;application/x-tar;application/x-bzip2;application/x-xz;application/gzip;application/pdf;audio/mpeg;audio/ogg;audio/flac;audio/wav;audio/aac;audio/x-m4a;video/mp4;video/x-matroska;video/x-msvideo;video/mpeg;video/webm;video/quicktime;image/jpeg;image/png;image/gif;image/webp;image/svg+xml;image/bmp;image/tiff;text/plain;text/html;text/css;text/javascript;text/xml;text/csv;text/markdown;"

if [[ -r /usr/share/mime/types ]]; then
    MIME_TYPES="$(tr '\n' ';' < /usr/share/mime/types)"
elif MIME_TYPES="$(find /usr/share/mime -name '*.xml' -not -path '*/packages/*' -printf '%P\n' 2>/dev/null | sed 's|\.xml$||' | tr '\n' ';')" && [[ -n "$MIME_TYPES" ]]; then
    :
else
    MIME_TYPES="$FALLBACK_MIME_TYPES"
fi

if [[ -f "$DESKTOP_FILE" ]]; then
    CURRENT_EXEC="$(grep -m1 '^Exec=' "$DESKTOP_FILE" | cut -d= -f2-)"
    CURRENT_PATH="$(grep -m1 '^Path=' "$DESKTOP_FILE" | cut -d= -f2-)"
    if [[ "$CURRENT_EXEC" == "$DESIRED_EXEC" && "$CURRENT_PATH" == "$DESIRED_PATH" ]]; then
        echo -e "${yellow}Desktop entry already up to date, skipping.${nc}"
    else
        echo -e "${white}Updating desktop entry...${nc}"
        _write_desktop=1
    fi
else
    echo -e "${white}Writing desktop entry...${nc}"
    _write_desktop=1
fi

if [[ "${_write_desktop:-0}" == "1" ]]; then
    mkdir -p "$DESKTOP_DIR"
    cat > "$DESKTOP_FILE" <<EOF
[Desktop Entry]
Type=Application
Name=Apportia
Comment=Browse, install, and launch Windows portable apps on any platform
Path=$SCRIPT_DIR
Exec=$DESIRED_EXEC
Icon=Apportia
Categories=Utility;Network;FileTransfer;
Keywords=Windows;Wine;Portable;Launcher;Download;Sources;Repositories;Program;Software;App;Store;
MimeType=$MIME_TYPES
StartupNotify=true
TryExec=$BINARY
EOF
fi

echo -e "\n${green}Done!${nc}"
echo -e "  Binary:  ${green}$BINARY${nc}"
echo -e "  Desktop: ${green}$DESKTOP_FILE${nc}\n"
