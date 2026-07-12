// P7 gate (workspace-mwer): the PACKAGED app (electron-builder dir target, built by
// shell/build-desktop.sh --dir) boots on a CLEAN profile and passes the P3-style
// navigation outcome: launcher renders, a live fixture list loads through the app's
// fetch path, the pane auto-reveals with the lens at pane width, ONE window, and NO
// Playwright Chromium is spawned or downloaded (the bundled API attaches to the shell).
// The package keeps the Chromium sandbox ON — the container-only --no-sandbox argv is
// added by THIS GATE at launch, verified absent from the package itself.
// Prereq: bash shell/build-desktop.sh --dir  (path in shell/.last-desktop-build)
// Run from shell/: node gates/gate-p7-packaged.mjs
import http from 'node:http'
import { readFileSync, readdirSync, existsSync, statSync, mkdtempSync, mkdirSync } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { spawn, spawnSync, execFileSync } from 'node:child_process'
import { Env, Cdp, targets, attach, pollTermText, check, summary, sleep, SHELL_DIR } from './lib.mjs'

const FPORT = 8140
const story = (href, text) => `<li><a href="${href}">${text}</a></li>`
const fixture = http.createServer((_req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  res.end(`<!doctype html><meta charset="utf-8"><title>Packaged Wire</title><body>
<h1>Packaged Wire</h1><section><h2>Front Desk</h2><ul>
${story('/one.html', 'Sealed cartons: the packaged edition boots from a clean profile without a repo in sight')}
${story('/two.html', 'Resource ledger confirms the bundled runtime carried every page the reader asked for')}
</ul></section>`)
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))

// ---- locate the packaged build ----
const last = path.join(SHELL_DIR, '.last-desktop-build')
check('P7.0 packaged build present (run build-desktop.sh --dir first)', existsSync(last))
const stage = readFileSync(last, 'utf8').trim()
const unpacked = path.join(stage, 'app', 'dist',
  process.arch === 'arm64' ? 'linux-arm64-unpacked' : 'linux-unpacked')
check('P7.1 unpacked dir exists', existsSync(unpacked), unpacked)
const exe = ['wirecopy-desktop', 'WireCopy', 'wirecopy'].map(n => path.join(unpacked, n)).find(existsSync)
check('P7.2 packaged executable found', !!exe, exe || readdirSync(unpacked).join(','))

// (P7.3 runs after env.up() below — it is BEHAVIORAL: launching the package without
// the container argv must ABORT with the SUID sandbox error, proving the package never
// disables the sandbox on its own. String-grepping was a false-positive trap: the
// Playwright driver's source and main.js's own comment both mention the flag.)

// The bundled API is self-contained incl. the Playwright driver, minus any browser.
const api = path.join(unpacked, 'resources', 'api')
check('P7.4 self-contained API bundled (apphost + driver, no browser download)',
  existsSync(path.join(api, 'WireCopy.API')) && existsSync(path.join(api, '.playwright')))

// ---- clean-profile boot ----
const env = new Env({ display: ':98', cdpPort: 9263 })
await env.up()

// Sandbox ON by default: in this sandbox-less container a bare launch must REFUSE to
// start ("Rather than run without sandboxing I'm aborting now") — on a real desktop the
// installer sets chrome-sandbox root:4755 and the same default just works.
const probe = spawnSync(exe, [], { env: { ...process.env, DISPLAY: env.display }, timeout: 12000, encoding: 'utf8' })
check("P7.3 sandbox ON by default: bare launch ABORTS rather than degrade",
  /Rather than run without sandboxing I'm aborting/.test(probe.stderr || ''),
  (probe.stderr || '').split('\n').find(l => l.includes('sandbox')) || 'no sandbox message')

const base = mkdtempSync(path.join(os.tmpdir(), 'wc-gate-p7-'))
mkdirSync(path.join(base, 'xdg'), { recursive: true })
mkdirSync(path.join(base, 'electron'), { recursive: true })
// Container argv only (no setuid sandbox in this box) — the PACKAGE itself is clean (P7.3).
const proc = spawn(exe, ['--no-sandbox', '--disable-dev-shm-usage'], {
  stdio: ['ignore', 'ignore', 'pipe'],
  env: {
    ...process.env,
    DISPLAY: env.display,
    WIRECOPY_SHELL_CDP_PORT: String(env.cdpPort),
    XDG_DATA_HOME: path.join(base, 'xdg'),
    WIRECOPY_SHELL_USERDATA: path.join(base, 'electron')
  }
})
proc.stderrText = ''
proc.stderr.on('data', d => { proc.stderrText += d.toString('utf8') })
env.procs.push(proc)

function playwrightChromiumRunning () {
  try {
    return execFileSync('bash', ['-c', 'pgrep -fc "[m]s-playwright.*chrom" || true']).toString().trim() !== '0'
  } catch { return false }
}

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('P7.5 packaged app boots to the launcher (clean profile)', (await pollTermText(term, 'Go to URL', 40000)).ok)

  env.activate()
  await sleep(300)
  env.type('o'); await sleep(400)
  env.type(`http://127.0.0.1:${FPORT}/`); await sleep(300)
  env.key('Return')
  check('P7.6 live fixture list rendered through the packaged fetch path',
    (await pollTermText(term, 'Sealed cartons', 60000)).ok)

  let st = null
  for (let i = 0; i < 40; i++) { st = await term.eval('window.__wc.state()'); if (st.revealed) break; await sleep(500) }
  check('P7.7 pane auto-revealed (D2 semantics in the package)', !!st?.revealed)

  let lensOk = false
  for (let i = 0; i < 30 && !lensOk; i++) {
    const list = await targets(env.cdpPort).catch(() => [])
    for (const t of list.filter(t => t.type === 'page' && t.url.includes(`127.0.0.1:${FPORT}`))) {
      const c = await Cdp.connect(t.webSocketDebuggerUrl).catch(() => null)
      if (!c) continue
      const w = await c.eval('innerWidth').catch(() => -1)
      c.close()
      if (Math.abs(w - st.pageBounds.width) <= 2) { lensOk = true; break }
    }
    if (!lensOk) await sleep(500)
  }
  check('P7.8 lens shows the fixture at pane width', lensOk)
  check('P7.9 NO Playwright Chromium spawned (attach-only)', !playwrightChromiumRunning())
  const winCount = execFileSync('bash', ['-c', `DISPLAY=${env.display} xdotool search --name 'Wire Copy' | wc -l`]).toString().trim()
  check('P7.10 exactly one OS window', winCount === '1', `windows=${winCount}`)
  env.shot('p7-packaged-running')

  // Record sizes (acceptance): unpacked dir + any compressed artifacts present.
  const sizes = execFileSync('bash', ['-c', `du -sh '${unpacked}' '${stage}/app/dist'/*.AppImage '${stage}/app/dist'/*.tar.gz 2>/dev/null || du -sh '${unpacked}'`]).toString().trim()
  console.log('artifact sizes:\n' + sizes)
  term.close()
} catch (err) {
  console.error('GATE ERROR:', err)
  check('P7 gate completed without error', false, String(err))
  console.error((proc.stderrText || '').split('\n').slice(-15).join('\n'))
  try { env.shot('p7-error') } catch {}
} finally {
  env.down()
  fixture.close()
}
process.exit(summary())
