// Link-tree HTML-pane gate (workspace-vingi, rescaled workspace-h2caq): proves the REAL
// story-list WebContentsView (shell/linktree/linktree.html) — driven end-to-end by the
// live .NET TUI over the shell channel — renders the user's link list per
// "Link Tree.dc.html" and reacts to REAL keystrokes. Every check asserts a USER-VISIBLE
// OUTCOME on the pane's DOM / computed styles (per the project Verification Doctrine):
// the grid is EXACTLY 2 COLUMNS at every width, docked or not (workspace-21uy — the
// docked collapse to 1 and the wide fan-out to 3 are both regressions the user rejected,
// 2026-07-26), wide panes buying WIDER cards and BIGGER type instead of more columns,
// with one card height per page inside the design's 165-242px bounds (workspace-wp16o:
// the rows SHARE the grid's height, so the remainder is spent on the cards instead of
// left as a dead band) and 21→34px headline type,
// the design's palette (phosphor headline, title-pink byline, muted date tail, all-white
// when selected), NO row sliced by the grid's bottom edge AND no dead band a further row
// would fill (A13/D…e — the check whose absence let "3 of 15 shown" ship green),
// Down/Right recolor the visually adjacent card, Enter NAVIGATES the live page pane to
// the selected story's URL and the pane hides, Tab brings it back, Ctrl+= zooms it.
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

// Wide virtual screen for the 3-column leg (the design's 700px track ceiling means a
// third column only lands past ~2250px); boot DIRECTLY at 1360x900 via pre-seeded
// window bounds so no pre-resize render lingers.
const env = new Env({ display: ':83', cdpPort: 9293, screen: '2600x1400x24' })
fs.writeFileSync(path.join(env.extraEnv.WIRECOPY_SHELL_USERDATA, 'window-bounds.json'),
  JSON.stringify({ x: 0, y: 0, width: 1360, height: 900 }))
await env.up()
const shell = env.launchShell()

// ── Probes evaluated INSIDE the real pane DOM ─────────────────────────────────────
const GEO_JS = `(function(){
  const g=document.getElementById('grid'); const gr=g.getBoundingClientRect();
  const cs=(el)=>el?getComputedStyle(el):null;
  const cards=[...g.querySelectorAll('.card')].map(c=>{const r=c.getBoundingClientRect();
    const t=c.querySelector('.card-title'), m=c.querySelector('.card-meta');
    const lead=c.querySelector('.card-meta .meta-lead'), sub=c.querySelector('.card-meta .meta-sub');
    return {x:Math.round(r.left),y:Math.round(r.top),w:Math.round(r.width),h:Math.round(r.height),
      bottom:Math.round(r.bottom),right:Math.round(r.right),
      sel:c.classList.contains('selected'), rail:!!c.querySelector('.card-rail'),
      bg:cs(c).backgroundColor,
      title:(t||{}).textContent||'',
      ts:t?parseFloat(cs(t).fontSize):0, tc:t?cs(t).color:'',
      ms:m?parseFloat(cs(m).fontSize):0,
      leadC:lead?cs(lead).color:'', subC:sub?cs(sub).color:'', subT:(sub||{}).textContent||'',
      meta:(m||{}).textContent||''};});
  const sections=[...g.querySelectorAll('.section')].map(s=>{const sr=s.getBoundingClientRect();
    const sc=getComputedStyle(s);
    return {text:s.textContent.replace(/─+/g,'').trim().slice(0,48),
      w:Math.round(sr.width),
      // outer height: a section's margins are part of the row it occupies
      h:Math.round(sr.height+parseFloat(sc.marginTop)+parseFloat(sc.marginBottom))};});
  return {cols:cs(g).gridTemplateColumns.split(' ').length,
    grid:{top:Math.round(gr.top),bottom:Math.round(gr.bottom),right:Math.round(gr.right)},
    cards, sections, winW:window.innerWidth, winH:window.innerHeight};
})()`
// The design's own clamps, in one place — every geometry check reads them.
// The card's design BOUNDS. Since workspace-wp16o the card no longer has one fixed
// height at a given width: C# admits the row count whose share of the grid lands inside
// these bounds and the rows share the grid's height, so the remainder is spent on the
// cards instead of left as blank space. What still has to hold is that every card on a
// page is the SAME height and that height is one the design's card can be.
const CARD_MIN = 165
const CARD_MAX = 242
const CARD_SEP = 12
const sameHeight = cards => cards.length > 0 &&
  Math.max(...cards.map(c => c.h)) - Math.min(...cards.map(c => c.h)) <= 1
const inBounds = cards => cards.length > 0 &&
  cards.every(c => c.h >= CARD_MIN - 1 && c.h <= CARD_MAX + 1)
const expTs = vw => Math.min(34, Math.max(21, 0.0225 * vw))   // headline
const expMs = vw => Math.min(20, Math.max(14, 0.0136 * vw))   // byline
const GREEN = 'rgb(0, 215, 0)'
const PINK = 'rgb(255, 135, 215)'
const MUTED = 'rgb(95, 135, 95)'
const WHITE = 'rgb(228, 228, 228)'
// No card may be sliced by the grid's clipping edge — the whole admitted window
// has to be visible, not merely present in the DOM.
const unclipped = g => g.cards.filter(c => c.bottom > g.grid.bottom + 1 || c.right > g.grid.right + 1)
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
// The pager marks as the user READS them — the rendered strings, not the pushed
// numbers behind them (workspace-u4p2z).
const PAGER_JS = `(function(){
  const txt=id=>{const e=document.getElementById(id);return e?e.textContent.trim():''};
  const n=s=>{const m=s.match(/(\\d+)/);return m?parseInt(m[1],10):0};
  const shown=(document.querySelector('#header-right .hr-shown')||{}).textContent||'';
  const sm=shown.match(/(\\d+) of (\\d+)/);
  return {above:txt('moreabove'), below:txt('morebelow'),
    aboveN:n(txt('moreabove')), belowN:n(txt('morebelow')),
    shownN:sm?parseInt(sm[1],10):0, totalN:sm?parseInt(sm[2],10):0, shownText:shown};
})()`

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
  // `revealed` flips at the START of the reveal, but the pane's setBounds lands as the
  // transition finishes — so wait for the BOUNDS to narrow too, or A1 samples the
  // full-window rectangle from mid-slide and fails at random (seen 2026-07-26 once the
  // TUI began booting earlier and shifted the timing).
  let st = null
  const docked = s => !!s?.revealed && s.linkTreeShown === true && s.linkTreeBounds && s.contentSize &&
    s.linkTreeBounds.width < s.contentSize[0] * 0.75
  for (let i = 0; i < 40; i++) { st = await term.eval('window.__wc.state()'); if (docked(st)) break; await sleep(500) }
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
  // workspace-u4p2z: the pager sits inches from "N of M shown" and says "N more
  // below", so its N is read as stories. It counted grid ROWS, which put "6 of 15
  // shown" beside "5 more below" when 9 were below. Read both strings off the pane
  // the user is looking at and require them to account for every story.
  const pg = await lt.eval(PAGER_JS)
  check('A4c the pager and the header agree, in STORIES: above + shown + below == total',
    pg.totalN > 0 && pg.shownN < pg.totalN && pg.aboveN + pg.shownN + pg.belowN === pg.totalN,
    `"${pg.shownText}" · above="${pg.above}" below="${pg.below}"`)

  const gD = await lt.eval(GEO_JS)
  const stateD = JSON.parse(await lt.eval(STATE_JS)).state
  check('A5 DOCKED (~58% of 1360px): grid renders 2 columns == pushed state.columns',
    gD.cols === 2 && stateD.columns === 2, `rendered=${gD.cols} state=${stateD.columns} paneW=${gD.winW}`)
  const cardW = gD.cards.length ? gD.cards[0].w : 0
  check('A6 section headers span the grid with real name + count',
    gD.sections.length >= 1 && /DISPATCHES/.test(gD.sections[0].text) && /\(9\)/.test(gD.sections[0].text) &&
    gD.sections[0].w >= cardW - 1,
    JSON.stringify(gD.sections))
  // The tree may boot with the SECTION HEADER selected — step onto the first
  // card with Down (the real key a user presses) before asserting card selection.
  let gD2 = gD
  if (gD.cards.findIndex(c => c.sel) < 0) {
    env.key('Down'); await sleep(900)
    gD2 = await lt.eval(GEO_JS)
  }
  const selD = gD2.cards.findIndex(c => c.sel)
  check(`A7 every card is the same height, inside the design's ${CARD_MIN}-${CARD_MAX}px bounds; selection is a recolor (sel-bg + glowing rail)`,
    sameHeight(gD2.cards) && inBounds(gD2.cards) && selD >= 0 &&
    gD2.cards[selD].rail && gD2.cards[selD].bg === 'rgb(0, 95, 0)',
    `heights=${[...new Set(gD2.cards.map(c => c.h))]} bounds=${CARD_MIN}-${CARD_MAX} sel=${selD}`)
  check('A8 story meta line renders the real author', gD2.cards.length > 0 && gD2.cards[0].meta.includes(AUTHOR),
    `meta="${gD2.cards[0] && gD2.cards[0].meta}"`)
  // The whole point of this import: the design's palette, on the real pane.
  const unsel = gD2.cards.find(c => !c.sel)
  check('A9 palette per Link Tree.dc.html — headline phosphor, byline title-pink, date tail muted',
    !!unsel && unsel.tc === GREEN && unsel.leadC === PINK && unsel.subC === MUTED && /^ · /.test(unsel.subT),
    unsel ? `title=${unsel.tc} lead=${unsel.leadC} sub=${unsel.subC} subText="${unsel.subT}"` : 'no unselected card')
  check('A10 the SELECTED card reads all-white on the phosphor fill',
    selD >= 0 && gD2.cards[selD].tc === WHITE && gD2.cards[selD].leadC === WHITE && gD2.cards[selD].subC === WHITE,
    selD >= 0 ? `title=${gD2.cards[selD].tc} lead=${gD2.cards[selD].leadC} sub=${gD2.cards[selD].subC}` : 'none')
  check('A11 no card is sliced by the grid edge — the admitted window is fully VISIBLE',
    unclipped(gD2).length === 0,
    `clipped=${JSON.stringify(unclipped(gD2).map(c => ({ y: c.y, bottom: c.bottom })))} gridBottom=${gD2.grid.bottom}`)
  // Docked, the pane and the terminal are the SAME rectangle (A1) — so this reads
  // the C# px-mirror straight off the running app. The constants were CALIBRATED
  // against this measurement (11.43 px/cell, 21.9 px/line, 2026-07-26), so the
  // estimate should now track the real pane within ~2% on both axes rather than
  // deliberately running under it. Drift past that band means the pane's type scale
  // moved and the row budget is being computed against a pane that no longer exists.
  const dims = await term.eval('window.__dims()')
  const estW = dims.cols * 11.43
  const estH = dims.rows * 21.9
  check('A12 the C# px-mirror tracks the real pane within 2% on both axes',
    Math.abs(estW - gD2.winW) <= gD2.winW * 0.02 && Math.abs(estH - gD2.winH) <= gD2.winH * 0.02,
    `${dims.cols}x${dims.rows} cells → est ${estW.toFixed(0)}x${estH.toFixed(0)}px vs real ${gD2.winW}x${gD2.winH}px`)

  // ── The check whose ABSENCE let "3 of 15 shown" ship green: the pane must admit
  //    every row it has room to draw. Under-filling is as much a bug as clipping —
  //    it is what the user sees as links vanishing below the fold. Allow the last
  //    partial row (a fixed card stride can never divide an arbitrary pane height).
  // Since workspace-wp16o this is no longer "the leftover is under one stride" — the
  // rows SHARE the grid, so the only thing that may sit below the last card is its own
  // 12px separator, plus whatever the card's 242px ceiling refused to absorb in the
  // narrow regime where the grid is too tall for N rows and too short for N+1. Anything
  // more is the dead band this replaced. "used" measures to the last card's BOTTOM, so
  // the separator is always part of the leftover.
  const fillCheck = (g, label) => {
    const rowTops = [...new Set(g.cards.map(c => c.y))].sort((a, b) => a - b)
    const gridH = g.grid.bottom - g.grid.top
    const used = g.cards.length ? Math.max(...g.cards.map(c => c.bottom)) - g.grid.top : 0
    const room = gridH - used
    // Section headers take their own rows; the card rows share what is left of the grid.
    const headersPx = g.sections.reduce((sum, s) => sum + (s.h || 0), 0)
    const track = rowTops.length ? (gridH - headersPx) / rowTops.length : gridH
    const allowed = CARD_SEP + Math.max(0, track - (CARD_MAX + CARD_SEP)) + 4
    check(`${label} the rows SPEND the grid — nothing left below the last card but its rule`,
      room <= allowed,
      `gridH=${gridH} used=${used} spare=${room.toFixed(0)} allowed=${allowed.toFixed(0)} ` +
      `track=${track.toFixed(0)} rows=${rowTops.length} cards=${g.cards.length} headers=${headersPx}`)
  }
  fillCheck(gD2, 'A13 DOCKED:')
  env.shot('ltree-a-docked')

  // ── (b) STATE CHANGE: Down recolors the card directly BELOW. Docked is TWO
  //    columns now, so "the next row down" is index + columns, not index + 1 —
  //    the geometric assertions below are what actually pin it. ──────────────────────
  const selB = gD2.cards.findIndex(c => c.sel)
  env.key('Down'); await sleep(900)
  const gB = await lt.eval(GEO_JS)
  const selR = gB.cards.findIndex(c => c.sel)
  const okB0 = selB >= 0 && selR === selB + gB.cols && !!gB.cards[selR] && !!gB.cards[selB] &&
    gB.cards[selR].x === gB.cards[selB].x && gB.cards[selR].y > gB.cards[selB].y
  check('B0 Down selected the VISUALLY next card (same column, next row down)', okB0,
    `sel ${selB}(${gB.cards[selB] && gB.cards[selB].x},${gB.cards[selB] && gB.cards[selB].y}) → ${selR}(${gB.cards[selR] && gB.cards[selR].x},${gB.cards[selR] && gB.cards[selR].y})`)
  check('B1 selection did NOT resize the card — the newly selected one matches every other',
    okB0 && sameHeight(gB.cards) && inBounds(gB.cards) && gB.cards[selR].w === gB.cards[selB].w,
    okB0 ? `${gB.cards[selR].w}x${gB.cards[selR].h} vs ${gB.cards[selB].w}x${gB.cards[selB].h} all=${[...new Set(gB.cards.map(c => c.h))]}` : 'no selected card')
  env.shot('ltree-b-moved')

  // ── (c) UNDOCK (|): pane grows to the full window; still 2 columns, now WIDER
  //    ones with bigger type; the selection survives on the SAME story ─────────────
  const selTitle = gB.cards[selR].title
  env.key('bar'); await sleep(600)
  let st2 = null
  for (let i = 0; i < 30; i++) { st2 = await term.eval('window.__wc.state()'); if (st2 && st2.revealed === false) break; await sleep(400) }
  check('C0 | undocked the sidecar', st2?.revealed === false)
  await sleep(2500)
  const g3 = await lt.eval(GEO_JS)
  const state3 = JSON.parse(await lt.eval(STATE_JS)).state
  check('C1 full-width 1360px: grid reflows to 2 columns == pushed state',
    g3.cols === 2 && state3.columns === 2 && Math.abs(g3.winW - 1360) <= 40,
    `rendered=${g3.cols} state=${state3.columns} paneW=${g3.winW}`)
  const selC = g3.cards.findIndex(c => c.sel)
  check('C2 selection SURVIVED the undock on the same story',
    selC >= 0 && g3.cards[selC].title === selTitle,
    `"${selC >= 0 ? g3.cards[selC].title.slice(0, 40) : 'none'}" vs "${selTitle.slice(0, 40)}"`)
  check('C3 first grid row now holds 2 cards',
    g3.cards.filter(c => c.y === g3.cards[0].y).length === 2,
    `firstRow=${g3.cards.filter(c => c.y === g3.cards[0].y).length}`)
  // Two columns is the case the TUI's horizontal keys have to agree with. Aim at
  // whichever side the selected card actually HAS a neighbour on — the undock may
  // have left it in the last column, where Right correctly does nothing.
  const selC0 = selC
  const rowMates = selC0 >= 0 ? g3.cards.filter(c => c.y === g3.cards[selC0].y) : []
  const goRight = selC0 >= 0 && rowMates.some(c => c.x > g3.cards[selC0].x)
  env.key(goRight ? 'Right' : 'Left'); await sleep(900)
  const g4 = await lt.eval(GEO_JS)
  const selC1 = g4.cards.findIndex(c => c.sel)
  const okC4 = selC0 >= 0 && selC1 === selC0 + (goRight ? 1 : -1) && !!g4.cards[selC1] && !!g4.cards[selC0] &&
    g4.cards[selC1].y === g4.cards[selC0].y &&
    (goRight ? g4.cards[selC1].x > g4.cards[selC0].x : g4.cards[selC1].x < g4.cards[selC0].x)
  check(`C4 ${goRight ? 'Right' : 'Left'} selected the VISUALLY adjacent card (same row, ${goRight ? 'next' : 'previous'} column)`, okC4,
    `sel ${selC0}(${g4.cards[selC0] && g4.cards[selC0].x},${g4.cards[selC0] && g4.cards[selC0].y}) → ${selC1}(${g4.cards[selC1] && g4.cards[selC1].x},${g4.cards[selC1] && g4.cards[selC1].y})`)
  env.shot('ltree-c-undocked-2col')

  // ── (d) Width sweep (undocked): TWO columns at every width ───────────────────────
  const wid = env.activate()
  // The tile-grid contract (workspace-21uy): 2 columns everywhere. 700px is the
  // narrow end that used to collapse to 1 and 2320px the wide end that used to fan
  // out to 3 — the two extremes ResponsiveGrid's remarks record as rejected. Wide
  // panes buy WIDER cards and BIGGER type, never more columns.
  const EXP_COLS = 2
  for (const [w, h] of [[700, 900], [1000, 900], [1680, 1000], [2320, 1300]]) {
    const exp = EXP_COLS
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
    check(`D${w} at ~${w}px: exactly ${exp} columns, rendered == pushed state`,
      got && got.cols === exp && got.stateCols === exp,
      `rendered=${got && got.cols} state=${got && got.stateCols} winW=${got && got.winW}`)
    const vw = (got && got.winW) || w
    check(`D${w}b at ~${w}px: one card height for the whole page, inside ${CARD_MIN}-${CARD_MAX}px`,
      got && sameHeight(got.cards) && inBounds(got.cards),
      got && `heights=${[...new Set(got.cards.map(c => c.h))]} bounds=${CARD_MIN}-${CARD_MAX}`)
    // The point of the fluid track: wide panes buy WIDER cards and BIGGER headline
    // type, not more columns. Headline clamps 21→34px, byline 14→20px.
    const ts = expTs(vw)
    const ms = expMs(vw)
    check(`D${w}c at ~${w}px: headline type sits at the design's clamp (${ts.toFixed(1)}px / byline ${ms.toFixed(1)}px)`,
      got && got.cards.length > 0 && Math.abs(got.cards[0].ts - ts) <= 0.6 &&
      Math.abs(got.cards[0].ms - ms) <= 0.6,
      got && got.cards.length ? `title=${got.cards[0].ts} meta=${got.cards[0].ms}` : 'no cards')
    // NOTE: the old "every card is at least the design's 420px minimum track wide"
    // check lived here. It contradicts the 2-column contract by construction — a
    // docked pane is ~790px and two tracks in it are ~365px each — so it is gone
    // rather than weakened. Card WIDTH is now whatever half the pane is; what still
    // has to hold is the fixed card HEIGHT and the type scale, checked above.
    check(`D${w}d at ~${w}px: no card sliced by the grid edge`,
      got && got.cards.length > 0 && unclipped(got).length === 0,
      got && `clipped=${JSON.stringify(unclipped(got).map(c => ({ y: c.y, bottom: c.bottom })))} gridBottom=${got.grid.bottom} winH=${got.winH}`)
    if (got) fillCheck(got, `D${w}e at ~${w}px:`)
    env.shot(`ltree-d-${w}px-${exp}col`)
  }

  // ── (e) Enter OPENS the selected story: live page pane navigates to ITS url and
  //    the pane hides for the reader view ──────────────────────────────────────────
  // Re-dock first (the sweep left the sidecar out): "Enter navigates the live page
  // pane" is only a real claim while there IS a live page pane on screen.
  env.key('bar'); await sleep(600)
  for (let i = 0; i < 30; i++) { const s = await term.eval('window.__wc.state()'); if (s?.revealed === true) break; await sleep(400) }
  await sleep(2000)
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
  // …and the reader actually shows THAT story — the outcome the user reads.
  const storyPath = selItem ? new URL(selItem.url).pathname : '/none'
  check('E1b the reader is showing the SELECTED story, not some other one',
    (await pollTermText(term, `Story ${storyPath}`, 20000)).ok, `expected "Story ${storyPath}" in the terminal`)
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
