#!/usr/bin/env python3
"""
One-shot repair: regenerate feed.xml with the workspace-kxj3 fixes (drop empty
itunes:image, use true/false for itunes:explicit). Reads the existing
manifest.json to get episode metadata, regenerates the feed XML mirroring the
C# generator, and uploads via the SA in /workspace/creds/.
"""

import json
import sys
from datetime import datetime, timezone
from xml.etree.ElementTree import Element, SubElement, register_namespace, tostring
from xml.dom import minidom

from google.cloud import storage  # type: ignore

BUCKET = "tr_list_reader"
FEED_UUID = "2f0b829eddbe4e3ab8e4d948ce9b5c17"
SA_PATH = "/workspace/creds/annular-cogency-238418-35f480ebacf1.json"

# Mirrors PodcastConfiguration defaults — same as what the live feed uses.
PODCAST_LANGUAGE = "en-us"
PODCAST_EXPLICIT = False
PODCAST_CATEGORY = "News"
PODCAST_IMAGE_URL = ""  # the prod value is empty; under the fix this means omit element

ATOM_NS = "http://www.w3.org/2005/Atom"
ITUNES_NS = "http://www.itunes.com/dtds/podcast-1.0.dtd"
PODCAST_NS = "https://podcastindex.org/namespace/1.0"
PSC_NS = "http://podlove.org/simple-chapters"


def format_duration(td_str: str) -> str:
    """Convert C# TimeSpan-style "HH:MM:SS.fffffff" → "HH:MM:SS"."""
    # Parse "00:03:07.9520000" or "00:03:07"
    h, m, rest = td_str.split(":", 2)
    s = rest.split(".", 1)[0]
    return f"{int(h):02d}:{int(m):02d}:{int(s):02d}"


def format_chapter_time(td_str: str) -> str:
    """C# Podlove format: HH:MM:SS.mmm."""
    h, m, rest = td_str.split(":", 2)
    if "." in rest:
        s, frac = rest.split(".", 1)
        ms = int((frac + "000000")[:7]) // 10000  # 7-digit fractional ticks → ms
    else:
        s, ms = rest, 0
    return f"{int(h):02d}:{int(m):02d}:{int(s):02d}.{ms:03d}"


def rfc2822(iso: str) -> str:
    """ISO 8601 → RFC 2822-like string matching C#'s "R" format."""
    # Strip trailing Z if present and parse as UTC.
    dt = datetime.fromisoformat(iso.replace("Z", "+00:00"))
    return dt.astimezone(timezone.utc).strftime("%a, %d %b %Y %H:%M:%S GMT")


def build_feed(manifest: dict, feed_url: str) -> str:
    register_namespace("atom", ATOM_NS)
    register_namespace("itunes", ITUNES_NS)
    register_namespace("podcast", PODCAST_NS)
    register_namespace("psc", PSC_NS)

    rss = Element("rss", {"version": "2.0"})

    channel = SubElement(rss, "channel")
    SubElement(channel, "title").text = manifest["title"]

    desc = SubElement(channel, "description")
    desc.text = manifest["description"]

    SubElement(channel, "language").text = PODCAST_LANGUAGE

    last_build = max(ep["publishedAtUtc"] for ep in manifest["episodes"])
    SubElement(channel, "lastBuildDate").text = rfc2822(last_build)
    SubElement(channel, "generator").text = "Wire Copy Podcast Generator"

    SubElement(channel, f"{{{ITUNES_NS}}}author").text = manifest["author"]
    SubElement(channel, f"{{{ITUNES_NS}}}summary").text = manifest["description"]

    # workspace-kxj3 fix: emit "true"/"false", not "yes"/"no".
    SubElement(channel, f"{{{ITUNES_NS}}}explicit").text = (
        "true" if PODCAST_EXPLICIT else "false"
    )
    SubElement(channel, f"{{{ITUNES_NS}}}type").text = "episodic"
    SubElement(channel, f"{{{PODCAST_NS}}}locked").text = "yes"

    # workspace-kxj3 fix: omit itunes:image when no URL is configured.
    if PODCAST_IMAGE_URL.strip():
        SubElement(channel, f"{{{ITUNES_NS}}}image", {"href": PODCAST_IMAGE_URL})

    SubElement(channel, "link").text = feed_url
    SubElement(channel, f"{{{ATOM_NS}}}link", {
        "href": feed_url,
        "rel": "self",
        "type": "application/rss+xml",
    })
    SubElement(channel, f"{{{ITUNES_NS}}}category", {"text": PODCAST_CATEGORY})

    for ep in manifest["episodes"]:
        item = SubElement(channel, "item")
        SubElement(item, "title").text = ep["title"]
        ep_desc = SubElement(item, "description")
        ep_desc.text = ep["description"]
        SubElement(item, "pubDate").text = rfc2822(ep["publishedAtUtc"])
        SubElement(item, "guid", {"isPermaLink": "false"}).text = ep["id"]
        SubElement(item, "enclosure", {
            "url": ep["audioUrl"],
            "length": str(ep["audioSizeBytes"]),
            "type": ep["audioMimeType"],
        })
        SubElement(item, f"{{{ITUNES_NS}}}duration").text = format_duration(ep["duration"])
        ep_itunes_summary = SubElement(item, f"{{{ITUNES_NS}}}summary")
        ep_itunes_summary.text = ep["description"]

        chapters = ep.get("chapters") or []
        if chapters:
            ch_el = SubElement(item, f"{{{PSC_NS}}}chapters", {"version": "1.2"})
            for ch in chapters:
                attrs = {
                    "start": format_chapter_time(ch["startTime"]),
                    "title": ch["title"],
                }
                if ch.get("linkUrl"):
                    attrs["href"] = ch["linkUrl"]
                if ch.get("imageUrl"):
                    attrs["image"] = ch["imageUrl"]
                SubElement(ch_el, f"{{{PSC_NS}}}chapter", attrs)

    # Wrap description text in CDATA the way the C# generator does.
    raw = tostring(rss, encoding="utf-8", xml_declaration=True).decode()

    # Match the C# generator's CDATA wrapping for description/summary fields.
    # Replace the parsed-and-escaped text with <![CDATA[...]]> sections to
    # mirror the production output structure.
    def cdataify(tag: str, text: str) -> tuple[str, str]:
        # Escape the text into the form ElementTree will have emitted, then
        # swap to a CDATA wrapper.
        escaped = (text
                   .replace("&", "&amp;")
                   .replace("<", "&lt;")
                   .replace(">", "&gt;"))
        open_close = f"<{tag}>{escaped}</{tag}>"
        cdata = f"<{tag}><![CDATA[{text}]]></{tag}>"
        return open_close, cdata

    # Channel-level description and itunes:summary
    for ns, tag in [
        ("", "description"),
        (ITUNES_NS, "summary"),
    ]:
        prefix = "itunes:" if ns == ITUNES_NS else ""
        full_tag = f"{prefix}{tag}"
        old, new = cdataify(full_tag, manifest["description"])
        raw = raw.replace(old, new, 1)  # only the first occurrence (channel-level)

    # Per-episode descriptions
    for ep in manifest["episodes"]:
        old, new = cdataify("description", ep["description"])
        raw = raw.replace(old, new, 1)
        old, new = cdataify("itunes:summary", ep["description"])
        raw = raw.replace(old, new, 1)

    return raw


def main():
    print(f"Loading SA creds: {SA_PATH}")
    client = storage.Client.from_service_account_json(SA_PATH)
    bucket = client.bucket(BUCKET)

    feed_url = f"https://storage.googleapis.com/{BUCKET}/podcasts/{FEED_UUID}/feed.xml"
    manifest_blob = bucket.blob(f"podcasts/{FEED_UUID}/manifest.json")

    print(f"Loading manifest: {manifest_blob.name}")
    manifest = json.loads(manifest_blob.download_as_text())
    print(f"  episodes: {len(manifest['episodes'])}")

    new_xml = build_feed(manifest, feed_url)
    print("Generated XML preview (first 600 chars):")
    print(new_xml[:600])
    print("...")

    feed_blob = bucket.blob(f"podcasts/{FEED_UUID}/feed.xml")
    feed_blob.cache_control = "no-cache, max-age=0"
    feed_blob.upload_from_string(new_xml, content_type="application/rss+xml")
    print(f"\nUploaded fixed feed.xml → {feed_url}")

    # Also bump manifest's updatedAtUtc to invalidate any feed-index.json caching.
    manifest["updatedAtUtc"] = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")
    manifest_blob.cache_control = "no-cache, max-age=0"
    manifest_blob.upload_from_string(
        json.dumps(manifest, indent=2),
        content_type="application/json",
    )
    print("Bumped manifest.updatedAtUtc")


if __name__ == "__main__":
    main()
