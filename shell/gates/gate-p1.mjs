// P1 gate (workspace-ysmb): shell skeleton — boot, app-primary, styling ownership,
// typing round-trip, REAL window-resize reflow, single-instance, TUI-exit ⇒ app-exit.
// Run from shell/: node gates/gate-p1.mjs
import { Env, attach, pollTermText, check, summary, buildTui, sleep, targets } from './lib.mjs'

buildTui('Release')
const env = new Env({ display: ':81', cdpPort: 9241 })
await env.up()
const shell = env.launchShell()
let exited = false
shell.on('exit', () => { exited = true })

try {
  console.log('\n== P1: boot ==')
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  const boot = await pollTermText(term, 'Go to URL', 30000)
  check('P1.1 TUI launcher rendered in owned pane', boot.ok, boot.ok ? '' : boot.txt.slice(-300))
  const tiles = await pollTermText(term, 'NPR TEXT', 10000)
  check('P1.2 launcher tiles rendered', tiles.ok)
  const st0 = await term.eval('window.__wc.state()')
  check('P1.3 boot is app-primary (terminal full-width, pane hidden)',
    st0.revealed === false && st0.termBounds.width === st0.contentSize[0], JSON.stringify(st0.termBounds))
  const bg = await term.eval('getComputedStyle(document.body).backgroundColor')
  check('P1.4 pane styling is ours', bg === 'rgb(14, 14, 20)', bg)
  const wid = env.activate()
  await sleep(400)
  env.shot('p1-boot')

  console.log('\n== P1: typing round-trip ==')
  env.key('Up')
  await sleep(300)
  env.type('p1x')
  const typed = await pollTermText(term, 'p1x', 6000)
  check('P1.5 OS-level keys round-trip into the TUI URL bar', typed.ok)

  console.log('\n== P1: real window resize reflows the TUI ==')
  const colsBefore = (await term.eval('window.__dims()')).cols
  env.x('windowsize', '--sync', wid, '1100', '900')
  let dims = { cols: 0 }, ptyd = { cols: -1 }
  for (let i = 0; i < 20; i++) {
    dims = await term.eval('window.__dims()')
    ptyd = (await term.eval('window.__wc.state()')).ptyDims
    if (dims.cols < colsBefore && dims.cols === ptyd.cols) break
    await sleep(400)
  }
  check('P1.6 resize chain: xterm cols shrank and pty told',
    dims.cols > 20 && dims.cols < colsBefore && dims.cols === ptyd.cols,
    `before=${colsBefore} after=${dims.cols} pty=${ptyd.cols}`)
  const alive = await pollTermText(term, 'NPR TEXT', 8000) // stable tile text (URL bar now holds our typed chars)
  check('P1.7 TUI re-rendered at new size (still shows launcher)', alive.ok)
  await sleep(600)
  env.shot('p1-resized')

  console.log('\n== P1: single instance ==')
  const second = env.launchShell()
  let secondExit = null
  second.on('exit', code => { secondExit = code })
  await sleep(3500)
  check('P1.8 second instance exits immediately; first stays alive',
    secondExit !== null && !exited, `second exit=${secondExit} first exited=${exited}`)

  console.log('\n== P1: TUI exit ⇒ app exit ==')
  env.activate()
  await sleep(300)
  env.key('Escape') // leave URL bar edit back to tiles
  await sleep(400)
  env.type('q')
  let gone = false
  for (let i = 0; i < 20; i++) {
    await sleep(500)
    const alive2 = await targets(env.cdpPort).then(() => true).catch(() => false)
    if (exited || !alive2) { gone = true; break }
  }
  check('P1.9 TUI quit closes the whole app', gone)
  term.close()
} catch (err) {
  console.error('GATE ERROR:', err)
  check('P1 gate completed without error', false, String(err))
  try { env.shot('p1-error') } catch {}
} finally {
  env.down()
}
process.exit(summary())
