// workspace-e9bje: how long is the window a black rectangle?
//
// Launches the shell and grabs the REAL window every ~60ms from the moment it exists,
// asking ImageMagick for each frame's mean intensity. The first frame with any light in
// it is when the user stops looking at a black box. Reports that against the moment the
// TUI's first bytes land (the shell's own `tui-first-paint` boot mark), which is when the
// window used to become non-black.
//
// Headful under Xvfb (never headless). Run from shell/: node gates/probe-boot-paint.mjs
import { execFileSync } from 'node:child_process'
import path from 'node:path'
import { Env, OUT, attach, sleep, buildTui } from './lib.mjs'

buildTui('Release')

const env = new Env({ display: ':98', cdpPort: 9308, screen: '1400x900x24' })
await env.up()

const t0 = Date.now()
const shell = env.launchShell()
const marks = []
let wid = null
let firstLit = null
let firstWhite = null
let firstShot = null

try {
  // Wait for the window, grab it once for a human to look at, then read the page's own
  // paint timeline. Screenshot polling alone cannot time this: each `import` + `convert`
  // round-trip costs ~200ms, which is longer than the interval being measured, and a
  // newly mapped window shows an Electron WHITE flash that "not black" mistakes for the
  // app. The marks below come from the frame callback that actually painted.
  for (let i = 0; i < 200 && !wid; i++) {
    try { wid = env.x('search', '--name', 'Wire Copy').trim().split('\n')[0] || null } catch {}
    if (wid) marks.push(['window mapped', Date.now() - t0])
    else await sleep(50)
  }
  // Grab early and often for a few hundred ms so at least one frame catches the chrome.
  for (let i = 0; i < 6 && wid; i++) {
    try {
      execFileSync('import', ['-display', env.display, '-window', wid,
        path.join(OUT, `bootpaint-${i}.png`)])
    } catch {}
  }
  const term = await attach(env.cdpPort, 'term.html', 'terminal')
  for (let i = 0; i < 100; i++) {
    const m = await term.eval('JSON.stringify(window.__bootMarks || null)').catch(() => null)
    const parsed = m ? JSON.parse(m) : null
    if (parsed && parsed.firstPty != null) {
      marks.push([`page: boot chrome painted at ${parsed.chromePainted}ms`, parsed.chromePainted])
      marks.push([`page: TUI's first byte at ${parsed.firstPty}ms`, parsed.firstPty])
      firstLit = parsed.chromePainted
      firstWhite = parsed.firstPty
      break
    }
    await sleep(100)
  }

  console.log('\n--- window paint ---')
  for (const [what, t] of marks) console.log(`  ${String(t).padStart(5)}ms  ${what}`)
  console.log(`\n  APP CHROME PAINTED : ${firstLit == null ? 'never — the window stayed blank' : firstLit + 'ms into the page'}`)
  console.log(`  TUI FIRST BYTE     : ${firstWhite == null ? 'n/a' : firstWhite + 'ms into the page'}`)
  console.log(`  BLANK WINDOW FOR   : ${firstLit == null ? firstWhite : firstLit}ms` +
    (firstLit == null ? '  (nothing of the app until the TUI painted)' : ''))
  console.log('  frames kept at gates/out/bootpaint-0..5.png — LOOK AT THEM')
} catch (e) {
  console.error('PROBE ERROR:', e)
  console.error(shell.stderrText.slice(-2000))
} finally { env.down() }
process.exit(0)
