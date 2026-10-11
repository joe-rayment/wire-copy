// Overlay-cancel gate (workspace-olkxx): the link-tree HTML pane sits OVER the terminal, so it is
// hidden while a layout chooser / label-wizard card (painted into the terminal grid) is up. This
// gate proves the CANCEL exits bring it back at once: some of them clear the card without
// repainting the page, which used to leave the user looking at the terminal's plain-text tree
// until the next keypress. It does what the user does in the real shell: open a page, g l (the
// layout chooser), Escape; g l -> "Mark the links yourself" -> Escape out of the wizard. After each
// Escape the signal is the shell's own BrowserView flag (window.__wc.state().linkTreeShown), polled
// briefly with NO further keypress; a screenshot is taken so the pane can be looked at.
// Headful under Xvfb ONLY (never headless). Run from shell/: node gates/gate-overlay-pane.mjs
import http from 'node:http'
import fs from 'node:fs'
import path from 'node:path'
import { Env, ROOT, attach, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

buildTui('Release')

const FPORT = 8175
const STORIES = [
  ['Harbour commission approves the northern breakwater rebuild', '/local/2026/10/breakwater-rebuild'],
  ['City council votes to extend late-night bus routes', '/local/2026/10/late-night-buses'],
  ['Regional orchestra announces a free winter concert series', '/arts/2026/10/winter-concerts'],
  ['Port authority delays the new ferry terminal opening', '/local/2026/10/ferry-terminal-delay'],
  ['School board debates a four-day week for next autumn', '/education/2026/10/four-day-week'],
  ['Fishing fleet returns early as storm warnings widen', '/world/2026/10/fleet-returns-early'],
  ['Library announces extended weekend opening hours', '/local/2026/10/library-hours'],
  ['Hospital trust reports a drop in emergency wait times', '/health/2026/10/waiting-times-drop']
]
const fixture = http.createServer((req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  res.end(`<!doctype html><meta charset="utf-8"><title>Harbour Gazette</title><body>
<h1>Harbour Gazette</h1>
<main><ul class="river">${STORIES.map(([t, h]) => `<li><a href="${h}">${t}</a> <time datetime="2026-10-10T08:00:00Z">Oct 10</time></li>`).join('\n')}</ul></main>`)
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))
const base = `http://127.0.0.1:${FPORT}/`

const env = new Env({ display: process.env.GATE_DISPLAY || ':95', cdpPort: process.env.GATE_CDP_PORT || 9295 })
await env.up()
const shell = env.launchShell()
// The app's own log (<root>/logs/wirecopy-YYYYMMDD.log): marks let a step read ONLY the lines it caused.
const logFile = () => { const d = new Date(); const p = n => String(n).padStart(2, '0'); return path.join(ROOT, 'logs', `wirecopy-${d.getUTCFullYear()}${p(d.getUTCMonth() + 1)}${p(d.getUTCDate())}.log`) }
const logMark = () => { try { return fs.statSync(logFile()).size } catch { return 0 } }
const logSince = mark => { try { return fs.readFileSync(logFile()).subarray(mark).toString() } catch { return '' } }

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('T0 shell booted to launcher', (await pollTermText(term, 'Go to URL', 30000)).ok)
  const wid = env.activate(); await sleep(500)

  // Open the fixture the way the user does; keystrokes can drop under load, so retry.
  let opened = { ok: false }
  for (let attempt = 0; attempt < 3 && !opened.ok; attempt++) {
    if (attempt) { env.key('Escape'); await sleep(600); env.key('Escape'); await sleep(600) }
    env.key('o'); await sleep(900)
    env.type('http:/'); await sleep(200); env.type(base.slice('http:/'.length)); await sleep(500)
    env.key('Return')
    opened = await pollTermText(term, 'breakwater', 30000)
  }
  check('T1 TUI rendered the fixture page', opened.ok)

  // The REAL visibility of the pane: the shell's BrowserView flag, polled (no keypress in between).
  const paneShown = async (want, polls = 12) => {
    let st = null
    for (let i = 0; i < polls; i++) {
      st = await term.eval('window.__wc.state()').catch(() => null)
      if (st && st.linkTreeShown === want) break
      await sleep(250)
    }
    return { ok: !!st && st.linkTreeShown === want, got: st && st.linkTreeShown }
  }
  // Escape the card and wait for its text to leave the terminal (a dropped Escape is retried once
  // the card is provably still there — never blindly, since Escape on the bare page goes back).
  const escapeCard = async needle => {
    for (let attempt = 0; attempt < 3; attempt++) {
      env.key('Escape')
      for (let i = 0; i < 16; i++) {
        await sleep(250)
        if (!(await term.eval('window.__termText()')).includes(needle)) return true
      }
    }
    return false
  }
  const openChooser = async () => {
    let chooser = { ok: false }
    for (let attempt = 0; attempt < 3 && !chooser.ok; attempt++) {
      if (attempt) { env.key('Escape'); await sleep(600) }
      env.key('g'); await sleep(400); env.key('l')
      chooser = await pollTermText(term, 'Mark the links yourself', 8000)
    }
    return chooser.ok
  }

  const base0 = await paneShown(true)
  check('T1b pane is SHOWN before any card', base0.ok, `linkTreeShown=${base0.got}`)

  // ---- Cancel 1: layout chooser -> Escape ----
  check('C1a layout chooser is up', await openChooser())
  const c1hid = await paneShown(false)
  check('C1b pane is HIDDEN while the chooser is up', c1hid.ok, `linkTreeShown=${c1hid.got}`)
  const c1gone = await escapeCard('Mark the links yourself')
  check('C1b2 the chooser card left the terminal after Escape', c1gone)
  const c1back = await paneShown(true, 16) // no further keypress: it must come back by itself
  check('C1c pane is SHOWN again right after Escape on the chooser', c1back.ok, `linkTreeShown=${c1back.got}`)
  await sleep(500)
  env.shotWindow('overlay-pane-after-chooser-escape', wid)

  // ---- Cancel 2: label-mode wizard -> Escape ----
  // The xterm buffer can keep stale text, so "chooser up" is judged by what cannot be stale: the pane
  // flag going false again (the card was freshly painted) and the app log gaining lines after the mark.
  const mark2 = logMark()
  const c2open = await openChooser()
  const c2fresh = await paneShown(false)
  check('C2a layout chooser is up again (text there AND the pane freshly hidden)', c2open && c2fresh.ok, `text=${c2open} linkTreeShown=${c2fresh.got}`)
  env.key('Return') // "Mark the links yourself"
  const lbl = await pollTermText(term, 'Label the links', 20000)
  if (!lbl.ok) {
    env.shotWindow('overlay-pane-no-label', wid)
    console.log('--- app log since the second g l ---\n' + logSince(mark2).split('\n').map(l => l.slice(0, 200)).join('\n'))
    console.log('--- terminal tail ---\n' + (await term.eval('window.__termText()')).slice(-1500))
  }
  check('C2b label mode is up', lbl.ok)
  const c2hid = await paneShown(false)
  check('C2c pane is HIDDEN while the label card is up', c2hid.ok, `linkTreeShown=${c2hid.got}`)
  await sleep(600)
  // ---- Card geometry (workspace-rgf1i): the card must FIT the docked terminal column. ----
  // The docked terminal is only ~70 cols wide. A card laid out for a wider grid overruns the right
  // border and xterm wraps each row's tail into col 0 of the NEXT row (stray glyphs at the far left,
  // no right border). Read what the user sees: the last `cols` cells of each buffer row (the buffer
  // keeps stale text beyond `cols` from before the dock resize; it is never drawn, so it is cut).
  {
    const st = await term.eval('window.__wc.state()')
    const dims = await term.eval('window.__dims()')
    const cols = dims.cols
    const cellW = st.termBounds.width / cols
    const all = (await term.eval('window.__termText()')).split('\n')
    const rows = all.map(l => l.slice(0, cols)) // what is on screen
    let top = -1
    for (let i = all.length - 1; i >= 0; i--) if (all[i].includes('╭') && all[i].includes('Mark the links')) { top = i; break }
    let bot = -1
    if (top >= 0) for (let i = top + 1; i < all.length; i++) if (all[i].includes('╰')) { bot = i; break }
    const dump = () => console.log(
      `      card rows (cols=${cols}, termBounds.width=${st.termBounds.width}px, cell=${cellW.toFixed(2)}px; "|" marks col ${cols}):\n` +
      rows.slice(Math.max(0, top), Math.min(rows.length, bot + 2)).map((r, k) =>
        `      ${String(top + k).padStart(3)} ${JSON.stringify(r.padEnd(cols)).slice(0, -1)}|"  end=${JSON.stringify(r.trimEnd().slice(-1))}@${r.trimEnd().length - 1}  col0=${JSON.stringify(r[0] ?? '')}`).join('\n'))
    if (top < 0 || bot < 0) {
      check('C2e card found in the terminal buffer (top "╭ Mark the links", bottom "╰")', false, `top=${top} bottom=${bot}`)
      console.log('--- terminal tail ---\n' + rows.slice(-18).join('\n'))
    } else {
      const BORDER_END = new Set(['│', '╮', '╯'])
      const topEnd = rows[top].trimEnd().length - 1
      const badEnd = []
      for (let i = top; i <= bot; i++) {
        const t = rows[i].trimEnd()
        if (!(t.length - 1 === topEnd && BORDER_END.has(t[t.length - 1]))) badEnd.push(i)
      }
      const fits = topEnd < cols && (topEnd + 1) * cellW <= st.termBounds.width
      check('C2e every card row ends in a border glyph (│ ╮ ╯) at the top row\'s right edge, inside the docked column',
        badEnd.length === 0 && fits,
        `rightEdgeCol=${topEnd} cols=${cols} rowsNotEndingInBorderAtEdge=[${badEnd.join(',')}]`)
      // Col 0 is the card's left margin (a space) - or the text tree's gutter '▌' on rows the card does
      // not cover. Any other glyph there is a tail wrapped from the end of the row above.
      // A card flush left owns col 0, so there it must be exactly its own left border:
      // '╭' on the top row, '│' between, '╰' on the bottom row.
      const flushLeft = rows[top].indexOf('╭') === 0
      const expectCol0 = i => !flushLeft || i > bot ? new Set([' ', '▌'])
        : new Set([i === top ? '╭' : i === bot ? '╰' : '│'])
      const stray = []
      for (let i = top; i <= bot + 1 && i < rows.length; i++) if (!expectCol0(i).has(rows[i][0] ?? ' ')) stray.push(`${i}:${JSON.stringify(rows[i][0])}`)
      check('C2f no stray glyph wrapped into col 0 of the card rows (or the row below the card)', stray.length === 0, `strayCol0=[${stray.join(' ')}]`)
      if (badEnd.length || !fits || stray.length) dump()
    }
  }
  env.shotWindow('overlay-pane-label-up', wid) // the card itself, to be LOOKED at: painted, no HTML pane over it
  const c2gone = await escapeCard('Label the links')
  check('C2c2 the label card left the terminal after Escape', c2gone)
  const c2back = await paneShown(true, 16) // no further keypress
  check('C2d pane is SHOWN again once the label wizard is escaped', c2back.ok, `linkTreeShown=${c2back.got}`)
  await sleep(500)
  env.shotWindow('overlay-pane-after-label-escape', wid)
} catch (e) {
  console.log('GATE ERROR:', e)
  check('gate completed without error', false, String(e))
  try { env.shot('overlay-pane-error') } catch {}
} finally {
  if (process.env.GATE_DEBUG) console.log(shell.stderrText.slice(-4000))
  env.down()
  fixture.close()
}
process.exit(summary() ? 1 : 0)
