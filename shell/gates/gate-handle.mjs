// Drawer-handle gate (workspace-2dmuu): proves the browser sidecar's PULL — a real bar
// the user can see and a REAL OS mouse click that opens and closes the drawer.
//
// Per the project Verification Doctrine every check drives the user's actual action and
// asserts the user-visible OUTCOME, never presence or wiring:
//   - the bar is ABSENT on the launcher (no page to pull out, and '|' is unbound there,
//     so a click must not be able to type into the URL bar);
//   - once a page exists the bar is on screen, and its rect says which way it runs;
//   - an xdotool click AT THE BAR'S OWN COORDINATES closes the drawer — asserted by the
//     pane geometry moving, not by "a message was sent";
//   - the shell stays in reader mode with the terminal focused across the click (a
//     click on the handle must never be mistaken for a deliberate click into the page);
//   - mid-flight the bar TRAVELS and ROTATES, sampled off the live composited matrix;
//   - a second click reverses it and the bar re-orients back;
//   - the toggle goes through the TUI, so the link-tree pane's own "⇉ docked" badge
//     agrees with what the drawer did — a shell-local flip would desync it.
// Headful under Xvfb ONLY (never headless). Run from shell/: node gates/gate-handle.mjs
import http from 'node:http'
import fs from 'node:fs'
import path from 'node:path'
import { Env, attach, Cdp, findTarget, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

buildTui('Release')

// Fixture: a link list, so the lens gets a real page (the handle's availability rule).
const FPORT = 8171
const WORDS = ['one', 'two', 'three', 'four', 'five', 'six']
const fixture = http.createServer((req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  if (/^\/a\d+\.html/.test(req.url)) {
    res.end(`<!doctype html><meta charset="utf-8"><title>Story ${req.url}</title><body>` +
      `<article><h1>Story ${req.url}</h1><p>${'Readable body copy. '.repeat(60)}</p></article>`)
    return
  }
  // The key recorder is how the browser-mode leg proves the PAGE still owns the
  // keyboard — a focus flag can read false while nobody holds focus, which says
  // nothing about where a keystroke would land.
  res.end(`<!doctype html><meta charset="utf-8"><title>Drawer Wire</title><body>
<h1>Drawer Wire</h1>
<input id="probe" style="position:fixed;left:8px;bottom:24px;width:60%;height:48px;font-size:20px"><section><h2>Dispatches</h2><ul>
${WORDS.map((w, i) => `<li><a href="/a${i + 1}.html">Dispatch ${w}: a headline for the drawer-handle gate</a> <span class="byline">Ada Lovelace</span> <time datetime="2026-07-19T08:00:00Z">Jul 19</time></li>`).join('\n')}
</ul></section>`)
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))
const listUrl = `http://127.0.0.1:${FPORT}/`

const env = new Env({ display: ':87', cdpPort: 9297, screen: '1800x1100x24' })
fs.writeFileSync(path.join(env.extraEnv.WIRECOPY_SHELL_USERDATA, 'window-bounds.json'),
  JSON.stringify({ x: 40, y: 40, width: 1360, height: 900 }))
await env.up()
const shell = env.launchShell()

// Where the bar actually is, in ROOT coordinates, from the shell's own numbers.
// contentBounds is Electron's content rect in SCREEN coordinates, so this needs no
// guesswork about the window manager's border.
const barPoint = st => ({
  x: st.contentBounds.x + st.handleBounds.x + Math.round(st.handleBounds.width / 2),
  y: st.contentBounds.y + st.handleBounds.y + Math.round(st.handleBounds.height / 2)
})

async function settled (term, timeoutMs = 6000) {
  const t0 = Date.now()
  let st = null
  while (Date.now() - t0 < timeoutMs) {
    st = await term.eval('window.__wc.state()')
    if (st && !st.paneAnimating && !st.overlayShown) return st
    await sleep(120)
  }
  return st
}

let ov = null
let lt = null
let hp = null
try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('H0 shell booted to the launcher', (await pollTermText(term, 'Go to URL', 30000)).ok)
  env.activate(); await sleep(600)

  // ── (a) The launcher: NO bar. Two independent reasons it must be absent — the
  //    drawer has nothing to show, and '|' would be typed into the URL bar. ──────────
  const stLauncher = await term.eval('window.__wc.state()')
  check('H1 no rail on the launcher — the drawer holds no page yet',
    stLauncher.handleShown === false,
    `handleShown=${stLauncher.handleShown} lens=${stLauncher.pageBounds && stLauncher.pageBounds.x}`)
  check('H1b with no rail up the terminal is FULL width (the boot contract gate-p1 pins)',
    stLauncher.termBounds.width === stLauncher.contentSize[0],
    `term.w=${stLauncher.termBounds.width} content.w=${stLauncher.contentSize[0]}`)
  const wid = env.activate()
  env.shotWindow('handle-a-launcher', wid)

  // ── (b) Load a list. The sidecar auto-reveals on the first list, so the drawer is
  //    OPEN and the bar must be horizontal over the page pane. ──────────────────────
  env.key('o'); await sleep(700)
  env.type(listUrl); await sleep(400)
  env.key('Return')
  check('H2 TUI rendered the fixture link list', (await pollTermText(term, 'Dispatch one', 40000)).ok)
  let st = null
  for (let i = 0; i < 50; i++) { st = await term.eval('window.__wc.state()'); if (st?.revealed && st.handleShown) break; await sleep(400) }
  st = await settled(term, 8000)
  check('H3 with a page loaded and the drawer open, the rail is HORIZONTAL',
    st.handleShown === true && st.handleOrient === 'horizontal' &&
    st.handleBounds.width > st.handleBounds.height,
    `shown=${st.handleShown} orient=${st.handleOrient} bounds=${JSON.stringify(st.handleBounds)}`)
  check('H4 the open rail spans the FULL WIDTH of the browser panel, along its top edge',
    st.handleBounds.y === 0 &&
    st.handleBounds.x === st.pageBounds.x &&
    st.handleBounds.width === st.pageBounds.width,
    `rail=${JSON.stringify(st.handleBounds)} page=${JSON.stringify(st.pageBounds)}`)
  check('H5 the live page keeps its FULL height — the rail floats, it does not push it down',
    st.pageBounds.y === 0 && st.pageBounds.height === st.contentSize[1],
    `page=${JSON.stringify(st.pageBounds)} content=${JSON.stringify(st.contentSize)}`)
  // The label is the point of the new shape: assert the TEXT and that it is laid out
  // with real extent, not just present in the DOM.
  hp = await Cdp.connect((await findTarget(env.cdpPort, x => x.url.includes('handle.html'), 15000, 'handle pane')).webSocketDebuggerUrl)
  let hIn = JSON.parse(await hp.eval('JSON.stringify(window.__handle())'))
  check('H5b the open rail says what a click will DO, with its shortcut, reading across',
    /hide live page/i.test(hIn.label) && /\[\|\]/.test(hIn.label) &&
    hIn.labelRect.w > hIn.labelRect.h && hIn.labelRect.w >= 60,
    `label="${hIn.label}" rect=${JSON.stringify(hIn.labelRect)}`)
  check('H5c the phosphor edge runs the rail\'s whole length, on its underside',
    hIn.stroke.w === hIn.view.w && hIn.stroke.h <= 4 && hIn.stroke.y >= hIn.view.h - 4,
    `stroke=${JSON.stringify(hIn.stroke)} view=${JSON.stringify(hIn.view)}`)
  env.shotWindow('handle-b-open', wid)

  // ── (c) THE PRIMARY ACTION: a real mouse click on the bar closes the drawer ───────
  const openPt = barPoint(st)
  const modeBefore = st.mode
  env.click(openPt.x, openPt.y)
  let closed = null
  for (let i = 0; i < 40; i++) { closed = await term.eval('window.__wc.state()'); if (closed && closed.revealed === false) break; await sleep(200) }
  check('H6 a REAL mouse click on the bar CLOSED the drawer (pane geometry moved)',
    closed && closed.revealed === false,
    `clicked (${openPt.x},${openPt.y}) → revealed=${closed && closed.revealed}`)
  const stC = await settled(term, 8000)
  // "Full width" now means the whole window bar the rail's own strip — the rail is up
  // here, and it sits BESIDE the terminal rather than over it (H8b).
  check('H7 the drawer is really shut: page parked off-screen, reader back to full width',
    stC.paneShown === false && stC.pageBounds.x === stC.contentSize[0] &&
    stC.termBounds.width === stC.contentSize[0] - stC.handleBounds.width,
    `page.x=${stC.pageBounds.x} term.w=${stC.termBounds.width} content.w=${stC.contentSize[0]} rail.w=${stC.handleBounds.width}`)
  check('H8 the rail RE-ORIENTED to vertical, running the FULL HEIGHT of the dock edge',
    stC.handleOrient === 'vertical' && stC.handleBounds.height === stC.contentSize[1] &&
    stC.handleBounds.y === 0 &&
    stC.handleBounds.x + stC.handleBounds.width === stC.contentSize[0],
    `orient=${stC.handleOrient} bounds=${JSON.stringify(stC.handleBounds)} content=${JSON.stringify(stC.contentSize)}`)
  check('H8b the shut rail owns its strip — the terminal stops short of it, never under it',
    stC.termBounds.width === stC.contentSize[0] - stC.handleBounds.width,
    `term.w=${stC.termBounds.width} content.w=${stC.contentSize[0]} rail.w=${stC.handleBounds.width}`)
  hIn = JSON.parse(await hp.eval('JSON.stringify(window.__handle())'))
  check('H8c the shut rail FLIPPED to the opposite action, still with its shortcut',
    /show live page/i.test(hIn.label) && !/hide/i.test(hIn.label) && /\[\|\]/.test(hIn.label) &&
    hIn.labelRect.h > hIn.labelRect.w && hIn.labelRect.h >= 60,
    `label="${hIn.label}" rect=${JSON.stringify(hIn.labelRect)}`)
  // The shortcut the rail advertises must be the one that actually works: press it and
  // the drawer opens, with no mouse involved.
  env.key('bar')
  let byKey = null
  for (let i = 0; i < 30; i++) { byKey = await term.eval('window.__wc.state()'); if (byKey && byKey.revealed) break; await sleep(200) }
  check('H8d the advertised [|] shortcut really opens the drawer',
    byKey && byKey.revealed === true, `revealed=${byKey && byKey.revealed}`)
  const stBack = await settled(term, 8000)
  hIn = JSON.parse(await hp.eval('JSON.stringify(window.__handle())'))
  check('H8e …and the rail flipped back to the hide wording',
    /hide live page/i.test(hIn.label), `label="${hIn.label}"`)
  env.key('bar'); await settled(term, 8000)
  check('H9 clicking the handle did NOT hand the keyboard to the page',
    stC.mode === 'reader' && modeBefore === 'reader' && stC.termFocused === true &&
    stC.handleFocused === false,
    `mode=${stC.mode} termFocused=${stC.termFocused} handleFocused=${stC.handleFocused}`)
  // …and the TUI really got the keystroke: type a key and watch the TUI react.
  env.key('question'); await sleep(1200)
  const helpUp = (await pollTermText(term, 'Keys', 6000)).ok || (await pollTermText(term, 'help', 3000)).ok
  check('H10 the terminal still takes keys immediately after the click', helpUp)
  env.key('Escape'); await sleep(600)
  env.shotWindow('handle-c-collapsed', wid)

  // ── (d) Click it open again, sampling the animation while it runs ────────────────
  const stV = await settled(term, 6000)
  const shutPt = barPoint(stV)
  ov = await Cdp.connect((await findTarget(env.cdpPort, t => t.url.includes('overlay.html'), 15000, 'overlay')).webSocketDebuggerUrl)
  env.click(shutPt.x, shutPt.y)
  // Sample for long enough to cover the whole chain — the click has to travel
  // pty → TUI → shell channel → beginTransition → capturePage → armed before a single
  // frame of the slide exists, which is several hundred ms after the button goes down.
  const samples = []
  let shot = false
  const t0 = Date.now()
  while (Date.now() - t0 < 3000) {
    const a = await ov.eval('JSON.stringify(window.__anim())').catch(() => null)
    if (a) {
      const p = JSON.parse(a)
      if (p && p.handle) samples.push(p.handle)
      // Shoot the frame a HUMAN would judge the animation by: mid-slide, with the bar
      // part-travelled and part-turned. Shooting on sample count instead catches the
      // prep phase, before the overlay is even up.
      if (!shot && p && p.phase === 'slide' && p.progress > 0.35 && p.progress < 0.7) {
        shot = true
        env.shotWindow('handle-d-mid-open', wid)
      }
    }
    if (shot && samples.length > 40 && Date.now() - t0 > 1500) break
    await sleep(4)
  }
  check('H10b a mid-slide frame was captured for visual review', shot, `samples=${samples.length}`)
  const cxs = samples.map(s => s.cx)
  const orients = samples.map(s => s.orient)
  check('H11 mid-flight the bar TRAVELS across the window (≥4 distinct positions)',
    new Set(cxs).size >= 4, `${samples.length} samples, ${new Set(cxs).size} distinct cx: ${cxs.slice(0, 8).join(',')}…`)
  check('H12 mid-flight the bar RE-ORIENTS (both orientations observed in one slide)',
    new Set(orients).size === 2, `orients seen: ${[...new Set(orients)].join(',')}`)
  check('H13 the bar never vanishes mid-flight (every sample has a rect)',
    samples.length >= 4 && samples.every(s => s.w > 0 && s.h > 0),
    `samples=${samples.length}`)

  const stO = await settled(term, 8000)
  check('H14 the second click re-OPENED the drawer and the bar is horizontal again',
    stO.revealed === true && stO.handleOrient === 'horizontal' &&
    stO.handleBounds.width > stO.handleBounds.height,
    `revealed=${stO.revealed} orient=${stO.handleOrient} bounds=${JSON.stringify(stO.handleBounds)}`)
  check('H15 nothing is left covering the window',
    stO.overlayShown === false && stO.paneAnimating === false,
    `overlayShown=${stO.overlayShown} animating=${stO.paneAnimating}`)
  env.shotWindow('handle-e-reopened', wid)

  // ── (e) The toggle went through the TUI, so the TUI's own chrome agrees ──────────
  const t = await findTarget(env.cdpPort, x => x.url.includes('linktree.html'), 15000, 'link-tree pane').catch(() => null)
  if (t) {
    lt = await Cdp.connect(t.webSocketDebuggerUrl)
    const badge = await lt.eval(`(document.querySelector('#footer-right .docked')||{}).textContent||''`)
    check('H16 the link-tree pane\'s own "⇉ docked" badge agrees with the open drawer',
      /docked/.test(badge), `badge="${badge}" revealed=${stO.revealed}`)
  } else {
    check('H16 link-tree pane available to read its docked badge', false, 'pane target not found')
  }

  // ── (f) Negatives: the bar is not a hole in the reader, and not a click-into-page ─
  const midReader = {
    x: stO.contentBounds.x + Math.round(stO.termBounds.width / 2),
    y: stO.contentBounds.y + Math.round(stO.contentSize[1] / 2)
  }
  env.click(midReader.x, midReader.y); await sleep(700)
  const stR = await term.eval('window.__wc.state()')
  check('H17 clicking the reader is still inert (mode stays reader)',
    stR.mode === 'reader' && stR.revealed === true,
    `mode=${stR.mode} revealed=${stR.revealed}`)

  // ── (g) BROWSER MODE — the case the handle invented and the one most likely to be
  //    skipped. The handle is the first always-live view that exists while the PAGE
  //    owns the keyboard, so an unguarded focus bounce here would yank the keyboard
  //    off the page the user is reading. ────────────────────────────────────────────
  // Aim the browser-mode click at the fixture's own text field. ONE click then does all
  // three things at once — enters browser mode, focuses the page view (setMode only
  // calls pageView.focus() on an actual mode CHANGE), and focuses the field. A second
  // click into the page cannot restore focus later: setMode short-circuits when the
  // mode is already 'browser', so nothing re-focuses the view.
  const rect = JSON.parse(await term.eval(
    `window.__wc.lensEval("JSON.stringify(document.getElementById('probe').getBoundingClientRect())")`))
  const pagePt = {
    x: stO.contentBounds.x + stO.pageBounds.x + Math.round(rect.x + (rect.width / 2)),
    y: stO.contentBounds.y + stO.pageBounds.y + Math.round(rect.y + (rect.height / 2))
  }
  // Poll for the settled owner exactly as gate-p4.mjs:132-140 does: the mode flips on
  // the mouseDown but Chromium's focus lands a beat later, and reading it once is a
  // race, not a measurement.
  env.click(pagePt.x, pagePt.y)
  let stB = null
  for (let i = 0; i < 15; i++) {
    stB = await term.eval('window.__wc.state()')
    if (stB.mode === 'browser' && stB.pageFocused) break
    await sleep(400)
  }
  check('H18 a click into the page still enters browser mode (unchanged doctrine)',
    stB.mode === 'browser' && stB.pageFocused === true,
    `mode=${stB.mode} pageFocused=${stB.pageFocused} termFocused=${stB.termFocused}`)
  check('H19 the bar is still there in browser mode, so the mouse can put the drawer away',
    stB.handleShown === true && stB.handleOrient === 'horizontal',
    `shown=${stB.handleShown} orient=${stB.handleOrient}`)
  // The precise regression this guards: the handle's focus/key handlers were first
  // written as a verbatim copy of the launcher / link-tree panes', which bounce focus
  // to the TERMINAL unconditionally. Those panes can only exist in reader mode, so the
  // unguarded form is safe for them; the handle is the first always-live view that
  // survives into BROWSER mode, where the page owns the keyboard. The defect's exact
  // signature is the terminal taking focus back while mode === 'browser' — so sample
  // the whole window in which the bar is on screen and require it never happens.
  // (Where a keystroke LANDS in browser mode is covered end-to-end by gate-p4's P4.8;
  // it needs a page-focused input this fixture has no way to restore, because setMode
  // short-circuits when the mode is already 'browser' and so never re-focuses the page.)
  // Asserted by where a KEYSTROKE LANDS, not by a focus flag. Click the fixture's own
  // input — that both enters browser mode and gives the field DOM focus, the way
  // gate-p4 drives its thief page — then type with NO CDP eval in between (every eval
  // against the term target pulls Chromium focus off the page, gate-p4.mjs:140-144).
  // Coordinates come from the lens's own client rect, offset by the pane origin.
  // The click→focus→type setup races Chromium's focus delivery, so retry the SETUP
  // (never the assertion) until the field is actually taking input. Observed flaky at
  // roughly one run in three; a check that fails at random teaches nothing.
  let typed = ''
  for (let attempt = 0; attempt < 3 && typed !== 'zz'; attempt++) {
    if (attempt > 0) {
      // Re-clicking alone cannot restore focus: setMode short-circuits when the mode is
      // already 'browser', so nothing re-focuses the view. Esc back to reader first, so
      // the next click is a real mode CHANGE and grants the page focus again.
      env.key('Escape'); await sleep(500)
      await term.eval(`window.__wc.lensEval("document.getElementById('probe').value = ''; 1")`)
      env.click(pagePt.x, pagePt.y)
      await sleep(800)
    }
    env.type('zz')
    await sleep(700)
    typed = await term.eval(`window.__wc.lensEval("document.getElementById('probe').value")`)
  }
  stB = await term.eval('window.__wc.state()')
  check('H20 the bar does not steal the keyboard — typing still lands on the PAGE',
    typed === 'zz' && stB.mode === 'browser' && stB.termFocused === false && stB.handleFocused === false,
    `typed="${typed}" mode=${stB.mode} termFocused=${stB.termFocused} handleFocused=${stB.handleFocused}`)
  const barPtB = barPoint(stB)
  env.click(barPtB.x, barPtB.y)
  let stB2 = null
  for (let i = 0; i < 40; i++) { stB2 = await term.eval('window.__wc.state()'); if (stB2 && stB2.revealed === false) break; await sleep(200) }
  check('H21 clicking the bar in BROWSER mode closes the drawer and returns the reader',
    stB2 && stB2.revealed === false && stB2.mode === 'reader',
    `revealed=${stB2 && stB2.revealed} mode=${stB2 && stB2.mode}`)
  const stB3 = await settled(term, 8000)
  check('H22 …and the terminal has the keyboard again immediately after',
    stB3.termFocused === true && stB3.handleOrient === 'vertical',
    `termFocused=${stB3.termFocused} orient=${stB3.handleOrient}`)
  env.shotWindow('handle-f-browser-mode-closed', wid)

  const crashed = /Uncaught|TypeError|ReferenceError|A JavaScript error occurred in the main process/.test(shell.stderrText)
  check('Z0 no main-process crash in stderr', !crashed,
    crashed ? shell.stderrText.split('\n').filter(l => /error|Error/.test(l)).slice(-3).join(' | ') : '')
} catch (err) {
  console.error('GATE ERROR:', err)
  check('gate completed without error', false, String(err))
  try { env.shotWindow('handle-error', env.activate()) } catch {}
} finally {
  if (ov) ov.close()
  if (lt) lt.close()
  if (hp) hp.close()
  env.down()
  try { fixture.close() } catch {}
}
process.exit(summary())
