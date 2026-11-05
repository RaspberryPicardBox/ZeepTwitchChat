#!/usr/bin/env bash
#
# package-mod.sh — build a release zip ready to upload to mod.io (and to hand to
# Modkist, which installs the DLLs a mod's zip contains).
#
# Usage:
#   tools/package-mod.sh [--config Release] [--game-dir /path/to/Zeepkist]
#
# Output:
#   release/ZeepTwitchChat-<version>.zip
#       ZeepTwitchChat.dll
#       README.md
#       CHANGELOG.md
#
set -euo pipefail

CONFIGURATION="${CONFIGURATION:-Release}"
GAME_DIR="${ZEEPKIST_GAME_DIR:-/mnt/RAID_SSD/SteamLibrary/steamapps/common/Zeepkist}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_ROOT/plugin/ZeepTwitchChat.Plugin/ZeepTwitchChat.Plugin.csproj"

while [ "$#" -gt 0 ]; do
    case "$1" in
        --config|-c) CONFIGURATION="$2"; shift 2 ;;
        --game-dir)  GAME_DIR="$2";      shift 2 ;;
        -h|--help)   sed -n '2,15p' "$0"; exit 0 ;;
        *) echo "package-mod: unknown argument: $1" >&2; exit 2 ;;
    esac
done

VERSION="$(grep -oPm1 '(?<=<Version>)[^<]+' "$PROJECT")"
DLL="$REPO_ROOT/plugin/ZeepTwitchChat.Plugin/bin/${CONFIGURATION}/net472/ZeepTwitchChat.dll"
STAGE="$REPO_ROOT/release/stage"
ZIP="$REPO_ROOT/release/ZeepTwitchChat-${VERSION}.zip"

echo "package-mod: building $CONFIGURATION for version $VERSION"
dotnet build "$PROJECT" -c "$CONFIGURATION" -p:GameDir="$GAME_DIR" -v minimal

if [ ! -f "$DLL" ]; then
    echo "package-mod: build output missing: $DLL" >&2
    exit 1
fi

rm -rf "$STAGE"
mkdir -p "$STAGE"
cp "$DLL" "$STAGE/"
cp "$REPO_ROOT/README.md" "$STAGE/"
cp "$REPO_ROOT/CHANGELOG.md" "$STAGE/"

rm -f "$ZIP"
( cd "$STAGE" && zip -qr "$ZIP" . )
rm -rf "$STAGE"

echo "package-mod: created $ZIP"
unzip -l "$ZIP"
