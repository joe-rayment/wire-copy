# Extension gate infrastructure

Drives a REAL, headful Chromium or Vivaldi (never headless) against a fake news site, so the browser
extension can be exercised online and offline.

- `start-browser.sh <chromium|vivaldi> <display> <cdp-port> <proxy-port> [url...]` - launches the browser
  with CDP on `<cdp-port>`, all traffic through `<proxy-port>`, and `noinet.so` preloaded.
  **It refuses to run unless `XDG_CONFIG_HOME` and `XDG_DATA_HOME` are both set to TEMP dirs**: the
  browser uses its default profile (the app registers its native-messaging host there), so without
  the guard a run would touch the real profile. Needs a display (Xvfb + a window manager).
- `proxy.py <port>` - the browser's ONLY route out. Serves `gazette.wirecopy.test` from `fixture/site/`
  and forwards everything else. Killing it is how a run goes offline (reload -> `ERR_PROXY_CONNECTION_FAILED`).
- `noinet.c` / `make.sh` -> `noinet.so` - LD_PRELOAD shim: non-loopback `connect`/`sendto`/`sendmsg`/
  `sendmmsg`/`getaddrinfo` fail and are logged to `$NOINET_LOG` (default `$XDG_DATA_HOME/noinet.log`), so a
  child that bypasses the proxy (e.g. the native host) shows up there as `verdict=block`.
- `cdp.mjs` - tiny CDP client (`targets`, `new`, `eval`, `sw-eval`, `offline-check`, `wait-target`).
  `offline-check <url-substring>` fetches https://example.com from that page: `online` or `offline`.
- `fixture/` - the fake site; `check_fixture.py` validates it.

Going offline: start `proxy.py`, start the browser, run `cdp.mjs <port> offline-check ...` (-> `online`),
kill the proxy, check again (-> `offline`).

Vivaldi notes: (1) it needs `--disable-dev-shm-usage` (set in `start-browser.sh`): the container's 64 MB
`/dev/shm` fills up and Vivaldi dies ~1 s after CDP is up; this is not caused by `noinet.so`. (2) A fresh
profile shows a full-window welcome tab and ignores command-line URLs, so `start-browser.sh` opens the URLs
through `cdp.mjs <port> new <url>` instead; the new tab becomes active, the welcome tab stays behind it.
