// P5 gate (workspace-y0bi): in-pane login + session persistence — the FULL acceptance
// ("logged-in session renders subscriber prose in reader + LIVE PANE, session persists
// across restart"), credential-free. A fixture "account" page renders SIGNED OUT until a
// login button (clicked in the pane, browser mode) sets a session cookie; the live lens
// pane AND the reader must then render the signed-in subscriber prose. The shell is then
// QUIT and relaunched with the SAME browser partition but a FRESH app data dir (no app
// cache): both the reader and the live pane must render the SIGNED-IN copy — proving the
// session lives in the browser partition and survives an app restart, with zero external
// credentials (nytreader-EQUIVALENT: a real subscriber site is a dogfood confidence item,
// not a capability gap, and must never be parked on the user — see bd memory
// never-park-beads-on-user-mac). Run from shell/: node gates/gate-p5.mjs
import http from 'node:http'
import { mkdtempSync, mkdirSync } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { Env, attach, targets, Cdp, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

// Read the VISIBLE lens pane's rendered DOM text — width-matched to the page pane like
// gate-p5-sso, so the assert lands on the live page the USER sees, never a hidden
// fetch/prefetch WebContents that shares the same URL. Polls until `want` appears.
async function lensText (env, term, urlPart, want, timeoutMs = 40000) {
  const t0 = Date.now()
  const wantN = want.replace(/\s+/g, ' ')
  let last = ''
  while (Date.now() - t0 < timeoutMs) {
    const st = await term.eval('window.__wc.state()').catch(() => null)
    const list = await targets(env.cdpPort).catch(() => [])
    for (const t of list.filter(t => t.type === 'page' && t.url.includes(urlPart))) {
      const c = await Cdp.connect(t.webSocketDebuggerUrl).catch(() => null)
      if (!c) continue
      const w = await c.eval('innerWidth').catch(() => -1)
      if (st?.pageBounds && Math.abs(w - st.pageBounds.width) <= 2) {
        last = await c.eval('document.body.innerText').catch(() => '')
        c.close()
        if (last.replace(/\s+/g, ' ').includes(wantN)) return { ok: true, txt: last }
      } else { c.close() }
    }
    await sleep(500)
  }
  return { ok: false, txt: last }
}

const FPORT = 8127
const FILLER = Array.from({ length: 10 }, (_, i) =>
  `<p>Ledger section ${i + 1}: the account desk kept meticulous notes on the day's activity, comparing the ` +
  'morning tallies against the evening close, filing corrections where the numbers drifted, and appending a ' +
  'memorandum whenever a discrepancy could not be reconciled before the office lights went out for the night. ' +
  'Every figure was initialed twice and countersigned before the page turned.</p>').join('\n')
const fixture = http.createServer((req, res) => {
  const url = new URL(req.url, `http://127.0.0.1:${FPORT}`)
  res.setHeader('content-type', 'text/html; charset=utf-8')
  if (url.pathname === '/account.html') {
    const signedIn = /(^|;\s*)sid=ok(;|$)/.test(req.headers.cookie || '')
    if (signedIn) {
      res.end(`<!doctype html><meta charset="utf-8"><title>Account</title><body><article>
<h1>Account</h1><p>Signed in as Wire Reader. Welcome back to the account ledger.</p>\n${FILLER}</article>`)
    } else {
      res.end(`<!doctype html><meta charset="utf-8"><title>Account</title><body><article>
<h1>Account</h1><p>Signed out. Please log in to view the account ledger.</p>
<button id="login" style="display:block;width:100%;height:45vh;font-size:28px">Log in</button>
<script>document.getElementById('login').onclick = () => { document.cookie = 'sid=ok; max-age=86400; path=/'; location.reload() }</script>
${FILLER}</article>`)
    }
    return
  }
  res.end(`<!doctype html><meta charset="utf-8"><title>Account Wire</title><body>
<h1>Account Wire</h1><ul><li><a href="/account.html">Account ledger overview</a></li></ul>`)
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))

buildTui('Release')
const env = new Env({ display: ':86', cdpPort: 9251 })
await env.up()
let shell = env.launchShell()
const acctUrl = `http://127.0.0.1:${FPORT}/account.html`

async function openAccount (term) {
  env.activate()
  await sleep(300)
  env.type('o'); await sleep(400)
  env.type(acctUrl); await sleep(300)
  env.key('Return')
}

try {
  console.log('\n== P5 run 1: signed out → log in inside the pane ==')
  let term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('P5.0 TUI booted', (await pollTermText(term, 'Go to URL', 30000)).ok)
  await openAccount(term)
  check('P5.1 reader shows the SIGNED-OUT account page', (await pollTermText(term, 'Signed out. Please log in', 40000)).ok)
  env.type('|')
  let st = null
  for (let i = 0; i < 30; i++) { st = await term.eval('window.__wc.state()'); if (st.revealed) break; await sleep(500) }
  check('P5.2 pane revealed', !!st?.revealed)
  await sleep(2500) // lens follow-navigates to the account page
  const wid = env.activate()
  const clickX = Math.round(st.termBounds.width + st.pageBounds.width / 2)
  env.x('mousemove', '--window', wid, String(clickX), '350')
  env.x('click', '1') // browser mode + hits the half-screen Log in button
  await sleep(2000)
  env.shot('p5-logged-in-pane')

  // Acceptance is "reader + LIVE PANE": assert the live lens page itself rendered the
  // signed-in subscriber prose after the in-pane login — not only the reader mirror.
  const preLens = await lensText(env, term, 'account.html', 'Signed in as Wire Reader')
  check('P5.2c LIVE PANE renders the signed-in subscriber prose after in-pane login',
    preLens.ok && preLens.txt.replace(/\s+/g, ' ').includes('the account desk kept meticulous notes'),
    preLens.ok ? '' : preLens.txt.slice(-260))

  console.log('\n== P5: quit and relaunch — same partition, fresh app data ==')
  env.key('Escape') // back to reader mode so 'q' reaches the TUI
  await sleep(600)
  env.type('q')
  let gone = false
  for (let i = 0; i < 20; i++) { await sleep(500); if (shell.exitCode !== null) { gone = true; break } }
  check('P5.3 app quit cleanly', gone)

  // Fresh XDG (no app-side cache can serve the stale signed-out copy); SAME partition.
  const freshXdg = mkdtempSync(path.join(os.tmpdir(), 'wc-gate-p5-xdg2-'))
  mkdirSync(freshXdg, { recursive: true })
  env.extraEnv.XDG_DATA_HOME = freshXdg
  shell = env.launchShell()
  term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('P5.4 TUI rebooted', (await pollTermText(term, 'Go to URL', 30000)).ok)
  await openAccount(term)
  const signedIn = await pollTermText(term, 'Signed in as Wire Reader', 40000)
  check('P5.5 session SURVIVED the restart: reader shows the signed-in page', signedIn.ok,
    signedIn.ok ? '' : signedIn.txt.slice(-300))
  await sleep(400)
  env.shot('p5-signed-in-after-restart')

  // The reader proved persistence; now prove the LIVE PANE also renders the signed-in
  // prose AFTER the restart — the full "reader + live pane, survives restart" acceptance,
  // credential-free (the session lives in the browser partition, not app-side cache).
  env.activate(); await sleep(300)
  env.type('|')
  let st2 = null
  for (let i = 0; i < 30; i++) { st2 = await term.eval('window.__wc.state()').catch(() => null); if (st2?.revealed) break; await sleep(500) }
  check('P5.6 pane revealed after restart', !!st2?.revealed)
  const postLens = await lensText(env, term, 'account.html', 'Signed in as Wire Reader')
  check('P5.7 LIVE PANE shows the signed-in prose AFTER restart (session persisted in the partition)',
    postLens.ok && postLens.txt.replace(/\s+/g, ' ').includes('the account desk kept meticulous notes'),
    postLens.ok ? '' : postLens.txt.slice(-260))
  env.shot('p5-live-pane-signed-in-after-restart')
  term.close()
} catch (err) {
  console.error('GATE ERROR:', err)
  check('P5 gate completed without error', false, String(err))
  try { env.shot('p5-error') } catch {}
} finally {
  env.down()
  fixture.close()
}
process.exit(summary())
