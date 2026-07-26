// Reproduction probe #3: does a REDIRECT make the spotlight decide "the user drove the
// lens" and permanently stop following?
// DockSpotlight.cs:377-393 compares the lens's ACTUAL url against _lastLensNav (the url
// the spotlight ASKED for). A site that redirects (canonical slash, https, /amp/, m.,
// consent hop — i.e. every real news site) makes those differ, so userDroveLens latches
// _divergedFromTarget and every later sync returns early WITHOUT navigating or
// re-applying the highlight.
// Control arm = the SAME drive against a non-redirecting site. Headful under Xvfb.
// Run from shell/: node gates/probe-redirect-diverge.mjs
import http from 'node:http'
import { mkdirSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { Env, attach, pollTermText, sleep, buildTui } from './lib.mjs'

const PORT_R = 8261 // redirecting site
const PORT_S = 8262 // straight site (control)
const FILLER = Array.from({ length: 10 }, (_, i) =>
  `<p>Body paragraph ${i + 1}: the desk kept careful notes on the day's dispatches.</p>`).join('\n')

// REDIRECTING site: /storyN.html -> 302 -> /articles/N  (a canonical redirect)
const redirecting = http.createServer((req, res) => {
  const m = req.url.match(/^\/story(\d+)\.html/)
  if (m) { res.writeHead(302, { location: `/articles/${m[1]}` }); res.end(); return }
  const art = req.url.match(/^\/articles\/(\d+)/)
  res.setHeader('content-type', 'text/html; charset=utf-8')
  if (art) { res.end(`<!doctype html><meta charset="utf-8"><title>REDIR Story ${art[1]}</title><body><article><h1>REDIR Story ${art[1]} headline</h1>${FILLER}</article>`); return }
  const stories = Array.from({ length: 6 }, (_, i) =>
    `<li><a href="/story${i + 1}.html">REDIR STORY ${i + 1} — a headline long enough to wrap in a card</a></li>`).join('\n')
  res.end(`<!doctype html><meta charset="utf-8"><title>REDIR Front</title><body><h1>REDIR Front Page</h1><section><h2>Front Desk</h2><ul>${stories}</ul></section>`)
})
// STRAIGHT site: no redirects at all.
const straight = http.createServer((req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  const m = req.url.match(/^\/story(\d+)\.html/)
  if (m) { res.end(`<!doctype html><meta charset="utf-8"><title>PLAIN Story ${m[1]}</title><body><article><h1>PLAIN Story ${m[1]} headline</h1>${FILLER}</article>`); return }
  const stories = Array.from({ length: 6 }, (_, i) =>
    `<li><a href="/story${i + 1}.html">PLAIN STORY ${i + 1} — a headline long enough to wrap in a card</a></li>`).join('\n')
  res.end(`<!doctype html><meta charset="utf-8"><title>PLAIN Front</title><body><h1>PLAIN Front Page</h1><section><h2>Front Desk</h2><ul>${stories}</ul></section>`)
})
await new Promise(r => redirecting.listen(PORT_R, '127.0.0.1', r))
await new Promise(r => straight.listen(PORT_S, '127.0.0.1', r))

buildTui('Release')
const env = new Env({ display: ':94', cdpPort: 9304, screen: '1800x1100x24' })
const dataDir = path.join(env.extraEnv.XDG_DATA_HOME, 'WireCopy')
mkdirSync(dataDir, { recursive: true })
writeFileSync(path.join(dataDir, 'bookmarks.json'), JSON.stringify({
  version: 1,
  bookmarks: [
    { name: 'REDIR WIRE', url: `http://127.0.0.1:${PORT_R}/` },
    { name: 'PLAIN WIRE', url: `http://127.0.0.1:${PORT_S}/` }
  ]
}, null, 2))
await env.up()
const shell = env.launchShell()

const LENS_JS = `(function(){
  const o=document.getElementById('__wirecopy-spotlight');
  let on=null;
  if(o){const ob=o.getBoundingClientRect();let best=null,bo=0;
    for(const el of document.querySelectorAll('a[href]')){const r=el.getBoundingClientRect();
      const ov=Math.max(0,Math.min(ob.right,r.right)-Math.max(ob.left,r.left))*Math.max(0,Math.min(ob.bottom,r.bottom)-Math.max(ob.top,r.top));
      if(ov>bo){bo=ov;best=el;}}
    on=best?best.getAttribute('href'):null;}
  return {url:location.href,title:document.title,spot:o?Math.round(o.getBoundingClientRect().top):null,onAnchor:on};
})()`

let n = 0
async function look (term, label) {
  const l = await term.eval(`wc.lensEval(${JSON.stringify(LENS_JS)})`)
  console.log(`  [${++n}] ${label}`)
  console.log(`        PANE: ${l && l.title}  (${l && l.url})`)
  console.log(`        SPOT: ${l && l.spot != null ? 'top=' + l.spot + ' on ' + l.onAnchor : 'NONE'}`)
  return l
}
const key = async (k, ms = 1400) => { env.key(k); await sleep(ms) }

async function arm (term, wid, label, badge, listMarker, tag) {
  console.log(`\n================ ${label} ================`)
  await key(badge, 1200)
  await key('Return', 500)
  console.log('  list reached:', (await pollTermText(term, listMarker, 45000)).ok)
  await sleep(5000)
  const a = await look(term, 'front page up, selection on story 1')
  await key('Down', 3000)
  const b = await look(term, 'after Down — spotlight MUST move')
  await key('Right', 3000)
  const c = await look(term, 'after Right — spotlight MUST move again')
  env.shotWindow(`rd-${tag}-list`, wid)
  await key('Return', 500)
  await pollTermText(term, 'headline', 30000)
  await sleep(4500)
  const d = await look(term, 'after Enter — pane MUST show that story')
  env.shotWindow(`rd-${tag}-story`, wid)
  await key('Escape', 3000)
  const e = await look(term, 'back on the list — spotlight MUST come back')
  env.shotWindow(`rd-${tag}-back`, wid)
  console.log(`  >>> follow-along tracked selection : ${a && b && c && (a.spot !== b.spot) && (b.spot !== c.spot)}`)
  console.log(`  >>> Enter navigated the pane       : ${!!(d && /Story/i.test(d.title))}`)
  console.log(`  >>> spotlight returned on Back     : ${!!(e && e.spot != null)}`)
  await key('Escape', 3000)
}

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal')
  for (let i = 0; i < 120; i++) { const t = await term.eval('window.__termText?window.__termText():""').catch(() => ''); if (t.trim().length > 20) break; await sleep(400) }
  const wid = env.activate()
  await sleep(1200)

  await arm(term, wid, 'CONTROL — site that never redirects (badge 3)', '3', 'PLAIN STORY', 'plain')
  await arm(term, wid, 'TEST — site with a canonical redirect (badge 1)', '1', 'REDIR STORY', 'redir')

  const lines = shell.stderrText.split('\n').filter(l => /spotlight|lens|diverg|adopt/i.test(l))
  console.log('\n--- TUI/shell log lines mentioning spotlight/lens/divergence ---\n' + lines.slice(-40).join('\n'))
} catch (e) {
  console.error('PROBE ERROR:', e); console.error(shell.stderrText.slice(-4000))
} finally { env.down(); redirecting.close(); straight.close() }
process.exit(0)
