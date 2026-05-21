# workspace-kxj3 — W3C feed validator fixes

## What this bead fixed

W3C feed validator (https://validator.w3.org/feed/) reported the live
podcast feed as **Invalid** with two critical errors:

1. `<itunes:image href="" />` — Apple's iTunes podcast DTD requires the
   `href` to be a full URL; an empty value is rejected.
2. `<itunes:explicit>no</itunes:explicit>` — current iTunes spec
   requires `true` or `false`, not the legacy `yes`/`no` strings.

Both errors traced back to `PodcastFeedGenerator.BuildChannel`
(src/WireCopy.Infrastructure/Podcast/PodcastFeedGenerator.cs:72).

## What changed

- **PodcastFeedGenerator.cs:82** — `itunes:explicit` now emits
  `"true"` / `"false"` (was `"yes"` / `"no"`).
- **PodcastFeedGenerator.cs:87–90** — `itunes:image` element is
  only added when `ImageUrl` is non-empty and non-whitespace.
  Previously it was emitted unconditionally with `href=""`.
- **PodcastFeedGeneratorTests.cs** — adjusted existing assertion
  (`"no"` → `"false"`); added 3 new tests:
  - `ItunesExplicit_True_EmitsTrue`
  - `EmptyImageUrl_OmitsItunesImageElement`
  - `WhitespaceImageUrl_OmitsItunesImageElement`

## Live verification (2026-05-21)

1. Built Release; 0 warnings, 0 errors.
2. Ran `PodcastFeedGenerator` test class — 21/21 passing.
3. Regenerated the production feed via a one-shot repair script
   that mirrors the C# generator's output shape, then uploaded to
   `gs://tr_list_reader/podcasts/2f0b829eddbe4e3ab8e4d948ce9b5c17/feed.xml`.
4. Curled the new feed:

   ```
   <itunes:explicit>false</itunes:explicit>
   ```

   And `<itunes:image>` is absent entirely (no empty `href` attribute).

5. Confirmed the **bare canonical URL** (no cache-busting query string,
   what real podcast clients hit) returns the fixed XML:

   ```
   $ curl -s '.../feed.xml' | grep -oE '<itunes:explicit>[^<]+</itunes:explicit>'
   <itunes:explicit>false</itunes:explicit>

   $ curl -sI '.../feed.xml' | grep -iE 'generation|content-length|cache-control'
   x-goog-generation: 1779381969428246
   x-goog-stored-content-length: 1790
   content-length: 1790
   cache-control: no-cache, max-age=0
   ```

   And no `<itunes:image>` element appears anywhere in the body.

6. Validated via W3C against the bare URL — the validator caches by URL
   string, so the trailing fragment `#revalidate-after-cache-purge` was
   added to force a re-fetch (fragments are stripped before the HTTP
   request, so the actual GCS query is identical to the bare URL):

   https://validator.w3.org/feed/check.cgi?url=https%3A%2F%2Fstorage.googleapis.com%2Ftr_list_reader%2Fpodcasts%2F2f0b829eddbe4e3ab8e4d948ce9b5c17%2Ffeed.xml%23revalidate-after-cache-purge

   Result: **"Congratulations! This is a valid RSS feed."**

   The only remaining notice is the non-blocking warning about the
   `podlove/simple-chapters` namespace — a legitimate, intentional
   extension supported by Pocket Casts/Overcast.

## Edge-cache aside (split into workspace-7m8d)

The QA enforcer flagged that the bucket-default cache-control
(`public, max-age=3600`) was trapping the broken feed at the GCS edge
for up to an hour. That's a real publisher bug (a republish doesn't
take effect for clients until the cache TTL expires). It is now fixed
in workspace-7m8d: PodcastPublisher now passes
`cache-control: no-cache, max-age=0` for feed.xml, manifest.json,
and feed-index.json uploads, so post-fix republishes will be visible
immediately.

## Why this matters for workspace-om6q

workspace-om6q's last open item was a castfeedvalidator.com /
W3C feed-validator check. Apple Podcasts in particular treats an
invalid `itunes:explicit` value as a parse failure and may fall back
to default rendering, which is the exact symptom (`Open` instead of
inline `Play`) the umbrella was chasing. With this fix the feed is
W3C-valid, removing the last server-side blocker.

## Repair script

The Python repair script lives at
`scripts/regenerate_feed_xml_only.py` for future use if the feed
ever needs a no-TTS, no-FFmpeg republish (mirror the C# generator's
output shape; upload to GCS using the SA in `/workspace/creds/`).
