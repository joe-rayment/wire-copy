// OS-level focus-trap gate (field report 2026-07-12): with the shell running in reader
// mode, the user must be able to switch to ANOTHER app and STAY there — no reader-refocus
// path may re-activate our window. (macOS field bug: the term-blur bounce called
// webContents.focus(), which re-activates the app; the fix guards every refocus path on
// win.isFocused(). This gate pins the invariant cross-app under a real WM.)
// Run from shell/: node gates/gate-focus-os.mjs
import { spawn } from 'node:child_process'
import { Env, attach, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

buildTui('Release')
const env = new Env({ display: ':87', cdpPort: 9253 })
await env.up()
env.launchShell()

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('F.0 TUI booted', (await pollTermText(term, 'Go to URL', 30000)).ok)
  const shellWid = env.activate()
  await sleep(500)

  // A second, foreign app window.
  const xm = spawn('xmessage', ['-geometry', '400x200+900+600', 'other app'],
    { env: { ...process.env, DISPLAY: env.display }, stdio: 'ignore' })
  env.procs.push(xm)
  env.x('search', '--sync', '--name', 'xmessage')
  const otherWid = env.x('search', '--name', 'xmessage').trim().split('\n')[0]

  console.log('\n== switch away; the shell must NOT pull focus back ==')
  env.x('windowactivate', '--sync', otherWid)
  await sleep(400)
  check('F.1 foreign window took focus', env.x('getactivewindow').trim() === otherWid)
  // 4s = ~26 enforcement ticks + any deferred bounces; ample time for a steal to show.
  await sleep(4000)
  const active = env.x('getactivewindow').trim()
  check('F.2 foreign window STILL focused after 4s (no OS-level focus steal)',
    active === otherWid, `active=${active} shell=${shellWid} other=${otherWid}`)

  console.log('\n== return to the shell; reader focus discipline resumes ==')
  env.x('windowactivate', '--sync', shellWid)
  await sleep(800)
  env.type('o')
  await sleep(400)
  env.type('fx9')
  check('F.3 back in the shell, keys drive the TUI again', (await pollTermText(term, 'fx9', 8000)).ok)
  term.close()
} catch (err) {
  console.error('GATE ERROR:', err)
  check('focus-os gate completed without error', false, String(err))
  try { env.shot('focus-os-error') } catch {}
} finally {
  env.down()
}
process.exit(summary())
