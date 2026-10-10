// ':help' reference gate (workspace-brbmb): proves the colon-command reference the user opens
// with ':help' advertises the layout chooser's REAL chord. Ctrl+L was unbound (workspace-1dmr)
// but the reference still said "site layout chooser (Ctrl+L)", sending users to a dead key.
// Headful under Xvfb ONLY (never headless). Run from shell/: node gates/gate-help-reference.mjs
import http from 'node:http'
import { Env, attach, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

buildTui('Release')

// A plain article to stand on: the launcher is an HTML pane drawn OVER the terminal, so the
// reference is checked where the user actually sees the terminal — in the reader.
const FPORT = 8177
const fixture = http.createServer((req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  res.end(`<!doctype html><meta charset="utf-8"><title>Breakwater rebuild approved</title><body><article>
<h1>Breakwater rebuild approved</h1>${'<p>The harbour commission voted on Tuesday to rebuild the northern breakwater, according to officials.</p>'.repeat(6)}</article>`)
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))
const base = `http://127.0.0.1:${FPORT}`

const env = new Env({ display: ':86', cdpPort: 9296 })
await env.up()
const shell = env.launchShell()

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('P0 shell booted to launcher', (await pollTermText(term, 'Go to URL', 30000)).ok)
  const wid = env.activate(); await sleep(500)
  let opened = { ok: false }
  for (let attempt = 0; attempt < 3 && !opened.ok; attempt++) {
    if (attempt) { env.key('Escape'); await sleep(600) }
    env.key('o'); await sleep(900)
    env.type('http:/'); await sleep(200); env.type(base.slice('http:/'.length) + '/article'); await sleep(500)
    env.key('Return')
    opened = await pollTermText(term, 'Breakwater rebuild approved', 30000)
  }
  await sleep(3000); env.key('r'); await pollTermText(term, 'northern breakwater', 15000); await sleep(1000)
  check('P0b an article is open in the reader', opened.ok)
  let ref = { ok: false }
  for (let attempt = 0; attempt < 3 && !ref.ok; attempt++) {
    if (attempt) { env.key('Escape'); await sleep(600) }
    env.type(':help'); await sleep(300); env.key('Return')
    ref = await pollTermText(term, 'site layout chooser', 10000)
  }
  check('P1 ":help" opened the colon-command reference', ref.ok)
  const flat = ref.txt.replace(/\s+/g, ' ')
  check('P2 the :layout row advertises the working chord "g l"', /site layout chooser \(g l\)/.test(flat),
    `row: ${(flat.match(/:layout.{0,60}/) || [''])[0]}`)
  check('P3 the dead "Ctrl+L" is no longer advertised for it', !/site layout chooser \(Ctrl\+L\)/.test(flat))
  env.shotWindow('help-reference', wid)
} catch (e) {
  console.log('GATE ERROR:', e)
  check('gate completed without error', false, String(e))
  try { env.shot('help-reference-error') } catch {}
} finally {
  if (process.env.GATE_DEBUG) console.log(shell.stderrText.slice(-4000))
  env.down()
  fixture.close()
}
process.exit(summary() ? 1 : 0)
