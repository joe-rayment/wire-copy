// History gate (workspace-2ojw1): proves, in the REAL shell, two back/forward outcomes.
// (1) Reading position survives the round trip: read an article well past the first screen,
//     go back to the front page, come forward again, and keep reading — the next line is the
//     one after where you were, not the top. Before the fix the forward trip restored the
//     scroll but put the reader cursor on line 0, so the first keypress jumped to the top.
// (2) Backing out of a load leaves nothing behind: open a slow page, press b while it is
//     still loading, then Shift+B — you stay on the article. Before the fix the empty
//     "Loading..." placeholder was parked on the forward stack and Shift+B landed on it.
// Every check reads what the terminal shows (which paragraphs are on screen).
// Headful under Xvfb ONLY (never headless). Run from shell/: node gates/gate-history.mjs
import http from 'node:http'
import { Env, attach, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

buildTui('Release')

const FPORT = 8176
const PARAS = 60
const para = n => `<p>Paragraph ${n}: the harbour commission heard from residents about the breakwater, ` +
  `the ferry timetable and the late-night buses, according to the minutes of meeting number ${n}.</p>`
const slowRes = []
const fixture = http.createServer((req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  if (req.url.startsWith('/slow')) { slowRes.push(res); return }   // never answers: a load the user backs out of
  if (req.url.startsWith('/a/long')) {
    res.end(`<!doctype html><meta charset="utf-8"><title>Breakwater minutes</title><body><article>` +
      `<h1>Breakwater minutes</h1>${Array.from({ length: PARAS }, (_, i) => para(i + 1)).join('\n')}</article>`)
    return
  }
  res.end(`<!doctype html><meta charset="utf-8"><title>Harbour Gazette</title><body><h1>Harbour Gazette</h1>
<section><h2>Top stories</h2><ul>
<li><a href="/a/long-breakwater-minutes">Breakwater minutes published in full after a long meeting</a></li>
<li><a href="/a/long-ferry-timetable">Ferry operator adds an early sailing on weekdays this winter</a></li>
<li><a href="/a/long-bus-routes">City council votes to extend the late-night bus routes again</a></li>
</ul></section>`)
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))
const base = `http://127.0.0.1:${FPORT}`

const env = new Env({ display: ':86', cdpPort: 9296 })
await env.up()
const shell = env.launchShell()

const screenText = async term => (await pollTermText(term, '\u0000never', 300)).txt
const visibleParas = txt => [...txt.matchAll(/Paragraph (\d+):/g)].map(m => Number(m[1]))
// Keystrokes can be lost while the machine is busy, so opening a URL is retried.
async function openUrl (env, term, url, expect) {
  let r = { ok: false }
  for (let attempt = 0; attempt < 3 && !r.ok; attempt++) {
    if (attempt) { env.key('Escape'); await sleep(600) }
    env.key('o'); await sleep(900)
    env.type('http:/'); await sleep(200); env.type(url.slice('http:/'.length)); await sleep(500)
    env.key('Return')
    if (!expect) return { ok: true }
    r = await pollTermText(term, expect, 30000)
  }
  return r
}

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('H0 shell booted to launcher', (await pollTermText(term, 'Go to URL', 30000)).ok)
  const wid = env.activate(); await sleep(500)
  check('H1 front page rendered', (await openUrl(env, term, base + '/', 'Breakwater minutes published')).ok)
  await sleep(1500)

  // Open the first story and read it in the reader, well past the first screen.
  env.key('Return'); await sleep(4000)
  // Articles may open in the link view first; 'r' is the reader view either way.
  env.key('r')
  check('H2 the article opened in the reader', (await pollTermText(term, 'Paragraph 1:', 30000)).ok)
  await sleep(1000)
  for (let i = 0; i < 70; i++) { env.key('j'); await sleep(40) }
  await sleep(1200)
  const before = visibleParas(await screenText(term))
  check('H3 reading moved well past the first screen', before.length > 0 && Math.min(...before) > 3,
    `visible paragraphs: ${JSON.stringify(before)}`)

  // Back to the front page, forward to the article, then ONE more line of reading.
  env.key('b')
  check('H4 b returned to the front page', (await pollTermText(term, 'Harbour Gazette', 15000)).ok)
  await sleep(1500)
  env.key('shift+b')
  check('H5 Shift+B returned to the article', (await pollTermText(term, 'Paragraph', 15000)).ok)
  await sleep(1500)
  env.key('j'); await sleep(1200)
  const after = visibleParas(await screenText(term))
  check('H6 the next line of reading continues where it left off (no jump to the top)',
    after.length > 0 && !after.includes(1) && Math.min(...after) >= Math.min(...before) - 1,
    `before=${JSON.stringify(before)} after=${JSON.stringify(after)}`)
  env.shotWindow('history-cursor', wid)

  // Back out of a load, then try to go forward. In the reader 'o' opens the live page, so
  // the URL goes through the ':open' command line, as a user on an article would type it.
  env.type(':open '); await sleep(300)
  env.type('http:/'); await sleep(200); env.type((base + '/slow/never-loads').slice('http:/'.length)); await sleep(400)
  env.key('Return')
  // The placeholder replaces the article on screen while the page loads (it never will).
  let loadTxt = ''
  for (let i = 0; i < 20; i++) { loadTxt = await screenText(term); if (!visibleParas(loadTxt).length) break; await sleep(500) }
  check('H7 the slow page started loading (the article left the screen for its placeholder)',
    visibleParas(loadTxt).length === 0, `screen: ${loadTxt.replace(/\s+/g, ' ').slice(0, 300)}`)
  await sleep(2000)
  env.key('b')
  const backAgain = await pollTermText(term, 'Paragraph', 15000)
  check('H8 b during the load came back to the article', backAgain.ok)
  await sleep(1500)
  env.key('shift+b')
  const noFwd = await pollTermText(term, 'No forward history', 4000)
  await sleep(1500)
  const fwdTxt = await screenText(term)
  check('H9 Shift+B after backing out of the load has nothing to go forward to, and stays on the article',
    noFwd.ok && visibleParas(fwdTxt).length > 0 && !fwdTxt.includes('never-loads'),
    `noForwardMsg=${noFwd.ok} screen: ${fwdTxt.replace(/\s+/g, ' ').slice(0, 300)}`)
  env.shotWindow('history-forward-after-cancel', wid)
} catch (e) {
  console.log('GATE ERROR:', e)
  check('gate completed without error', false, String(e))
  try { env.shot('history-error') } catch {}
} finally {
  if (process.env.GATE_DEBUG) console.log(shell.stderrText.slice(-4000))
  env.down()
  for (const r of slowRes) { try { r.destroy() } catch {} }
  fixture.closeAllConnections?.()
  fixture.close()
}
process.exit(summary() ? 1 : 0)
