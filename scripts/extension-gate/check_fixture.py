#!/usr/bin/env python3
"""Sanity check (stdlib only, no C#) for the gate fixtures.

Checks that fixture/layouts/gazette.wirecopy.test.json parses and carries the
fields ArticleSelectorConfig expects (XPath selectors), and that the fixture
article has the shape the layout relies on: 8 story-body paragraphs (promo
excluded), ~650 words, a promo aside between paragraphs 4 and 5, 15 section
links, 10 related links; and that the front page links to the article.

Run: python3 scripts/extension-gate/check_fixture.py
"""
import json
import os
import sys
from html.parser import HTMLParser

HERE = os.path.dirname(os.path.abspath(__file__))
LAYOUT = os.path.join(HERE, "fixture", "layouts", "gazette.wirecopy.test.json")
ARTICLE = os.path.join(HERE, "fixture", "site", "2026", "10", "11", "harbour-lights-return", "index.html")
FRONT = os.path.join(HERE, "fixture", "site", "index.html")

failures = []


def check(cond, msg):
    print(("ok   " if cond else "FAIL ") + msg)
    if not cond:
        failures.append(msg)


with open(LAYOUT, encoding="utf-8") as f:
    cfg = json.load(f)
check(cfg.get("schemaVersion") == 1, "schemaVersion == 1")
check(cfg.get("domain") == "gazette.wirecopy.test", "domain == gazette.wirecopy.test")
entries = cfg.get("pageTypes", [])
check(len(entries) == 1, "exactly one pageTypes entry")
e = entries[0]
check(e.get("pageType") == "article", "pageType is camelCase string 'article'")
sel = e.get("selectors", {})
check(sel.get("headline") == ["//h1"], "headline == ['//h1']")
check(sel.get("body") == ["//div[contains(@class,'story-body')]"], "body is the story-body XPath")
check(sel.get("excludeRegions") == ["//aside[contains(@class,'inline-promo')]"], "excludeRegions is the inline-promo XPath")
check(all(s.startswith("//") for k in ("headline", "byline", "publishDate", "body", "excludeRegions") for s in sel.get(k, [])),
      "every selector is XPath (starts with //)")
check(e.get("quality") == {"minWords": 100, "minParagraphs": 3}, "quality minWords=100 minParagraphs=3")


class Article(HTMLParser):
    def __init__(self):
        super().__init__()
        self.stack = []
        self.paras = []        # (text, inside_aside)
        self.cur = None
        self.nav_links = 0
        self.related_links = 0
        self.order = []        # sequence of 'p' / 'aside' markers inside story-body
        self.in_body = 0
        self.ld = False
        self.ldtext = ""
        self.og_article = False

    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        cls = a.get("class", "") or ""
        self.stack.append((tag, cls))
        if tag == "meta" and a.get("property") == "og:type" and a.get("content") == "article":
            self.og_article = True
        if tag == "script" and a.get("type") == "application/ld+json":
            self.ld = True
        if tag == "div" and "story-body" in cls:
            self.in_body += 1
        if tag == "aside" and self.in_body and "inline-promo" in cls:
            self.order.append("aside")
        if tag == "p" and self.in_body:
            aside = any(t == "aside" for t, _ in self.stack)
            self.cur = ["", aside]
        if tag == "a":
            if any(t == "nav" and "sections" in c for t, c in self.stack):
                self.nav_links += 1
            if any(t == "section" and "related" in c for t, c in self.stack):
                self.related_links += 1

    def handle_endtag(self, tag):
        if tag == "p" and self.cur is not None:
            if not self.cur[1]:
                self.order.append("p")
                self.paras.append(self.cur[0])
            self.cur = None
        if tag == "div" and self.in_body and self.stack and "story-body" in self.stack[-1][1]:
            self.in_body -= 1
        if tag == "script":
            self.ld = False
        if self.stack:
            self.stack.pop()

    def handle_data(self, data):
        if self.cur is not None:
            self.cur[0] += data
        if self.ld:
            self.ldtext += data


p = Article()
with open(ARTICLE, encoding="utf-8") as f:
    p.feed(f.read())
words = sum(len(t.split()) for t in p.paras)
check(len(p.paras) == 8, "8 story-body paragraphs outside the promo (got %d)" % len(p.paras))
check(600 <= words <= 700, "story-body ~650 words (got %d)" % words)
check(p.order == ["p"] * 4 + ["aside"] + ["p"] * 4, "promo aside sits between paragraph 4 and 5 (order=%s)" % p.order)
check(p.nav_links == 15, "15 section links (got %d)" % p.nav_links)
check(p.related_links == 10, "10 related links (got %d)" % p.related_links)
check(p.og_article, "og:type article present")
ld = json.loads(p.ldtext)
check(ld.get("@type") == "NewsArticle" and ld.get("headline") and ld.get("author") and ld.get("datePublished"),
      "ld+json NewsArticle has headline/author/datePublished")

with open(FRONT, encoding="utf-8") as f:
    front = f.read()
check(front.count("<li><a href=") == 20, "front page has 20 headline links")
check('href="/2026/10/11/harbour-lights-return"' in front, "front page links to the article")

print("\nALL OK" if not failures else "\n%d FAILED" % len(failures))
sys.exit(1 if failures else 0)
