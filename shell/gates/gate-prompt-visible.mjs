// ':' / '/' prompt visibility gate (workspace-up3zx).
//
// GUARDS: the launcher and the link-view are HTML panes drawn OVER the terminal. The ':' command
// prompt (and '/' search) is drawn by the TUI on the terminal's bottom row — i.e. UNDER the pane,
// so the user typed ':help' and saw nothing. The TUI now mirrors the open prompt into the pane's
// state (state.promptLine = { prefix, buffer, cursor }) and each pane paints its own prompt row in
// place of its key-hint footer. This gate proves the user can SEE what they type, in both panes,
// and that closing the prompt brings the footer back.
//
// DRIVES (real X keys via xdotool, a real Electron shell + the live .NET TUI, headful Xvfb :93):
//   (a) launcher:  ':' then 'hel'  -> the launcher pane's #promptline is visible and reads ":hel",
//                  the hint footer is hidden; Escape -> prompt hidden, footer back.
//   (b) link view: open a fixture page ('o' + URL on the launcher), ':' then 'hel' -> the link-tree
//                  pane's #promptline reads ":hel"; Escape -> gone, footer back.
//   (c) link view: ':help' + Return still RUNS the command (the colon-command reference opens).
// Screenshots (gates/out/prompt-visible-*.png) are the artifact a human reads: ":hel" must be seen
// painted in the bottom row of each pane.
//
// Run from shell/:  node gates/gate-prompt-visible.mjs     (Xvfb display :93, CDP port 9393)
import http from 'node:http'
import { Env, attach, Cdp, findTarget, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

buildTui('Release')

// A small link-list page: one section of stories, so ':open <url>' / 'o' lands in the link view.
const FPORT = 8193
const WORDS = ['one', 'two', 'three', 'four', 'five', 'six']
const story = (w, i) =>
  `<li><a href="/a${i}.html">Dispatch ${w}: a headline long enough to read as a story link number ${i}</a></li>`
const fixture = http.createServer((req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  if (/^\/a\d+\.html/.test(req.url)) {
    res.end(`<!doctype html><meta charset="utf-8"><title>Story ${req.url}</title><body><article>` +
      `<h1>Story ${req.url}</h1><p>${'Readable body copy for the reader view. '.repeat(30)}</p></article>`)
    return
  }
  res.end(`<!doctype html><meta charset="utf-8"><title>Column Wire</title><body><h1>Column Wire</h1>
<section><h2>Dispatches</h2><ul>${WORDS.map(story).join('\n')}</ul></section>`)
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))
const base = `http://127.0.0.1:${FPORT}`

const env = new Env({ display: ':93', cdpPort: 9393 })
await env.up()
const shell = env.launchShell()

// ── Probes evaluated INSIDE the real pane DOM ───────────────────────────────────────────
// What the user sees in the pane's bottom row: is #promptline actually displayed, with a box
// inside the viewport, what does it read, is the hint footer hidden. (The state hook differs
// per pane, so the pane's pushed promptLine is read separately.)
const DOM_JS = `(function(){
  const pl=document.getElementById('promptline'), row=document.getElementById('footer-row');
  if(!pl) return {exists:false};
  const r=pl.getBoundingClientRect(), cs=getComputedStyle(pl), rowCs=getComputedStyle(row);
  return {exists:true, display:cs.display,
    visible: cs.display!=='none' && r.width>0 && r.height>0 && r.bottom<=window.innerHeight+1 && r.top>=0,
    text: pl.textContent, typed:[...pl.querySelectorAll('.typed')].map(e=>e.textContent).join(''),
    prefix:(pl.querySelector('.prefix')||{}).textContent||'', cursors: pl.querySelectorAll('.cursor').length,
    rowDisplay: rowCs.display, rowVisible: rowCs.display!=='none' && row.getBoundingClientRect().height>0,
    bottom: Math.round(r.bottom), winH: window.innerHeight};
})()`

// One pane under test: how to read its pushed promptLine and how to inspect its DOM.
const paneOps = (pane, hook) => ({
  prompt: async () => (await pane.eval(`(function(){const m=window.${hook}();return m&&m.state?(m.state.promptLine||null):null})()`).catch(() => null)),
  dom: () => pane.eval(DOM_JS)
})

async function waitUntil (fn, ms = 6000) {
  const t0 = Date.now()
  while (Date.now() - t0 < ms) { const v = await fn(); if (v) return v; await sleep(250) }
  return null
}
// xdotool drops keys under load: re-press until the pane PROVES the prompt is open.
async function openPrompt (ops) {
  for (let i = 0; i < 4; i++) {
    env.type(':'); await sleep(500)
    const p = await waitUntil(() => ops.prompt(), 2500)
    if (p) return p
    env.key('Escape'); await sleep(500)
  }
  return null
}
// Type `want` into the open prompt, repairing dropped keys by watching the pane's own buffer.
async function typeBuffer (ops, want) {
  for (let i = 0; i < 5; i++) {
    const p = await ops.prompt()
    const cur = p ? p.buffer : ''
    if (cur === want) return true
    if (want.startsWith(cur)) env.type(want.slice(cur.length))
    else for (let n = 0; n < cur.length; n++) env.key('BackSpace')
    await sleep(500)
  }
  return (await ops.prompt())?.buffer === want
}
async function closePrompt (ops) {
  for (let i = 0; i < 3; i++) {
    env.key('Escape'); await sleep(600)
    if (await waitUntil(async () => (await ops.prompt()) === null, 2000)) return true
  }
  return false
}

let la = null
let lt = null
try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('P0 shell booted to the launcher', (await pollTermText(term, 'Go to URL', 30000)).ok)
  const wid = env.activate(); await sleep(500)
  const l0 = await findTarget(env.cdpPort, t => t.url.includes('launcher.html'), 20000, 'launcher pane')
  la = await Cdp.connect(l0.webSocketDebuggerUrl)
  for (let i = 0; i < 40 && !(await la.eval('!!window.__launcherReady').catch(() => false)); i++) await sleep(250)
  const st0 = await term.eval('window.__wc.state()')
  check('P0b the launcher pane is the thing on screen (it covers the terminal)', st0.launcherShown === true,
    `launcherShown=${st0.launcherShown}`)

  // ── (a) LAUNCHER ──────────────────────────────────────────────────────────────────────
  const lo = paneOps(la, '__launcherState')
  const idle = await lo.dom()
  check('A0 idle launcher: no prompt row, the key-hint footer is showing',
    idle.exists && !idle.visible && idle.rowVisible, JSON.stringify(idle))
  const lp = await openPrompt(lo)
  check('A1 pressing ":" on the launcher opens the prompt (pane receives promptLine with prefix ":")',
    !!lp && lp.prefix === ':', JSON.stringify(lp))
  const typedOk = await typeBuffer(lo, 'hel')
  await sleep(500)
  const dA = await lo.dom()
  check('A2 launcher #promptline is VISIBLE (displayed, inside the pane) and reads ":hel"',
    typedOk && dA.visible && dA.text.trim() === ':hel' && dA.prefix === ':' && dA.typed === 'hel',
    JSON.stringify(dA))
  check('A3 the prompt row has a block cursor and REPLACES the hint footer (hint row hidden)',
    dA.cursors === 1 && !dA.rowVisible, `cursors=${dA.cursors} rowDisplay=${dA.rowDisplay}`)
  env.shotWindow('prompt-visible-launcher', wid)
  check('A4 Escape closes the prompt: #promptline hidden, key-hint footer back',
    await closePrompt(lo) && await waitUntil(async () => { const d = await lo.dom(); return !d.visible && d.rowVisible }, 3000) !== null,
    JSON.stringify(await lo.dom()))

  // ── (b) LINK VIEW ─────────────────────────────────────────────────────────────────────
  // 'o' on the launcher opens the URL bar (the reader's 'o' would open the live browser).
  let ltTarget = null
  for (let attempt = 0; attempt < 3 && !ltTarget; attempt++) {
    if (attempt) { env.key('Escape'); await sleep(700) }
    env.key('o'); await sleep(900)
    env.type('http:/'); await sleep(200); env.type(base.slice('http:/'.length) + '/'); await sleep(500)
    env.key('Return')
    ltTarget = await findTarget(env.cdpPort, t => t.url.includes('linktree.html'), 15000, 'link-tree pane').catch(() => null)
  }
  check('B0 opening the fixture page created the link-tree pane', !!ltTarget)
  lt = await Cdp.connect(ltTarget.webSocketDebuggerUrl)
  for (let i = 0; i < 60 && !(await lt.eval('!!window.__linkTreeReady && window.__linkTreeState().state.items.length > 0').catch(() => false)); i++) await sleep(250)
  const stateLt = await lt.eval('JSON.stringify(window.__linkTreeState())')
  check('B1 the link-tree pane shows the fixture stories',
    /Dispatch one/.test(stateLt), `items=${(JSON.parse(stateLt).state.items || []).length}`)
  await waitUntil(async () => (await term.eval('window.__wc.state()')).linkTreeShown === true, 20000)
  await sleep(2500) // let the dock/reveal transition settle before sampling or screenshotting
  const st1 = await term.eval('window.__wc.state()')
  check('B2 the link-tree pane is SHOWN over the terminal', st1.linkTreeShown === true, `linkTreeShown=${st1.linkTreeShown}`)

  const to = paneOps(lt, '__linkTreeState')
  const idleLt = await to.dom()
  check('B3 idle link view: no prompt row, the footer is showing',
    idleLt.exists && !idleLt.visible && idleLt.rowVisible, JSON.stringify(idleLt))
  const tp = await openPrompt(to)
  check('B4 pressing ":" in the link view opens the prompt (pane receives promptLine with prefix ":")',
    !!tp && tp.prefix === ':', JSON.stringify(tp))
  const typedLt = await typeBuffer(to, 'hel')
  await sleep(500)
  const dB = await to.dom()
  check('B5 link-tree #promptline is VISIBLE (displayed, inside the pane) and reads ":hel"',
    typedLt && dB.visible && dB.text.trim() === ':hel' && dB.prefix === ':' && dB.typed === 'hel',
    JSON.stringify(dB))
  check('B6 the prompt row has a block cursor and REPLACES the footer row',
    dB.cursors === 1 && !dB.rowVisible, `cursors=${dB.cursors} rowDisplay=${dB.rowDisplay}`)
  env.shotWindow('prompt-visible-linktree', wid)
  check('B7 Escape closes the prompt: #promptline hidden, footer back',
    await closePrompt(to) && await waitUntil(async () => { const d = await to.dom(); return !d.visible && d.rowVisible }, 3000) !== null,
    JSON.stringify(await to.dom()))

  // ── (c) the command still RUNS from the link view ─────────────────────────────────────
  let ref = { ok: false }
  for (let attempt = 0; attempt < 3 && !ref.ok; attempt++) {
    if (attempt) { await closePrompt(to); await sleep(400) }
    if (!(await openPrompt(to))) continue
    if (!(await typeBuffer(to, 'help'))) continue
    env.key('Return')
    ref = await pollTermText(term, 'site layout chooser', 10000)
  }
  check('C1 ":help" + Return from the link view still runs the command (colon-command reference opened)', ref.ok)
  await sleep(800)
  const afterHelp = await to.prompt()
  check('C2 after Return the link-tree pane no longer carries a prompt', afterHelp === null, JSON.stringify(afterHelp))
  env.shotWindow('prompt-visible-after-help', wid)
} catch (e) {
  console.log('GATE ERROR:', e)
  check('gate completed without error', false, String(e))
  try { env.shot('prompt-visible-error') } catch {}
} finally {
  if (process.env.GATE_DEBUG) console.log(shell.stderrText.slice(-4000))
  la?.close(); lt?.close()
  env.down()
  fixture.close()
}
process.exit(summary() ? 1 : 0)
