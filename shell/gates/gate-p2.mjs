// P2 gate (workspace-4b14): control channel — the TUI child connects to the shell's
// UDS socket at boot and completes the hello handshake (CDP endpoint received).
// Outcome asserted from BOTH ends: the child's own log line (port-unique, appended
// after launch) and the shell's client-connected log.
// Run from shell/: node gates/gate-p2.mjs
import { readFileSync, existsSync } from 'node:fs'
import path from 'node:path'
import { Env, attach, pollTermText, check, summary, buildTui, sleep, ROOT } from './lib.mjs'

buildTui('Release')

function todayLog () {
  const d = new Date()
  const name = `wirecopy-${d.getFullYear()}${String(d.getMonth() + 1).padStart(2, '0')}${String(d.getDate()).padStart(2, '0')}.log`
  return path.join(ROOT, 'logs', name)
}
const logFile = todayLog()
const baseLen = existsSync(logFile) ? readFileSync(logFile, 'utf8').length : 0

const env = new Env({ display: ':82', cdpPort: 9243 })
await env.up()
const shell = env.launchShell()

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  const boot = await pollTermText(term, 'Go to URL', 30000)
  check('P2.1 TUI booted under the shell', boot.ok)

  const wanted = 'Shell channel connected; CDP endpoint http://127.0.0.1:9243'
  let seen = false
  for (let i = 0; i < 40 && !seen; i++) {
    const txt = existsSync(logFile) ? readFileSync(logFile, 'utf8').slice(baseLen) : ''
    seen = txt.includes(wanted)
    if (!seen) await sleep(500)
  }
  check('P2.2 child log proves hello handshake with THIS run\'s endpoint', seen, wanted)

  check('P2.3 shell saw the channel client connect', shell.stderrText.includes('channel: client connected'))
  term.close()
} catch (err) {
  console.error('GATE ERROR:', err)
  check('P2 gate completed without error', false, String(err))
} finally {
  env.down()
}
process.exit(summary())
