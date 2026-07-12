// D1 gate (workspace-iyq6): child stderr must NEVER reach the terminal stream.
// node-pty merges the child's stdout+stderr into one pty; main.js now redirects the
// TUI child's fd2 to logs/shell-child-stderr-<ts>.log via an sh exec wrapper.
// This gate FORCES a stderr marker from the child tree (a WIRECOPY_SHELL_DOTNET
// wrapper that writes to fd2 before exec'ing the real dotnet) and asserts:
//   - the marker lands in the log file
//   - the marker NEVER appears in the terminal buffer
//   - after a real navigation the buffer holds no '(node:' / 'DeprecationWarning'
// Also proves the D6 (workspace-iflq) blocklist: known-noise lines are filtered from
// the desktop launcher console while a novel error passes through — tested against
// the ACTUAL list parsed out of ../run.
// Run from shell/: node gates/gate-d1-stderr.mjs
import http from 'node:http'
import { execFileSync } from 'node:child_process'
import { writeFileSync, readFileSync, readdirSync, chmodSync, mkdtempSync } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { Env, ROOT, attach, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

const FPORT = 8131
// Sectioned markup with long varied headlines so links classify as STORIES, not
// chrome/menu links (short repetitive anchors collapse into the MORE group).
const fixture = http.createServer((_req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  res.end(`<!doctype html><meta charset="utf-8"><title>Stderr Wire</title><body>
<h1>Stderr Wire</h1>
<section><h2>Front Desk</h2><ul>
<li><a href="/one.html">Silent channels: how the desk rerouted every stray diagnostic away from the newsroom floor</a></li>
<li><a href="/two.html">Ledger of losses recovered after the archive team replayed the unfiltered stream overnight</a></li>
</ul></section>`)
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))

const MARKER = 'WC_D1_STDERR_MARKER_must_never_render'
const tmp = mkdtempSync(path.join(os.tmpdir(), 'wc-gate-d1-'))
const wrapper = path.join(tmp, 'dotnet-noisy')
writeFileSync(wrapper, `#!/bin/sh
echo "${MARKER}" >&2
exec "${path.join(ROOT, 'dotnet')}" "$@"
`)
chmodSync(wrapper, 0o755)

buildTui('Release')
const env = new Env({ display: ':87', cdpPort: 9253 })
await env.up()
const t0 = Date.now()
env.launchShell({ WIRECOPY_SHELL_DOTNET: wrapper })

function newestChildStderrLog () {
  const dir = path.join(ROOT, 'logs')
  const hits = readdirSync(dir).filter(f => f.startsWith('shell-child-stderr-'))
    .map(f => path.join(dir, f))
    .filter(f => execFileSync('stat', ['-c', '%Y', f]).toString().trim() * 1000 >= t0 - 2000)
  return hits.sort().at(-1)
}

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('D1.0 TUI booted', (await pollTermText(term, 'Go to URL', 30000)).ok)

  const logFile = newestChildStderrLog()
  check('D1.1 child stderr log created', !!logFile, logFile || 'no shell-child-stderr-*.log from this run')
  const logText = logFile ? readFileSync(logFile, 'utf8') : ''
  check('D1.2 forced stderr marker landed in the LOG', logText.includes(MARKER))
  let buf = await term.eval('window.__termText()')
  check('D1.3 forced stderr marker NOT in the terminal buffer', !buf.includes(MARKER))

  // Real flow: navigate (spawns the Patchright node driver — the field DEP0169 source)
  // and re-assert the buffer stays clean of node/driver stderr.
  env.activate()
  await sleep(300)
  env.type('o'); await sleep(400)
  env.type(`http://127.0.0.1:${FPORT}/`); await sleep(300)
  env.key('Return')
  check('D1.4 navigation rendered the fixture list', (await pollTermText(term, 'Silent channels', 40000)).ok)
  await sleep(2000)
  buf = await term.eval('window.__termText()')
  check('D1.5 no node stderr in buffer after navigation',
    !buf.includes('(node:') && !buf.includes('DeprecationWarning') && !buf.includes(MARKER))
  env.shot('d1-clean-terminal')
  term.close()

  // D6 blocklist: parse the ACTUAL noise list out of ../run and replay it.
  const runSrc = readFileSync(path.join(ROOT, 'run'), 'utf8')
  const listSrc = runSrc.match(/shell_noise=\(([\s\S]*?)\n\s*\)/)?.[1] || ''
  const patterns = [...listSrc.matchAll(/-e\s+"([^"]+)"/g)].map(m => m[1])
  patterns.push('^\\[shell\\] ') // the non-debug default branch appends it
  check('D6.0 blocklist parsed from ./run', patterns.length >= 5, `${patterns.length} patterns`)
  const sample = [
    'DevTools listening on ws://127.0.0.1:9223/devtools/browser/abc',
    '0712/1035.485:ERROR:ssl_client_socket_impl.cc(878)] handshake failed; net_error -201',
    'task_policy_set TASK_CATEGORY_POLICY invalid argument',
    '[shell] mode → browser',
    'WC_NOVEL_ERROR: something genuinely new exploded'
  ].join('\n') + '\n'
  const grepArgs = ['--line-buffered', '-v', ...patterns.flatMap(p => ['-e', p])]
  const out = execFileSync('bash', ['-c', `printf '%s' "$SAMPLE" | grep ${grepArgs.map(a => `'${a}'`).join(' ')} || true`],
    { env: { ...process.env, SAMPLE: sample } }).toString()
  check('D6.1 known noise filtered from console', !out.includes('DevTools') && !out.includes('task_policy_set')
    && !out.includes('handshake failed') && !out.includes('[shell]'), JSON.stringify(out))
  check('D6.2 novel error still surfaces', out.includes('WC_NOVEL_ERROR'), JSON.stringify(out))
} catch (err) {
  console.error('GATE ERROR:', err)
  check('D1 gate completed without error', false, String(err))
  try { env.shot('d1-error') } catch {}
} finally {
  env.down()
  fixture.close()
}
process.exit(summary())
