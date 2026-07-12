// Gate (workspace-ai4c): 'o' on a story opens it IN THE APP'S OWN PANE, never the OS
// default browser. Field bug 2026-07-12: Process.Start(url) launched the system browser
// under the shell. Now: 'o' → pane reveals, LENS navigates to the SELECTED story's URL,
// keyboard goes to the page (browser mode), Esc returns. Selection movement is exercised:
// a second 'o' after moving the selection must open the NEWLY selected story.
// Run from shell/: node gates/gate-o-pane.mjs
import http from 'node:http'
import { Env, Cdp, targets, attach, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

const FPORT = 8135
const story = (href, text) => `<li><a href="${href}">${text}</a></li>`
const FILLER = Array.from({ length: 8 }, (_, i) =>
  `<p>Body section ${i + 1}: the desk kept careful notes on the day's dispatches, comparing morning tallies ` +
  'against the evening close and filing corrections wherever the numbers drifted apart before press time.</p>').join('\n')
const fixture = http.createServer((req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  if (req.url.startsWith('/story1')) {
    res.end(`<!doctype html><meta charset="utf-8"><title>Story One</title><body><article><h1>Opening bell rings twice</h1>${FILLER}</article>`)
    return
  }
  if (req.url.startsWith('/story2')) {
    res.end(`<!doctype html><meta charset="utf-8"><title>Story Two</title><body><article><h1>Charter renewed at the reading room</h1>${FILLER}</article>`)
    return
  }
  res.end(`<!doctype html><meta charset="utf-8"><title>Pane Wire</title><body>
<h1>Pane Wire</h1><section><h2>Front Desk</h2><ul>
${story('/story1.html', 'Opening bell rings twice for the first desk of the morning after a quiet unremarkable week')}
${story('/story2.html', 'Charter renewed at the reading room following a season of carefully counted footnotes')}
</ul></section>`)
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))
const listUrl = `http://127.0.0.1:${FPORT}/`

buildTui('Release')
const env = new Env({ display: ':91', cdpPort: 9257 })
await env.up()
env.launchShell()

// The lens is the page at PANE width (hidden fetch pages run the 1280 emulated viewport).
async function lensUrlAt (paneWidth, urlPart, timeoutMs = 20000) {
  const t0 = Date.now()
  while (Date.now() - t0 < timeoutMs) {
    const list = await targets(env.cdpPort).catch(() => [])
    for (const t of list.filter(t => t.type === 'page' && t.url.includes(urlPart))) {
      const c = await Cdp.connect(t.webSocketDebuggerUrl).catch(() => null)
      if (!c) continue
      const w = await c.eval('innerWidth').catch(() => -1)
      c.close()
      if (Math.abs(w - paneWidth) <= 2) return t.url
    }
    await sleep(400)
  }
  return null
}

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('O.0 TUI booted', (await pollTermText(term, 'Go to URL', 30000)).ok)
  env.activate()
  await sleep(300)
  env.type('o'); await sleep(400)
  env.type(listUrl); await sleep(300)
  env.key('Return')
  check('O.1 list rendered', (await pollTermText(term, 'Opening bell', 40000)).ok)

  // D2 auto-reveals on the first list; wait for the pane so state is deterministic.
  let st = null
  for (let i = 0; i < 40; i++) { st = await term.eval('window.__wc.state()'); if (st.revealed) break; await sleep(500) }
  check('O.2 pane present (auto-reveal)', !!st?.revealed)
  const paneW = st.pageBounds.width
  await sleep(2500) // let the spotlight settle on the list before 'o'

  console.log('\n== O: press o on the selected (first) story ==')
  env.activate(); await sleep(200)
  env.type('o')
  let lens1 = await lensUrlAt(paneW, '/story1.html', 8000)
  if (!lens1) {
    // User-realistic retry (the 4991eca2 de-flake pattern): under load the focus-settle
    // gap right after the auto-reveal can swallow a keypress before it reaches the TUI —
    // the child log shows NO 'Opening in pane' for a swallowed press. A user presses again.
    env.activate(); await sleep(300)
    env.type('o')
    lens1 = await lensUrlAt(paneW, '/story1.html', 20000)
  }
  check('O.3 lens navigated to the SELECTED story (story1) at pane width', !!lens1, String(lens1))
  let mode = null
  for (let i = 0; i < 20; i++) { mode = (await term.eval('window.__wc.state()')).mode; if (mode === 'browser') break; await sleep(400) }
  check('O.4 keyboard handed to the page (browser mode)', mode === 'browser', `mode=${mode}`)
  await sleep(3000)
  const still = await lensUrlAt(paneW, '/story1.html', 1500)
  check('O.5 lens STAYS on the story (spotlight suspended, no yank-back)', !!still)
  env.shot('o-pane-story1')

  console.log('\n== O: Esc returns to reader; move selection; o opens the NEW story ==')
  // Gate hygiene (epic gotcha #7): the mode-poll CDP evals above pull renderer focus
  // back to the TERM view, so an immediate Esc would land in the TUI. A user touching
  // the page they just opened is the realistic shape — click the pane, then Esc.
  const wid2 = env.activate(); await sleep(200)
  st = await term.eval('window.__wc.state()')
  env.x('mousemove', '--window', wid2, String(Math.round(st.termBounds.width + st.pageBounds.width / 2)), '400')
  env.x('click', '1'); await sleep(600)
  env.key('Escape')
  for (let i = 0; i < 20; i++) { mode = (await term.eval('window.__wc.state()')).mode; if (mode === 'reader') break; await sleep(400) }
  check('O.6 Esc returned to reader mode', mode === 'reader', `mode=${mode}`)
  env.key('Right'); await sleep(800) // story1 → story2 in the two-tile row
  env.type('o')
  const lens2 = await lensUrlAt(paneW, '/story2.html', 25000)
  check('O.7 second o opened the NEWLY selected story (story2)', !!lens2, String(lens2))
  for (let i = 0; i < 20; i++) { mode = (await term.eval('window.__wc.state()')).mode; if (mode === 'browser') break; await sleep(400) }
  check('O.8 browser mode again', mode === 'browser', `mode=${mode}`)
  env.shot('o-pane-story2')
  term.close()
} catch (err) {
  console.error('GATE ERROR:', err)
  check('o-pane gate completed without error', false, String(err))
  try { env.shot('o-pane-error') } catch {}
} finally {
  env.down()
  fixture.close()
}
process.exit(summary())
