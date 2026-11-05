#!/usr/bin/env bash
#
# check-dangerous-apis.sh — fails if the plugin source uses an API that could turn
# network data into code, or that reaches outside the game process.
#
# This is the automated half of docs/security.md: the audit is prose, this is the gate.
# Run it from the repository root (CI does). Exit code 0 = clean.
#
set -euo pipefail

cd "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

PATTERN='Assembly\.Load|AssemblyBuilder|Type\.GetType|Activator\.CreateInstance|BinaryFormatter|NetDataContractSerializer|CSharpCodeProvider|Process\.Start|ProcessStartInfo|DllImport|System\.Diagnostics\.Process'

# Comment lines are allowed to *discuss* these APIs (the security doc does).
if hits=$(grep -rnE "$PATTERN" plugin --include='*.cs' | grep -vE ':[[:space:]]*(//|\*)' || true); [ -n "$hits" ]; then
    echo "check-dangerous-apis: forbidden API use:" >&2
    echo "$hits" >&2
    echo >&2
    echo "These are exactly the mechanisms docs/security.md rules out. If one is genuinely" >&2
    echo "needed, it has to come with an audit note and a test, not just a code change." >&2
    exit 1
fi

echo "check-dangerous-apis: clean (no dynamic loading, reflection on names, deserialisation, process spawn or P/Invoke)"
