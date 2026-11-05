#!/usr/bin/env bash
#
# deploy-plugin.sh — copy the built ZeepTwitchChat.dll into the game's plugins folder.
#
# Modkist installs mod.io mods under BepInEx/plugins/Mods/<id>_<file>_<name>/, but a
# bare BepInEx/plugins/ZeepTwitchChat.dll works exactly the same for local testing.
#
# Usage:
#   tools/deploy-plugin.sh [--config Release] [--game-dir /path/to/Zeepkist]
#
# Environment overrides:
#   ZEEPKIST_GAME_DIR   game install root (default below)
#   CONFIGURATION       build configuration (default Release)
#
set -euo pipefail

CONFIGURATION="${CONFIGURATION:-Release}"
GAME_DIR="${ZEEPKIST_GAME_DIR:-}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

while [ "$#" -gt 0 ]; do
    case "$1" in
        --config|-c) CONFIGURATION="$2"; shift 2 ;;
        --game-dir)  GAME_DIR="$2";      shift 2 ;;
        -h|--help)   sed -n '2,14p' "$0"; exit 0 ;;
        *) echo "deploy-plugin: unknown argument: $1" >&2; exit 2 ;;
    esac
done

if [ -z "$GAME_DIR" ]; then
    echo "deploy-plugin: GameDir is not set. Export ZEEPKIST_GAME_DIR=/path/to/Zeepkist (the folder with Zeepkist.exe) or pass --game-dir." >&2
    exit 2
fi


DLL="$REPO_ROOT/plugin/ZeepTwitchChat.Plugin/bin/${CONFIGURATION}/net472/ZeepTwitchChat.dll"
DEST="$GAME_DIR/BepInEx/plugins/ZeepTwitchChat.dll"

if [ ! -f "$DLL" ]; then
    echo "deploy-plugin: built plugin not found: $DLL" >&2
    echo "deploy-plugin: build it first, e.g.  dotnet build plugin/ZeepTwitchChat.Plugin -c $CONFIGURATION" >&2
    exit 1
fi

if [ ! -d "$GAME_DIR/BepInEx/plugins" ]; then
    echo "deploy-plugin: $GAME_DIR/BepInEx/plugins does not exist. Is BepInEx installed and GameDir correct?" >&2
    exit 1
fi

mkdir -p "$(dirname "$DEST")"
cp -v "$DLL" "$DEST"
echo "deploy-plugin: deployed -> $DEST"
echo "deploy-plugin: set Twitch/Channel in $GAME_DIR/BepInEx/config/com.nyxeonfox.zeepkist.twitchchat.cfg"
