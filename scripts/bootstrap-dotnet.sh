#!/usr/bin/env bash
# Installs a workspace-local .NET 10 SDK into ./.dotnet/ so the repo is
# self-contained — no system-wide dotnet install required to build or run
# WireCopy. Idempotent: re-runs are a no-op once the SDK is present.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET_DIR="$ROOT/.dotnet"
DOTNET_BIN="$DOTNET_DIR/dotnet"
CHANNEL="${DOTNET_CHANNEL:-10.0}"

if [ -x "$DOTNET_BIN" ]; then
  sdk_match="$("$DOTNET_BIN" --list-sdks 2>/dev/null | awk '{print $1}' | grep -E "^${CHANNEL%.*}\." | head -1 || true)"
  if [ -n "$sdk_match" ]; then
    exit 0
  fi
fi

echo "Bootstrapping .NET ${CHANNEL} SDK into ${DOTNET_DIR}…" >&2
INSTALL_SCRIPT="$(mktemp)"
trap 'rm -f "$INSTALL_SCRIPT"' EXIT
curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$INSTALL_SCRIPT"
bash "$INSTALL_SCRIPT" --channel "$CHANNEL" --install-dir "$DOTNET_DIR" >&2
echo "Installed: $("$DOTNET_BIN" --list-sdks)" >&2
