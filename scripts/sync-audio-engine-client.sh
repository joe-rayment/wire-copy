#!/usr/bin/env bash
# Refresh the vendored audio-engine C# client from a local audio-engine checkout.
# Usage: scripts/sync-audio-engine-client.sh <path-to-audio-engine-checkout>
# Copies the four client source files verbatim into src/AudioEngine.Client/ and
# prints the checkout's commit so README.md's "Source" line can be updated.
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: $0 <path-to-audio-engine-checkout>" >&2
  exit 2
fi

src="$1/sdks/csharp/AudioEngine.Client"
dest="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/src/AudioEngine.Client"

if [[ ! -d "$src" ]]; then
  echo "error: $src not found (is $1 an audio-engine checkout?)" >&2
  exit 1
fi

for f in AudioEngineClient.cs Exceptions.cs Models.cs WebhookVerifier.cs; do
  cp "$src/$f" "$dest/$f"
  echo "copied $f"
done

sha="$(git -C "$1" rev-parse --short HEAD 2>/dev/null || echo unknown)"
echo "audio-engine @ $sha -- update the 'Source' line in src/AudioEngine.Client/README.md"
