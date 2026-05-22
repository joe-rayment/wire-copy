#!/usr/bin/env bash
# Installs a workspace-local .NET 10 SDK into ./.dotnet/<os>-<arch>/ so
# the repo is self-contained — no system-wide dotnet install required to
# build or run WireCopy. Idempotent: re-runs are a no-op once the SDK is
# present at the target dir.
#
# The dotnet wrapper passes DOTNET_INSTALL_DIR so the right per-platform
# subdir is populated. When invoked stand-alone, defaults to detecting
# the current host the same way the wrapper does.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [ -z "${DOTNET_INSTALL_DIR:-}" ]; then
  case "$(uname -s)" in
    Darwin) os=osx ;;
    Linux)  os=linux ;;
    *)      echo "Unsupported OS: $(uname -s)" >&2; exit 1 ;;
  esac
  case "$(uname -m)" in
    x86_64|amd64) arch=x64 ;;
    arm64|aarch64) arch=arm64 ;;
    *)             echo "Unsupported CPU: $(uname -m)" >&2; exit 1 ;;
  esac
  DOTNET_INSTALL_DIR="$ROOT/.dotnet/${os}-${arch}"
fi

DOTNET_BIN="$DOTNET_INSTALL_DIR/dotnet"
CHANNEL="${DOTNET_CHANNEL:-10.0}"

if [ -x "$DOTNET_BIN" ]; then
  sdk_match="$("$DOTNET_BIN" --list-sdks 2>/dev/null | awk '{print $1}' | grep -E "^${CHANNEL%.*}\." | head -1 || true)"
  if [ -n "$sdk_match" ]; then
    exit 0
  fi
fi

echo "Bootstrapping .NET ${CHANNEL} SDK into ${DOTNET_INSTALL_DIR}…" >&2
INSTALL_SCRIPT="$(mktemp)"
trap 'rm -f "$INSTALL_SCRIPT"' EXIT
curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$INSTALL_SCRIPT"
bash "$INSTALL_SCRIPT" --channel "$CHANNEL" --install-dir "$DOTNET_INSTALL_DIR" >&2
echo "Installed: $("$DOTNET_BIN" --list-sdks)" >&2
