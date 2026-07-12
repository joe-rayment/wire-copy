// D2 gate (workspace-3keu): the live-page pane auto-reveals ONCE on the FIRST rendered
// link list of a session (xink.7 semantics), then stays opt-in via | (sticky):
//   1. fresh state → navigate to list A → pane reveals WITHOUT any | press,
//      and the LENS pane is actually showing list A (found by URL at pane width)
//   2. | hides the pane
//   3. a SECOND list (B) does NOT re-reveal — the one-shot never refires
//   4. | re-shows, sticky
// Run from shell/: node gates/gate-d2-reveal.mjs
import http from 'node:http'
import { Env, Cdp, targets, attach, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

const FPORT = 8134
const story = (href, text) => `<li><a href="${href}">${text}</a></li>`
const listPage = (title, items) => `<!doctype html><meta charset="utf-8"><title>${title}</title><body>
<h1>${title}</h1><section><h2>Front Desk</h2><ul>${items.join('\n')}</ul></section>`
const fixture = http.createServer((req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  if (req.url.startsWith('/b')) {
    res.end(listPage('Second Wire', [
      story('/b1.html', 'Quiet harbors: the second desk kept its own ledger of arriving dispatches through the night'),
      story('/b2.html', 'Afternoon audit finds the relay tower repeating yesterday’s bulletins to an empty room')
    ]))
    return
  }
  res.end(listPage('First Wire', [
    story('/a1.html', 'Opening bell: the first list of the morning decides which pane the reader wakes up to'),
    story('/a2.html', 'Charter renewed for the reading room after a season of carefully counted footnotes')
  ]))
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))
const urlA = `http://127.0.0.1:${FPORT}/a`
const urlB = `http://127.0.0.1:${FPORT}/b`

buildTui('Release')
const env = new Env({ display: ':90', cdpPort: 9256 })
await env.up()
env.launchShell()

// 'o' is the go-to-URL flow ONLY on the launcher; from a page the : command line
// accepts a raw URL (SearchCommandHandler.HandleCommandLineInput) — typing a URL
// without a prompt would fire single-key commands instead.
async function gotoUrl (url, { fromPage = false } = {}) {
  env.activate()
  await sleep(300)
  env.type(fromPage ? ':' : 'o'); await sleep(400)
  env.type(url); await sleep(300)
  env.key('Return')
}
async function paneState (term) { return term.eval('window.__wc.state()') }

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('D2.0 TUI booted', (await pollTermText(term, 'Go to URL', 30000)).ok)
  const st0 = await paneState(term)
  check('D2.1 boots FULL (no pane at the launcher)', st0.revealed === false)

  await gotoUrl(urlA)
  check('D2.2 list A rendered', (await pollTermText(term, 'Opening bell', 40000)).ok)
  let st = null
  for (let i = 0; i < 40; i++) { st = await paneState(term); if (st.revealed) break; await sleep(500) }
  check('D2.3 pane AUTO-revealed on the first list — no | pressed', !!st?.revealed)
  check('D2.4 reader stays primary', st && st.termBounds.width > st.pageBounds.width,
    st ? `term=${st.termBounds.width} page=${st.pageBounds.width}` : '')

  // The revealed pane must be SHOWING list A: find a CDP page target on the fixture
  // URL whose innerWidth matches the pane (the hidden fetch page runs the 1280 viewport).
  let lensOk = false
  for (let i = 0; i < 30 && !lensOk; i++) {
    const list = await targets(env.cdpPort).catch(() => [])
    for (const t of list.filter(t => t.type === 'page' && t.url.startsWith(urlA))) {
      const c = await Cdp.connect(t.webSocketDebuggerUrl).catch(() => null)
      if (!c) continue
      const w = await c.eval('innerWidth').catch(() => -1)
      c.close()
      if (Math.abs(w - st.pageBounds.width) <= 2) { lensOk = true; break }
    }
    if (!lensOk) await sleep(500)
  }
  check('D2.5 lens pane is showing list A at pane width', lensOk)
  env.shot('d2-auto-revealed')

  env.type('|')
  let stHid = null
  for (let i = 0; i < 20; i++) { stHid = await paneState(term); if (!stHid.revealed) break; await sleep(400) }
  check('D2.6 | hides the pane', stHid.revealed === false)

  await gotoUrl(urlB, { fromPage: true })
  check('D2.7 list B rendered', (await pollTermText(term, 'Quiet harbors', 40000)).ok)
  await sleep(6000) // generous window for a would-be (buggy) re-reveal to fire
  st = await paneState(term)
  check('D2.8 second list does NOT re-reveal (one-shot)', st.revealed === false)
  env.shot('d2-second-list-stays-full')

  env.type('|')
  let stBack = null
  for (let i = 0; i < 30; i++) { stBack = await paneState(term); if (stBack.revealed) break; await sleep(500) }
  check('D2.9 | re-shows the pane (sticky opt-in)', !!stBack?.revealed)
  term.close()
} catch (err) {
  console.error('GATE ERROR:', err)
  check('D2 gate completed without error', false, String(err))
  try { env.shot('d2-error') } catch {}
} finally {
  env.down()
  fixture.close()
}
process.exit(summary())
