// Cache-restart gate (workspace-o9g9i, defect 1): proves a page reached through a REDIRECT
// is still served from the build cache after the app is quit and relaunched on the SAME data
// dir. Before the fix DiskCacheStore.LoadAllBuildCaches re-keyed every build-cache file by
// Normalize(FinalUrl), so after a restart the REQUEST-url key was gone: revisiting the url the
// user actually typed missed the build cache and rebuilt the page instead of "skipping
// extraction" (and a preloaded page always missed it). User actions driven: open a url that
// 302s to another path -> `q` quits the app -> relaunch with the same XDG/userData dir ->
// open the SAME request url again. Outcome asserted: the revisit paints the same page + stories,
// and the app's own log names the build-cache HIT for the request url and no MISS for it.
// The log is the ONLY signal that separates a build-cache hit from the HTML-cache rebuild that
// happened before the fix (same stories painted either way, so painted stories alone prove
// nothing). Deliberately NOT asserted, because they are false/blind on main too (observed):
//  - the link-tree "cached" header: LinkTreeRenderer compares page.Url (the FINAL url) with the
//    HTML cache's request urls, so a redirected page never shows it, restart or not;
//  - the fixture request count: the live page pane re-fetches the final url and the story
//    preload re-fetches stories on every open, and the HTML cache already served the request url.
// Headful under Xvfb ONLY (never headless). Run from shell/: node gates/gate-cache-restart.mjs
import http from 'node:http'
import { readdirSync, readFileSync, statSync } from 'node:fs'
import path from 'node:path'
import { Env, ROOT, attach, Cdp, findTarget, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

buildTui('Release')

const FPORT = 8173
const REQ_PATH = '/world'            // what the user types (a bare section url)
const FINAL_PATH = '/world/front-desk' // where the site 302s to
const hits = {}                      // path -> count of fixture requests
const stories = [
  ['Harbour commission approves the northern breakwater rebuild', '/local/2026/10/breakwater-rebuild'],
  ['City council votes to extend late-night bus routes', '/local/2026/10/late-night-buses'],
  ['Regional orchestra announces a free winter concert series', '/arts/2026/10/winter-concerts'],
  ['Port authority publishes the autumn ferry timetable', '/local/2026/10/ferry-timetable'],
  ['Library opens a reading room for the evening shift', '/local/2026/10/library-reading-room'],
  ['Farmers market adds a second weekly stall day', '/local/2026/10/market-stall-day']
]
const li = ([t, h]) => `<li><a href="${h}">${t}</a> <time datetime="2026-10-10T08:00:00Z">Oct 10</time></li>`
const intro = Array.from({ length: 8 }, () =>
  'The desk kept careful notes on the day and collected the dispatches below for readers.').join(' ')
const fixture = http.createServer((req, res) => {
  const p = req.url.split('?')[0]
  hits[p] = (hits[p] || 0) + 1
  if (p === REQ_PATH) { res.writeHead(302, { location: FINAL_PATH }); res.end(); return }
  res.setHeader('content-type', 'text/html; charset=utf-8')
  res.end(`<!doctype html><meta charset="utf-8"><title>Harbour Gazette World Desk</title><body>
<h1>Harbour Gazette</h1><p>${intro}</p>
<section><h2>Top stories</h2><ul>${stories.map(li).join('\n')}</ul></section>`)
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))
const reqUrl = `http://127.0.0.1:${FPORT}${REQ_PATH}`

// The TUI's own log (cwd=ROOT/logs, rolling daily) shared across runs: read only what a phase appended.
const logFile = () => {
  const dir = path.join(ROOT, 'logs')
  const f = readdirSync(dir).filter(n => /^wirecopy-.*\.log$/.test(n)).map(n => path.join(dir, n))
    .sort((a, b) => statSync(b).mtimeMs - statSync(a).mtimeMs)[0]
  return f
}
const logSize = () => { try { return statSync(logFile()).size } catch { return 0 } }
const logSince = off => { try { return readFileSync(logFile()).subarray(off).toString('utf8') } catch { return '' } }

const env = new Env({ display: ':87', cdpPort: 9297 })
await env.up()

// One user visit: launcher -> `o` -> type url -> Enter, retried (dropped keystrokes), then read the
// link-tree pane. Returns what the pane showed.
async function openAndRead (shell, label) {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check(`${label} shell booted to launcher`, (await pollTermText(term, 'Go to URL', 30000)).ok)
  const wid = env.activate(); await sleep(500)
  let opened = { ok: false }
  for (let attempt = 0; attempt < 3 && !opened.ok; attempt++) {
    if (attempt) { env.key('Escape'); await sleep(600); env.key('Escape'); await sleep(600) }
    env.key('o'); await sleep(900)
    env.type('http:/'); await sleep(200); env.type(reqUrl.slice('http:/'.length)); await sleep(500)
    env.key('Return')
    opened = await pollTermText(term, 'breakwater', 30000)
  }
  check(`${label} TUI rendered the redirected front page`, opened.ok)
  const t0 = await findTarget(env.cdpPort, t => t.url.includes('linktree.html'), 20000, 'link-tree pane')
  const lt = await Cdp.connect(t0.webSocketDebuggerUrl)
  for (let i = 0; i < 40 && !(await lt.eval('!!window.__linkTreeReady').catch(() => false)); i++) await sleep(250)
  await sleep(1500)
  const pane = JSON.parse(await lt.eval(`JSON.stringify({
    title: (document.getElementById('pagetitle') || {}).textContent || '',
    meta: (document.getElementById('metaline') || {}).textContent || '',
    cards: [...document.querySelectorAll('#grid .card-title')].map(e => e.textContent)
  })`))
  return { term, lt, wid, pane }
}

try {
  // ---- Phase 1: first visit (cold) ----
  const off1 = logSize()
  const shell1 = env.launchShell()
  const v1 = await openAndRead(shell1, 'V1')
  check('V1b fixture served the redirect + final page (cold fetch happened)',
    (hits[REQ_PATH] || 0) >= 1 && (hits[FINAL_PATH] || 0) >= 1, JSON.stringify(hits))
  check('V1c cold visit was NOT a build-cache hit', !logSince(off1).includes(`build cache HIT for ${reqUrl}`))
  await sleep(3000) // let the disk cache flush its files
  // ---- quit like a user: `q` in the TUI closes the whole app ----
  v1.lt.close(); v1.term.close()
  env.activate(); env.type('q')
  let gone = false
  for (let i = 0; i < 20; i++) { await sleep(500); if (shell1.exitCode !== null) { gone = true; break } }
  check('R1 app quit cleanly on `q`', gone)

  // ---- Phase 2: relaunch on the SAME data dir, open the SAME request url ----
  const off2 = logSize()
  const shell2 = env.launchShell()
  const v2 = await openAndRead(shell2, 'V2')
  const log2 = logSince(off2)
  const buildLines = log2.split('\n').filter(l => /build ?cache/i.test(l))
  check('V2b app log: build cache HIT for the REQUEST url (the redirected page skipped re-extraction)',
    log2.includes(`build cache HIT for ${reqUrl}`), buildLines.slice(-4).join(' | '))
  check('V2c app log: no build cache MISS for the request url after the restart',
    !buildLines.some(l => /MISS/.test(l) && l.includes(reqUrl)), buildLines.filter(l => /MISS/.test(l)).slice(-2).join(' | '))
  check('V2d same page + stories painted after the restart',
    v2.pane.title === 'Harbour Gazette World Desk' && v2.pane.cards.length === stories.length &&
      v2.pane.cards.some(c => c.includes('breakwater')),
    JSON.stringify(v2.pane))
  env.shotWindow('cache-restart-revisit', v2.wid)
  v2.lt.close(); v2.term.close()
} catch (e) {
  console.log('GATE ERROR:', e)
  check('gate completed without error', false, String(e))
  try { env.shot('cache-restart-error') } catch {}
} finally {
  env.down()
  fixture.close()
}
process.exit(summary() ? 1 : 0)
