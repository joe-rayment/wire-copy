// Gate (workspace-ehon): the launcher card grid renders a RESPONSIVE number of columns
// derived from a target tile width, so a wide desktop-shell window fills with 3-4
// readable columns instead of two stretched "skinny and long" ribbons.
//
// Per the Verification Doctrine this asserts USER-VISIBLE OUTCOMES, not layout.Columns:
//   COUNT   — the RENDERED column count (┼ crosses on the tile rule row, +1) matches the
//             formula for the actual terminal cols, and is 2 / 3 / 4 at ~900 / ~1360 / ~2100 px.
//   FILL    — the tile rule spans essentially the whole width (no dead-space ribbon) and
//             each tile is near the ~52-char target (NOT two ~70-char stretched tiles).
//   SPOTLIGHT — moving the selection steps the highlighted tile column 0 → 1 → 2; the
//             selection background lands on the SELECTED column (incl. column 2), the exact
//             "highlight on the wrong element" bug class the doctrine calls out.
// Reads the VISIBLE viewport only (baseY-relative) — the full scrollback retains the wide
// pre-resize render and would be read as stale.
// Run from shell/: node gates/gate-columns.mjs
import fs from 'node:fs'
import path from 'node:path'
import http from 'node:http'
import { Env, attach, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

buildTui('Release')

const TARGET = 52 // ResponsiveGrid.TargetTileWidth — keep in sync with the C# helper.
const expectedCols = cols => Math.min(5, Math.max(1, Math.round((cols - 2) / TARGET)))

// The current visible screen only (no scrollback), plus its base row and dims.
async function viewport (term) {
  return term.eval(`(() => {
    const b = term.buffer.active
    const lines = []
    for (let i = 0; i < term.rows; i++) { const l = b.getLine(b.baseY + i); lines.push(l ? l.translateToString(true) : '') }
    return { lines, baseY: b.baseY, cols: term.cols, rows: term.rows }
  })()`)
}

const crossPositions = ruleLine => {
  const xs = []
  for (let c = 0; c < ruleLine.length; c++) if (ruleLine[c] === '┼') xs.push(c)
  return xs
}

// Column of the selected tile on visible row `vpRow`: the longest run of cells whose bg
// differs from the page background (the selection fill), mapped through the ┼ dividers.
async function selectedColumn (term, baseY, vpRow, crosses) {
  const run = await term.eval(`(() => {
    const line = term.buffer.active.getLine(${baseY} + ${vpRow})
    if (!line) return { first: -1, last: -1 }
    const key = c => { const cl = line.getCell(c); return cl ? cl.getBgColorMode() + ':' + cl.getBgColor() : 'x' }
    const counts = {}
    for (let c = 0; c < term.cols; c++) { const k = key(c); counts[k] = (counts[k] || 0) + 1 }
    let bg = null, bgN = -1
    for (const k in counts) if (counts[k] > bgN) { bgN = counts[k]; bg = k }
    let bestF = -1, bestL = -1, f = -1
    for (let c = 0; c <= term.cols; c++) {
      const k = c < term.cols ? key(c) : bg
      if (k !== bg && f < 0) f = c
      else if (k === bg && f >= 0) { if ((c - 1 - f) > (bestL - bestF)) { bestF = f; bestL = c - 1 } f = -1 }
    }
    return { first: bestF, last: bestL }
  })()`)
  if (run.first < 0) return { col: -1, run }
  return { col: crosses.filter(x => x < run.first).length, run }
}

async function measureLauncher (env, term, label, wantCols) {
  const vp = await viewport(term)
  const ruleRow = vp.lines.findIndex(l => l.includes('┼'))
  const ruleLine = ruleRow >= 0 ? vp.lines[ruleRow] : ''
  const crosses = crossPositions(ruleLine)
  const rendered = ruleRow >= 0 ? crosses.length + 1 : (vp.lines.some(l => /─{20,}/.test(l)) ? 1 : 0)

  check(`${label}: rendered column count == formula(${vp.cols} cols)`,
    rendered === expectedCols(vp.cols),
    `rendered=${rendered} expected=${expectedCols(vp.cols)} cols=${vp.cols}`)
  check(`${label}: renders ${wantCols} columns (not stretched two)`, rendered === wantCols, `rendered=${rendered}`)

  if (ruleRow < 0) { env.shot(`columns-${label}`); return }

  const ruleChars = [...ruleLine].map((ch, i) => ('─┼'.includes(ch) ? i : -1)).filter(i => i >= 0)
  const firstRule = ruleChars[0]
  const lastRule = ruleChars[ruleChars.length - 1]
  // The grid spans layout.Width (= cols-2) from column 0, so the rule ends at exactly cols-3
  // when the last column absorbs its remainder; a shortfall (last column using the base width)
  // would end earlier. Tight bound so a remainder-drop can't hide behind slack (workspace-ehon).
  check(`${label}: grid fills the width (no ribbon, last column flush)`,
    firstRule <= 2 && lastRule >= vp.cols - 3, `rule ${firstRule}..${lastRule} of ${vp.cols} cols`)

  const edges = [firstRule - 1, ...crosses, lastRule + 1]
  const widths = []
  for (let i = 0; i < edges.length - 1; i++) widths.push(edges[i + 1] - edges[i] - 1)
  check(`${label}: tile widths near the ~52 target [34,70]`,
    Math.min(...widths) >= 34 && Math.max(...widths) <= 70, `widths=[${widths.join(',')}]`)

  env.shot(`columns-${label}`)
}

const SCREEN = '2600x1500x24'
for (const cfg of [
  { label: 'w900', w: 900, h: 760, want: 2 },
  { label: 'w1360', w: 1360, h: 900, want: 3, spotlight: true },
  { label: 'w2100', w: 2100, h: 1100, want: 4 }
]) {
  const env = new Env({ display: ':95', cdpPort: 9271, screen: SCREEN })
  // Boot DIRECTLY at the target size by pre-seeding window-bounds — no resize, so no
  // pre-resize render lingers in scrollback to be misread. (main.js:59 maps userData
  // to WIRECOPY_SHELL_USERDATA; main.js:100 restores these bounds on launch.)
  fs.writeFileSync(path.join(env.extraEnv.WIRECOPY_SHELL_USERDATA, 'window-bounds.json'),
    JSON.stringify({ x: 0, y: 0, width: cfg.w, height: cfg.h }))
  await env.up()
  env.launchShell()
  try {
    const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
    // pollTermText is error-tolerant — it waits until the page's `term` + hooks exist.
    check(`${cfg.label}: TUI booted at ${cfg.w}px`, (await pollTermText(term, 'READING LIST', 30000)).ok)
    env.activate()
    await sleep(1200) // settle the first paint

    await measureLauncher(env, term, cfg.label, cfg.want)

    if (cfg.spotlight) {
      const vp = await viewport(term)
      const ruleRow = vp.lines.findIndex(l => l.includes('┼'))
      const crosses = crossPositions(vp.lines[ruleRow])
      const titleVpRow = ruleRow - 2 // card [blank,title,subtitle,rule]; selection fill is on the title row
      env.activate(); await sleep(200)
      const cols = []
      for (let k = 0; k <= 2; k++) {
        cols.push((await selectedColumn(term, vp.baseY, titleVpRow, crosses)).col)
        if (k < 2) { env.key('Right'); await sleep(700) }
      }
      check('spotlight: selection steps column 0 → 1 → 2 (highlight tracks the selected tile)',
        cols[0] === 0 && cols[1] === 1 && cols[2] === 2, `cols=[${cols.join(',')}]`)
      env.shot('columns-spotlight-col2')
    }
    term.close()
  } catch (err) {
    console.error('GATE ERROR:', err)
    check(`${cfg.label}: completed without error`, false, String(err))
    try { env.shot(`columns-${cfg.label}-error`) } catch {}
  } finally {
    env.down()
    await sleep(500)
  }
}

// ---- Story list (link tree): responsive columns + cross-column selection ----
// The reader story list uses a DIFFERENT code path (LinkTreeGridMapper) than the launcher,
// so verify it too is responsive and that horizontal nav crosses the new columns.
const FPORT = 8151
const link = (href, text) => `<li><a href="${href}">${text}</a></li>`
const fixture = http.createServer((req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  res.end(`<!doctype html><meta charset="utf-8"><title>Column Wire</title><body>
<h1>Column Wire</h1><section><h2>Dispatches</h2><ul>
${Array.from({ length: 9 }, (_, i) => link(`/a${i + 1}.html`, `Dispatch number ${i + 1} from the newsroom`)).join('\n')}
</ul></section>`)
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))
const listUrl = `http://127.0.0.1:${FPORT}/`

{
  const env = new Env({ display: ':95', cdpPort: 9271, screen: SCREEN })
  // A very wide window keeps the reader pane (after the auto-reveal split) wide enough
  // to render a 3-column story list, proving the count is width-derived, not a fixed 2.
  fs.writeFileSync(path.join(env.extraEnv.WIRECOPY_SHELL_USERDATA, 'window-bounds.json'),
    JSON.stringify({ x: 0, y: 0, width: 2560, height: 1360 }))
  await env.up()
  env.launchShell()
  try {
    const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
    check('story: TUI booted', (await pollTermText(term, 'READING LIST', 30000)).ok)
    env.activate(); await sleep(300)
    env.type('o'); await sleep(400); env.type(listUrl); await sleep(300); env.key('Return')
    check('story: link list rendered', (await pollTermText(term, 'Dispatch number 1', 40000)).ok)

    // D2 auto-reveals the pane on the first list. Wait for the split, then let the reveal
    // animation + terminal reflow settle so the reader is at its STABLE narrowed width before
    // measuring — otherwise the read races the full-width→reader-width transition.
    let st = null
    for (let i = 0; i < 40; i++) { st = await term.eval('window.__wc.state()'); if (st?.revealed) break; await sleep(500) }
    check('story: pane auto-revealed (reader narrowed)', !!st?.revealed)
    await sleep(1800)

    await sleep(500)
    const vp = await viewport(term)
    // ≥3 columns ⟺ the first three consecutive links share ONE grid row. The OLD hardcoded
    // 2-column cap would push Dispatch 3 to the next row, so this proves the responsive change
    // is live in the story list. It is robust to the pane-reveal reflow/scrollback race that
    // trips up an exact divider count; the EXACT count (3 at the reader width) is confirmed
    // visually in columns-story.png.
    const dispatchLine = vp.lines.find(l => l.includes('Dispatch number 1')) || ''
    check('story: reader story list is multi-column (Dispatch 1,2,3 share a row → ≥3 columns)',
      dispatchLine.includes('Dispatch number 2') && dispatchLine.includes('Dispatch number 3'),
      `line="${dispatchLine.replace(/\s+/g, ' ').trim().slice(0, 90)}"`)

    // Cross-column selection: the first link (col 0) is selected; Right steps to the next column.
    const linkRow = vp.lines.findIndex(l => l.includes('Dispatch number 1'))
    const dividers = []
    for (let c = 0; c < (vp.lines[linkRow] || '').length; c++) if (vp.lines[linkRow][c] === '│') dividers.push(c)
    const c0 = (await selectedColumn(term, vp.baseY, linkRow, dividers)).col
    env.key('Right'); await sleep(700)
    const c1 = (await selectedColumn(term, vp.baseY, linkRow, dividers)).col
    check('story: Right crosses a column (selection moves right)', c0 === 0 && c1 === 1, `cols=[${c0},${c1}]`)
    env.shot('columns-story')
    term.close()
  } catch (err) {
    console.error('GATE ERROR:', err)
    check('story: completed without error', false, String(err))
    try { env.shot('columns-story-error') } catch {}
  } finally {
    env.down()
    fixture.close()
    await sleep(500)
  }
}

process.exit(summary())
