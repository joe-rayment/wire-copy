// P5 gate (workspace-y0bi, credential-free slice): login/session persistence.
// A fixture "account" page renders SIGNED OUT until a login button (clicked in the
// pane, browser mode) sets a session cookie. The shell is then QUIT and relaunched
// with the SAME browser partition but a FRESH app data dir (no app cache): the reader
// must render the SIGNED-IN copy — proving the login session lives in the partition
// and survives an app restart, with zero external credentials.
// Run from shell/: node gates/gate-p5.mjs
import http from 'node:http'
import { mkdtempSync, mkdirSync } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { Env, attach, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

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
