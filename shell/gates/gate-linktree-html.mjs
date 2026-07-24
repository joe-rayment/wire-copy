// Link-tree HTML-pane gate (workspace-vingi): proves the REAL story-list WebContentsView
// (shell/linktree/linktree.html) — driven end-to-end by the live .NET TUI over the shell
// channel — renders the user's link list per "Link Tree Responsive.dc.html" and reacts
// to REAL keystrokes. Every check asserts a USER-VISIBLE OUTCOME on the pane's DOM /
// computed styles (per the project Verification Doctrine): the pane shows over the
// DOCKED terminal split with 2 columns, undocking reflows to 3 at 1360px, the width
// sweep hits the design's 1/2/3/4-column breakpoints over a FLUID minimum track
// (1680px stays at 3 — it must NOT fan out — and only ~1900px+ buys a 4th) with a
// fixed 162px card footprint and headline type at its 23px ceiling when wide, Right
// recolors the visually adjacent card, Enter NAVIGATES the live page pane to the
// selected story's URL and the pane hides, Tab brings it back, Ctrl+= zooms it.
// Headful under Xvfb ONLY (never headless). Run from shell/: node gates/gate-linktree-html.mjs
import http from 'node:http'
import fs from 'node:fs'
import path from 'node:path'
import { Env, attach, targets, Cdp, findTarget, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

buildTui('Release')

// ── Fixture: a two-section link page (section headers + 15 stories with byline/date
//    meta) plus REAL readable article pages, so Enter-opens proves an actual
//    navigation to a URL we control. ──────────────────────────────────────────────
const FPORT = 8163
const AUTHOR = 'Ada Lovelace'
const WORDS = ['one', 'two', 'three', 'four', 'five', 'six', 'seven', 'eight', 'nine']
const ARCH = ['alpha', 'beta', 'gamma', 'delta', 'epsilon', 'zeta']
const story = (i, w) =>
  `<li><a href="/a${i}.html">Dispatch ${w}: a headline sized for the responsive grid gate number ${i}</a>` +
  ` <span class="byline">${AUTHOR}</span> <time datetime="2026-07-19T08:00:00Z">Jul 19</time></li>`
const fixture = http.createServer((req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  if (/^\/a\d+\.html/.test(req.url)) {
    // Real readable body + a small related-links section, so Tab back to the
    // link view on THIS page has a tree to show (the pane must return).
    res.end(`<!doctype html><meta charset="utf-8"><title>Story ${req.url}</title><body>` +
      `<article><h1>Story ${req.url}</h1>` +
      `<p>${'Readable body copy for the reader view to parse. '.repeat(30)}</p>`.repeat(6) +
      `</article>` +
      `<section><h2>Related</h2><ul>` +
      [1, 2, 3].map(n => story(n, `related-${n}`)).join('\n') +
      `</ul></section>`)
    return
  }
  res.end(`<!doctype html><meta charset="utf-8"><title>Column Wire</title><body>
<h1>Column Wire</h1>
<section><h2>Dispatches</h2><ul>${WORDS.map((w, i) => story(i + 1, w)).join('\n')}</ul></section>
<section><h2>Archive</h2><ul>${ARCH.map((w, i) => story(i + 10, w)).join('\n')}</ul></section>`)
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))
const listUrl = `http://127.0.0.1:${FPORT}/`

// Wide virtual screen for the 4-column leg; boot DIRECTLY at 1360x900 via pre-seeded
// window bounds so no pre-resize render lingers.
const env = new Env({ display: ':83', cdpPort: 9293, screen: '2000x1200x24' })
fs.writeFileSync(path.join(env.extraEnv.WIRECOPY_SHELL_USERDATA, 'window-bounds.json'),
  JSON.stringify({ x: 0, y: 0, width: 1360, height: 900 }))
await env.up()
const shell = env.launchShell()

// ── Probes evaluated INSIDE the real pane DOM ─────────────────────────────────────
const GEO_JS = `(function(){
  const g=document.getElementById('grid');
  const cards=[...g.querySelectorAll('.card')].map(c=>{const r=c.getBoundingClientRect();
    return {x:Math.round(r.left),y:Math.round(r.top),w:Math.round(r.width),h:Math.round(r.height),
      sel:c.classList.contains('selected'), rail:!!c.querySelector('.card-rail'),
      bg:getComputedStyle(c).backgroundColor,
      title:(c.querySelector('.card-title')||{}).textContent||'',
      ts:c.querySelector('.card-title')?parseFloat(getComputedStyle(c.querySelector('.card-title')).fontSize):0,
      ms:c.querySelector('.card-meta')?parseFloat(getComputedStyle(c.querySelector('.card-meta')).fontSize):0,
      meta:(c.querySelector('.card-meta')||{}).textContent||''};});
  const sections=[...g.querySelectorAll('.section')].map(s=>({text:s.textContent.replace(/─+/g,'').trim().slice(0,48),
    w:Math.round(s.getBoundingClientRect().width)}));
  return {cols:getComputedStyle(g).gridTemplateColumns.split(' ').length,
    cards, sections, winW:window.innerWidth, winH:window.innerHeight};
})()`
const HDR_JS = `(function(){
  const t=document.getElementById('pagetitle'); const cs=getComputedStyle(t);
  return {title:t.textContent, fontSize:parseFloat(cs.fontSize), weight:cs.fontWeight,
    glow:cs.textShadow!=='none',
    eyebrow:document.getElementById('eyebrow').textContent.trim(),
    metaline:document.getElementById('metaline').textContent,
    shown:(document.querySelector('#header-right .hr-shown')||{}).textContent||'',
    docked:(document.querySelector('#footer-right .docked')||{}).textContent||''};
})()`
const STATE_JS = `JSON.stringify(window.__linkTreeState())`

let lt = null
try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('L0 shell booted to launcher', (await pollTermText(term, 'Go to URL', 30000)).ok)
  env.activate(); await sleep(500)
  env.key('o'); await sleep(700)
  env.type(listUrl); await sleep(500)
  env.key('Return')
  check('L1 TUI rendered the fixture link list', (await pollTermText(term, 'Dispatch one', 40000)).ok)

  // Attach to the REAL link-tree pane and wait for painted pushed state.
  const t0 = await findTarget(env.cdpPort, t => t.url.includes('linktree.html'), 20000, 'link-tree pane')
  lt = await Cdp.connect(t0.webSocketDebuggerUrl)
  for (let i = 0; i < 40 && !(await lt.eval('!!window.__linkTreeReady').catch(() => false)); i++) await sleep(250)

  // ── (a) DOCKED (the auto-revealed default): pane over the terminal split ─────────
  let st = null
  for (let i = 0; i < 40; i++) { st = await term.eval('window.__wc.state()'); if (st?.revealed && st.linkTreeShown) break; await sleep(500) }
  check('A0 sidecar auto-docked and link pane SHOWN', !!st?.revealed && st.linkTreeShown === true,
    `revealed=${st && st.revealed} linkTreeShown=${st && st.linkTreeShown}`)
  check('A1 pane bounds track the TERMINAL region (left split), not the full window',
    st.linkTreeBounds && st.termBounds && st.linkTreeBounds.width === st.termBounds.width &&
    st.linkTreeBounds.width < st.contentSize[0] * 0.75,
    JSON.stringify({ lt: st.linkTreeBounds, term: st.termBounds, win: st.contentSize }))
  await sleep(1800) // let the reveal animation + reflow fully settle

  const hdr = await lt.eval(HDR_JS)
  check('A2 CRT header shows the real page title in clamped glow type (28–42px, 700)',
    hdr.title === 'Column Wire' && hdr.weight === '700' && hdr.glow && hdr.fontSize >= 28 && hdr.fontSize <= 42,
    JSON.stringify({ title: hdr.title, fontSize: hdr.fontSize, weight: hdr.weight, glow: hdr.glow }))
  check('A3 eyebrow reads Link Tree; meta line carries the real domain + link count',
    /Link Tree/i.test(hdr.eyebrow) && hdr.metaline.includes('127.0.0.1') && /\d+ links/.test(hdr.metaline),
    `eyebrow="${hdr.eyebrow}" meta="${hdr.metaline}"`)
  check('A4 footer wears the ⇉ docked marker while the sidecar is out',
    /docked/.test(hdr.docked), `docked="${hdr.docked}"`)
  check('A4b header readout counts shown stories', /\d+ of \d+ shown/.test(hdr.shown), hdr.shown)

  const gD = await lt.eval(GEO_JS)
  const stateD = JSON.parse(await lt.eval(STATE_JS)).state
  check('A5 DOCKED (~58% of 1360px): grid renders 2 columns == pushed state.columns',
    gD.cols === 2 && stateD.columns === 2, `rendered=${gD.cols} state=${stateD.columns} paneW=${gD.winW}`)
  const cardW = gD.cards.length ? gD.cards[0].w : 0
  check('A6 section headers span the grid with real name + count',
    gD.sections.length >= 1 && /DISPATCHES/.test(gD.sections[0].text) && /\(9\)/.test(gD.sections[0].text) &&
    gD.sections[0].w > cardW * 1.8,
    JSON.stringify(gD.sections))
  // The tree may boot with the SECTION HEADER selected — step onto the first
  // card with Down (the real key a user presses) before asserting card selection.
  let gD2 = gD
  if (gD.cards.findIndex(c => c.sel) < 0) {
    env.key('Down'); await sleep(900)
    gD2 = await lt.eval(GEO_JS)
  }
  const selD = gD2.cards.findIndex(c => c.sel)
  check('A7 cards hold the fixed 162px footprint; selection is a recolor (sel-bg + glowing rail)',
    gD2.cards.length > 0 && gD2.cards.every(c => Math.abs(c.h - 162) <= 1) && selD >= 0 &&
    gD2.cards[selD].rail && gD2.cards[selD].bg === 'rgb(0, 95, 0)',
    `heights=${[...new Set(gD2.cards.map(c => c.h))]} sel=${selD}`)
  check('A8 story meta line renders the real author', gD2.cards.length > 0 && gD2.cards[0].meta.includes(AUTHOR),
    `meta="${gD2.cards[0] && gD2.cards[0].meta}"`)
  env.shot('ltree-a-docked')

  // ── (b) STATE CHANGE: Right recolors the visually adjacent card ──────────────────
  const selB = gD2.cards.findIndex(c => c.sel)
  env.key('Right'); await sleep(900)
  const gB = await lt.eval(GEO_JS)
  const selR = gB.cards.findIndex(c => c.sel)
  const okB0 = selB >= 0 && selR === selB + 1 && !!gB.cards[selR] && !!gB.cards[selB] &&
    gB.cards[selR].y === gB.cards[selB].y && gB.cards[selR].x > gB.cards[selB].x
  check('B0 Right selected the VISUALLY adjacent card (same row, next column)', okB0,
    `sel ${selB}(${gB.cards[selB] && gB.cards[selB].x},${gB.cards[selB] && gB.cards[selB].y}) → ${selR}(${gB.cards[selR] && gB.cards[selR].x},${gB.cards[selR] && gB.cards[selR].y})`)
  check('B1 selection did NOT resize the card (fixed footprint)',
    okB0 && Math.abs(gB.cards[selR].h - 162) <= 1 && gB.cards[selR].w === gB.cards[selB].w,
    okB0 ? `${gB.cards[selR].w}x${gB.cards[selR].h} vs ${gB.cards[selB].w}x${gB.cards[selB].h}` : 'no selected card')
  env.shot('ltree-b-moved')

  // ── (c) UNDOCK (|): pane grows to the full window; 2 → 3 columns at 1360px;
  //    the selection survives on the SAME story ────────────────────────────────────
  const selTitle = gB.cards[selR].title
  env.key('bar'); await sleep(600)
  let st2 = null
  for (let i = 0; i < 30; i++) { st2 = await term.eval('window.__wc.state()'); if (st2 && st2.revealed === false) break; await sleep(400) }
  check('C0 | undocked the sidecar', st2?.revealed === false)
  await sleep(2500)
  const g3 = await lt.eval(GEO_JS)
  const state3 = JSON.parse(await lt.eval(STATE_JS)).state
  check('C1 full-width 1360px: grid reflows to 3 columns == pushed state',
    g3.cols === 3 && state3.columns === 3 && Math.abs(g3.winW - 1360) <= 40,
    `rendered=${g3.cols} state=${state3.columns} paneW=${g3.winW}`)
  const selC = g3.cards.findIndex(c => c.sel)
  check('C2 selection SURVIVED the undock on the same story',
    selC >= 0 && g3.cards[selC].title === selTitle,
    `"${selC >= 0 ? g3.cards[selC].title.slice(0, 40) : 'none'}" vs "${selTitle.slice(0, 40)}"`)
  check('C3 first grid row now holds 3 cards',
    g3.cards.filter(c => c.y === g3.cards[0].y).length === 3,
    `firstRow=${g3.cards.filter(c => c.y === g3.cards[0].y).length}`)
  env.shot('ltree-c-undocked-3col')

  // ── (d) Width sweep (undocked): the design's breakpoints on the REAL window ──────
  const wid = env.activate()
  // The design's own table: ≤650 → 1, 650–975 → 2, 975+ → 3, 1900+ → 4. 1680 is the
  // regression case — a fixed 300px minimum track fanned it out to 4 narrow columns;
  // the design's fluid clamp(300px,26vw,430px) holds it at 3 wide ones.
  for (const [w, h, exp] of [[620, 900, 1], [900, 900, 2], [1680, 1000, 3], [1980, 1100, 4]]) {
    env.x('windowsize', wid, String(w), String(h))
    let got = null
    const t1 = Date.now()
    while (Date.now() - t1 < 14000) {
      got = await lt.eval(GEO_JS)
      const sc = JSON.parse(await lt.eval(STATE_JS)).state
      got.stateCols = sc.columns
      if (Math.abs(got.winW - w) <= 20 && got.cols === exp && sc.columns === exp) break
      await sleep(300)
    }
    check(`D${exp} at ~${w}px: ${exp} column(s), rendered == pushed state`,
      got && got.cols === exp && got.stateCols === exp,
      `rendered=${got && got.cols} state=${got && got.stateCols} winW=${got && got.winW}`)
    check(`D${exp}b at ~${w}px: card footprint still fixed at 162px`,
      got && got.cards.length > 0 && got.cards.every(c => Math.abs(c.h - 162) <= 1),
      got && `heights=${[...new Set(got.cards.map(c => c.h))]}`)
    // The point of the fluid track: wide panes buy WIDER cards and BIGGER headline
    // type, not more columns. Headline clamps 17→23px, byline 12.5→15.5px.
    const vw = (got && got.winW) || w
    const expTs = Math.min(23, Math.max(17, 0.0152 * vw))
    const expMs = Math.min(15.5, Math.max(12.5, 0.0102 * vw))
    check(`D${exp}c at ~${w}px: headline type sits at the design's clamp (${expTs.toFixed(1)}px / byline ${expMs.toFixed(1)}px)`,
      got && got.cards.length > 0 && Math.abs(got.cards[0].ts - expTs) <= 0.6 &&
      Math.abs(got.cards[0].ms - expMs) <= 0.6,
      got && got.cards.length ? `title=${got.cards[0].ts} meta=${got.cards[0].ms}` : 'no cards')
    check(`D${exp}c2 at ~${w}px: every card is at least the 300px minimum track wide`,
      got && got.cards.length > 0 && got.cards.every(c => c.w >= 300),
      got && `widths=${[...new Set(got.cards.map(c => c.w))]}`)
    env.shot(`ltree-d-${exp}col`)
  }

  // ── (e) Enter OPENS the selected story: live page pane navigates to ITS url and
  //    the pane hides for the reader view ──────────────────────────────────────────
  const stateE = JSON.parse(await lt.eval(STATE_JS)).state
  const selItem = stateE.items[stateE.selectedIndex]
  check('E0 selected item is a story carrying its real href',
    selItem && selItem.kind === 'story' && /\/a\d+\.html$/.test(selItem.url || ''),
    JSON.stringify(selItem && { kind: selItem.kind, url: selItem.url }))
  env.key('Return')
  let nav = null
  for (let i = 0; i < 40; i++) {
    const list = await targets(env.cdpPort).catch(() => [])
    nav = list.find(t => t.type === 'page' && t.url === selItem.url)
    if (nav) break
    await sleep(400)
  }
  check('E1 live page pane NAVIGATED to the selected story URL (real nav, not "command sent")',
    !!nav, nav ? nav.url : `no target at ${selItem && selItem.url}`)
  let hid = null
  for (let i = 0; i < 30; i++) { hid = await term.eval('window.__wc.state()'); if (hid && hid.linkTreeShown === false) break; await sleep(400) }
  check('E2 link pane HID when the story view took over', hid?.linkTreeShown === false,
    `linkTreeShown=${hid && hid.linkTreeShown}`)
  env.shot('ltree-e-opened')

  // ── (f) Tab returns to the link list: the pane comes back with real items ────────
  env.key('Tab'); await sleep(1200)
  let back = null
  for (let i = 0; i < 30; i++) { back = await term.eval('window.__wc.state()'); if (back?.linkTreeShown === true) break; await sleep(400) }
  check('F0 Tab (reader↔links) brings the link pane back', back?.linkTreeShown === true,
    `linkTreeShown=${back && back.linkTreeShown}`)
  env.shot('ltree-f-return')

  // ── (g) Zoom rides the shared wc:font contract ───────────────────────────────────
  env.key('ctrl+equal'); await sleep(900)
  const zoom = await lt.eval('document.documentElement.style.zoom')
  check('G0 Ctrl+= zooms the link pane to 19/18', Math.abs(parseFloat(zoom) - 19 / 18) < 0.002, `zoom=${zoom}`)
  env.key('ctrl+0'); await sleep(600)

  const crashed = /Uncaught|TypeError|ReferenceError|A JavaScript error occurred in the main process/.test(shell.stderrText)
  check('Z0 no main-process crash in stderr', !crashed,
    crashed ? shell.stderrText.split('\n').filter(l => /error|Error/.test(l)).slice(-3).join(' | ') : '')
} catch (err) {
  console.error('GATE ERROR:', err)
  check('gate completed without error', false, String(err))
  try { env.shot('ltree-error') } catch {}
} finally {
  if (lt) lt.close()
  env.down()
  try { fixture.close() } catch {}
}
process.exit(summary())
