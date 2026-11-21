// A routine run nobody is watching (workspace-q06ia): drives ONE real run in the desktop
// shell against the fixture site and the fake audio engine, then PRINTS what happened —
// the run row, the job row, the app's own log lines, who fetched each article, the
// episode file. It asserts nothing: read the output and the two screenshots.
//
// Run:  source ~/.local/bin/x-env.sh && node shell/gates/probe-routine-unattended.mjs --case <name> [--no-build]
//   (no ~/.local/bin/x-env.sh after a container reset? bash scripts/sandbox-x-env.sh)
//
//   lease   run-now; articles answer 403 to anything but a real browser navigation
//   db      run-now with the run view open through ~2 minutes of narration: every progress
//           report makes the app write the job row and re-read the run row at the same moment
//   long    SCHEDULED run (slot already due at launch); narration takes ~3 min with a part
//           every 30 s; stall window 45 s
//   stall   SCHEDULED run; the engine stops making progress after 2 parts; window 45 s
//   attach  run-now after the app's Playwright driver process has been killed
//   e2e     SCHEDULED run, browser-only articles, narration longer than 20 minutes, no
//           override of anything (takes ~25 minutes)
//
// Env: PROBE_DISPLAY (:96), PROBE_CDP (9316), FIXTURE_PORT (8124), ENGINE_PORT (8091),
//      PROBE_DLL (run this WireCopy.API.dll instead of the repo's Release build — a frozen
//      copy, so a long case survives a rebuild).
// The app runs with its working directory inside the run directory, so its logs/ never
// mix with the real ones in <repo>/logs.
import fs from 'node:fs'
import path from 'node:path'
import { spawn, execFileSync } from 'node:child_process'
import { Env, attach, Cdp, findTarget, pollTermText, sleep, buildTui, ROOT } from './lib.mjs'

const argv = process.argv.slice(2)
const arg = (name, dflt) => argv.includes(name) ? argv[argv.indexOf(name) + 1] : dflt
const CASE = arg('--case', 'lease')
const CASES = {
  //            scheduled  fixture args                 engine args                         app env                                    take  wait(s)
  lease:  { scheduled: false, fixture: ['--browser-only-articles'], engine: ['--chunk-seconds', '2'], appEnv: {}, take: 3, waitS: 420 },
  db:     { scheduled: false, fixture: [], engine: ['--chunk-seconds', '12'], appEnv: {}, take: 9, waitS: 600 },
  long:   { scheduled: true, fixture: [], engine: ['--chunk-seconds', '30'], appEnv: { WIRECOPY_ROUTINE_STALL_SECONDS: '45' }, take: 6, waitS: 600 },
  stall:  { scheduled: true, fixture: [], engine: ['--chunk-seconds', '10', '--stall-after', '2'], appEnv: { WIRECOPY_ROUTINE_STALL_SECONDS: '45' }, take: 6, waitS: 420 },
  attach: { scheduled: false, fixture: [], engine: ['--chunk-seconds', '2'], appEnv: {}, take: 3, waitS: 420, killDriver: true },
  e2e:    { scheduled: true, fixture: ['--browser-only-articles'], engine: ['--chunk-seconds', '215'], appEnv: {}, take: 6, waitS: 1800 }
}
const C = CASES[CASE]
if (!C) { console.error(`unknown --case ${CASE}; one of ${Object.keys(CASES).join(', ')}`); process.exit(2) }
if (!argv.includes('--no-build') && !process.env.PROBE_DLL) buildTui('Release')

const FPORT = Number(process.env.FIXTURE_PORT || 8124)
const EPORT = Number(process.env.ENGINE_PORT || 8091)
const env = new Env({
  display: process.env.PROBE_DISPLAY || ':96',
  cdpPort: Number(process.env.PROBE_CDP || 9316),
  screen: '1500x950x24'
})
const xdg = env.extraEnv.XDG_DATA_HOME
const base = path.dirname(xdg)
const dataDir = path.join(xdg, 'WireCopy')
const appCwd = path.join(base, 'app')
fs.mkdirSync(dataDir, { recursive: true })
fs.mkdirSync(appCwd, { recursive: true })
console.log(`[probe] case=${CASE} run directory ${base}`)

// The shell starts the TUI as `<dotnet> <repo>/…/WireCopy.API.dll` from the repo root, which
// puts the app's logs in the real <repo>/logs. This stand-in starts the same thing from the
// run directory instead (and from PROBE_DLL when given).
const wrapper = path.join(base, 'dotnet-here.sh')
fs.writeFileSync(wrapper, '#!/bin/sh\ncd "$WC_PROBE_CWD" || exit 1\ndll="${WC_PROBE_DLL:-$1}"; shift\nexec "$WC_PROBE_REAL_DOTNET" "$dll" "$@"\n', { mode: 0o755 })
Object.assign(env.extraEnv, {
  WIRECOPY_SHELL_DOTNET: wrapper,
  WC_PROBE_CWD: appCwd,
  WC_PROBE_REAL_DOTNET: path.join(ROOT, 'dotnet'),
  ...(process.env.PROBE_DLL ? { WC_PROBE_DLL: process.env.PROBE_DLL } : {}),
  AUDIO_ENGINE_URL: `http://127.0.0.1:${EPORT}`,
  ...C.appEnv
})

fs.writeFileSync(path.join(dataDir, 'bookmarks.json'), JSON.stringify({
  version: 1, bookmarks: [{ name: 'Routine Fixture Gazette', url: `http://127.0.0.1:${FPORT}/` }]
}, null, 2))
const ALL = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']
const hm = d => `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
// Scheduled: the slot passed two minutes ago, so the scheduler's first look runs it.
// Run-now: the slot is six hours off and the probe presses r.
const slot = C.scheduled ? hm(new Date(Date.now() - 2 * 60000)) : hm(new Date(Date.now() + 6 * 3600e3))
fs.writeFileSync(path.join(dataDir, 'schedules.json'), JSON.stringify({
  version: 1,
  recipes: [{
    id: 'b2000000-0000-4000-8000-000000000001', name: 'Morning brief', enabled: true, days: ALL, localTime: slot,
    steps: [{
      sourceUrl: `http://127.0.0.1:${FPORT}/`, domain: `127.0.0.1:${FPORT}`,
      configUrlPattern: `^https?://(www\\.)?127\\.0\\.0\\.1:${FPORT}/?`,
      sectionName: 'World', scope: 'PinnedSection', sortOrderFallback: 0, headingAliases: [],
      takeMode: 'TopN', takeCount: C.take, positions: [], required: false
    }],
    outputCollectionName: 'Morning brief', version: 1, lastStatus: 'Never',
    ...(C.scheduled ? {} : { scheduleArmedAtUtc: new Date().toISOString() })
  }]
}, null, 2))

const FLOG = path.join(base, 'fixture-requests.log')
const ELOG = path.join(base, 'engine.log')
const fixture = spawn('python3', [path.join(ROOT, 'scripts', 'routine_fixture_site.py'),
  '--port', String(FPORT), '--write-config', xdg, '--log', FLOG, ...C.fixture], { stdio: 'ignore' })
const engine = spawn('python3', [path.join(ROOT, 'scripts', 'fake_audio_engine.py'),
  '--port', String(EPORT), '--log', ELOG, ...C.engine], { stdio: 'ignore' })

if (process.env.XVFB_PRELOAD) {
  // Plain Xvfb dies in this container (no /usr/bin/xkbcomp): start it through the shim.
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

const dbPath = path.join(dataDir, 'wirecopy.db')
const py = (code, ...args) => {
  try { return execFileSync('python3', ['-I', '-c', code, ...args]).toString().trim() } catch (e) { return `ERR ${String(e.message).split('\n')[0]}` }
}
const table = name => {
  if (!fs.existsSync(dbPath)) return []
  const out = py(`
import sqlite3,sys,json
c=sqlite3.connect('file:'+sys.argv[1]+'?mode=ro',uri=True); c.row_factory=sqlite3.Row
print(json.dumps([dict(r) for r in c.execute('select * from '+sys.argv[2])]))`, dbPath, name)
  try { return JSON.parse(out) } catch { return [] }
}
const journalMode = () => fs.existsSync(dbPath)
  ? py("import sqlite3,sys\nprint(sqlite3.connect('file:'+sys.argv[1]+'?mode=ro',uri=True).execute('pragma journal_mode').fetchone()[0])", dbPath)
  : '(no database yet)'
// The enums are stored as numbers (ScheduledRunStatus, PodcastJobStatus): print the names.
const RUN_STATUS = ['Pending', 'Running', 'Completed', 'PartialSuccess', 'Failed', 'Skipped', 'Interrupted']
const JOB_STATUS = ['Pending', 'Running', 'Cancelled', 'FullSuccess', 'PartialSuccess', 'TotalFailure', 'Interrupted']
const named = (names, v) => (typeof v === 'number' && names[v]) || v
const runRows = () => table('ScheduledRuns').map(r => ({
  occurrence: r.OccurrenceKey, status: named(RUN_STATUS, r.Status), items: r.ItemCount, errorClass: r.ErrorClass, error: r.ErrorMessage,
  started: r.StartedAtUtc, finished: r.FinishedAtUtc ?? null
}))
const isRunning = s => s === 'Running' || s === 'Pending'
const appLog = () => {
  const dir = path.join(appCwd, 'logs')
  if (!fs.existsSync(dir)) return ''
  return fs.readdirSync(dir).filter(f => /^wirecopy-\d+\.log$/.test(f)).sort()
    .map(f => fs.readFileSync(path.join(dir, f), 'utf8')).join('')
}
const count = (text, needle) => text.split(needle).length - 1
// The Playwright driver the app spawned: a node process running the driver's cli.js
// whose parent chain leads to this probe's shell.
const driverPids = shellPid => {
  let out = ''
  try { out = execFileSync('ps', ['-eo', 'pid=,ppid=,args=']).toString() } catch { return [] }
  const rows = out.split('\n').map(l => l.trim().match(/^(\d+)\s+(\d+)\s+(.*)$/)).filter(Boolean)
    .map(m => ({ pid: Number(m[1]), ppid: Number(m[2]), args: m[3] }))
  const parent = new Map(rows.map(r => [r.pid, r.ppid]))
  const under = pid => { for (let p = pid, i = 0; p && i < 40; p = parent.get(p), i++) if (p === shellPid) return true; return false }
  return rows.filter(r => /run-driver/.test(r.args) && under(r.pid)).map(r => r.pid)
}

let wid = null
let shots = 0
const shot = name => { try { env.shotWindow(`unattended-${CASE}-${String(++shots).padStart(2, '0')}-${name}`, wid) } catch (e) { console.log('      shot failed:', String(e.message).split('\n')[0]) } }
await env.up()
const shell = env.launchShell()
const t0 = Date.now()
const since = () => `t+${Math.round((Date.now() - t0) / 1000)}s`
try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal')
  const termText = async () => (await term.eval('window.__termText()').catch(() => '')).replace(/\s+/g, ' ')
  const pane = async () => {
    try {
      const t = await findTarget(env.cdpPort, x => (x.url || '').includes('launcher.html'), 4000, 'launcher')
      const c = await Cdp.connect(t.webSocketDebuggerUrl)
      const v = JSON.parse(await c.eval('JSON.stringify(window.__launcherState())')); c.close(); return v
    } catch (e) { return { err: e.message } }
  }
  const brief = v => { const s = (v && v.state) || {}; return JSON.stringify({ screen: s.screen, title: s.title, tagline: s.tagline, slots: s.slots, err: v && v.err }).slice(0, 900) }
  const k = async (key, wait = 400) => { env.key(key); await sleep(wait) }
  console.log(`[probe] launcher painted: ${(await pollTermText(term, 'Go to URL', 60000)).ok} (${since()})`)
  wid = env.activate()
  await sleep(2500)

  console.log(`[probe] database journal mode after boot: ${journalMode()}`)
  if (C.killDriver) {
    for (let i = 0; i < 40 && !/Attached to the desktop shell browser/.test(appLog()); i++) await sleep(500)
    const pids = driverPids(shell.pid)
    console.log(`[probe] attached: ${/Attached to the desktop shell browser/.test(appLog())}; Playwright driver pids: ${JSON.stringify(pids)}`)
    for (const p of pids) { try { process.kill(p, 'SIGKILL') } catch (e) { console.log('kill failed', p, e.message) } }
    await sleep(1500)
    console.log(`[probe] driver pids after kill: ${JSON.stringify(driverPids(shell.pid))}`)
  }

  if (!C.scheduled) {
    env.type(':routines'); await sleep(300); await k('Return', 2000)
    await k('1', 600)
    await k('r', 1500)
    console.log(`[probe] pressed r (${since()}); pane: ${brief(await pane())}`)
  } else {
    console.log(`[probe] waiting for the scheduler to pick up the ${slot} slot (it looks every 45 s)`)
  }

  let rows = []
  let shotRunning = false
  let lastLine = ''
  for (let waited = 0; waited < C.waitS; waited += 5) {
    await sleep(5000)
    rows = runRows()
    const line = rows.map(r => `${r.occurrence}:${r.status}`).join(' | ')
    if (line !== lastLine || waited % 60 === 55) { console.log(`[probe] ${since()} runs: ${line || '(none yet)'}`); lastLine = line }
    if (rows.length && !shotRunning && waited >= 10) {
      shotRunning = true
      if (C.scheduled) { env.type(':routines'); await sleep(300); await k('Return', 2000); await k('1', 600); await k('Return', 1500) }
      console.log(`[probe] ${since()} while running, pane: ${brief(await pane())}`)
      console.log(`[probe] ${since()} while running, terminal: ${(await termText()).slice(0, 500)}`)
      shot('running')
    }
    if (rows.length && rows.every(r => !isRunning(r.status))) break
  }
  await sleep(4000)
  console.log(`[probe] ${since()} at the end, pane: ${brief(await pane())}`)
  console.log(`[probe] ${since()} at the end, terminal: ${(await termText()).slice(0, 700)}`)
  shot('finished')

  const log = appLog()
  console.log('\n===== ScheduledRuns')
  for (const r of runRows()) console.log('  ', JSON.stringify(r))
  console.log('===== PodcastJobs')
  for (const j of table('PodcastJobs')) console.log('  ', JSON.stringify({ status: named(JOB_STATUS, j.Status), title: j.CollectionTitle, errorClass: j.ErrorClass, error: j.ErrorMessage, started: j.StartedAtUtc, lastProgress: j.LastProgressAtUtc }))
  console.log(`===== database journal mode: ${journalMode()}`)
  console.log('===== app log, counted')
  for (const needle of ['Browser session creation timed out', 'Article extraction timed out', 'Successfully loaded page via browser',
    'HTTP fallback also failed', 'database is locked', "the browser isn't available", 'Re-attached', 'Routine run finished'])
    console.log(`   ${String(count(log, needle)).padStart(3)}  × "${needle}"`)
  console.log('===== app log, the run')
  for (const l of log.split('\n')) {
    if (/Routine run|Scheduler:|Extracted \d+\/\d+|Generating podcast|Loading http|: (Resolved|LoadFailed|Blocked|ZeroMatch)|timed out|Successfully loaded page via browser|fallback|database is locked|\[WRN\]|\[ERR\]|[Aa]ttach|progress for|stall/i.test(l) &&
        !/^\s+at /.test(l)) console.log('   ' + l.slice(0, 300))
  }
  console.log('===== fixture: who asked for each article (browser = real navigation, plain = scripted HTTP)')
  try { for (const l of fs.readFileSync(FLOG, 'utf8').trim().split('\n')) console.log('   ' + l) } catch { console.log('   (no article requests)') }
  console.log('===== engine: jobs received')
  try { for (const l of fs.readFileSync(ELOG, 'utf8').split('\n').filter(l => /^(POST \/v1\/jobs|CHAPTER|DELETE)/.test(l))) console.log('   ' + l.slice(0, 200)) } catch { console.log('   (engine saw nothing)') }
  console.log('===== episode files')
  console.log(execFileSync('bash', ['-c', `find "${dataDir}/output" -type f -printf '   %s bytes  %p\\n' 2>/dev/null || true`]).toString() || '   (none)')
} catch (e) {
  console.log('PROBE ERROR', e.stack || e)
  console.log((shell.stderrText || '').slice(-2000))
} finally {
  engine.kill()
  env.down()
  fixture.kill()
  console.log(`[probe] run directory left in place: ${base}`)
  process.exit(0)
}
