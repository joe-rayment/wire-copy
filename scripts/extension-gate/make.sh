#!/usr/bin/env bash
# Builds noinet.so next to this script.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
gcc -shared -fPIC -O2 -o "$here/noinet.so" "$here/noinet.c" -ldl
echo "built $here/noinet.so"
