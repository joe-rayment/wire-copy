#!/bin/bash
# WireCopy Desktop packaging (workspace-mwer).
#
# Stages the shell into a THROWAWAY build dir (the repo dir is a shared volume across
# machines — platform binaries must never persist in it), publishes the self-contained
# .NET API for this host's RID, installs the app's runtime deps there, and runs
# electron-builder. Chromium sandbox stays ON in packages: nothing here passes
# --no-sandbox (that flag is container/CI argv only). ./run terminal flow is untouched.
#
# Usage:
#   shell/build-desktop.sh              # full artifacts (AppImage/tar.gz on linux, zip on mac)
#   shell/build-desktop.sh --dir        # unpacked dir target only (fast; used by the gate)
#
# Output: artifacts + linux-unpacked/mac dirs under the staging dir; the path is printed
# and also written to shell/.last-desktop-build so gates/scripts can find it.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"

case "$(uname -s)-$(uname -m)" in
  Linux-aarch64)  RID=linux-arm64; EB_FLAGS=(--linux) ;;
  Linux-x86_64)   RID=linux-x64;   EB_FLAGS=(--linux) ;;
  Darwin-arm64)   RID=osx-arm64;   EB_FLAGS=(--mac --arm64) ;;
  Darwin-x86_64)  RID=osx-x64;     EB_FLAGS=(--mac --x64) ;;
  *) echo "build-desktop: unsupported host $(uname -s)-$(uname -m)" >&2; exit 1 ;;
esac

DIR_ONLY=0
for a in "$@"; do [ "$a" = "--dir" ] && DIR_ONLY=1; done

STAGE="$(mktemp -d /tmp/wirecopy-desktop-build-XXXXXX)"
echo "build-desktop: staging in $STAGE (RID $RID)"

echo "build-desktop: publishing self-contained WireCopy.API ($RID)..."
# Folder publish, UNTRIMMED, non-AOT: the Playwright node driver (.playwright/) is loose
# content resolved via AppContext.BaseDirectory and cannot live inside a single file.
#
# PublishReadyToRun (workspace-u1is6): MEASURED, not assumed. Twelve boots of the real
# shell against two publishes of the same tree, INTERLEAVED so neither variant got the
# warmer machine, reading the boot marks the app already emits:
#   dotnet-main -> tui-run   base 471.5ms   R2R 303.5ms   median -168ms
#   spawn-tui   -> tui-run   base 532.5ms   R2R 361.5ms   median -171ms
# Every R2R boot's tui-run (633-715ms) came in under every base boot's (804-1140ms). The
# gain is concentrated in host-started -> db-ready — EF's first-use model build, which is
# JIT-heavy — and first PAINT is unchanged (+3ms), because that happens before any of it.
# Costs ~3x the size of the app's own assemblies (Infrastructure.dll 2.1MB -> 5.9MB) in a
# folder publish that is already hundreds of MB, and nothing else: R2R code is verified
# against the runtime and falls back to JIT if it does not match.
# Re-measure with gates/probe-r2r.mjs if this ever looks doubtful; the numbers above are
# from an arm64 Linux container, and the Mac's are the ones that decide whether it stays.
"$ROOT/dotnet" publish "$ROOT/src/WireCopy.API/WireCopy.API.csproj" -c Release -r "$RID" \
  --self-contained true -p:PublishReadyToRun=true -o "$STAGE/app/stage-api" --nologo -v q

echo "build-desktop: staging shell sources..."
mkdir -p "$STAGE/app/fonts"
for f in main.js menu.js preload.js popup-preload.js overlay-preload.js channel.js term.html overlay.html package.json; do
  cp "$HERE/$f" "$STAGE/app/"
done
# The HTML panes are whole directories served off the loopback static server. They were
# missing from BOTH hand-maintained file lists (here and package.json build.files), so a
# packaged build shipped a launcher and link tree that 404'd — invisible because
# gate-p7-packaged only checked that the TUI boots (workspace-2dmuu).
for d in launcher linktree handle; do
  cp -R "$HERE/$d" "$STAGE/app/"
done
cp "$HERE"/fonts/* "$STAGE/app/fonts/"

echo "build-desktop: installing app runtime deps (node-pty, xterm)..."
(cd "$STAGE/app" && npm install --omit=dev --no-fund --no-audit >/dev/null)

echo "build-desktop: running electron-builder..."
EB="$HERE/deps/$(node -p 'process.platform + "-" + process.arch')/node_modules/.bin/electron-builder"
if [ ! -x "$EB" ]; then
  echo "build-desktop: electron-builder missing — run ./run once (installs shell deps), or npm i in the deps dir" >&2
  exit 1
fi
if [ "$DIR_ONLY" = "1" ]; then
  (cd "$STAGE/app" && "$EB" "${EB_FLAGS[@]}" --dir)
else
  (cd "$STAGE/app" && "$EB" "${EB_FLAGS[@]}")
fi

echo "$STAGE" > "$HERE/.last-desktop-build"
echo "build-desktop: artifacts:"
du -sh "$STAGE/app/dist"/* 2>/dev/null | sed 's/^/  /'
echo "build-desktop: done — staging kept at $STAGE (throwaway /tmp dir)"
