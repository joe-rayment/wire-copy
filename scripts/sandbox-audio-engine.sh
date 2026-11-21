#!/usr/bin/env bash
# Puts what the REAL audio-engine needs into this container, with no root: uv, a static
# ffmpeg + ffprobe, and a checkout of the engine whose ./dotnet uses this repo's vendored
# SDK. For running a routine against the real engine (shell/gates/probe-routine-real.mjs)
# instead of scripts/fake_audio_engine.py. Idempotent; a container reset wipes /tmp, so run
# it again after one.
#
#   bash scripts/sandbox-audio-engine.sh     # install / repair
#   source ~/.local/bin/ae-env.sh            # then, in the shell that runs the probe
#
# Layout under AE_SANDBOX_DIR (default /tmp/wc-audio-engine - several GB, so it stays off
# the home folder, which is shared with the host):
#   bin/            uv, ffmpeg, ffprobe
#   audio-engine/   git clone of the engine (read and run here, never changed or pushed);
#                   AUDIO_ENGINE_SRC=<dir> uses an existing checkout instead
#   cache/uv        the Chatterbox worker's Python environment (about 1.5 GB)
#   cache/hf        the model weights (standard about 3 GB, turbo about 4 GB; first job downloads)
set -euo pipefail

AE="${AE_SANDBOX_DIR:-/tmp/wc-audio-engine}"
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARCH="$(uname -m | sed 's/aarch64/arm64/;s/x86_64/amd64/')"
mkdir -p "$AE/bin" "$AE/cache/uv" "$AE/cache/hf" "$AE/dl" "$HOME/.local/bin"

if [ ! -x "$AE/bin/uv" ]; then
  echo "sandbox-audio-engine: installing uv..." >&2
  curl -LsSf https://astral.sh/uv/install.sh -o "$AE/dl/uv-install.sh" \
    || { echo "sandbox-audio-engine: could not download the uv installer (network?)" >&2; exit 1; }
  UV_INSTALL_DIR="$AE/bin" UV_NO_MODIFY_PATH=1 sh "$AE/dl/uv-install.sh" >/dev/null 2>&1 \
    || { echo "sandbox-audio-engine: the uv installer failed" >&2; exit 1; }
fi

if [ ! -x "$AE/bin/ffmpeg" ] || [ ! -x "$AE/bin/ffprobe" ]; then
  echo "sandbox-audio-engine: fetching a static ffmpeg + ffprobe..." >&2
  curl -LsS -o "$AE/dl/ffmpeg.tar.xz" "https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-$ARCH-static.tar.xz" \
    || { echo "sandbox-audio-engine: could not download ffmpeg (network?)" >&2; exit 1; }
  mkdir -p "$AE/dl/ff" && tar -xJf "$AE/dl/ffmpeg.tar.xz" -C "$AE/dl/ff" --strip-components=1
  cp "$AE/dl/ff/ffmpeg" "$AE/dl/ff/ffprobe" "$AE/bin/"
fi

HOME_DIR="${AUDIO_ENGINE_SRC:-$AE/audio-engine}"
if [ ! -d "$HOME_DIR/src/AudioEngine.Server" ]; then
  echo "sandbox-audio-engine: cloning the engine into $HOME_DIR..." >&2
  git -c credential.helper= -c 'credential.helper=!gh auth git-credential' \
    clone --quiet https://github.com/joe-rayment/audio-engine.git "$HOME_DIR" \
    || { echo "sandbox-audio-engine: could not clone the engine (gh auth status?); set AUDIO_ENGINE_SRC to a checkout" >&2; exit 1; }
fi
# The engine's ./dotnet wants its own .dotnet/<os>-<arch>; this repo already has one.
SDK="$(ls -d "$REPO"/.dotnet/linux-* 2>/dev/null | head -1)"
if [ -n "$SDK" ] && [ ! -e "$HOME_DIR/.dotnet/$(basename "$SDK")" ]; then
  mkdir -p "$HOME_DIR/.dotnet" && ln -s "$SDK" "$HOME_DIR/.dotnet/$(basename "$SDK")"
fi

cat > "$HOME/.local/bin/ae-env.sh" <<EOF
# Source me. Written by scripts/sandbox-audio-engine.sh - what the real audio-engine needs.
export AE_SANDBOX_DIR=$AE
export AUDIO_ENGINE_HOME=$HOME_DIR
export PATH=$AE/bin:\$PATH
export UV_CACHE_DIR=$AE/cache/uv
export HF_HOME=$AE/cache/hf
EOF

fail=0
for b in uv ffmpeg ffprobe; do
  "$AE/bin/$b" ${b/uv/--version} ${b/ff*/-version} >/dev/null 2>&1 || { echo "sandbox-audio-engine: $b does not run" >&2; fail=1; }
done
[ -x "$HOME_DIR/dotnet" ] || { echo "sandbox-audio-engine: $HOME_DIR has no ./dotnet wrapper" >&2; fail=1; }
[ "$fail" = 0 ] && echo "sandbox-audio-engine: ready - source ~/.local/bin/ae-env.sh (engine at $HOME_DIR, $(git -C "$HOME_DIR" log -1 --format='%h %ad' --date=short 2>/dev/null))" >&2
exit "$fail"
