#!/usr/bin/env bash
# Removes the Apportia desktop entry and hicolor icons for the current user.

set -euo pipefail

nc='\033[0m'
red='\033[0;31m'
green='\033[0;32m'
white='\033[1;37m'
ul='\033[4m'

ICONS_DST="$HOME/.local/share/icons/hicolor"
DESKTOP_FILE="$HOME/.local/share/applications/Apportia.desktop"

if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
    echo -e "
${ul}${white}Usage${nc}
  $(basename "$0")

Removes the Apportia desktop entry and hicolor icons for the current user.
"
    exit 0
fi

echo -e "\n${ul}${white}Uninstalling Apportia desktop entry${nc}\n"

# ── Desktop entry ─────────────────────────────────────────────────────────────

if [[ -f "$DESKTOP_FILE" ]]; then
    echo -e "${white}Removing desktop entry...${nc}"
    rm -f "$DESKTOP_FILE"
else
    echo -e "${white}Desktop entry not found, skipping.${nc}"
fi

# ── Icons ─────────────────────────────────────────────────────────────────────

echo -e "${white}Removing icons...${nc}"
find "$ICONS_DST" -name "Apportia.png" -delete 2>/dev/null || true

if command -v gtk-update-icon-cache &>/dev/null; then
    gtk-update-icon-cache -f -t "$ICONS_DST" 2>/dev/null || true
fi

echo -e "\n${green}Done!${nc}\n"
