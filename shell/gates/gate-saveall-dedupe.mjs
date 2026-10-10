// Save-all dedupe gate (workspace-9g8r3): proves, in the REAL shell, that saving every story
// to the Reading List twice does not double the list and that the status line reports what
// actually happened. Before the fix Shift+A appended every link again on each press and
// always announced "Saved (N)"; and typing "Favs " (stray trailing space) at the Shift+S
// prompt failed to save into the existing "Favs" collection. Every check reads what the
// user sees: the TUI status line, and the collections view.
// Headful under Xvfb ONLY (never headless). Run from shell/: node gates/gate-saveall-dedupe.mjs
import http from 'node:http'
import { Env, attach, Cdp, findTarget, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

buildTui('Release')

const FPORT = 8175
const STORIES = [
  'Harbour commission approves the northern breakwater rebuild',
  'City council votes to extend late-night bus routes',
  'Regional orchestra announces a free winter concert series',
  'Ferry operator adds an early sailing on weekdays',
  'Library extends opening hours through the exam season',
]
const fixture = http.createServer((req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  res.end(`<!doctype html><meta charset="utf-8"><title>Harbour Gazette</title><body>
<h1>Harbour Gazette</h1><section><h2>Top stories</h2><ul>
${STORIES.map((t, i) => `<li><a href="/local/2026/10/story-${i + 1}">${t}</a> <time datetime="2026-10-10T08:00:00Z">Oct 10</time></li>`).join('\n')}
</ul></section>`)
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))
const base = `http://127.0.0.1:${FPORT}`

const env = new Env({ display: ':86', cdpPort: 9296 })
await env.up()
const shell = env.launchShell()

let lt = null
try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('S0 shell booted to launcher', (await pollTermText(term, 'Go to URL', 30000)).ok)
  const wid = env.activate(); await sleep(500)
  // Keystrokes can be lost while the machine is busy, so the open is retried.
  let opened = { ok: false }
  for (let attempt = 0; attempt < 3 && !opened.ok; attempt++) {
    if (attempt) { env.key('Escape'); await sleep(600); env.key('Escape'); await sleep(600) }
    env.key('o'); await sleep(900)
    env.type('http:/'); await sleep(200); env.type(base.slice('http:/'.length) + '/'); await sleep(500)
    env.key('Return')
    opened = await pollTermText(term, 'Harbour Gazette', 30000)
  }
  check('S1 TUI rendered the fixture front page', opened.ok)
  const t0 = await findTarget(env.cdpPort, t => t.url.includes('linktree.html'), 20000, 'link-tree pane')
  lt = await Cdp.connect(t0.webSocketDebuggerUrl)
  for (let i = 0; i < 40 && !(await lt.eval('!!window.__linkTreeReady').catch(() => false)); i++) await sleep(250)
  await sleep(1000)

  // First save-all: every story is new.
  env.key('shift+a')
  const first = await pollTermText(term, `Saved (${STORIES.length})`, 8000)
  check(`S2 first Shift+A announces "Saved (${STORIES.length})"`, first.ok,
    first.ok ? '' : `term: ${first.txt.replace(/\s+/g, ' ').slice(-300)}`)
  await sleep(2500)

  // Second save-all on the same page: nothing new — and it must SAY so.
  env.key('shift+a')
  const second = await pollTermText(term, `All ${STORIES.length} already in list`, 8000)
  check(`S3 second Shift+A announces "All ${STORIES.length} already in list" (not another "Saved (${STORIES.length})")`,
    second.ok, second.ok ? '' : `term: ${second.txt.replace(/\s+/g, ' ').slice(-300)}`)
  await sleep(2500)

  // Shift+S into "Favs", then again into "Favs " — the stray space must reuse the same
  // collection. (The outcome is read from the collections view below: one Favs, 2 items.)
  env.key('shift+s'); await sleep(1200)
  env.type('Favs'); await sleep(300); env.key('Return')
  await sleep(3000)
  env.key('Down'); await sleep(500)
  env.key('shift+s'); await sleep(1200)
  env.type('Favs '); await sleep(300); env.key('Return')
  const failed = await pollTermText(term, 'Failed to save', 4000)
  check('S4 Shift+S "Favs " (trailing space) does not fail', !failed.ok,
    failed.ok ? `term: ${failed.txt.replace(/\s+/g, ' ').slice(-300)}` : '')
  await sleep(1500)

  // The collections view: the Reading List holds each story ONCE, and there is ONE Favs.
  env.key('c')
  const cols = await pollTermText(term, 'Favs', 10000)
  await sleep(1200)
  const txt = (await pollTermText(term, '\u0000never', 500)).txt
  const flat = txt.replace(/\s+/g, ' ')
  const favCount = (flat.match(/\bFavs\b/g) || []).length
  const favItems = flat.match(/\bFavs \((\d+)\)/)
  check('S5 the collections view lists exactly one "Favs", holding BOTH saved stories',
    cols.ok && favCount === 1 && !!favItems && Number(favItems[1]) === 2,
    `Favs x${favCount} items=${favItems && favItems[1]}; view: ${flat.slice(0, 400)}`)
  const rl = flat.match(/Reading List[^0-9]{0,40}(\d+)/i)
  check(`S6 the Reading List holds ${STORIES.length} items (not ${STORIES.length * 2})`,
    !!rl && Number(rl[1]) === STORIES.length, rl ? `count=${rl[1]} in "${rl[0]}"` : `no count found; view: ${flat.slice(0, 600)}`)
  env.shotWindow('saveall-dedupe-collections', wid)
} catch (e) {
  console.log('GATE ERROR:', e)
  check('gate completed without error', false, String(e))
  try { env.shot('saveall-dedupe-error') } catch {}
} finally {
  if (process.env.GATE_DEBUG) console.log(shell.stderrText.slice(-4000))
  try { lt?.close?.() } catch {}
  env.down()
  fixture.close()
}
process.exit(summary() ? 1 : 0)
