#!/usr/bin/env bash
#
# package-extension.sh — build a Chrome Web Store-ready zip of the Wire Copy extension.
# Bead: workspace-b480 (P3.1 packaging). Epic: workspace-blg5 (host-browser-as-renderer).
#
# What it does
#   Zips the CONTENTS of extension/ (manifest.json at the zip ROOT — not nested under an
#   extension/ folder) into dist/wire-copy-extension.zip. The Chrome Web Store requires the
#   manifest to live at the archive root, so we zip from inside the extension directory.
#   Dev-only files (README.md, .DS_Store, source maps, etc.) are excluded from the package.
#
# Install / distribute
#   Dev install : chrome://extensions -> enable "Developer mode" -> "Load unpacked"
#                 -> select the extension/ directory (use the source dir directly; the zip
#                 is for distribution, not for loading unpacked).
#   Prod install: upload dist/wire-copy-extension.zip to the Chrome Web Store
#                 (https://chrome.google.com/webstore/devconsole), OR pack it into a signed
#                 .crx via chrome://extensions -> "Pack extension" for self-hosted installs.
#
# Behaviour
#   - Idempotent: recreates dist/ on every run.
#   - Reads and prints the version from manifest.json.
#   - Fails loudly (non-zero exit) if the extension dir or manifest.json is missing,
#     or if the required `zip` tool is unavailable.
#
# Usage: scripts/package-extension.sh

set -euo pipefail

# Resolve repo root relative to this script so it works from any CWD.
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

EXT_DIR="$ROOT/extension"
MANIFEST="$EXT_DIR/manifest.json"
DIST_DIR="$ROOT/dist"
OUT_ZIP="$DIST_DIR/wire-copy-extension.zip"

# --- preconditions ------------------------------------------------------------------------------
if [ ! -d "$EXT_DIR" ]; then
    echo "ERROR: extension directory not found: $EXT_DIR" >&2
    exit 1
fi
if [ ! -f "$MANIFEST" ]; then
    echo "ERROR: manifest not found: $MANIFEST" >&2
    exit 1
fi
if ! command -v zip >/dev/null 2>&1; then
    echo "ERROR: 'zip' is required but not found on PATH." >&2
    exit 1
fi

# --- read & print version -----------------------------------------------------------------------
# Prefer a JSON-aware reader if available; fall back to a grep/sed extraction of the
# "version": "x.y.z" field so the script has no hard dependency on jq/python.
read_version() {
    if command -v jq >/dev/null 2>&1; then
        jq -r '.version' "$MANIFEST"
    elif command -v python3 >/dev/null 2>&1; then
        python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$MANIFEST"
    else
        grep -oE '"version"[[:space:]]*:[[:space:]]*"[^"]+"' "$MANIFEST" \
            | head -n1 \
            | sed -E 's/.*"version"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/'
    fi
}

VERSION="$(read_version)"
if [ -z "${VERSION:-}" ]; then
    echo "ERROR: could not read version from $MANIFEST" >&2
    exit 1
fi
echo "Wire Copy extension version: $VERSION"

# --- (re)build dist/ ----------------------------------------------------------------------------
rm -rf "$DIST_DIR"
mkdir -p "$DIST_DIR"

# Zip the CONTENTS of extension/ with manifest.json at the root. Running zip from inside
# EXT_DIR (subshell `cd`) keeps paths relative so nothing is nested under extension/.
#
# Excludes (dev-only / noise):
#   README.md          developer docs, not needed at runtime
#   .DS_Store          macOS Finder cruft
#   *.map              source maps (vendor bundles ship without them)
#   .git*              any stray VCS metadata
( cd "$EXT_DIR" && zip -r -X "$OUT_ZIP" . \
    -x "README.md" \
    -x "*.DS_Store" \
    -x "*.map" \
    -x ".git*" >/dev/null )

echo "Packaged: $OUT_ZIP"
echo
echo "Contents (manifest.json must appear at the root, with no extension/ prefix):"
unzip -l "$OUT_ZIP"
