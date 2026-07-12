// D3+D5+D8 gate (workspace-9d04, workspace-u5b0, workspace-wtut):
//   D5: fresh profile boots at ~85% of the work area centered; a resize+move is
//       remembered and RESTORED (clamped) across an app restart.
//   D3: default type scale 16px on a platform font stack; Ctrl/Cmd +/-/0 zoom changes
//       cols and the TUI reflows (pty told); the zoom level survives the restart.
//   D8: Linux shows NO menu bar — the terminal pane fills the content area exactly.
// OS-level keys via xdotool; screenshots read by a human/vision pass.
// Run from shell/: node gates/gate-d3d5d8.mjs
import { Env, attach, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

buildTui('Release')
const env = new Env({ display: ':88', cdpPort: 9254 })
await env.up()
let shell = env.launchShell()

const near = (a, b, tol) => Math.abs(a - b) <= tol

try {
  let term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('boot: TUI up', (await pollTermText(term, 'Go to URL', 30000)).ok)
  let st = await term.eval('window.__wc.state()')

  // ---- D5 default: ~85% of the 1600x1000 work area, centered ----
  check('D5.0 fresh profile: proportional default bounds',
    near(st.winBounds.width, 1360, 40) && near(st.winBounds.height, 850, 40) &&
    near(st.winBounds.x, 120, 40) && near(st.winBounds.y, 75, 40),
    JSON.stringify(st.winBounds))

  // ---- D8 (Linux): no menu bar — term pane fills the content area exactly ----
  check('D8.0 no menu bar: term pane fills content height',
    st.termBounds.height === st.contentSize[1] && st.termBounds.y === 0,
    `termBounds=${JSON.stringify(st.termBounds)} content=${JSON.stringify(st.contentSize)}`)

  // ---- D3 defaults ----
  const font0 = await term.eval('window.__font()')
  check('D3.0 default type scale is 16', font0 === 16, `font=${font0}`)
  const family = await term.eval('getComputedStyle(document.querySelector(".xterm-rows")).fontFamily')
  check('D3.1 platform font stack active (DejaVu on linux)', /DejaVu Sans Mono/.test(family), family)
  env.shot('d3-default-16px')

  // ---- D3 zoom in: cols shrink, TUI reflows (pty told) ----
  const cols0 = (await term.eval('window.__dims()')).cols
  env.activate()
  await sleep(300)
  env.key('ctrl+equal')
  env.key('ctrl+equal')
  await sleep(1200)
  const font1 = await term.eval('window.__font()')
  const cols1 = (await term.eval('window.__dims()')).cols
  check('D3.2 Ctrl+= twice → 18px', font1 === 18, `font=${font1}`)
  check('D3.3 zoom-in shrank cols', cols1 < cols0, `cols ${cols0} → ${cols1}`)
  // Layout-stable content assert: the launcher footer re-words itself per width
  // ('Go to URL' vs 'go to url'), but the demo masthead renders in every layout.
  const reflow = await pollTermText(term, 'DAILY GAZETTE', 15000)
  check('D3.4 TUI reflowed at the new scale (pty resized)', reflow.ok)
  env.shot('d3-zoomed-18px')

  // ---- D3 reset then settle on 17 for the persistence leg ----
  env.key('ctrl+0'); await sleep(800)
  check('D3.5 Ctrl+0 resets to 16', (await term.eval('window.__font()')) === 16)
  env.key('ctrl+equal'); await sleep(800)
  check('D3.6 Ctrl+= → 17 (persisted for restart leg)', (await term.eval('window.__font()')) === 17)

  // ---- D5 resize + move, then restart ----
  const wid = env.activate()
  env.x('windowsize', wid, '1100', '700')
  await sleep(400)
  env.x('windowmove', wid, '60', '80')
  await sleep(1200) // debounced save (400ms) must flush
  st = await term.eval('window.__wc.state()')
  const preQuit = st.winBounds
  check('D5.1 resize+move took (sanity)', near(preQuit.width, 1100, 30) && near(preQuit.height, 700, 30),
    JSON.stringify(preQuit))
  term.close()

  env.type('q')
  let gone = false
  for (let i = 0; i < 20; i++) { await sleep(500); if (shell.exitCode !== null) { gone = true; break } }
  check('quit: app exited', gone)

  shell = env.launchShell()
  term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('reboot: TUI up', (await pollTermText(term, 'Go to URL', 30000)).ok)
  st = await term.eval('window.__wc.state()')
  check('D5.2 bounds RESTORED across restart',
    near(st.winBounds.x, preQuit.x, 20) && near(st.winBounds.y, preQuit.y, 20) &&
    near(st.winBounds.width, preQuit.width, 20) && near(st.winBounds.height, preQuit.height, 20),
    `pre=${JSON.stringify(preQuit)} post=${JSON.stringify(st.winBounds)}`)
  const font2 = await term.eval('window.__font()')
  check('D3.7 zoom level SURVIVED the restart (17px)', font2 === 17, `font=${font2}`)
  env.shot('d5-restored-bounds')
  term.close()
} catch (err) {
  console.error('GATE ERROR:', err)
  check('gate completed without error', false, String(err))
  try { env.shot('d3d5d8-error') } catch {}
} finally {
  env.down()
}
process.exit(summary())
