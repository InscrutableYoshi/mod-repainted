#!/bin/bash
# Repainted mod build script
# Requires .NET 8+ SDK (install via: curl -sL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 8.0)

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# Ensure dotnet is on PATH
export PATH="$HOME/.dotnet:$PATH"

if ! command -v dotnet &>/dev/null; then
    echo "ERROR: dotnet SDK not found. Install with:"
    echo "  curl -sL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 8.0"
    exit 1
fi

DEV_FLAG=""
BUILD_LABEL="Release"
for arg in "$@"; do
    case "$arg" in
        --dev)
            # REPAINTED_DEV compiles in src/Dev/* (F9 wall-dump tool etc).
            # Never use for release builds.
            DEV_FLAG='-p:DefineConstants=REPAINTED_DEV'
            BUILD_LABEL="Release+DEV"
            ;;
    esac
done

echo "=== Repainted Build ($BUILD_LABEL) ==="
cd "$SCRIPT_DIR"
dotnet build -c Release $DEV_FLAG

DLL="$SCRIPT_DIR/build/Repainted.dll"
if [ -f "$DLL" ]; then
    echo ""
    echo "Output: $DLL ($(stat -c%s "$DLL" 2>/dev/null || stat -f%z "$DLL") bytes)"
    echo ""
    echo "To install: copy Repainted.dll to your game's BepInEx/plugins/ folder"
fi
