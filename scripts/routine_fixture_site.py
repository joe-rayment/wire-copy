#!/usr/bin/env python3
"""Fixture news site for the Routines harness (workspace-wyvga.9). stdlib only.

  routine_fixture_site.py --port 8123 [--write-config <XDG_DATA_HOME>]
                           [--browser-only-articles] [--log FILE]

GET /        front page: nav + footer chrome, four sections in name markup
GET /flat    same stories, one flat list, same order
POST /_rotate  shift every section's order by one (first goes last)
GET /_state  JSON {section: [absolute urls in current order]}

--browser-only-articles: an article answers 403 to anything but a real browser
  navigation, the way a paywalled site now treats a scripted HTTP client however well it
  copies a browser's headers. "Real" = the Accept header is the one Chromium itself sends
  for a navigation (it offers application/signed-exchange); the app's HTTP fallback copies
  most browser headers but not that one.
--log FILE: one line per article request: "<unix time> <browser|plain> <status> <path>".
"""
import argparse, json, os, re, datetime, html, time
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "demo", "site")
# section -> (url prefix, [(source dir, count or None)])
SECTIONS = [("World", "world", [("world", None), ("news", 2)]),
            ("Disasters", "disasters", [("disaster", None)]),
            ("Science", "science", [("science", None)]),
            ("Arts", "arts", [("arts", None)])]

def slug_files(d, n):
    p = os.path.join(ROOT, d)
    fs = sorted(f for f in os.listdir(p) if f.endswith(".html"))
    return fs[:n] if n else fs

BROWSER_ONLY = False
LOGF = None
ARTICLES = {}   # url path -> (title, file path)
ORDER = {}      # section name -> [url paths]
for name, prefix, srcs in SECTIONS:
    ORDER[name] = []
    for d, n in srcs:
        for f in slug_files(d, n):
            path = f"/{prefix}/{f}"
            fp = os.path.join(ROOT, d, f)
            m = re.search(r"<h1[^>]*>(.*?)</h1>", open(fp, encoding="utf-8").read(), re.S)
            ARTICLES[path] = (html.unescape(re.sub(r"<[^>]+>", "", m.group(1))).strip(), fp)
            ORDER[name].append(path)

def chrome_top():
    return ('<nav><a href="/">Home</a> | <a href="/about.html">About</a> | '
            '<a href="/contact.html">Contact</a></nav>')
CHROME_BOTTOM = '<footer><a href="/privacy.html">Privacy</a> | <a href="/terms.html">Terms</a></footer>'

def link(p):
    return f'<li><a href="{p}">{html.escape(ARTICLES[p][0])}</a></li>'

def front():
    secs = "".join(f'<section><h2>{n}</h2><ul>{"".join(link(p) for p in ORDER[n])}</ul></section>'
                   for n, _, _ in SECTIONS)
    return page("Fixture Gazette", f"<h1>Fixture Gazette</h1>{secs}")

def flat():
    items = "".join(link(p) for n, _, _ in SECTIONS for p in ORDER[n])
    return page("Fixture Gazette - flat", f"<h1>All stories</h1><ul>{items}</ul>")

def page(title, body):
    return (f'<!DOCTYPE html><html><head><meta charset="utf-8"><title>{title}</title></head>'
            f'<body>{chrome_top()}<main>{body}</main>{CHROME_BOTTOM}</body></html>').encode()

class H(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def send(self, code, body, ctype="text/html; charset=utf-8"):
        self.send_response(code); self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body))); self.end_headers()
        if self.command != "HEAD": self.wfile.write(body)
    do_HEAD = lambda self: self.do_GET()
    def do_GET(self):
        path = self.path.split("?")[0].split("#")[0]
        if path == "/": self.send(200, front())
        elif path == "/flat": self.send(200, flat())
        elif path == "/_state":
            base = f"http://{self.headers.get('Host') or 'localhost'}"
            self.send(200, json.dumps({n: [base + p for p in ORDER[n]] for n, _, _ in SECTIONS}).encode(),
                      "application/json")
        elif path == "/gazette.css":
            self.send(200, open(os.path.join(ROOT, "gazette.css"), "rb").read(), "text/css")
        elif path in ARTICLES:
            real = "application/signed-exchange" in (self.headers.get("Accept") or "")
            code = 200 if (real or not BROWSER_ONLY) else 403
            if LOGF:
                with open(LOGF, "a", encoding="utf-8") as f:
                    f.write(f"{time.time():.3f} {'browser' if real else 'plain'} {code} {path}\n")
            if code == 200: self.send(200, open(ARTICLES[path][1], "rb").read())
            else: self.send(403, b"<html><body><h1>403 Forbidden</h1></body></html>")
        else: self.send(404, b"not found", "text/plain")
    def do_POST(self):
        if self.path.split("?")[0] == "/_rotate":
            for n in ORDER: ORDER[n].append(ORDER[n].pop(0))
            self.send(200, b'{"ok":true}', "application/json")
        else: self.send(404, b"not found", "text/plain")

def write_config(xdg, port):
    d = os.path.join(xdg, "WireCopy", "hierarchy"); os.makedirs(d, exist_ok=True)
    host = f"127.0.0.1:{port}"; esc = f"127\\.0\\.0\\.1:{port}"
    now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    base = dict(domain=host, excludeSelectors=[], excludeUrlPatterns=[], excludeSectionTitles=[],
                moreSelectors=[], moreUrlPatterns=[], userLabels=[], userInstructions=[],
                userSectionNames=[], needsReanalyze=False, createdAt=now, modelVersion="fixture-1")
    # LayoutKind has no JsonStringEnumConverter in HierarchyConfigStore -> numeric:
    # AiHierarchical=0, DocumentOrder=1.
    root = dict(base, urlPattern=f"^https?://(www\\.)?{esc}/?", kind=0, version=2, strategy="fixture-sections",
                sections=[dict(name=n, sortOrder=i, parentSelectors=[], urlPatterns=[f"/{pre}/"], startCollapsed=False)
                          for i, (n, pre, _) in enumerate(SECTIONS)])
    flatc = dict(base, urlPattern=f"^https?://(www\\.)?{esc}/flat(?=[/?#]|$)", kind=1, version=2,
                 strategy="document-order", structuralSignature="doc-order:23", sections=[])
    with open(os.path.join(d, f"127.0.0.1_{port}.json"), "w") as f:
        json.dump([root, flatc], f, indent=2)

if __name__ == "__main__":
    ap = argparse.ArgumentParser(); ap.add_argument("--port", type=int, default=8123)
    ap.add_argument("--write-config"); ap.add_argument("--browser-only-articles", action="store_true")
    ap.add_argument("--log"); a = ap.parse_args(); BROWSER_ONLY = a.browser_only_articles; LOGF = a.log
    if a.write_config: write_config(a.write_config, a.port)
    ThreadingHTTPServer(("127.0.0.1", a.port), H).serve_forever()
