// Routine catch-up (workspace-6ifpp.6): late catch-up, no rerun on relaunch, missed earlier day, armed-now, on-time.
// Run from shell/: source ~/.local/bin/x-env.sh && node gates/probe-routine-catchup.mjs --no-build
// Shots: gates/out/atlas-NN-<name>.png. Env: PROBE_DISPLAY (:96), PROBE_CDP (9316), FIXTURE_PORT (8124).
import fs from 'node:fs'
import path from 'node:path'
import { spawn, execFileSync } from 'node:child_process'
import { Env, attach, Cdp, findTarget, pollTermText, sleep, buildTui, ROOT } from './lib.mjs'

const argv = process.argv.slice(2)
// --tag <t> names the shots; --only 12,13 runs just those steps (the shell boots on the launcher).
const tag = argv.includes('--tag') ? argv[argv.indexOf('--tag') + 1] : 'run'
// The app starts a real AudioEngine.Server on 8080 the first time a routine runs and leaves it
// behind; a leftover one makes the next shell launch hang. Remember what was here before us.
const enginePids = () => {
  try { return execFileSync('pgrep', ['-x', 'AudioEngine.Ser']).toString().split('\n').filter(Boolean).map(Number) } catch { return [] }
}
const enginesBefore = new Set(enginePids())
const killOurEngines = () => { for (const p of enginePids()) if (!enginesBefore.has(p)) { try { process.kill(p) } catch {} } }
const FPORT = Number(process.env.FIXTURE_PORT || 8124)
if (!argv.includes('--no-build')) buildTui('Release')

const env = new Env({
  display: process.env.PROBE_DISPLAY || ':96',
  cdpPort: Number(process.env.PROBE_CDP || 9316),
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
fs.writeFileSync(path.join(dataDir, 'schedules.json'), JSON.stringify({ version: 1, recipes: [{
  id: 'b2000000-0000-4000-8000-000000000001', name: 'Morning brief', enabled: true, days: ALL, localTime: (() => { const d = new Date(Date.now() + 6 * 3600e3); return String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0') })(),
  steps: [{ ...step('World'), takeMode: 'TopN', takeCount: 2 }], outputCollectionName: 'Morning brief', version: 1, lastStatus: 'Never',
  scheduleArmedAtUtc: new Date().toISOString() }] }, null, 2))
const fixture = spawn('python3', [path.join(ROOT, 'scripts', 'routine_fixture_site.py'),
  '--port', String(FPORT), '--write-config', xdg], { stdio: 'ignore' })
const ELOG = path.join(xdg, 'engine.log')
const engine = spawn('python3', [path.join(ROOT, 'scripts', 'fake_audio_engine.py'), '--port', '8091', '--chunk-seconds', '10', '--log', ELOG], { stdio: 'ignore' })
env.extraEnv.AUDIO_ENGINE_URL = 'http://127.0.0.1:8091'
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


const dbRows = () => {
  const db = path.join(dataDir, 'wirecopy.db')
  if (!fs.existsSync(db)) return []
  try {
    const out = execFileSync('python3', ['-I', '-c', `
import sqlite3,sys,json
c=sqlite3.connect('file:'+sys.argv[1]+'?mode=ro',uri=True)
cur=c.execute('select RecipeName,OccurrenceKey,Status,ErrorClass,ErrorMessage,StartedAtUtc from ScheduledRuns order by StartedAtUtc')
print(json.dumps(cur.fetchall()))`, db]).toString()
    return JSON.parse(out)
  } catch (e) { return [] }
}
const show = l => { const r = dbRows(); console.log(`[db ${l}] ${r.length} rows`); r.forEach(x => console.log('   ', JSON.stringify(x))); return r }

let wid = null
let n = 0
const shot = name => { env.shotWindow(`${tag}-${String(++n).padStart(2, '0')}-${name}`, wid); console.log(`   [shot run-${String(n).padStart(2, '0')}-${name}]`) }
const pane = async () => { try { const t = await findTarget(env.cdpPort, x => (x.url || '').includes('launcher.html'), 4000, 'launcher'); const c = await Cdp.connect(t.webSocketDebuggerUrl); const v = JSON.parse(await c.eval('JSON.stringify(window.__launcherState())')); c.close(); return v } catch (e) { return { err: e.message } } }
const J = o => JSON.stringify(o)
const sl = x => `${x.title}=${x.hint || ''}/${x.detail || ''}${x.disabled ? '(dim)' : ''}${x.badge ? '(badge:' + x.badge + ')' : ''}`
const line = v => { const s = v && v.state; if (!s) return J(v); return [s.title, (s.tagline || '').replace(/\d+s so far/, 'Ns so far'), s.countLabel, s.selectedIndex, (s.slots || []).map(sl).join(' ; ')].join(' | ') }
await env.up()
const shell = env.launchShell()
try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal')
  const k = async (key, wait = 400) => { env.key(key); await sleep(wait) }
  const openHome = async () => { env.type(':routines'); await sleep(300); await k('Return', 2000) }
  console.log('launcher painted:', (await pollTermText(term, 'Go to URL', 40000)).ok)
  wid = env.activate()
  await sleep(2000)

  console.log('=== 1')
  await openHome()
  let v = await pane(); console.log('home', line(v)); shot('home')
  await k('r', 0)
  const tr = Date.now()
  for (let i = 0; i < 10; i++) { v = await pane(); if (v.state && v.state.screen === 'run') break; await sleep(200) }
  console.log('after r: screen', v.state && v.state.screen, 'title', v.state && v.state.title, 'ms', Date.now() - tr); shot('after-r')
  console.log('=== 2-4 watch')
  let prev = '', esc = false, narrating = false
  const t0 = Date.now()
  while (Date.now() - t0 < 220000) {
    v = await pane(); const s = v.state || {}; const l = line(v)
    if (l !== prev) { console.log(`t+${Math.round((Date.now() - t0) / 1000)}s [${s.screen}] ${l}`); console.log('   tagline:', s.tagline); shot('watch'); prev = l }
    const hints = (s.slots || []).map(x => x.hint || '')
    if (!esc && hints.some(h => /reading story 1 of 2/.test(h))) {
      esc = true
      await k('Escape', 1000)
      v = await pane(); console.log('=== 3 after Esc: screen', v.state.screen, 'tile1', J(v.state.slots[0])); shot('home-during-run')
      await k('Return', 1200)
      v = await pane(); console.log('after Return: screen', v.state.screen, 'title', v.state.title); shot('reopened')
      prev = ''
      continue
    }
    if (esc && s.screen === 'run' && hints.some(h => /narrating story/.test(h))) {
      narrating = true
      console.log('=== 4 narrating'); shot('narrating')
      await k('x', 700)
      v = await pane(); console.log('after x: footer', J(v.state.footer), 'hints', J(v.state.hints)); shot('confirm')
      await k('y', 0)
      const ty = Date.now()
      for (let i = 0; i < 30; i++) {
        await sleep(500); v = await pane(); const ss = v.state || {}
        if (ss.title && ss.title !== 'Running…' && ss.title !== 'Stopping…') break
      }
      console.log('seconds to leave Running/Stopping:', ((Date.now() - ty) / 1000).toFixed(1), '| title', v.state.title, '| tagline', v.state.tagline); shot('stopped')
      break
    }
    if (s.screen === 'run' && s.title && s.title !== 'Running…' && s.title !== 'Stopping…') { console.log('run ended early'); break }
    await sleep(1500)
  }
  console.log('=== 5')
  show('after cancel')
  try { console.log(fs.readFileSync(ELOG, 'utf8').trim().split('\n').slice(-12).join('\n')) } catch (e) { console.log('no engine log', e.message) }
  console.log('=== 6')
  await k('Escape', 1500)
  v = await pane(); let s6 = v.state || {}; console.log('home screen', s6.screen, 'tile1 hint/detail/alert', J([s6.slots[0].hint, s6.slots[0].detail, s6.slots[0].alert]), 'footer', J(s6.footer)); shot('home-after')
  await k('Return', 1500)
  v = await pane(); s6 = v.state || {}; console.log('after Return screen', s6.screen, 'slots', (s6.slots || []).map(sl).join(' ; ')); shot('routine-page')
  await k('Escape', 1500); await k('Escape', 1500)
  v = await pane(); s6 = v.state || {}; console.log('launcher screen', s6.screen); (s6.slots || []).forEach(x => console.log('  ', [x.kind, x.title, x.hint, J(x.alert), x.badge].join('|'))); shot('launcher-after')
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
