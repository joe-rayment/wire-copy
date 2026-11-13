// Routine catch-up (workspace-6ifpp.6): late catch-up, no rerun on relaunch, missed earlier day, armed-now, on-time.
// Run from shell/: source ~/.local/bin/x-env.sh && node gates/probe-routine-catchup.mjs --no-build
// Shots: gates/out/atlas-NN-<name>.png. Env: PROBE_DISPLAY (:96), PROBE_CDP (9315), FIXTURE_PORT (8128).
import fs from 'node:fs'
import path from 'node:path'
import { spawn, execFileSync } from 'node:child_process'
import { Env, attach, Cdp, findTarget, pollTermText, sleep, buildTui, ROOT } from './lib.mjs'

const argv = process.argv.slice(2)
// --tag <t> names the shots; --only 12,13 runs just those steps (the shell boots on the launcher).
const tag = argv.includes('--tag') ? argv[argv.indexOf('--tag') + 1] : 'rund'
// The app starts a real AudioEngine.Server on 8080 the first time a routine runs and leaves it
// behind; a leftover one makes the next shell launch hang. Remember what was here before us.
const enginePids = () => {
  try { return execFileSync('pgrep', ['-x', 'AudioEngine.Ser']).toString().split('\n').filter(Boolean).map(Number) } catch { return [] }
}
const enginesBefore = new Set(enginePids())
const killOurEngines = () => { for (const p of enginePids()) if (!enginesBefore.has(p)) { try { process.kill(p) } catch {} } }
const FPORT = Number(process.env.FIXTURE_PORT || 8128)
if (!argv.includes('--no-build')) buildTui('Release')

const env = new Env({
  display: process.env.PROBE_DISPLAY || ':95',
  cdpPort: Number(process.env.PROBE_CDP || 9315),
  screen: '1500x950x24'
})
const xdg = env.extraEnv.XDG_DATA_HOME
const dataDir = path.join(xdg, 'WireCopy')
fs.mkdirSync(dataDir, { recursive: true })
fs.writeFileSync(path.join(dataDir, 'bookmarks.json'), JSON.stringify({
  version: 1,
  bookmarks: [
    { name: 'Routine Fixture Gazette', url: `http://127.0.0.1:${FPORT}/` },
    { name: 'The Guardian', url: 'https://www.theguardian.com/international' }
  ]
}, null, 2))



const ALL = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']
const step = (sectionName) => ({
  sourceUrl: `http://127.0.0.1:${FPORT}/`, domain: `127.0.0.1:${FPORT}`,
  configUrlPattern: `^https?://(www\\.)?127\\.0\\.0\\.1:${FPORT}/?`,
  sectionName, scope: 'PinnedSection', sortOrderFallback: 0, headingAliases: [],
  takeMode: 'WholeSection', takeCount: 0, positions: [], required: false
})
const lt = (() => { const d = new Date(Date.now() + 6 * 3600e3); return String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0') })()
const rec = (n, name, steps) => ({
  id: `b2000000-0000-4000-8000-00000000000${n}`, name, enabled: true, days: ALL, localTime: lt,
  steps, outputCollectionName: name, version: 1, lastStatus: 'Never', scheduleArmedAtUtc: new Date().toISOString()
})
fs.writeFileSync(path.join(dataDir, 'schedules.json'), JSON.stringify({ version: 1, recipes: [
  rec(1, 'Morning brief', [{ ...step('World'), takeMode: 'TopN', takeCount: 2 }]), rec(2, 'Never ran', [{ ...step('Arts'), takeMode: 'TopN', takeCount: 1 }])] }, null, 2))
const fixture = spawn('python3', [path.join(ROOT, 'scripts', 'routine_fixture_site.py'),
  '--port', String(FPORT), '--write-config', xdg], { stdio: 'ignore' })
const ELOG = path.join(xdg, 'engine.log')
const engine = spawn('python3', [path.join(ROOT, 'scripts', 'fake_audio_engine.py'), '--port', '8092', '--chunk-seconds', '1', '--log', ELOG], { stdio: ['ignore', fs.openSync(path.join(xdg, 'engine.out'), 'w'), fs.openSync(path.join(xdg, 'engine.out'), 'a')] })
env.extraEnv.AUDIO_ENGINE_URL = 'http://127.0.0.1:8092'
const marker = path.join(xdg, 'START-MARK'); fs.writeFileSync(marker, 'x')

if (process.env.XVFB_PRELOAD) {
  env.up = async function () {
    this.xvfb = spawn('Xvfb', [this.display, '-screen', '0', this.screen, '-xkbdir', process.env.XVFB_XKBDIR],
      { stdio: 'ignore', env: { ...process.env, LD_PRELOAD: process.env.XVFB_PRELOAD } })
    this.procs.push(this.xvfb)
    await sleep(1200)
    this.wm = spawn('openbox', [], { stdio: 'ignore', env: { ...process.env, DISPLAY: this.display } })
    this.procs.push(this.wm)
    await sleep(700)
  }
}



let wid = null
let n = 0
const shot = name => { env.shotWindow(`${tag}-${String(++n).padStart(2, '0')}-${name}`, wid) }
await env.up()
const shell = env.launchShell()
try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal')
  const termText = async () => (await term.eval('window.__termText()').catch(() => '')).replace(/\s+/g, ' ')
  const pane = async () => { try { const t = await findTarget(env.cdpPort, x => (x.url || '').includes('launcher.html'), 4000, 'launcher'); const c = await Cdp.connect(t.webSocketDebuggerUrl); const v = JSON.parse(await c.eval('JSON.stringify(window.__launcherState())')); c.close(); return v } catch (e) { return { err: e.message } } }
  const compact = v => { const s = (v && v.state) || {}; return JSON.stringify({ vis: v && v.visible, screen: s.screen, title: s.title, tagline: s.tagline, countLabel: s.countLabel, selectedIndex: s.selectedIndex, setupHint: s.setupHint, hints: s.hints, footer: s.footer, slots: s.slots, err: v && v.err }) }
  const k = async (key, wait = 400) => { env.key(key); await sleep(wait) }
  const openHome = async () => { env.type(':routines'); await sleep(300); await k('Return', 2000) }
  const log = async (l) => { const v = await pane(); console.log(`[${l}] ${compact(v)}`); return v }
  console.log('launcher painted:', (await pollTermText(term, 'Go to URL', 40000)).ok)
  wid = env.activate()
  await sleep(2000)

  await openHome()
  await log('1 home'); shot('home')
  await k('1', 600)
  await log('1 selected')
  await k('r', 1500)
  let v = await log('1 running'); shot('running')
  let fin = false
  for (let i = 0; i < 150; i++) { await sleep(2000); v = await pane(); const t = v.state && v.state.title; if (t && t !== 'Running…' && t !== 'Stopping…') { fin = true; break } }
  console.log('finished after poll:', fin)
  if (fin) {
  await sleep(1000)
  v = await log('2 finished'); shot('finished')
  console.log('run-now@ in state:', JSON.stringify(v.state || {}).includes('run-now@'))
  try { console.log('engine POST lines:\n' + fs.readFileSync(ELOG, 'utf8').split('\n').filter(l => l.startsWith('POST')).join('\n')) } catch (e) { console.log('no engine log', e.message) }
  console.log('audio files:', execFileSync('bash', ['-c', `find ${xdg}/WireCopy/output -type f 2>&1`]).toString())

  await k('Return', 2000)
  await log('3 play'); console.log('[3 term]', (await termText()).slice(0, 300)); shot('play')

  await k('2', 400); await k('Return', 1500)
  await log('4 copy')
  try { console.log('clipboard (CDP):', await term.eval('navigator.clipboard.readText()')) } catch (e) { console.log('clipboard CDP threw:', e.message) }
  shot('copy')

  await k('3', 400); await k('Return', 1500)
  await log('5 show'); console.log('[5 term]', (await termText()).slice(0, 300)); shot('showfile')

  await k('l', 1800)
  await log('6 log'); console.log('[6 term]', (await termText()).slice(0, 600)); shot('log')
  await k('Escape', 1000)
  await log('6 back'); shot('log-back')

  await k('Escape', 1500)
  v = await log('7 home'); shot('home-after')
  const sl = (v.state && v.state.slots) || []
  let idx = sl.findIndex(x => /never ran/i.test(JSON.stringify(x)))
  console.log('never-ran slot idx', idx)
  await k(String((idx < 0 ? 1 : idx) + 1), 500)
  await k('e', 1500)
  await log('7 never ran'); console.log('[7 term]', (await termText()).slice(0, 400)); shot('never-ran')
  await k('Escape', 800)
  }
} catch (e) {
  console.log('PROBE ERROR', e.stack || e)
  console.log((shell.stderrText || '').slice(-2000))
} finally {
  killOurEngines()
  engine.kill()
  env.down()
  fixture.kill()
  process.exit(0)
}
