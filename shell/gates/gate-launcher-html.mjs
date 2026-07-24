// L6 launcher HTML-pane gate (workspace-k1spg): proves the REAL launcher WebContentsView
// (shell/launcher/launcher.html) — driven end-to-end by the live .NET TUI over the shell
// channel — renders the user's launcher and reacts correctly to REAL keystrokes. Every
// check asserts a USER-VISIBLE OUTCOME on the actual pane's DOM/computed styles, not a
// proxy (per the project Verification Doctrine): the pane is visible with real cards, the
// selection fill MOVES when the user presses a key, the URL echo flows in and clears, a
// bookmark whose URL is a LOCAL fixture server actually navigates the live page pane, the
// launcher returns, and Ctrl+= zooms the pane. Headful under Xvfb ONLY (never headless).
// Run from shell/: node gates/gate-launcher-html.mjs
import http from 'node:http'
import { mkdirSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { Env, attach, targets, Cdp, findTarget, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

// ── Local fixture: card 1 points here, so "Enter opens" proves a REAL navigation to a
//    URL we control (not "command sent"). The page carries a marker we can read back. ──
const FIX_PORT = 8199
const FIX_URL = `http://127.0.0.1:${FIX_PORT}/`
const fixture = http.createServer((req, res) => {
  res.writeHead(200, { 'Content-Type': 'text/html' })
  res.end('<!doctype html><html><head><title>WC FIXTURE</title></head>' +
    '<body style="background:#111;color:#0f0"><h1 id="fx">WIRE COPY FIXTURE PAGE</h1>' +
    `<p>path: ${req.url}</p></body></html>`)
})
await new Promise(r => fixture.listen(FIX_PORT, '127.0.0.1', r))

buildTui('Release')

const env = new Env({ display: ':81', cdpPort: 9291 })

// ── Pre-seed bookmarks.json BEFORE launch so card 1 = our fixture. The reconciler treats
//    an existing user config as the source of truth (array order = card order); index 0 is
//    launcher card 1. Path: $XDG_DATA_HOME/WireCopy/bookmarks.json. ──
const dataDir = path.join(env.extraEnv.XDG_DATA_HOME, 'WireCopy')
mkdirSync(dataDir, { recursive: true })
writeFileSync(path.join(dataDir, 'bookmarks.json'), JSON.stringify({
  version: 1,
  bookmarks: [
    { name: 'FIXTURE HOME', url: FIX_URL },
    { name: 'SECOND SITE', url: `http://127.0.0.1:${FIX_PORT}/second` },
    { name: 'THIRD SITE', url: `http://127.0.0.1:${FIX_PORT}/third` }
  ]
}, null, 2))
const SEEDED_BOOKMARKS = 3

await env.up()
const shell = env.launchShell()

// Read helpers evaluated INSIDE the real launcher pane DOM.
const CARDS_JS = `(function(){
  const cards=[...document.querySelectorAll('#grid .card')];
  return cards.map(c=>({selected:c.classList.contains('selected'),
    bg:getComputedStyle(c).backgroundColor, shadow:getComputedStyle(c).boxShadow,
    title:(c.querySelector('.card-title')||{}).textContent||''}));
})()`
const TITLE_JS = `(function(){
  const t=document.querySelector('#grid .card:not(.empty) .card-title');
  if(!t) return null;
  const cs=getComputedStyle(t);
  return {fontSize:cs.fontSize, fontWeight:cs.fontWeight, fontFamily:cs.fontFamily,
    h:t.getBoundingClientRect().height, text:t.textContent};
})()`
const URLBAR_JS = `(function(){
  const b=document.getElementById('urlbar'); const r=b.getBoundingClientRect();
  return {width:Math.round(r.width), centerX:Math.round(r.left+r.width/2),
    winW:window.innerWidth, text:b.textContent, hasCursor:!!b.querySelector('.cursor'),
    focused:b.classList.contains('focused')};
})()`
const ZOOM_JS = `document.documentElement.style.zoom`

let la = null // launcher-pane CDP client
try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('L0 shell booted to launcher', (await pollTermText(term, 'Go to URL', 30000)).ok)
  await pollTermText(term, 'FIXTURE HOME', 15000)
  await sleep(800)

  // Attach to the REAL launcher pane and wait until it has painted pushed state.
  const lt = await findTarget(env.cdpPort, t => t.url.includes('launcher.html'), 20000, 'launcher pane')
  la = await Cdp.connect(lt.webSocketDebuggerUrl)
  for (let i = 0; i < 40 && !(await la.eval('!!window.__launcherReady').catch(() => false)); i++) await sleep(250)

  // ── (a) Pane VISIBLE with REAL state + design-correct computed styles ──────────────
  const st0 = await term.eval('window.__wc.state()')
  check('A0 launcherView is visible (mode reader)', st0.launcherShown === true && st0.mode === 'reader',
    `launcherShown=${st0.launcherShown} mode=${st0.mode}`)
  const ls0 = await la.eval('JSON.stringify(window.__launcherState())')
  const state0 = JSON.parse(ls0).state
  check('A1 pane rendered real state: slots present', state0.slots.length > 0, `slots=${state0.slots.length}`)
  check('A2 bookmarkCount matches seeded bookmarks', state0.bookmarkCount === SEEDED_BOOKMARKS,
    `bookmarkCount=${state0.bookmarkCount} seeded=${SEEDED_BOOKMARKS}`)
  const footerText = (await pollTermText(term, `${SEEDED_BOOKMARKS} bookmark`, 8000))
  check('A2b TUI footer shows the same bookmark count', footerText.ok,
    `looked for "${SEEDED_BOOKMARKS} bookmark" in TUI`)
  check('A3 card 1 is the seeded fixture bookmark', /FIXTURE HOME/.test(state0.slots[0].title),
    `slot0.title="${state0.slots[0].title}"`)
  check('A4 default selection is card 1 (selectedIndex 0)', state0.selectedIndex === 0,
    `selectedIndex=${state0.selectedIndex}`)

  const title = await la.eval(TITLE_JS)
  check('A5 card title is 34px weight 700 JetBrains Mono (computed)',
    title && title.fontSize === '34px' && title.fontWeight === '700' && /JetBrains Mono/.test(title.fontFamily),
    JSON.stringify(title))
  const bar0 = await la.eval(URLBAR_JS)
  check('A6 URL bar is ~560px wide and centred',
    Math.abs(bar0.width - 560) <= 12 && Math.abs(bar0.centerX - bar0.winW / 2) <= 40,
    `width=${bar0.width} centerX=${bar0.centerX} winW=${bar0.winW}`)
  const cards0 = await la.eval(CARDS_JS)
  const sel0 = cards0[0]
  check('A7 selected card has sel-bg fill rgb(0,95,0)', sel0.selected && sel0.bg === 'rgb(0, 95, 0)',
    `selected=${sel0.selected} bg=${sel0.bg}`)
  check('A8 selected card has 3px muted inset left rail (box-shadow)',
    /inset/.test(sel0.shadow) && /3px/.test(sel0.shadow) && /rgb\(95, 135, 95\)/.test(sel0.shadow),
    sel0.shadow)
  check('A9 non-selected card has no sel-bg fill',
    cards0[1] && !cards0[1].selected && cards0[1].bg !== 'rgb(0, 95, 0)', `card1 bg=${cards0[1] && cards0[1].bg}`)
  env.shot('lhtml-a-home')

  // ── (b) STATE CHANGE: a real key moves the selection; fill+rail follow in the DOM ────
  env.activate(); await sleep(300)
  env.key('Right'); await sleep(700)
  let stateB = JSON.parse(await la.eval('JSON.stringify(window.__launcherState())')).state
  let movedTo = stateB.selectedIndex
  if (movedTo === state0.selectedIndex) { // Right didn't move — try Down
    env.key('Down'); await sleep(700)
    stateB = JSON.parse(await la.eval('JSON.stringify(window.__launcherState())')).state
    movedTo = stateB.selectedIndex
  }
  check('B0 selection index CHANGED after a movement key', movedTo !== state0.selectedIndex,
    `${state0.selectedIndex} → ${movedTo}`)
  const cardsB = await la.eval(CARDS_JS)
  check('B1 old card 0 dropped its sel-bg fill in the DOM',
    cardsB[0] && !cardsB[0].selected && cardsB[0].bg !== 'rgb(0, 95, 0)',
    `card0 selected=${cardsB[0] && cardsB[0].selected} bg=${cardsB[0] && cardsB[0].bg}`)
  check('B2 newly-selected card gained sel-bg fill + rail in the DOM',
    cardsB[movedTo] && cardsB[movedTo].selected && cardsB[movedTo].bg === 'rgb(0, 95, 0)' &&
    /inset/.test(cardsB[movedTo].shadow) && /3px/.test(cardsB[movedTo].shadow),
    `card${movedTo}: selected=${cardsB[movedTo] && cardsB[movedTo].selected} bg=${cardsB[movedTo] && cardsB[movedTo].bg}`)
  env.shot('lhtml-b-moved')

  // ── (c) URL typing echo: 'o' then type flows into the pane's URL bar; Escape clears ──
  env.key('o'); await sleep(900)
  env.type('example.com'); await sleep(1200)
  const barType = await la.eval(URLBAR_JS)
  check('C0 URL bar echoes the typed buffer with block cursor',
    /example\.com/.test(barType.text) && barType.hasCursor && barType.focused,
    `text="${barType.text.trim()}" cursor=${barType.hasCursor} focused=${barType.focused}`)
  env.shot('lhtml-c-typing')
  env.key('Escape'); await sleep(900)
  const barCleared = await la.eval(URLBAR_JS)
  check('C1 Escape cleared the URL echo (back to placeholder, no typed text)',
    !/example\.com/.test(barCleared.text) && /Go to URL/.test(barCleared.text),
    `text="${barCleared.text.trim()}"`)

  // ── (d) Enter-opens: reselect card 1, Enter → pane hides + live page navigates ───────
  // After Escape the URL bar is focused (index −1); a printable key there SEEDS a new URL
  // entry rather than digit-jumping, so we step onto the grid with Down (selects card 1 =
  // the fixture at index 0) — the real keys a user presses to land back on card 1.
  env.key('Down'); await sleep(700)
  let preEnter = JSON.parse(await la.eval('JSON.stringify(window.__launcherState())')).state
  if (preEnter.selectedIndex !== 0) { env.key('1'); await sleep(700); preEnter = JSON.parse(await la.eval('JSON.stringify(window.__launcherState())')).state }
  check('D0 card 1 (fixture) reselected before Enter', preEnter.selectedIndex === 0, `selectedIndex=${preEnter.selectedIndex}`)
  env.key('Return')
  // Poll for the launcher to HIDE and the live page pane to actually reach the fixture URL.
  let hid = false
  for (let i = 0; i < 30; i++) { await sleep(400); if ((await term.eval('window.__wc.state()')).launcherShown === false) { hid = true; break } }
  check('D1 launcherView HID on Enter (visible false)', hid, `launcherShown after Enter`)
  let navTarget = null
  for (let i = 0; i < 30; i++) {
    const list = await targets(env.cdpPort).catch(() => [])
    navTarget = list.find(t => t.type === 'page' && t.url.startsWith(FIX_URL))
    if (navTarget) break
    await sleep(400)
  }
  check('D2 live page pane NAVIGATED to the fixture URL (real nav, not "command sent")',
    !!navTarget, navTarget ? navTarget.url : `no target at ${FIX_URL}`)
  if (navTarget) {
    const pc = await Cdp.connect(navTarget.webSocketDebuggerUrl).catch(() => null)
    if (pc) {
      const marker = await pc.eval('(document.getElementById("fx")||{}).textContent||""').catch(() => '')
      check('D3 fixture page actually rendered (marker present in the loaded page)',
        /WIRE COPY FIXTURE PAGE/.test(marker), `marker="${marker}"`)
      pc.close()
    }
  }
  env.shot('lhtml-d-opened')

  // ── (e) Return: the back key brings the launcher pane back, state restored ───────────
  env.activate(); await sleep(200)
  env.key('Escape'); await sleep(900)
  let back = (await term.eval('window.__wc.state()')).launcherShown === true
  if (!back) { env.key('b'); await sleep(900); back = (await term.eval('window.__wc.state()')).launcherShown === true }
  check('E0 launcherView VISIBLE again after back key', back, `launcherShown=${back}`)
  if (back) {
    const stateE = JSON.parse(await la.eval('JSON.stringify(window.__launcherState())')).state
    check('E1 launcher state restored (fixture card + real slots)',
      stateE.slots.length > 0 && /FIXTURE HOME/.test(stateE.slots[0].title), `slot0="${stateE.slots[0] && stateE.slots[0].title}"`)
  }
  env.shot('lhtml-e-return')

  // ── (f) Zoom: Ctrl+= raises the pane's zoom AND enlarges a rendered title ────────────
  const fontBefore = await term.eval('window.__font()')
  const hBefore = (await la.eval(TITLE_JS)).h
  const zoomBefore = await la.eval(ZOOM_JS)
  env.activate(); await sleep(200)
  env.key('ctrl+equal'); await sleep(800)
  const fontAfter = await term.eval('window.__font()')
  const zoomAfter = await la.eval(ZOOM_JS)
  const hAfter = (await la.eval(TITLE_JS)).h
  check('F0 Ctrl+= bumped the TUI font (18→19)', fontAfter === fontBefore + 1, `${fontBefore} → ${fontAfter}`)
  check('F1 pane zoom rose to 19/18', Math.abs(parseFloat(zoomAfter) - 19 / 18) < 0.002,
    `zoom ${zoomBefore} → ${zoomAfter} (want ${(19 / 18).toFixed(4)})`)
  check('F2 a rendered title actually got BIGGER on screen', hAfter > hBefore + 0.5, `title h ${hBefore} → ${hAfter}`)
  env.shot('lhtml-f-zoomed')
  env.key('ctrl+0'); await sleep(700)
  const zoomReset = await la.eval(ZOOM_JS)
  check('F3 Ctrl+0 reset the pane zoom to 1', Math.abs(parseFloat(zoomReset) - 1) < 0.002, `zoom=${zoomReset}`)

  // No main-process crash while driving all of the above.
  const crashed = /Uncaught|TypeError|ReferenceError|A JavaScript error occurred in the main process/.test(shell.stderrText)
  check('Z0 no main-process crash in stderr', !crashed,
    crashed ? shell.stderrText.split('\n').filter(l => /error|Error/.test(l)).slice(-3).join(' | ') : '')
} catch (err) {
  console.error('GATE ERROR:', err)
  check('gate completed without error', false, String(err))
  try { env.shot('lhtml-error') } catch {}
} finally {
  if (la) la.close()
  env.down()
  try { fixture.close() } catch {}
}
process.exit(summary())
