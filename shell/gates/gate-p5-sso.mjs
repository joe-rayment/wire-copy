// P5 SSO gate (workspace-y0bi, popup-routing slice): a REAL window.open popup (the SSO
// shape) routes to an owned TEMP PANE inside the ONE window — window.opener/postMessage
// work across it, the provider's own window.close() tears it down, Esc dismisses it,
// and the signed-in outcome lands back on the opener page. Zero external credentials.
// Run from shell/: node gates/gate-p5-sso.mjs
import http from 'node:http'
import { execFileSync } from 'node:child_process'
import { Env, Cdp, targets, attach, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

const FPORT = 8137
const FILLER = Array.from({ length: 8 }, (_, i) =>
  `<p>Account section ${i + 1}: the desk reconciled the day's sign-ins against the visitor ledger, noting which ` +
  'credentials arrived by the front door and which came through the side gate before the evening close.</p>').join('\n')
const fixture = http.createServer((req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  if (req.url.startsWith('/sso.html')) {
    res.end(`<!doctype html><meta charset="utf-8"><title>SSO Provider</title><body>
<h1>SSO PROVIDER</h1>
<button id="approve" style="display:block;width:100%;height:70vh;font-size:30px">Approve sign-in</button>
<script>document.getElementById('approve').onclick = () => {
  window.opener.postMessage({ wcToken: 'tok-42' }, '*')
  setTimeout(() => window.close(), 80)
}</script>`)
    return
  }
  const signedIn = /(^|;\s*)sid=sso-ok(;|$)/.test(req.headers.cookie || '')
  // The status paragraphs are LONG on purpose: the reader's content-quality filters
  // drop one-line paragraphs, and the gate asserts on this text in the reader.
  const statusLine = signedIn
    ? 'SIGNED IN AS WIRE READER via SSO. The account desk records the provider handshake in the visitor ledger and keeps the reading room open for the rest of the evening shift without asking again.'
    : 'SIGNED OUT. Use the provider to continue. The account desk keeps this notice pinned above the ledger so every visitor knows the reading room requires a provider sign-in before the archive opens.'
  res.end(`<!doctype html><meta charset="utf-8"><title>Account Wire</title><body><article>
<h1>Account Wire</h1>
<p id="status">${statusLine}</p>
${signedIn ? '' : `<button id="sso" style="display:block;width:100%;height:45vh;font-size:28px">Sign in with Provider</button>
<script>
document.getElementById('sso').onclick = () => window.open('/sso.html', 'wcsso', 'width=480,height=560,popup')
window.addEventListener('message', e => {
  if (e.data && e.data.wcToken) { document.cookie = 'sid=sso-ok; max-age=86400; path=/'; location.reload() }
})
</script>`}
${FILLER}</article>`)
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))
const appUrl = `http://127.0.0.1:${FPORT}/app.html`

buildTui('Release')
const env = new Env({ display: ':95', cdpPort: 9261 })
await env.up()
const shellProc = env.launchShell()

async function findTargetUrl (urlPart, timeoutMs = 15000) {
  const t0 = Date.now()
  while (Date.now() - t0 < timeoutMs) {
    const list = await targets(env.cdpPort).catch(() => [])
    const hit = list.find(t => t.type === 'page' && t.url.includes(urlPart))
    if (hit) return hit
    await sleep(400)
  }
  return null
}

const raced = (p, ms, fb) => Promise.race([p, sleep(ms).then(() => fb)])
try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('S.0 TUI booted', (await pollTermText(term, 'Go to URL', 30000)).ok)
  env.activate(); await sleep(300)
  env.type('o'); await sleep(400)
  env.type(appUrl); await sleep(300)
  env.key('Return')
  const s1 = await pollTermText(term, 'SIGNED OUT', 40000)
  check('S.1 reader shows the SIGNED-OUT account page', s1.ok, s1.ok ? '' : s1.txt.replace(/\s+/g, ' ').slice(-260))
  env.shot('sso-s1-reader')

  console.log('\n== S: open the page in the pane (o), launch the SSO popup ==')
  await sleep(2000)
  env.type('o') // opens app.html in the pane, browser mode (workspace-ai4c flow)
  let st = null
  for (let i = 0; i < 30; i++) { st = await term.eval('window.__wc.state()'); if (st.mode === 'browser' && st.revealed) break; await sleep(500) }
  check('S.2 pane open in browser mode', st?.mode === 'browser' && st?.revealed)

  // Click the REAL button rect (computed from the LENS page — matched by URL + pane
  // width, never the 1280 fetch page whose socket can detach mid-eval and hang a raw
  // CDP send). Fresh connection per click; every eval raced against a timeout.
  const wid = env.activate()
  async function clickLensButton (id) {
    const t0 = Date.now()
    while (Date.now() - t0 < 25000) {
      const list = await targets(env.cdpPort).catch(() => [])
      for (const t of list.filter(t => t.type === 'page' && t.url.includes('/app.html'))) {
        const c = await Cdp.connect(t.webSocketDebuggerUrl).catch(() => null)
        if (!c) continue
        const w = await raced(c.eval('innerWidth').catch(() => -1), 3000, -1)
        if (Math.abs(w - st.pageBounds.width) <= 2) {
          const r = await raced(c.eval(`(() => { const b = document.getElementById('${id}'); if (!b) return null; const r = b.getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 } })()`).catch(() => null), 3000, null)
          c.close()
          if (r) {
            env.x('mousemove', '--window', wid, String(Math.round(st.termBounds.width + r.x)), String(Math.round(r.y)))
            env.x('click', '1')
            return true
          }
        } else {
          c.close()
        }
      }
      await sleep(500)
    }
    return false
  }
  check('S.2b SSO button located on the lens', await clickLensButton('sso'))
  let stPop = null
  for (let i = 0; i < 20; i++) { stPop = await raced(term.eval('window.__wc.state()'), 4000, stPop); if (stPop?.popupOpen) break; await sleep(500) }
  check('S.3 popup PANE opened (no OS window)', !!stPop?.popupOpen, JSON.stringify(stPop?.popupBounds))
  const ssoT = await findTargetUrl('/sso.html')
  check('S.4 popup is on the provider page', !!ssoT, ssoT?.url)
  const winCount = execFileSync('bash', ['-c', `DISPLAY=${env.display} xdotool search --name 'Wire Copy' | wc -l`]).toString().trim()
  check('S.5 still exactly ONE OS window', winCount === '1', `windows=${winCount}`)
  env.shot('sso-popup-open')

  console.log('\n== S: Esc dismisses; reopen; provider approves → opener signs in ==')
  env.key('Escape') // popup has focus (createWindow focused it); Esc closes the POPUP pane
  let stEsc = null
  for (let i = 0; i < 15; i++) { stEsc = await raced(term.eval('window.__wc.state()'), 4000, stEsc); if (stEsc && !stEsc.popupOpen) break; await sleep(400) }
  check('S.6 Esc closed the popup pane (mode stays browser)', stEsc.popupOpen === false && stEsc.mode === 'browser',
    `popupOpen=${stEsc.popupOpen} mode=${stEsc.mode}`)

  await clickLensButton('sso')
  for (let i = 0; i < 20; i++) { stPop = await raced(term.eval('window.__wc.state()'), 4000, stPop); if (stPop?.popupOpen) break; await sleep(500) }
  check('S.7 popup reopened', !!stPop?.popupOpen)
  // Click Approve in the POPUP (popupBounds are window-content coordinates).
  const pb = stPop?.popupBounds
  check('S.7b popup bounds available', !!pb, JSON.stringify(stPop))
  if (!pb) throw new Error('no popup bounds — cannot continue')
  // Diagnose-in-one-run probe: is the opener shim present, and does the click LAND?
  const ssoT2 = await findTargetUrl('/sso.html', 8000)
  const popC = ssoT2 ? await Cdp.connect(ssoT2.webSocketDebuggerUrl).catch(() => null) : null
  if (popC) {
    const shim = await raced(popC.eval('({ opener: !!window.opener, closeShim: window.close.toString().includes("wcpopup") ? "shimmed" : "native" })').catch(e => String(e)), 4000, 'eval-timeout')
    check('S.7c opener shim present in the popup', shim && shim.opener === true, JSON.stringify(shim))
    await raced(popC.eval('window.__clicked = false; document.addEventListener("mousedown", () => { window.__clicked = true }, true); 1').catch(() => null), 4000, null)
  }
  await sleep(600) // popup scripts settle before the approve press
  env.x('mousemove', '--window', wid, String(Math.round(pb.x + pb.width / 2)), String(Math.round(pb.y + pb.height * 0.6)))
  env.x('click', '1')
  if (popC) {
    await sleep(1200)
    // A hung/closed eval here usually means the approve click WORKED (the popup tore
    // itself down mid-eval) — disambiguate with the shell's own popup state.
    const landed = await raced(popC.eval('window.__clicked === true').catch(() => 'gone'), 4000, 'closed-during-eval')
    const stNow = await raced(term.eval('window.__wc.state()'), 4000, null)
    check('S.7d approve click LANDED (flag, or popup already closed)',
      landed === true || stNow?.popupOpen === false, `landed=${landed} popupOpen=${stNow?.popupOpen}`)
    popC.close()
  }
  let closed = false
  for (let i = 0; i < 25; i++) { const s = await raced(term.eval('window.__wc.state()'), 4000, null); if (s && !s.popupOpen) { closed = true; break } await sleep(400) }
  check('S.8 provider window.close() tore the popup down', closed)

  // The OPENER page must now be signed in (postMessage → cookie → reload).
  let signedIn = false
  for (let i = 0; i < 25 && !signedIn; i++) {
    const appT = await findTargetUrl('/app.html', 2000)
    if (appT) {
      const c = await Cdp.connect(appT.webSocketDebuggerUrl).catch(() => null)
      if (c) {
        signedIn = await raced(
          c.eval('document.body.innerText.includes("SIGNED IN AS WIRE READER")').catch(() => false), 3000, false)
        c.close()
      }
    }
    if (!signedIn) await sleep(500)
  }
  check('S.9 opener page signed in via the popup postMessage flow', signedIn)
  env.shot('sso-signed-in')
  term.close()
} catch (err) {
  console.error('GATE ERROR:', err)
  check('SSO gate completed without error', false, String(err))
  try { env.shot('sso-error') } catch {}
} finally {
  console.error('--- shell stderr tail ---')
  console.error((shellProc.stderrText || '').split('\n').filter(l => l.trim()).slice(-25).join('\n'))
  env.down()
  fixture.close()
}
process.exit(summary())
