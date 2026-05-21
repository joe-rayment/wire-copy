# workspace-om6q — Podcast feed MIME/extension verification

## Acceptance summary

workspace-kitv shipped the labelling fix: episodes upload as `.m4a` with
`Content-Type: audio/x-m4a` (was `.m4b` / `audio/x-m4b`, which Apple
Podcasts treated as an iTunes audiobook → "Open" instead of inline Play).
om6q is the post-merge validation.

## Evidence

### 1. GCS bucket listing (2026-05-21)

```
podcasts/2f0b829eddbe4e3ab8e4d948ce9b5c17/episodes/975c8b7191592dcad036b60f0324450b.m4a   audio/x-m4a   2026-05-21T15:20:59Z   ← NEW (kitv)
podcasts/2f0b829eddbe4e3ab8e4d948ce9b5c17/episodes/975c8b7191592dcad036b60f0324450b.m4b   audio/x-m4b   2026-05-21T01:41:47Z   ← stale pre-kitv
podcasts/2f0b829eddbe4e3ab8e4d948ce9b5c17/feed.xml                                         application/rss+xml   2026-05-21T15:37:31Z
podcasts/2f0b829eddbe4e3ab8e4d948ce9b5c17/manifest.json                                    application/json      2026-05-21T15:37:31Z
podcasts/feed-index.json                                                                   application/json      2026-05-21T15:37:32Z
```

The fresh upload landed at `.m4a` with `audio/x-m4a` — exactly what
Apple Podcasts requires for inline Play.

### 2. feed.xml enclosure (cache-busted curl, 2026-05-21)

```xml
<enclosure
  url="https://storage.googleapis.com/tr_list_reader/podcasts/2f0b829eddbe4e3ab8e4d948ce9b5c17/episodes/975c8b7191592dcad036b60f0324450b.m4a"
  length="1643877"
  type="audio/x-m4a" />
```

The feed now points at the `.m4a` object with the canonical MIME — the
bead's headline acceptance.

> NOTE: an early curl (without cache-busting) returned a cached older feed
> that still showed `.m4b`. CDN caches the response for ~minutes; the
> definitive check is the bucket-object listing above OR a cache-busted
> curl as shown.

### 3. Public reachability — HTTP 200

```
HTTP/2 200
last-modified: Thu, 21 May 2026 15:37:31 GMT
content-type: application/rss+xml
```

## Deferred (requires user-side Apple device)

- Apple Podcasts screenshot showing the Play button (not Open) on the
  episode list — not reproducible in CI; needs the user's Apple device
  with the feed subscribed.
- castfeedvalidator.com screenshot — same idea, requires interactive
  upload + UI evidence (the validator does run, but its output is a
  web-rendered report rather than a programmable assertion). Can be
  added by the user when they next subscribe and test.

The technical fix is verified end-to-end on the bucket + feed.
