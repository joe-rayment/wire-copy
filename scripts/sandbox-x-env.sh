#!/usr/bin/env bash
# Rebuilds the user-space X display the shell gates and probes need, in a container
# with no root and no apt install: Xvfb, xkbcomp, xdotool, openbox, ImageMagick's
# `import`, and every shared library Electron wants. Idempotent; a container reset
# wipes ~/.local, so run this again after one (about a minute, ~200 MB).
#
#   bash scripts/sandbox-x-env.sh          # install / repair
#   source ~/.local/bin/x-env.sh           # then, in the shell that runs a probe
#
# What it leaves behind:
#   ~/.local/apt/pkgs        dpkg -x of the packages (nothing is "installed")
#   ~/.local/xshim/xkbshim.so  LD_PRELOAD shim: Xvfb execs the compiled-in
#                            /usr/bin/xkbcomp through sh -c; the shim rewrites that one
#                            path to $XKBCOMP_PATH. Without it Xvfb dies with
#                            "Failed to activate virtual core keyboard".
#   ~/.local/bin/x-env.sh    PATH, LD_LIBRARY_PATH, XVFB_PRELOAD, XVFB_XKBDIR, fonts,
#                            GTK pixbuf loaders, ImageMagick module paths
# Start a display by hand:
#   LD_PRELOAD="$XVFB_PRELOAD" nohup Xvfb :96 -screen 0 1500x950x24 -xkbdir "$XVFB_XKBDIR" &
set -euo pipefail

A="$HOME/.local/apt"
ETC="$HOME/.local/etc"
SHIM_DIR="$HOME/.local/xshim"
mkdir -p "$A/lists/partial" "$A/cache/archives/partial" "$A/pkgs" "$A/debs" "$ETC/fonts" "$SHIM_DIR" "$HOME/.local/bin"

APT=(apt-get -o "Dir::State::lists=$A/lists" -o "Dir::Cache=$A/cache"
     -o Dir::State::status=/var/lib/dpkg/status -o Debug::NoLocking=1)

WANT=(xvfb x11-xkb-utils xkb-data xdotool openbox xauth x11-utils imagemagick-6.q16
      libgtk-3-0t64 libnss3 libasound2t64 libgbm1 libxss1 libatk-bridge2.0-0t64 libcups2t64
      libxkbcommon0 libxcomposite1 libxdamage1 libxrandr2 libatspi2.0-0t64
      adwaita-icon-theme hicolor-icon-theme librsvg2-2 librsvg2-common fonts-dejavu-core)
# Pulled in as dependencies but useless here (an init system, a print stack, big icon
# and font sets) - skipping them halves the download.
SKIP='^(systemd|systemd-sysv|systemd-dev|libsystemd-shared|libsystemd0|libudev1|libpam-systemd|dbus-user-session|libcryptsetup12|libdevmapper1\.02\.1|libfdisk1|libkmod2|libargon2-1|libjson-c5|poppler-data|libgs10|libgs10-common|libgs-common|fonts-urw-base35|humanity-icon-theme|ubuntu-mono|libspectre1|libijs-0\.35|libjbig2dec0|libpaper1|libidn12)$'

if [ ! -x "$A/pkgs/usr/bin/Xvfb" ] || [ ! -x "$A/pkgs/usr/bin/xdotool" ] || ! ls "$A/pkgs/usr/bin/"import* >/dev/null 2>&1; then
  echo "sandbox-x-env: fetching packages into $A (no root needed)..." >&2
  "${APT[@]}" update >/dev/null 2>&1 || { echo "sandbox-x-env: apt-get update failed (network?)" >&2; exit 1; }
  mapfile -t PKGS < <("${APT[@]}" -s install --no-install-recommends "${WANT[@]}" 2>/dev/null \
                       | awk '/^Inst /{print $2}' | grep -vE "$SKIP")
  [ "${#PKGS[@]}" -gt 0 ] || { echo "sandbox-x-env: apt resolved no packages" >&2; exit 1; }
  (cd "$A/debs" && "${APT[@]}" download "${PKGS[@]}" >/dev/null 2>&1) \
    || { echo "sandbox-x-env: apt-get download failed" >&2; exit 1; }
  for deb in "$A"/debs/*.deb; do dpkg -x "$deb" "$A/pkgs"; done
fi

cat > "$SHIM_DIR/xkbshim.c" <<'EOF'
#define _GNU_SOURCE
#include <stdarg.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
/* Xvfb runs the keymap compiler as execl("/bin/sh","sh","-c",cmd) with the compiled-in
   /usr/bin/xkbcomp. Rewrite that one path to $XKBCOMP_PATH. */
int execl(const char *path, const char *arg, ...) {
  char *argv[64]; int n = 0; va_list ap; va_start(ap, arg);
  argv[n++] = (char *)arg;
  while (n < 63 && (argv[n] = va_arg(ap, char *)) != NULL) n++;
  argv[n] = NULL; va_end(ap);
  const char *repl = getenv("XKBCOMP_PATH");
  const char *needle = "/usr/bin/xkbcomp";
  if (repl) for (int i = 0; i < n; i++) {
    char *p = strstr(argv[i], needle);
    if (p) {
      size_t pre = (size_t)(p - argv[i]);
      char *s = malloc(strlen(argv[i]) + strlen(repl) + 1);
      memcpy(s, argv[i], pre); strcpy(s + pre, repl); strcat(s, p + strlen(needle));
      argv[i] = s;
    }
  }
  return execv(path, argv);
}
EOF
gcc -shared -fPIC -O2 -o "$SHIM_DIR/xkbshim.so" "$SHIM_DIR/xkbshim.c"

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cat > "$ETC/fonts/fonts.conf" <<EOF
<?xml version="1.0"?>
<!DOCTYPE fontconfig SYSTEM "fonts.dtd">
<fontconfig>
  <dir>$A/pkgs/usr/share/fonts</dir>
  <dir>/usr/share/fonts</dir>
  <dir>$REPO/shell/fonts</dir>
  <cachedir>$HOME/.cache/fontconfig</cachedir>
</fontconfig>
EOF

LIB="$A/pkgs/usr/lib/aarch64-linux-gnu"
[ -d "$LIB" ] || LIB="$A/pkgs/usr/lib/$(uname -m)-linux-gnu"
IM_MODULES="$(find "$LIB" -maxdepth 2 -type d -name 'modules-Q16' | head -1)"
IM_ETC="$(find "$A/pkgs/etc" -maxdepth 1 -type d -name 'ImageMagick-6*' | head -1)"

cat > "$HOME/.local/bin/x-env.sh" <<EOF
# Source me. Written by scripts/sandbox-x-env.sh - user-space X + GTK/Chromium libraries.
A=$A
export PATH=$HOME/.local/bin:\$A/pkgs/usr/bin:\$PATH
export LD_LIBRARY_PATH=$LIB:\$A/pkgs/usr/lib\${LD_LIBRARY_PATH:+:\$LD_LIBRARY_PATH}
export XVFB_XKBDIR=\$A/pkgs/usr/share/X11/xkb
export XKBCOMP_PATH=\$A/pkgs/usr/bin/xkbcomp
export XVFB_PRELOAD=$SHIM_DIR/xkbshim.so
export XDG_DATA_DIRS=\$A/pkgs/usr/share:/usr/local/share:/usr/share
export FONTCONFIG_FILE=$ETC/fonts/fonts.conf
export GDK_PIXBUF_MODULE_FILE=$ETC/gdk-loaders.cache
export LIBGL_DRIVERS_PATH=$LIB/dri
export MAGICK_CONFIGURE_PATH=$IM_ETC
export MAGICK_CODER_MODULE_PATH=$IM_MODULES/coders
export MAGICK_FILTER_MODULE_PATH=$IM_MODULES/filters
EOF
# shellcheck disable=SC1091
source "$HOME/.local/bin/x-env.sh"

Q="$(find "$A/pkgs" -name gdk-pixbuf-query-loaders | head -1)"
if [ -n "$Q" ]; then
  # shellcheck disable=SC2046
  "$Q" $(find "$A/pkgs" -path '*gdk-pixbuf-2.0*loaders*' -name '*.so') > "$ETC/gdk-loaders.cache" 2>/dev/null || true
fi
# The gates call `import`; Ubuntu ships it as import-im6.q16 behind an alternatives link
# that dpkg -x does not create.
IMPORT_BIN="$(ls "$A"/pkgs/usr/bin/import-im6* 2>/dev/null | head -1)"
[ -n "$IMPORT_BIN" ] && ln -sf "$IMPORT_BIN" "$HOME/.local/bin/import"

fail=0
for b in Xvfb xkbcomp xdotool openbox import; do
  p="$(command -v "$b" || true)"
  if [ -z "$p" ]; then echo "sandbox-x-env: MISSING $b" >&2; fail=1; continue; fi
  miss="$(ldd "$p" 2>/dev/null | grep -c 'not found' || true)"
  [ "$miss" = 0 ] || { echo "sandbox-x-env: $b has $miss unresolved libraries" >&2; fail=1; }
done
ELECTRON="$REPO/shell/deps/linux-$(uname -m | sed 's/aarch64/arm64/;s/x86_64/x64/')/node_modules/electron/dist/electron"
if [ -x "$ELECTRON" ]; then
  miss="$(ldd "$ELECTRON" | grep -c 'not found' || true)"
  [ "$miss" = 0 ] || { echo "sandbox-x-env: electron has $miss unresolved libraries" >&2; fail=1; }
else
  echo "sandbox-x-env: note - $ELECTRON not installed yet (./run installs shell deps on first launch)" >&2
fi
[ "$fail" = 0 ] && echo "sandbox-x-env: ready - source ~/.local/bin/x-env.sh" >&2
exit "$fail"
