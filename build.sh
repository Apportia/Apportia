#!/usr/bin/env bash
# Builds Apportia into bin/<configuration>/.
# Debug: linux-x64 only. Release: linux-x64 and win-x64 share the same output directory by design.
# Usage: $0 [Debug|Release]

set -euo pipefail

# ── Colors ────────────────────────────────────────────────────────────────────

nc='\033[0m'
red='\033[0;31m'
green='\033[0;32m'
white='\033[1;37m'
ol='\033[53m'
ul='\033[4m'

# ── Args ──────────────────────────────────────────────────────────────────────

configuration="Debug"

while [[ $# -gt 0 ]]; do
    case "$1" in
        Debug|Release) configuration="$1" ;;
        --help|-h)
            echo -e "
${ul}${white}Usage${nc}
  $(basename "$0") [Debug|Release]

${ul}${white}Options${nc}
  ${green}Debug${nc}     Build with debug symbols (default).
  ${green}Release${nc}   Build optimised release output.
  ${green}--help, -h${nc}  Show this help text and exit.
"
            exit 0 ;;
        *) echo -e "${red}[ERROR]${nc} Unknown argument: $1"; exit 1 ;;
    esac
    shift
done

# ── Dependency check ──────────────────────────────────────────────────────────

for cmd in dotnet makensis; do
    if ! command -v "$cmd" &>/dev/null; then
        echo -e "${red}[ERROR]${nc} Executable not found: $cmd"
        exit 1
    fi
done

# ── Paths ─────────────────────────────────────────────────────────────────────

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CSPROJ="$REPO_ROOT/src/Apportia/Apportia.csproj"
NSI="$REPO_ROOT/src/FakePortableAppsPlatform/FakePortableAppsPlatform.nsi"
OUT_BASE="$REPO_ROOT/bin/$configuration"

echo -e "\n${ul}${white}Building ${green}${ul}Apportia [$configuration]${white}${ul}...${nc}\n"

# ── Publish ───────────────────────────────────────────────────────────────────

publish() {
    local rid="$1"

    echo -e "${white}Platform:${nc} $rid"

    local extra=()
    if [[ "$configuration" == "Release" ]]; then
        extra+=(-p:DebugType=none -p:DebugSymbols=false)
    fi

    dotnet publish "$CSPROJ" \
        --configuration "$configuration" \
        --runtime "$rid" \
        --output "$OUT_BASE" \
        --nologo \
        -v minimal \
        "${extra[@]}"

    if [[ "$configuration" == "Release" ]]; then
        # Native SkiaSharp/HarfBuzzSharp PDBs are shipped by their NuGet packages and
        # ignore DebugType=none, so strip them here.
        find "$OUT_BASE" -name "*.pdb" -delete
    fi
}

if [[ "$configuration" == "Debug" ]]; then
    publish linux-x64
else
    publish linux-x64
    publish win-x64
fi

# ── SystemIntegration ────────────────────────────────────────────────────────

if [[ "$configuration" == "Release" ]]; then
    cp -r "$REPO_ROOT/dist/." "$OUT_BASE/Setup/"
fi

# ── FakePortableAppsPlatform ──────────────────────────────────────────────────

PAP="$OUT_BASE/Apps/PortableApps.com/PortableAppsPlatform.exe"

mkdir -p "$(dirname "$PAP")"
makensis "-DOUTPUT=$PAP" "$NSI"

if [[ "$configuration" == "Release" ]]; then
    cat > "$(dirname "$PAP")/FakePortableAppsPlatform.txt" <<'EOF'
PortableAppsPlatform.exe — Fake PortableApps.com Platform stub

The PortableApps.com installer (paf.exe) requires PortableAppsPlatform.exe to
exist in this folder AND be running before it accepts silent installation flags.
This stub satisfies those conditions without running the real platform.

Apportia starts this dummy process before invoking paf.exe and closes it afterward.
It is NOT the real PortableApps.com Platform and serves no other purpose.
EOF
fi

echo -e "\n${green}${ol}$(basename "$0")${white}${ol} done!${nc}"
echo -e "  Output: ${green}$OUT_BASE/${nc}\n"
