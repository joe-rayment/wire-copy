// Routine catch-up (workspace-6ifpp.6): late catch-up, no rerun on relaunch, missed earlier day, armed-now, on-time.
// Run from shell/: source ~/.local/bin/x-env.sh && node gates/probe-routine-catchup.mjs --no-build
// Shots: gates/out/atlas-NN-<name>.png. Env: PROBE_DISPLAY (:96), PROBE_CDP (9316), FIXTURE_PORT (8124).
import fs from 'node:fs'
import path from 'node:path'
import { spawn, execFileSync } from 'node:child_process'
import { Env, attach, Cdp, findTarget, pollTermText, sleep, buildTui, ROOT } from './lib.mjs'

const argv = process.argv.slice(2)
// --tag <t> names the shots; --only 12,13 runs just those steps (the shell boots on the launcher).
const tag = argv.includes('--tag') ? argv[argv.indexOf('--tag') + 1] : 'catchup'
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
    rec(1, 'A late catch-up', ALL, at(-2)),
    rec(2, 'B on time', ALL, at(2)),
    // history + a cadence day before today with no row; only that weekday is scheduled
    rec(3, 'C missed yesterday', [yesterday], '06:00',
      { lastStatus: 'Success', lastRunOccurrenceKey: '2020-01-01@06:00', lastRunLocalDate: '2020-01-01', lastRunStartedAtUtc: oldStart }),
    // armed just now, slot already past -> must wait for its next slot
    rec(4, 'D armed now', ALL, at(-2), { scheduleArmedAtUtc: new Date().toISOString() })
  ]
}
fs.writeFileSync(path.join(dataDir, 'schedules.json'), JSON.stringify(schedules, null, 2))
console.log('seeded: late', at(-2), 'ontime', at(2), 'missed day', yesterday)
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

let wid = null
let n = 0
const shot = name => { env.shotWindow(`${tag}-${String(++n).padStart(2, '0')}-${name}`, wid) }
await env.up()
let shell = env.launchShell()
try {
  let term = await attach(env.cdpPort, 'term.html', 'terminal')
  const termText = async () => (await term.eval('window.__termText()').catch(() => '')).replace(/\s+/g, ' ')
  const snap = async (label, wait = 1500) => {
    await sleep(wait)
    const t = await termText()
    console.log(`[${label}] shot#${String(n).padStart(2, '0')}+1 term: ${t.slice(0, 700)}`)
    shot(label)
    return t
  }
  const k = async (key, wait = 400) => { env.key(key); await sleep(wait) }
  const openHome = async () => { env.type(':routines'); await sleep(300); await k('Return', 2000) }
  console.log('launcher painted:', (await pollTermText(term, 'Go to URL', 40000)).ok)
  wid = env.activate()
  await sleep(2000)

  // narration needs an engine: fake one on 8080 if nothing real is up
  // (started by the caller before launch)
  let rows = []
  for (let i = 0; i < 40; i++) {
    await sleep(15000)
    rows = dbRows()
    const done = n => rows.some(r => r[0] === n && r[2] !== 'Running' && r[2] !== 0)
    console.log(`t+${(i + 1) * 15}s rows=${rows.length}`, rows.map(r => `${r[0]}:${r[2]}`).join(' | '))
    if (rows.some(r => r[0] === 'A late catch-up') && rows.some(r => r[0] === 'B on time') && rows.some(r => r[0] === 'C missed yesterday') && i > 8) break
  }
  await sleep(45000) // let the last run settle
  show('after first launch')
  await openHome()
  await snap('home-first-launch', 1500)
  for (let i = 0; i < 3; i++) { await k('Right', 600); await snap(`home-right${i + 1}`, 500) }
  await k('e', 1500); await snap('details-selected', 800)
  await k('Escape', 800)

  // relaunch on the same data dir: no new rows for A/B/C/D
  const before = dbRows().length
  shell.kill('SIGTERM'); await sleep(4000)
  shell = env.launchShell()
  term = await attach(env.cdpPort, 'term.html', 'terminal')
  console.log('relaunched:', (await pollTermText(term, 'Go to URL', 40000)).ok)
  wid = env.activate()
  await sleep(90000)
  const after = show('after relaunch')
  console.log(`RELAUNCH rows before=${before} after=${after.length}`)
  await openHome()
  await snap('home-after-relaunch', 1500)
} catch (e) {
  console.log('PROBE ERROR', e.stack || e)
  console.log((shell.stderrText || '').slice(-2000))
} finally {
  killOurEngines()
  env.down()
  fixture.kill()
  process.exit(0)
}
