// Routine Run view: open an earlier run, and a run that fails because no engine is reachable.
// Run from shell/: PROBE_DISPLAY=:97 PROBE_CDP=9317 FIXTURE_PORT=8125 node gates/probe-routine-run-fail.mjs --no-build
import fs from 'node:fs'
import path from 'node:path'
import { spawn, execFileSync } from 'node:child_process'
import { Env, attach, Cdp, findTarget, pollTermText, sleep, buildTui, ROOT } from './lib.mjs'

const argv = process.argv.slice(2)
const tag = 'runf'
const enginePids = () => { try { return execFileSync('pgrep', ['-x', 'AudioEngine.Ser']).toString().split('\n').filter(Boolean).map(Number) } catch { return [] } }
const enginesBefore = new Set(enginePids())
const killOurEngines = () => { for (const p of enginePids()) if (!enginesBefore.has(p)) { try { process.kill(p) } catch {} } }
const FPORT = Number(process.env.FIXTURE_PORT || 8125)
if (!argv.includes('--no-build')) buildTui('Release')

const env = new Env({ display: process.env.PROBE_DISPLAY || ':97', cdpPort: Number(process.env.PROBE_CDP || 9317), screen: '1500x950x24' })
const xdg = env.extraEnv.XDG_DATA_HOME
const dataDir = path.join(xdg, 'WireCopy')
fs.mkdirSync(dataDir, { recursive: true })
fs.writeFileSync(path.join(dataDir, 'bookmarks.json'), JSON.stringify({ version: 1, bookmarks: [
  { name: 'Routine Fixture Gazette', url: `http://127.0.0.1:${FPORT}/` },
  { name: 'The Guardian', url: 'https://www.theguardian.com/international' } ] }, null, 2))
const ALL = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']
const step = (sectionName) => ({
  sourceUrl: `http://127.0.0.1:${FPORT}/`, domain: `127.0.0.1:${FPORT}`,
  configUrlPattern: `^https?://(www\\.)?127\\.0\\.0\\.1:${FPORT}/?`,
  sectionName, scope: 'PinnedSection', sortOrderFallback: 0, headingAliases: [],
  takeMode: 'WholeSection', takeCount: 0, positions: [], required: false })
fs.writeFileSync(path.join(dataDir, 'schedules.json'), JSON.stringify({ version: 1, recipes: [{
  id: 'b2000000-0000-4000-8000-000000000001', name: 'Morning brief', enabled: true, days: ALL, localTime: (() => { const d = new Date(Date.now() + 6 * 3600e3); return String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0') })(),
  steps: [{ ...step('World'), takeMode: 'TopN', takeCount: 1 }], outputCollectionName: 'Morning brief', version: 1, lastStatus: 'Never',
  scheduleArmedAtUtc: new Date().toISOString() }] }, null, 2))

const fixture = spawn('python3', [path.join(ROOT, 'scripts', 'routine_fixture_site.py'), '--port', String(FPORT), '--write-config', xdg], { stdio: 'ignore' })
const engine = { kill() {} }
env.extraEnv.AUDIO_ENGINE_URL = 'http://127.0.0.1:8093'

if (process.env.XVFB_PRELOAD) {
  env.up = async function () {
    this.xvfb = spawn('Xvfb', [this.display, '-screen', '0', this.screen, '-xkbdir', process.env.XVFB_XKBDIR], { stdio: 'ignore', env: { ...process.env, LD_PRELOAD: process.env.XVFB_PRELOAD } })
    this.procs.push(this.xvfb); await sleep(1200)
    this.wm = spawn('openbox', [], { stdio: 'ignore', env: { ...process.env, DISPLAY: this.display } })
    this.procs.push(this.wm); await sleep(700)
  }
}
const dbRows = () => {
  const db = path.join(dataDir, 'wirecopy.db')
  if (!fs.existsSync(db)) return []
  try {
    return JSON.parse(execFileSync('python3', ['-I', '-c', `
import sqlite3,sys,json
c=sqlite3.connect('file:'+sys.argv[1]+'?mode=ro',uri=True)
print(json.dumps(c.execute('select RecipeName,OccurrenceKey,Status,ErrorClass,ErrorMessage,StartedAtUtc from ScheduledRuns order by StartedAtUtc').fetchall()))`, db]).toString())
  } catch (e) { return [] }
}
let wid = null, n = 0
const shot = name => { env.shotWindow(`${tag}-${String(++n).padStart(2, '0')}-${name}`, wid); return `${tag}-${String(n).padStart(2, '0')}-${name}` }
await env.up()
const shell = env.launchShell()
try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal')
  const termText = async () => (await term.eval('window.__termText()').catch(() => '')).replace(/\s+/g, ' ')
  const pane = async () => { try { const t = await findTarget(env.cdpPort, x => (x.url || '').includes('launcher.html'), 4000, 'launcher'); const c = await Cdp.connect(t.webSocketDebuggerUrl); const v = JSON.parse(await c.eval('JSON.stringify(window.__launcherState())')); c.close(); return v } catch (e) { return null } }
  const compact = v => { const s = v && v.state; return s ? JSON.stringify({ visible: v.visible, screen: s.screen, eyebrow: s.eyebrow, title: s.title, tagline: s.tagline, countLabel: s.countLabel, selectedIndex: s.selectedIndex, hints: s.hints, footer: s.footer, slots: s.slots }) : 'null' }
  const log = async (label) => { const v = await pane(); console.log(`[${label}] ${compact(v)}`); return v && v.state }
  const k = async (key, wait = 400) => { env.key(key); await sleep(wait) }
  const openHome = async () => { env.type(':routines'); await sleep(300); await k('Return', 2000) }
  const waitDone = async (max) => {
    const t0 = Date.now()
    while (Date.now() - t0 < max) { await sleep(2000); const s = (await pane())?.state; if (s && s.title && !/^Running/.test(s.title)) return (Date.now() - t0) / 1000 }
    return -1
  }
  console.log('launcher painted:', (await pollTermText(term, 'Go to URL', 40000)).ok)
  wid = env.activate(); await sleep(2000)

  const sl = s => (s?.slots || []).map(x => `${x.kind}|${x.title}|${x.hint}|${x.alert}|${x.badge}`)
  const ps = async (label) => { const s = (await pane())?.state; console.log(`[${label}] ` + JSON.stringify(s && { screen: s.screen, title: s.title, tagline: s.tagline, countLabel: s.countLabel, selectedIndex: s.selectedIndex, setupHint: s.setupHint, hints: s.hints, footer: s.footer, slots: s.slots })); return s }
  console.log('--- 1'); await openHome(); await ps('1-home'); await k('r', 1500); const r1 = await ps('1-run'); shot('run-start')
  await k('Escape', 1000); await ps('1-esc1'); await k('Escape', 1200); const l1 = await ps('1-esc2'); console.log('1 screen', l1?.screen)
  { const t0 = Date.now(); let st = null; while (Date.now() - t0 < 90000) { await sleep(2000); const r = dbRows(); st = r[0]?.[2]; if (st === 4) break } console.log('1 Status', st, 'after', (Date.now() - t0) / 1000, 's') }
  console.log('--- 2'); await sleep(3000); let s2 = (await pane())?.state; console.log('2 (3s, no key) screen', s2?.screen, 'footer', JSON.stringify(s2?.footer)); sl(s2).forEach(x => console.log('  slot', x)); shot('launcher-after')
  if (!s2?.footer?.badge) { await sleep(5000); s2 = (await pane())?.state; console.log('2 (8s, no key) footer', JSON.stringify(s2?.footer)); sl(s2).forEach(x => console.log('  slot', x)); shot('launcher-after8') }
  console.log('--- 3'); await openHome(); const s3 = await ps('3-home'); console.log('3 tile1 alert', JSON.stringify(s3?.slots?.[0]?.alert), 'badge', JSON.stringify(s3?.footer?.badge)); shot('home')
  console.log('--- 4'); await k('Return', 2000); const s4 = await ps('4-failed'); console.log('FAILED_STATE', JSON.stringify(s4)); shot('failed-page')
  { const lg = (s4?.slots || []).find(x => /log/i.test(x.title || '')); console.log('4 LOG slot', JSON.stringify(lg)) }
  await k('l', 1500); { const v = await pane(); console.log('4 log visible', v?.visible, 'screen', v?.state?.screen); const t = await termText(); const i = t.indexOf('ffmpeg'); console.log('4 first200', t.slice(0, 200)); console.log('4 hasRunLog', t.includes('Run log'), 'hasFfmpeg', i >= 0); console.log('4 around', i < 0 ? 'none' : t.slice(Math.max(0, i - 150), i + 150)); shot('runlog') }
  await k('G', 800); console.log('4 G', (await termText()).slice(0, 120)); shot('runlog-bottom')
  await k('g', 800); console.log('4 g', (await termText()).slice(0, 120))
  for (let i = 1; i <= 3; i++) { await k('Down', 500); console.log('4 Down' + i, (await termText()).slice(0, 120)) }
  await k('Escape', 1200); await ps('4-after-log-esc')
  console.log('--- 5'); await k('Return', 4000); await ps('5-after-fix'); console.log('term:', (await termText()).slice(0, 400)); try { console.log('xdg', execFileSync('pgrep', ['-a', 'xdg-open']).toString()) } catch { console.log('xdg none') }; shot('fix-pressed')
  for (let i = 0; i < 6; i++) { const s = (await pane())?.state; if (s && s.screen === 'run') break; await k('Escape', 1000); await ps(`5-esc${i + 1}`) }
  console.log('5 lit after', (await pane())?.state?.selectedIndex)


  console.log('--- 7'); await k('Escape', 1500); const s7 = await ps('7-home'); console.log('7 tile1 alert', JSON.stringify(s7?.slots?.[0]?.alert), 'badge', JSON.stringify(s7?.footer?.badge))
  await k('Escape', 1500); const s7b = (await pane())?.state; console.log('7 screen', s7b?.screen, 'footer', JSON.stringify(s7b?.footer)); sl(s7b).forEach(x => console.log('  slot', x)); shot('launcher-end')
  console.log('--- 8'); console.log('DB rows'); dbRows().forEach(r => console.log('   ', JSON.stringify(r)))
} catch (e) {
  console.log('PROBE ERROR', e.stack || e)
  console.log((shell.stderrText || '').slice(-2000))
} finally {
  try { engine.kill() } catch {}
  killOurEngines(); env.down(); fixture.kill(); process.exit(0)
}
