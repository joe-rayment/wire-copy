// Routine catch-up (workspace-6ifpp.6): late catch-up, no rerun on relaunch, missed earlier day, armed-now, on-time.
// Run from shell/: source ~/.local/bin/x-env.sh && node gates/probe-routine-catchup.mjs --no-build
// Shots: gates/out/atlas-NN-<name>.png. Env: PROBE_DISPLAY (:98), PROBE_CDP (9318), FIXTURE_PORT (8126).
import fs from 'node:fs'
import path from 'node:path'
import { spawn, execFileSync } from 'node:child_process'
import { Env, attach, Cdp, findTarget, pollTermText, sleep, buildTui, ROOT } from './lib.mjs'

const argv = process.argv.slice(2)
// --tag <t> names the shots; --only 12,13 runs just those steps (the shell boots on the launcher).
const tag = argv.includes('--tag') ? argv[argv.indexOf('--tag') + 1] : 'missed'
// The app starts a real AudioEngine.Server on 8080 the first time a routine runs and leaves it
// behind; a leftover one makes the next shell launch hang. Remember what was here before us.
const enginePids = () => {
  try { return execFileSync('pgrep', ['-x', 'AudioEngine.Ser']).toString().split('\n').filter(Boolean).map(Number) } catch { return [] }
}
const enginesBefore = new Set(enginePids())
const killOurEngines = () => { for (const p of enginePids()) if (!enginesBefore.has(p)) { try { process.kill(p) } catch {} } }
const FPORT = Number(process.env.FIXTURE_PORT || 8126)
if (!argv.includes('--no-build')) buildTui('Release')

const env = new Env({
  display: process.env.PROBE_DISPLAY || ':98',
  cdpPort: Number(process.env.PROBE_CDP || 9318),
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
const hm = d => `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
const start = new Date()
const at = mins => hm(new Date(start.getTime() + mins * 60000))
const yesterday = ALL[(start.getDay() + 6) % 7]
const rec = (n, name, days, localTime, extra = {}) => ({
  id: `b2000000-0000-4000-8000-00000000000${n}`, name, enabled: true, days, localTime,
  steps: [step('World')], outputCollectionName: name, version: 1, lastStatus: 'Never', ...extra
})
const oldStart = new Date(start.getTime() - 3 * 86400000).toISOString()
const schedules = {
  version: 1,
  recipes: [
    rec(3, 'C missed yesterday', [yesterday], '06:00',
      { lastStatus: 'Success', lastRunOccurrenceKey: '2020-01-01@06:00', lastRunLocalDate: '2020-01-01', lastRunStartedAtUtc: oldStart }),
    rec(5, 'Daily one', ALL, at(360), { scheduleArmedAtUtc: new Date().toISOString() })
  ]
}
fs.writeFileSync(path.join(dataDir, 'schedules.json'), JSON.stringify(schedules, null, 2))
console.log('seeded: daily', at(360), 'missed day', yesterday)
const fixture = spawn('python3', [path.join(ROOT, 'scripts', 'routine_fixture_site.py'),
  '--port', String(FPORT), '--write-config', xdg], { stdio: 'ignore' })

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


const pane = async () => { try { const t = await findTarget(env.cdpPort, x => (x.url || '').includes('launcher.html'), 4000, 'launcher'); const c = await Cdp.connect(t.webSocketDebuggerUrl); const v = JSON.parse(await c.eval('JSON.stringify(window.__launcherState())')); c.close(); return v } catch (e) { return null } }
let wid = null
let n = 0
const shot = name => { env.shotWindow(`${tag}-${String(++n).padStart(2, '0')}-${name}`, wid) }
await env.up()
let shell = env.launchShell()
try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal')
  const termText = async () => (await term.eval('window.__termText()').catch(() => '')).replace(/\s+/g, ' ')
  const k = async (key, wait = 600) => { env.key(key); await sleep(wait) }
  const logPane = async (label) => {
    const p = await pane(); const s = p && p.state
    if (!s) { console.log(`[${label}] pane null`); return null }
    console.log(`[${label}] visible=${p.visible} screen=${s.screen} title=${s.title} | tagline=${s.tagline} | eyebrow=${s.eyebrow} | count=${s.countLabel} sel=${s.selectedIndex} | badge=${s.footer && s.footer.badge} | footer=${JSON.stringify(s.footer)}`)
    console.log('   hints:', JSON.stringify(s.hints), 'setupHint:', s.setupHint)
    ;(s.slots || []).forEach((x, i) => console.log(`   slot${i + 1}: ${x.title} | ${x.hint} | ${x.detail} | alert=${x.alert} | dis=${x.disabled} | legend=${x.legend} | half=${x.half} | badge=${x.badge}`))
    return p
  }
  const digitOf = (s, re) => { const i = s.slots.findIndex(x => re.test(x.title || '')); return i < 0 ? null : s.slots[i].badge }
  console.log('launcher painted:', (await pollTermText(term, 'Go to URL', 40000)).ok)
  wid = env.activate()
  await sleep(2000)

  let rows = []
  for (let i = 0; i < 24; i++) {
    await sleep(5000); rows = dbRows()
    console.log(`t+${(i + 1) * 5}s rows=${rows.length}`)
    if (rows.some(r => r[0] === 'C missed yesterday')) break
  }
  await sleep(3000)
  const r1 = show('step1'); shot('db')
  env.type(':routines'); await sleep(300); await k('Return', 2500)
  let p = await logPane('2 home'); shot('home')
  let d = p && digitOf(p.state, /missed yesterday/i)
  console.log('tile digit', d)
  if (d) { await k(d, 700); await k('Return', 2000) }
  p = await logPane('3 page'); shot('page')
  d = p && digitOf(p.state, /last run/i)
  console.log('last run digit', d)
  if (d) { await k(d, 700); await k('Return', 2000) }
  p = await logPane('4 run page'); shot('run-page')
  console.log('STEP4 JSON', JSON.stringify(p && p.state))
  await k('l', 2000)
  p = await logPane('4 after l'); console.log('   term:', (await termText()).slice(0, 300)); shot('after-l')
  await k('Escape', 1500); await logPane('4 after Esc')
  await k('Escape', 1200); await k('Escape', 1500); await logPane('5 after 2 Esc (home?)')
  await k('Escape', 1500)
  p = await logPane('5 launcher'); shot('launcher')
  const rt = p && p.state.slots.find(x => x.kind === 'routines')
  console.log('routines slot', JSON.stringify(rt))
  show('step6')
} catch (e) {
  console.log('PROBE ERROR', e.stack || e)
  console.log((shell.stderrText || '').slice(-2000))
} finally {
  killOurEngines()
  env.down()
  fixture.kill()
  process.exit(0)
}
