#!/usr/bin/env bash
#
# check-personal-data.sh — fails if a tracked file contains machine paths, personal
# email addresses or anything that looks like a credential.
#
# The repository is public, and a mod that people download should not carry the
# author's home directory, account addresses or a test token. Run from the repository
# root (CI does). Exit code 0 = clean.
#
set -euo pipefail

cd "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

status=0
report() {
    echo "check-personal-data: $1" >&2
    shift
    printf '%s\n' "$@" >&2
    status=1
}

# Absolute paths from a developer machine, in the flavours Git Bash, WSL and Linux produce.
if hits=$(git grep -nE '(^|[^a-zA-Z0-9_.-])(/home/[a-z]|/Users/[a-zA-Z]|[A-Z]:\\\\Users\\\\|/mnt/[a-z]/)' -- . ':(exclude)tools/check-personal-data.sh' || true); [ -n "$hits" ]; then
    report 'machine specific absolute path found (use a relative path or $ZEEPKIST_GAME_DIR):' "$hits"
fi

# Personal mail domains. The GitHub noreply address is fine, it is a public alias.
if hits=$(git grep -nEi '(@gmail\.com|@googlemail\.com|@outlook\.com|@hotmail\.com|@yahoo\.com|@proton(mail)?\.(com|me)|@icloud\.com)' -- . ':(exclude)tools/check-personal-data.sh' || true); [ -n "$hits" ]; then
    report 'personal email address found:' "$hits"
fi

# Anything that looks like a token or key.
if hits=$(git grep -nE '(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}|-----BEGIN [A-Z ]*PRIVATE KEY-----|xox[baprs]-)' -- . ':(exclude)tools/check-personal-data.sh' || true); [ -n "$hits" ]; then
    report 'possible credential found:' "$hits"
fi

# The mod alias is public (it is in the plugin GUID and the config file name) and is
# therefore allowed. Anywhere else it is a leftover worth looking at.
if hits=$(git grep -nEi 'nyxeonfox' -- . ':(exclude)tools/check-personal-data.sh' | grep -vE 'com\.nyxeonfox\.zeepkist\.twitchchat|NyxeonFox$|Copyright \(c\)' || true); [ -n "$hits" ]; then
    report 'mod alias outside the plugin GUID / licence line (check it is intentional):' "$hits"
fi

if [ "$status" -ne 0 ]; then
    exit 1
fi

echo "check-personal-data: clean (no machine paths, personal addresses or credentials in tracked files)"
