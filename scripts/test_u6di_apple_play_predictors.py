#!/usr/bin/env python3
"""
workspace-u6di: server-side verification of every documented predictor
Apple Podcasts uses to decide whether to render a `Play` button (inline
streaming) vs an `Open` button (generic download fallback).

This is NOT the same as an Apple device screenshot — the iOS client could
still misclassify the feed for reasons outside this script's reach (e.g.
client-only sniffing, regional gating). But Apple's documented contract is
public and machine-checkable. If every predictor below holds, the only
reasons left for Apple to render `Open` are non-deterministic / opaque
(cache lag, client bug, account state).

Predictors (from Apple's "Podcast requirements" page and the original
workspace-kitv root cause analysis):
  1. enclosure URL ends in a recognised audio extension (.mp3, .m4a, .mp4)
  2. enclosure `type` attribute is one of audio/mpeg, audio/x-m4a, audio/mp4
  3. HTTP response Content-Type matches the enclosure type
  4. HTTP HEAD on the enclosure returns 200
  5. Feed itself returns 200, Content-Type contains rss/xml
  6. ffprobe of the actual audio reports a recognised container (mp4/m4a)
  7. Audio is non-trivial (duration > 5s, size > 10kB)
  8. The fix flipped .m4b/audio/x-m4b → .m4a/audio/x-m4a (regression guard)

Default feed: the workspace test bucket (see [[live-podcast-test-creds]]).
Override with FEED_URL env var.
"""
import json
import os
import re
import subprocess
import sys
import tempfile
import urllib.request
import xml.etree.ElementTree as ET

FEED_URL = os.environ.get(
    "FEED_URL",
    "https://storage.googleapis.com/tr_list_reader/podcasts/"
    "2f0b829eddbe4e3ab8e4d948ce9b5c17/feed.xml",
)

RECOGNISED_EXTENSIONS = {".mp3", ".m4a", ".mp4"}
RECOGNISED_MIMES = {"audio/mpeg", "audio/x-m4a", "audio/mp4", "audio/mp4a-latm"}
DISQUALIFYING_EXTENSIONS = {".m4b"}             # iTunes audiobook → Open
DISQUALIFYING_MIMES = {"audio/x-m4b"}


class Verdict:
    def __init__(self):
        self.checks = []
        self.failures = []

    def check(self, name, ok, detail=""):
        marker = "PASS" if ok else "FAIL"
        self.checks.append({"check": name, "result": marker, "detail": detail})
        if not ok:
            self.failures.append(f"{name}: {detail}")
        print(f"  [{marker}] {name}" + (f" — {detail}" if detail else ""))

    def all_passed(self):
        return len(self.failures) == 0


def http_head(url):
    req = urllib.request.Request(url, method="HEAD")
    with urllib.request.urlopen(req, timeout=15) as resp:
        return resp.status, dict(resp.headers)


def http_get(url, max_bytes=None):
    req = urllib.request.Request(url, method="GET")
    with urllib.request.urlopen(req, timeout=30) as resp:
        body = resp.read(max_bytes) if max_bytes else resp.read()
        return resp.status, dict(resp.headers), body


def ffprobe(path):
    out = subprocess.check_output(
        ["ffprobe", "-v", "error",
         "-show_entries", "format=duration,size,format_name,format_long_name",
         "-of", "json", path],
        text=True,
    )
    return json.loads(out)["format"]


def main():
    v = Verdict()
    print(f"Feed: {FEED_URL}\n")

    # 1. Feed reachability + Content-Type
    print("== Feed-level predictors ==")
    try:
        feed_status, feed_headers, feed_body = http_get(FEED_URL)
    except Exception as e:
        v.check("feed_get_succeeds", False, str(e))
        return 1
    v.check("feed_get_200", feed_status == 200, f"got HTTP {feed_status}")
    feed_ct = (feed_headers.get("Content-Type") or "").lower()
    v.check(
        "feed_content_type_rss_xml",
        "xml" in feed_ct or "rss" in feed_ct,
        f"Content-Type='{feed_ct}'",
    )

    # 2. Feed is well-formed XML with at least one <enclosure>
    try:
        root = ET.fromstring(feed_body)
    except ET.ParseError as e:
        v.check("feed_parses_as_xml", False, str(e))
        return 1
    v.check("feed_parses_as_xml", True)

    enclosures = root.findall(".//enclosure")
    v.check(
        "feed_has_enclosures",
        len(enclosures) >= 1,
        f"{len(enclosures)} enclosure tag(s) found",
    )
    if not enclosures:
        return 1

    # 3-8: per-enclosure predictors
    print("\n== Per-enclosure predictors ==")
    for i, enc in enumerate(enclosures, 1):
        print(f"\n[Episode {i}]")
        url = enc.get("url")
        declared_type = enc.get("type", "")
        length_attr = enc.get("length", "")
        v.check(
            f"enc{i}_has_url",
            bool(url),
            f"url='{url}'",
        )
        if not url:
            continue

        # 3. URL extension
        path_part = url.split("?", 1)[0]
        ext = "." + path_part.rsplit(".", 1)[-1].lower() if "." in path_part else ""
        v.check(
            f"enc{i}_url_ext_recognised",
            ext in RECOGNISED_EXTENSIONS,
            f"extension='{ext}' (expected one of {sorted(RECOGNISED_EXTENSIONS)})",
        )
        v.check(
            f"enc{i}_url_ext_not_disqualifying",
            ext not in DISQUALIFYING_EXTENSIONS,
            f"extension='{ext}' must NOT be in {sorted(DISQUALIFYING_EXTENSIONS)} "
            "(workspace-kitv regression guard)",
        )

        # 4. Enclosure MIME type
        v.check(
            f"enc{i}_type_recognised",
            declared_type in RECOGNISED_MIMES,
            f"type='{declared_type}' (expected one of {sorted(RECOGNISED_MIMES)})",
        )
        v.check(
            f"enc{i}_type_not_disqualifying",
            declared_type not in DISQUALIFYING_MIMES,
            f"type='{declared_type}' must NOT be in {sorted(DISQUALIFYING_MIMES)}",
        )

        # 5. HTTP HEAD reachable + Content-Type matches declared type
        try:
            status, headers = http_head(url)
        except Exception as e:
            v.check(f"enc{i}_head_succeeds", False, str(e))
            continue
        v.check(f"enc{i}_head_200", status == 200, f"got HTTP {status}")
        served_ct = (headers.get("Content-Type") or "").lower()
        v.check(
            f"enc{i}_served_content_type_matches",
            served_ct == declared_type.lower(),
            f"served='{served_ct}' declared='{declared_type}'",
        )

        # 6. Declared length vs HTTP Content-Length
        served_len = headers.get("Content-Length")
        if length_attr and served_len:
            v.check(
                f"enc{i}_length_attr_matches_http",
                int(length_attr) == int(served_len),
                f"feed says {length_attr}, HTTP says {served_len}",
            )

        # 7. Download a chunk + ffprobe to confirm container format
        with tempfile.NamedTemporaryFile(suffix=ext, delete=False) as tmp:
            tmp_path = tmp.name
        try:
            urllib.request.urlretrieve(url, tmp_path)
            probe = ffprobe(tmp_path)
            fmt = probe.get("format_name", "")
            dur = float(probe.get("duration", 0))
            size = int(probe.get("size", 0))
            v.check(
                f"enc{i}_ffprobe_container_is_mp4_family",
                "m4a" in fmt or "mp4" in fmt or "mpeg" in fmt,
                f"format_name='{fmt}'",
            )
            v.check(
                f"enc{i}_audio_duration_nontrivial",
                dur > 5.0,
                f"duration={dur:.2f}s",
            )
            v.check(
                f"enc{i}_audio_size_nontrivial",
                size > 10_000,
                f"size={size} bytes",
            )
        finally:
            try:
                os.unlink(tmp_path)
            except FileNotFoundError:
                pass

    # 8. Final verdict
    print("\n" + "=" * 60)
    if v.all_passed():
        print("ALL APPLE-PLAY PREDICTORS PASSED")
        print("Every server-side check Apple's client is documented to run")
        print("returned a positive verdict. If Apple Podcasts still renders")
        print("'Open' on this device, the cause is client-side (cache lag,")
        print("client bug, or undocumented sniffing) — not the feed.")
        return 0
    else:
        print(f"FAILED: {len(v.failures)} predictor(s) failed")
        for f in v.failures:
            print(f"  - {f}")
        print("\nFix the failing predictors before re-checking on Apple device.")
        return 1


if __name__ == "__main__":
    sys.exit(main())
