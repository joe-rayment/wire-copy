#!/usr/bin/env python3
"""
workspace-kt19.1 — builds the public-domain demo content pack into demo/site/.

Sources (all published before 1930 -> US public domain), fetched from Project
Gutenberg and carved into newspaper-style articles:

  #781   Sinking of the Titanic and Great Sea Disasters (1912, ed. Logan Marshall)
  #1560  The San Francisco Calamity by Earthquake and Fire (1906, Charles Morris)
  #13635 NYT Current History: The European War, Vol 1 No 1 (1915) — the Shaw debate
  #8297  Scientific American Supplement No. 286 (June 25, 1881)
  #27055 Punch, or the London Charivari, Vol. 147 (Sept 2, 1914)

Output: a self-contained static 'wire service' — The Daily Gazette — with a
sectioned front page (lead story + tiers + promo rail, wizard-friendly CSS
classes), per-section front pages, and >500-word articles (the prefetch
sufficiency floor). Re-run to regenerate; the generated HTML is committed.

Usage: python3 scripts/build_demo_content.py [--cache DIR]
"""

import argparse
import html
import os
import re
import sys
import urllib.request

SOURCES = {
    "781": "Sinking of the Titanic and Great Sea Disasters (1912), ed. Logan Marshall",
    "1560": "The San Francisco Calamity by Earthquake and Fire (1906), Charles Morris",
    "13635": "The New York Times Current History: The European War, Vol 1 No 1 (1915)",
    "8297": "Scientific American Supplement, No. 286 (June 25, 1881)",
    "27055": "Punch, or the London Charivari, Vol. 147 (September 2, 1914)",
}

OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "demo", "site")
MIN_WORDS = 520  # > MinPaywalledWordCount(500) so prefetch never skips a story


def fetch(gid, cache):
    path = os.path.join(cache, f"{gid}.txt")
    if not os.path.exists(path):
        url = f"https://www.gutenberg.org/ebooks/{gid}.txt.utf-8"
        print(f"fetching {url}")
        req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
        with urllib.request.urlopen(req, timeout=60) as r, open(path, "wb") as f:
            f.write(r.read())
    return open(path, encoding="utf-8").read()


def strip_gutenberg(t):
    s = re.search(r"\*\*\* ?START OF.*?\*\*\*", t, re.S)
    e = re.search(r"\*\*\* ?END OF", t)
    return t[s.end() if s else 0 : e.start() if e else len(t)]


def clean_paragraphs(block):
    """Blank-line separated paragraphs; unwraps hard line breaks; drops
    illustrations, footnote markers and pure-noise lines."""
    paras = []
    for raw in re.split(r"\n\s*\n", block):
        p = " ".join(line.strip() for line in raw.strip().splitlines())
        p = re.sub(r"\[Illustration[^\]]*\]", "", p)
        p = re.sub(r"\[\d+\]", "", p)
        p = re.sub(r"\s{2,}", " ", p).strip()
        p = p.replace("--", "—")
        if not p or len(p) < 3:
            continue
        if re.fullmatch(r"[*_\-=~\s.]+", p):
            continue
        if "{" in p or "}" in p:
            continue  # illustration caption fragments
        if re.fullmatch(r"[IVXL]+\.?", p):
            continue  # bare roman-numeral sub-headings
        paras.append(p)
    return paras


def words(paras):
    return sum(len(p.split()) for p in paras)


MAX_PARAS = 70


def abridge(paras):
    if len(paras) <= MAX_PARAS:
        return paras
    return paras[:MAX_PARAS] + ["[Abridged for this demonstration; the full text is freely available on Project Gutenberg.]"]


def title_case(s):
    SMALL = {"a", "an", "and", "at", "by", "for", "in", "of", "on", "or", "the", "to", "with"}
    out = []
    for i, w in enumerate(re.split(r"\s+", s.strip().lower())):
        out.append(w if (w in SMALL and i > 0) else w.capitalize())
    return " ".join(out)


def slugify(s):
    s = re.sub(r"[^a-z0-9]+", "-", s.lower()).strip("-")
    return s[:60].rstrip("-")


# ---- per-source extraction ----

def extract_titanic(t):
    """CHAPTER I TITLE ... — chapter heading and title share one line."""
    body = strip_gutenberg(t)
    # The TOC repeats the headings; start at the second occurrence of CHAPTER I.
    hits = [m for m in re.finditer(r"^CHAPTER ([IVXL]+)\.? (.+)$", body, re.M)]
    # Group: TOC entries come first; body chapters re-match the same numerals.
    seen, chapters = set(), []
    for m in hits:
        if m.group(1) in seen:
            chapters.append(m)
        else:
            seen.add(m.group(1))
    arts = []
    for i, m in enumerate(chapters):
        end = chapters[i + 1].start() if i + 1 < len(chapters) else len(body)
        paras = clean_paragraphs(body[m.end():end])
        arts.append((title_case(m.group(2)), "From the 1912 account edited by Logan Marshall", paras))
    return arts


def extract_sf(t):
    """CHAPTER N.\\nTITLE possibly over two lines."""
    body = strip_gutenberg(t)
    toc_end = body.find("CHAPTER I.", body.find("TABLE OF CONTENTS") + 1)
    # Skip past the TOC: chapters in the body are CHAPTER N. followed by title line(s).
    hits = [m for m in re.finditer(r"^CHAPTER ([IVXL]+)\.\s*\n\n?([A-Z][^\n]+)\n", body, re.M)]
    bych = {}
    for m in hits:
        bych.setdefault(m.group(1), []).append(m)  # last occurrence = body
    chapters = sorted((v[-1] for v in bych.values()), key=lambda m: m.start())
    arts = []
    for i, m in enumerate(chapters):
        end = chapters[i + 1].start() if i + 1 < len(chapters) else len(body)
        paras = clean_paragraphs(body[m.end():end])
        arts.append((title_case(m.group(2).rstrip(".")), "From eyewitness accounts compiled by Charles Morris, 1906", paras))
    return arts


def extract_nyt(t):
    """Title line (possibly *quoted*) directly above a 'By Author.' line."""
    body = strip_gutenberg(t)
    marks = list(re.finditer(r"^\s*By ([A-Z][a-zA-Z. ]+?)\.?\s*$", body, re.M))
    arts = []
    for i, m in enumerate(marks):
        head_start = body.rfind("\n\n", 0, m.start())
        title = body[head_start:m.start()].strip().strip("*_\"")
        title = re.sub(r"\s+", " ", title).strip().strip("\"")
        end = marks[i + 1].start() if i + 1 < len(marks) else len(body)
        seg = body[m.end():end]
        nxt = seg.rfind("\n\n", 0, len(seg))
        # trim the NEXT article's title block off the tail
        if i + 1 < len(marks) and nxt > 0:
            seg = seg[:nxt]
        paras = clean_paragraphs(seg)
        arts.append((title_case(title), f"By {m.group(1).strip()} · The New York Times Current History, 1915", paras))
    return arts


def extract_sciam(t, wanted):
    body = strip_gutenberg(t)
    positions = []
    for w in wanted:
        idxs = [m.start() for m in re.finditer(re.escape(w), body)]
        if idxs:
            positions.append((idxs[-1], w))  # last occurrence = body (first is TOC)
    positions.sort()
    arts = []
    for i, (pos, w) in enumerate(positions):
        end = positions[i + 1][0] if i + 1 < len(positions) else len(body)
        paras = clean_paragraphs(body[pos + len(w):end])
        arts.append((title_case(w.rstrip(".")), "Scientific American Supplement, June 25, 1881", paras))
    return arts


def extract_punch(t, wanted):
    return extract_sciam(t, wanted) and [
        (title, "Punch, or the London Charivari, September 2, 1914", paras)
        for title, _, paras in extract_sciam(t, wanted)
    ]


# ---- HTML emission ----

CSS = """
body{font-family:Georgia,'Times New Roman',serif;margin:0;background:#f7f4ec;color:#1c1b18}
header.masthead{border-bottom:3px double #1c1b18;padding:18px 16px 10px;text-align:center}
header.masthead h1{font-variant:small-caps;letter-spacing:.12em;margin:0;font-size:1.9em}
header.masthead p{margin:4px 0 0;font-style:italic;color:#6b6557;font-size:.85em}
main{max-width:720px;margin:0 auto;padding:12px}
section.lead-story{border-bottom:1px solid #b9b2a0;padding:14px 4px}
section.lead-story h2{font-size:1.45em;margin:0 0 6px}
section.lead-story p.deck{margin:0;color:#4c463a}
section.headline-list{border-bottom:1px solid #d6cfbd;padding:8px 4px}
section.headline-list h3.section-name{font-variant:small-caps;letter-spacing:.1em;font-size:.95em;color:#6b6557;margin:6px 0}
section.headline-list div.story{padding:7px 0;border-top:1px dotted #d6cfbd}
section.promo-rail,aside.promo-rail{padding:10px 4px;background:#efe9d9;margin-top:10px}
section.promo-rail h3,aside.promo-rail h3{font-size:.8em;color:#8a8270;text-transform:uppercase;margin:2px 0 6px}
section.promo-rail div,aside.promo-rail div{padding:4px 0;font-size:.92em}
a{color:#15407a;text-decoration:none}a:hover{text-decoration:underline}
article{max-width:680px;margin:0 auto;padding:18px 16px}
article h1{font-size:1.6em;line-height:1.2;margin:.2em 0}
article p.byline{font-style:italic;color:#6b6557;border-bottom:1px solid #d6cfbd;padding-bottom:10px}
article p{line-height:1.55;font-size:1.04em}
footer.attribution{max-width:680px;margin:18px auto;padding:10px 16px;border-top:1px solid #b9b2a0;font-size:.8em;color:#6b6557}
"""


def page(title, body, depth=0):
    css = ("../" * depth) + "gazette.css"
    return (f"<!DOCTYPE html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">"
            f"<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
            f"<title>{html.escape(title)}</title><link rel=\"stylesheet\" href=\"{css}\"></head>"
            f"<body>{body}</body></html>\n")


def article_html(title, byline, paras, source_line):
    ps = "\n".join(f"<p>{html.escape(p)}</p>" for p in paras)
    body = (f"<article><h1>{html.escape(title)}</h1>"
            f"<p class=\"byline\">{html.escape(byline)}</p>\n{ps}</article>"
            f"<footer class=\"attribution\">Public domain. Source: {html.escape(source_line)} "
            f"via Project Gutenberg. Presented as demo content for WireCopy.</footer>")
    return page(title, body, depth=1)


def front_page(masthead, tagline, lead, tiers, promos, depth=0):
    h = [f"<header class=\"masthead\"><h1>{html.escape(masthead)}</h1><p>{html.escape(tagline)}</p></header><main>"]
    url, title, deck = lead
    h.append(f"<section class=\"lead-story\"><h2><a href=\"{url}\">{html.escape(title)}</a></h2>"
             f"<p class=\"deck\">{html.escape(deck)}</p></section>")
    for name, cls, items in tiers:
        h.append(f"<section class=\"headline-list {cls}\"><h3 class=\"section-name\">{html.escape(name)}</h3>")
        for url, title in items:
            h.append(f"<div class=\"story\"><a href=\"{url}\">{html.escape(title)}</a></div>")
        h.append("</section>")
    if promos:
        # A labelled section (not a bare aside): the link-list heuristics group
        # it like the story tiers, so it sorts LAST in document order instead of
        # floating ungrouped to the top of the tree.
        h.append("<section class=\"headline-list promo-rail\"><h3 class=\"section-name\">Advertisements</h3>")
        for url, txt in promos:
            h.append(f"<div class=\"story\"><a href=\"{url}\">{html.escape(txt)}</a></div>")
        h.append("</section>")
    h.append("</main>")
    return page(masthead, "".join(h), depth=depth)


PROMOS = [
    ("Dr. Bartleby's Celebrated Liver Pills — Sound Digestion in Every Bottle",
     "Dr. Bartleby's Celebrated Liver Pills are compounded of the purest vegetable extracts and are warranted to relieve dyspepsia, biliousness, and all derangements of the digestive organs. For forty years the standard remedy of discerning households. Each bottle bears the doctor's signature, without which none is genuine. Beware of worthless imitations pressed upon the public by unscrupulous dealers. Sold by all reputable druggists at twenty-five cents."),
    ("The Crescent Safety Bicycle — Swift, Silent, Sure. Catalogue Free",
     "The Crescent Safety Bicycle is built of cold-drawn weldless steel tubing upon scientific principles, with ball bearings throughout and cushion tires of the newest pattern. Ladies' and gentlemen's models in all sizes. No hill too steep, no road too long. Write to-day for our handsomely illustrated catalogue, mailed free to any address, and the name of our nearest agent."),
    ("Steamship Tickets to All Parts of the World — Anchor Line Offices",
     "The Anchor Line begs to announce weekly sailings of its swift and commodious steamships to Liverpool, Glasgow, and the Continent. Staterooms of unusual size and airiness; a table unsurpassed upon the Atlantic. Second cabin and steerage at greatly reduced winter rates. Drafts issued payable in all principal cities. Apply at the company's offices or to any authorized agent."),
]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", default="/tmp/demo-src")
    args = ap.parse_args()
    os.makedirs(args.cache, exist_ok=True)
    os.makedirs(OUT, exist_ok=True)

    texts = {gid: fetch(gid, args.cache) for gid in SOURCES}

    sections = {}  # dir -> list[(slug, title, byline, paras, source)]

    def keep(sect, arts, source, limit):
        rows = sections.setdefault(sect, [])
        for title, byline, paras in arts:
            if words(paras) < MIN_WORDS or len(rows) >= limit:
                continue
            rows.append((slugify(title), title, byline, abridge(paras), source))

    keep("news", extract_titanic(texts["781"]), SOURCES["781"], limit=8)
    keep("disaster", extract_sf(texts["1560"]), SOURCES["1560"], limit=7)
    keep("world", extract_nyt(texts["13635"]), SOURCES["13635"], limit=6)
    keep("science", extract_sciam(texts["8297"], [
        "PETROLEUM AND COAL IN VENEZUELA.",
        "ONE THOUSAND HORSE-POWER CORLISS ENGINE.",
        "OPENING OF THE NEW WORKSHOP OF THE STEVENS INSTITUTE OF TECHNOLOGY.",
        "LIGHT STEAM ENGINE FOR BALLOONS.",
        "COMPLETE PREVENTION OF INCRUSTATION IN BOILERS.",
        "THE MICROPHONE IN PHYSICAL RESEARCHES.",
        "ON THE PRESERVATION OF WOOD.",
    ]), SOURCES["8297"], limit=7)
    keep("arts", extract_punch(texts["27055"], [
        "THE WATCH DOGS.",
        "ESSENCE OF PARLIAMENT.",
        "THE AVENGERS.",
        "CHARIVARIA.",
        "AT THE FRONT.",
    ]), SOURCES["27055"], limit=5)

    # promo pages
    os.makedirs(os.path.join(OUT, "sponsored"), exist_ok=True)
    promo_links = []
    for i, (headline, body_text) in enumerate(PROMOS, 1):
        slug = f"promo-{i}"
        paras = [body_text] * 1
        with open(os.path.join(OUT, "sponsored", f"{slug}.html"), "w") as f:
            f.write(article_html(headline.split(" — ")[0], "A paid announcement", paras,
                                 "Period-style advertisement written for this demo"))
        promo_links.append((f"sponsored/{slug}.html", headline))

    # article pages
    written = {}
    for sect, rows in sections.items():
        os.makedirs(os.path.join(OUT, sect), exist_ok=True)
        for slug, title, byline, paras, source in rows:
            with open(os.path.join(OUT, sect, f"{slug}.html"), "w") as f:
                f.write(article_html(title, byline, paras, source))
        written[sect] = rows
        print(f"  {sect}: {len(rows)} articles ({', '.join(str(words(p)) for _,_,_,p,_ in rows)} words)")

    with open(os.path.join(OUT, "gazette.css"), "w") as f:
        f.write(CSS)

    def links(sect, skip=0, n=99, prefix=""):
        return [(f"{prefix}{sect}/{slug}.html", title) for slug, title, *_ in written[sect][skip:skip + n]]

    # The Daily Gazette — main front page
    lead_slug, lead_title, *_ = written["news"][0]
    with open(os.path.join(OUT, "index.html"), "w") as f:
        f.write(front_page(
            "The Daily Gazette", "All the News of the Age, by Wire and by Post",
            (f"news/{lead_slug}.html", lead_title,
             "The maiden voyage of the largest vessel afloat ends in the greatest of marine disasters; the world waits upon the wireless for news."),
            [("The Disaster at Sea", "news-tier", links("news", skip=1)),
             ("The War in Europe", "world-tier", links("world", n=4)),
             ("Science & Industry", "science-tier", links("science", n=4)),
             ("The Lighter Side", "arts-tier", links("arts", n=3))],
            promo_links))

    # Section fronts
    fronts = [
        ("world.html", "The Gazette: Foreign Desk", "Dispatches and Opinions upon the European War",
         "world", "Writers of renown debate the war: Mr. Shaw and his critics."),
        ("science.html", "The Gazette: Science & Industry", "Inventions, Discoveries, and the Useful Arts",
         "science", "The marvels of the age, from the Corliss engine to the microphone."),
        ("disaster.html", "The Morning Chronicle — Special Edition", "San Francisco in Ruins: Eyewitness Accounts",
         "disaster", "The great city laid low by earthquake and fire; told by those who saw it."),
        ("arts.html", "The Gazette: The Lighter Side", "Wit and Observation from the Pages of Punch",
         "arts", "The London Charivari surveys a world at war with its pen unsheathed."),
    ]
    for fname, masthead, tagline, sect, deck in fronts:
        rows = written[sect]
        lead = (f"{sect}/{rows[0][0]}.html", rows[0][1], deck)
        with open(os.path.join(OUT, fname), "w") as f:
            f.write(front_page(masthead, tagline, lead,
                               [(masthead.split(": ")[-1], f"{sect}-tier", links(sect, skip=1))],
                               promo_links))

    # Attribution
    with open(os.path.join(OUT, "ATTRIBUTION.md"), "w") as f:
        f.write("# Demo content attribution\n\n"
                "All articles in this demo pack are in the United States public domain\n"
                "(published before 1930). Text obtained from Project Gutenberg:\n\n")
        for gid, desc in SOURCES.items():
            f.write(f"- {desc} — https://www.gutenberg.org/ebooks/{gid}\n")
        f.write("\nThe 'Advertisements' are period-style pastiches written for this demo.\n"
                "Front pages and arrangement © the WireCopy project, MIT licensed.\n")

    total = sum(len(r) for r in written.values())
    print(f"wrote {total} articles + {len(PROMOS)} promos + {1 + len(fronts)} front pages -> {OUT}")


if __name__ == "__main__":
    sys.exit(main())
