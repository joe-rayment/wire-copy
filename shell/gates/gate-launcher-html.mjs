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

// Wide virtual screen: the column section drives the REAL window up to ~1680px
// (a 15" MacBook), past the default 1600px Xvfb — the width that must STILL be
// 2 columns.
const env = new Env({ display: ':81', cdpPort: 9291, screen: '2000x1200x24' })

// ── Pre-seed bookmarks.json BEFORE launch so card 1 = our fixture. The reconciler treats
//    an existing user config as the source of truth (array order = card order); index 0 is
//    launcher card 1. Path: $XDG_DATA_HOME/WireCopy/bookmarks.json. ──
const dataDir = path.join(env.extraEnv.XDG_DATA_HOME, 'WireCopy')
mkdirSync(dataDir, { recursive: true })
// 9 bookmarks = the user's real launcher shape (2026-07-24): with the Reading
// List that is 10 slots → 3 visible card rows + paging, making the column
// TALLER than the window — the regime where the masthead was once flex-crushed
// and the tagline clipped. Card 1 stays the fixture for the Enter-opens leg.
writeFileSync(path.join(dataDir, 'bookmarks.json'), JSON.stringify({
  version: 1,
  bookmarks: [
    { name: 'FIXTURE HOME', url: FIX_URL },
    ...['second', 'third', 'fourth', 'fifth', 'sixth', 'seventh', 'eighth', 'ninth']
      .map(p => ({ name: `${p.toUpperCase()} SITE`, url: `http://127.0.0.1:${FIX_PORT}/${p}` }))
  ]
}, null, 2))
const SEEDED_BOOKMARKS = 9

await env.up()
const shell = env.launchShell()

// Read helpers evaluated INSIDE the real launcher pane DOM.
const CARDS_JS = `(function(){
  const cards=[...document.querySelectorAll('#grid .card')];
  return cards.map(c=>{
    const rail=c.querySelector('.card-rail');
    const rc=rail&&getComputedStyle(rail);
    const r=c.getBoundingClientRect();
    return {selected:c.classList.contains('selected'),
      bg:getComputedStyle(c).backgroundColor,
      rail:rail?{w:rc.width,bg:rc.backgroundColor,glow:rc.boxShadow!=='none'}:null,
      x:Math.round(r.left), y:Math.round(r.top), w:Math.round(r.width), h:Math.round(r.height),
      empty:c.classList.contains('empty'),
      title:(c.querySelector('.card-title')||{}).textContent||''};
  });
})()`
// Height re-paging probe (workspace-1ytix): how many card rows the pane actually DREW,
// what the TUI admitted, and whether the column still fits the window. `rows` counts
// distinct card tops, so it is the visual row count, not a slot arithmetic restatement.
const ROWS_JS = `(function(){
  const cards=[...document.querySelectorAll('#grid .card')].filter(c=>!c.classList.contains('empty'));
  const f=document.getElementById('footer').getBoundingClientRect();
  const st=(window.__launcherState()||{}).state||{};
  return {winH:window.innerHeight, winW:window.innerWidth,
    rows:new Set(cards.map(c=>Math.round(c.getBoundingClientRect().top))).size,
    visible:(st.paging&&st.paging.visible)||0, slots:(st.slots||[]).length,
    footerBottom:Math.round(f.bottom),
    colH:Math.round(document.getElementById('column').getBoundingClientRect().height)};
})()`
// Rendered grid geometry: the computed track count must equal the pushed
// state.columns (the count the TUI also uses for arrow keys).
const COLS_JS = `(function(){
  const g=document.getElementById('grid');
  return {cols:getComputedStyle(g).gridTemplateColumns.split(' ').length,
    stateCols:(window.__launcherState()||{}).state?.columns,
    winW:window.innerWidth};
})()`
const HDR_JS = `(function(){
  const e=document.querySelector('#header-right .hr-count'); if(!e) return null;
  const cs=getComputedStyle(e);
  return {text:e.textContent, color:cs.color, weight:cs.fontWeight};
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
  return {width:Math.round(r.width), left:Math.round(r.left),
    winW:window.innerWidth, text:b.textContent, hasCursor:!!b.querySelector('.cursor'),
    focused:b.classList.contains('focused')};
})()`
// The Wire Copy Launcher.dc.html masthead: CRT-glow "Wire Copy" title + blinking
// cursor block, left-aligned at the screen edge on the same axis as the grid.
const MASTHEAD_JS = `(function(){
  const t=document.getElementById('apptitle'); if(!t) return null;
  const cs=getComputedStyle(t); const r=t.getBoundingClientRect();
  const cur=document.getElementById('cursor-block');
  const curCs=cur&&getComputedStyle(cur); const curR=cur&&cur.getBoundingClientRect();
  const eye=document.getElementById('eyebrow');
  const grid=document.getElementById('grid').getBoundingClientRect();
  return {text:t.textContent, fontSize:cs.fontSize, fontWeight:cs.fontWeight,
    color:cs.color, shadow:cs.textShadow, left:Math.round(r.left),
    gridLeft:Math.round(grid.left), winW:window.innerWidth,
    cursor:cur?{w:Math.round(curR.width),h:Math.round(curR.height),
      bg:curCs.backgroundColor,anim:curCs.animationName}:null,
    eyebrow:eye?eye.textContent.trim():null};
})()`
const ZOOM_JS = `document.documentElement.style.zoom`
// Masthead clip probe (user regression 2026-07-24): when the column overflows
// the window, the tagline must remain FULLY visible above the URL bar — the
// stage scrolls; chrome is never crushed into its overflow:hidden.
const CLIP_JS = `(function(){
  const m=document.getElementById('masthead');
  const t=document.getElementById('tagline');
  const b=document.getElementById('urlbar');
  const s=document.getElementById('stage');
  const tr=t.getBoundingClientRect(), br=b.getBoundingClientRect();
  return {mScroll:m.scrollHeight, mClient:m.clientHeight,
    tagH:Math.round(tr.height), tagBottom:Math.round(tr.bottom),
    barTop:Math.round(br.top), stScroll:s.scrollHeight, stClient:s.clientHeight,
    tagText:t.textContent};
})()`

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
  // Design: font-size clamp(21px, 2.1vw, 28px) weight 700.
  const expTitle = Math.min(28, Math.max(21, 0.021 * (await la.eval('window.innerWidth'))))
  check('A5 card title wears the responsive clamp (21–28px) weight 700 JetBrains Mono (computed)',
    title && Math.abs(parseFloat(title.fontSize) - expTitle) <= 1 && title.fontWeight === '700' &&
    /JetBrains Mono/.test(title.fontFamily),
    `want ~${expTitle}px got ${title && title.fontSize}`)
  const bar0 = await la.eval(URLBAR_JS)
  // Design: URL bar spans the full content width inside the clamp() padding.
  const expPad = Math.min(60, Math.max(18, 0.04 * bar0.winW))
  check('A6 URL bar spans the FULL content width on the column axis',
    Math.abs(bar0.left - expPad) <= 3 && Math.abs(bar0.width - (bar0.winW - 2 * expPad)) <= 6,
    `width=${bar0.width} left=${bar0.left} winW=${bar0.winW} pad~${Math.round(expPad)}`)
  const mast = await la.eval(MASTHEAD_JS)
  check('A6b masthead is the Launcher.dc.html CRT title: "Wire Copy" 64px/700 phosphor green with glow',
    mast && mast.text === 'Wire Copy' && mast.fontSize === '64px' && mast.fontWeight === '700' &&
    mast.color === 'rgb(0, 215, 0)' && mast.shadow !== 'none',
    JSON.stringify(mast && { text: mast.text, fontSize: mast.fontSize, fontWeight: mast.fontWeight, color: mast.color }))
  check('A6c blinking block cursor sits beside the title (28×56 green, blink animation)',
    mast && mast.cursor && mast.cursor.w === 28 && mast.cursor.h === 56 &&
    mast.cursor.bg === 'rgb(0, 215, 0)' && /blink/.test(mast.cursor.anim),
    JSON.stringify(mast && mast.cursor))
  check('A6d title block sits on the clamp-padding left axis shared with the grid, eyebrow reads "Launcher"',
    mast && Math.abs(mast.left - expPad) <= 3 && Math.abs(mast.left - mast.gridLeft) <= 4 &&
    /Launcher/i.test(mast.eyebrow || ''),
    mast && `left=${mast.left} pad~${Math.round(expPad)} gridLeft=${mast.gridLeft} eyebrow="${mast.eyebrow}"`)
  const hdr = await la.eval(HDR_JS)
  check('A6f header status readout shows the real bookmark count (phosphor, bold)',
    hdr && hdr.text === `${SEEDED_BOOKMARKS} bookmarks` && hdr.color === 'rgb(0, 215, 0)' && hdr.weight === '700',
    JSON.stringify(hdr))
  const clip0 = await la.eval(CLIP_JS)
  check('A6e tagline fully visible above the URL bar at launch size (masthead not clipped)',
    clip0.mScroll <= clip0.mClient + 1 && clip0.tagH >= 15 && clip0.tagBottom <= clip0.barTop &&
    /All copy/.test(clip0.tagText),
    JSON.stringify(clip0))
  const cards0 = await la.eval(CARDS_JS)
  const sel0 = cards0[0]
  check('A7 selected card has sel-bg fill rgb(0,95,0)', sel0.selected && sel0.bg === 'rgb(0, 95, 0)',
    `selected=${sel0.selected} bg=${sel0.bg}`)
  check('A8 selected card carries the glowing 4px phosphor rail on its left edge',
    sel0.rail && sel0.rail.w === '4px' && sel0.rail.bg === 'rgb(0, 215, 0)' && sel0.rail.glow,
    JSON.stringify(sel0.rail))
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
  check('B2 newly-selected card gained sel-bg fill + glowing rail in the DOM',
    cardsB[movedTo] && cardsB[movedTo].selected && cardsB[movedTo].bg === 'rgb(0, 95, 0)' &&
    cardsB[movedTo].rail && cardsB[movedTo].rail.w === '4px',
    `card${movedTo}: selected=${cardsB[movedTo] && cardsB[movedTo].selected} bg=${cardsB[movedTo] && cardsB[movedTo].bg}`)
  check('B3 selection recolors WITHOUT resizing (footprints of selected and old card equal)',
    cardsB[movedTo] && cardsB[0] && Math.abs(cardsB[movedTo].h - cardsB[0].h) <= 1 &&
    Math.abs(cardsB[movedTo].w - cardsB[0].w) <= 1,
    `sel ${cardsB[movedTo] && cardsB[movedTo].w}x${cardsB[movedTo] && cardsB[movedTo].h} vs ${cardsB[0] && cardsB[0].w}x${cardsB[0] && cardsB[0].h}`)
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

  // ── (h) TWO COLUMNS AT EVERY WIDTH (the tile-grid contract, workspace-21uy):
  //    drive the REAL window through four widths — including 1680px, the 15" MacBook
  //    where the old responsive count rendered THREE columns and the user rejected it.
  //    The RENDERED grid must be 2 tracks at every width, match the pushed
  //    state.columns, and keep a fixed card footprint. Arrows must land on the
  //    VISUALLY adjacent card — the geometry the user's fingers feel. ─────────────────
  const widH = env.activate()
  // Wait until the WINDOW itself reaches the target width (a stale state can
  // coincidentally carry the expected column count), then until the rendered
  // tracks AND the pushed state agree on it.
  const waitCols = async (expectW, expect, timeout = 12000) => {
    const t0 = Date.now()
    let last = null
    while (Date.now() - t0 < timeout) {
      last = await la.eval(COLS_JS)
      if (Math.abs(last.winW - expectW) <= 20 && last.cols === expect && last.stateCols === expect) return last
      await sleep(300)
    }
    return last
  }
  const EXPECT_COLS = 2 // the contract, at EVERY width — never width-dependent
  for (const [w, h] of [[620, 900], [900, 900], [1280, 900], [1680, 1000]]) {
    env.x('windowsize', widH, String(w), String(h))
    const got = await waitCols(w, EXPECT_COLS)
    check(`H${w}a at ~${w}px the grid renders exactly ${EXPECT_COLS} columns, matching pushed state.columns`,
      got && got.cols === EXPECT_COLS && got.stateCols === EXPECT_COLS,
      `rendered=${got && got.cols} state=${got && got.stateCols} winW=${got && got.winW}`)
    const cardsH = (await la.eval(CARDS_JS)).filter(c => !c.empty)
    const rowY = [...new Set(cardsH.map(c => c.y))]
    const perRow = rowY.map(y => cardsH.filter(c => c.y === y).length)
    check(`H${w}b cards actually flow ${EXPECT_COLS} per visual row (fixed equal footprints)`,
      perRow.length > 0 && perRow[0] === Math.min(EXPECT_COLS, cardsH.length) &&
      Math.max(...cardsH.map(c => c.h)) - Math.min(...cardsH.map(c => c.h)) <= 1,
      `perRow=[${perRow}] heights=${[...new Set(cardsH.map(c => c.h))]}`)
    env.shot(`lhtml-h-${w}px`)
  }
  // Keyboard geometry at the widest window: from card 1, Right must select the
  // card physically beside it (same row, next track); Down the card physically
  // below it (same track, next row).
  await sleep(800) // let the 1680px layout fully settle before measuring geometry
  let stH = JSON.parse(await la.eval('JSON.stringify(window.__launcherState())')).state
  if (stH.selectedIndex !== 0) { env.key('1'); await sleep(700) }
  const before = (await la.eval(CARDS_JS)).filter(c => !c.empty)
  const selB = before.findIndex(c => c.selected)
  env.key('Right'); await sleep(700)
  const afterR = (await la.eval(CARDS_JS)).filter(c => !c.empty)
  const selR = afterR.findIndex(c => c.selected)
  check('H-arrows-c Right selected the VISUALLY adjacent card (same row, next column)',
    selB === 0 && selR >= 0 && afterR[selR].y === before[selB].y && afterR[selR].x > before[selB].x &&
    selR === selB + 1,
    `sel ${selB}(${before[selB] && before[selB].x},${before[selB] && before[selB].y}) → ${selR}(${afterR[selR] && afterR[selR].x},${afterR[selR] && afterR[selR].y})`)
  env.key('Down'); await sleep(700)
  const afterD = (await la.eval(CARDS_JS)).filter(c => !c.empty)
  const selD = afterD.findIndex(c => c.selected)
  check('H-arrows-d Down selected the card DIRECTLY BELOW (same column, next row)',
    selD >= 0 && afterD[selD].x === afterR[selR].x && afterD[selD].y > afterR[selR].y,
    `sel ${selR}(${afterR[selR] && afterR[selR].x},${afterR[selR] && afterR[selR].y}) → ${selD}(${afterD[selD] && afterD[selD].x},${afterD[selD] && afterD[selD].y})`)
  env.shot('lhtml-h-arrows')

  // ── (i) HEIGHT re-paging (workspace-1ytix): the H sweep above only varies WIDTH, and
  //    width is a number the pane can compute for itself. The ROW count comes from the
  //    TUI, which only learns the window changed when the pty is resized — and while the
  //    launcher pane covers termView, term.html's ResizeObserver is the ONLY thing that
  //    reports it. Chromium suspends observers in a hidden view, so before
  //    backgroundThrottling:false the pty stayed frozen at its boot size FOREVER: the
  //    launcher drew 5 rows in a 760px window with the footer scrolled off screen, and
  //    no gate noticed because none of them ever changed the HEIGHT with the pane up.
  //    So: drive the real window through four heights and assert the OUTCOME — the
  //    rendered row count actually tracks the height, and the footer stays on screen.
  const widI = env.activate()
  const HEIGHTS = [700, 850, 1000, 1150] // inside the 1200px virtual screen, decorations included
  const paged = []
  for (const h of HEIGHTS) {
    env.x('windowsize', widI, '1600', String(h))
    // Wait for the PANE to reach the new height, then settle a fixed 3s for the full
    // round-trip (window → term.html's observer → pty → TUI re-page → pushed state →
    // pane) before reading. Deliberately NOT a poll-until-it-looks-right loop: the
    // broken state is self-consistent (a stale paging still matches its own stale row
    // count), so any "wait until consistent" condition would break instantly on it and
    // read the same frozen frame the fix is supposed to eliminate.
    let m = null
    const t0 = Date.now()
    do { m = await la.eval(ROWS_JS); if (Math.abs(m.winH - h) <= 20) break; await sleep(300) }
    while (Date.now() - t0 < 8000)
    await sleep(3000)
    m = await la.eval(ROWS_JS)
    paged.push({ h, ...m })
    env.shot(`lhtml-i-${h}h`)
  }
  const rowsByH = paged.map(p => p.rows)
  check('I0 the rendered row count TRACKS the window height (the pty really re-sized)',
    new Set(rowsByH).size >= 3 && rowsByH.every((r, i) => i === 0 || r >= rowsByH[i - 1]) &&
    rowsByH[rowsByH.length - 1] > rowsByH[0],
    HEIGHTS.map((h, i) => `${h}px→${rowsByH[i]} rows`).join('  '))
  check('I1 the pane draws exactly the rows the TUI admitted, at every height',
    paged.every(p => p.rows === Math.ceil(p.visible / 2) && p.visible > 0),
    paged.map(p => `${p.h}px: rendered=${p.rows} admitted=${p.visible}`).join('  '))
  check('I2 the footer stays INSIDE the window at every height — content never outgrows it',
    paged.every(p => p.footerBottom <= p.winH + 1 && p.colH <= p.winH + 1),
    paged.map(p => `${p.h}px: footerBottom=${p.footerBottom} colH=${p.colH}`).join('  '))

  // ── (g) Short window (the user's regression, 2026-07-24): shrink the REAL window so
  //    the column cannot fit; the tagline must stay fully visible above the URL bar and
  //    the stage must SCROLL — the masthead must never be flex-crushed into its
  //    overflow:hidden clip. ──────────────────────────────────────────────────────────
  const wid = env.activate()
  env.x('windowsize', wid, '1000', '520')
  await sleep(1200)
  const clipG = await la.eval(CLIP_JS)
  check('G0 short window put the pane in the overflow regime (stage content taller than viewport)',
    clipG.stScroll > clipG.stClient + 20, `stageScroll=${clipG.stScroll} stageClient=${clipG.stClient}`)
  check('G1 tagline still FULLY visible above the URL bar — masthead not crushed/clipped',
    clipG.mScroll <= clipG.mClient + 1 && clipG.tagH >= 15 && clipG.tagBottom <= clipG.barTop &&
    /All copy/.test(clipG.tagText),
    JSON.stringify(clipG))
  env.shot('lhtml-g-short-window')

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
