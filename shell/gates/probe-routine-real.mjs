// One routine run with REAL parts, in the real desktop shell (headful, under Xvfb):
// the real nytimes.com Today's Paper, the routine shape the user's logs show ("NYT daily":
// Highlights, The Front Page, Business Day), and the REAL audio-engine, started by WireCopy
// itself from a checkout exactly as on the Mac (dotnet run, real Chatterbox on this CPU).
// It PRINTS what happened - section outcomes, each article's extraction, the run row, the
// engine's job, the audio file with its chapters (ffprobe) - and saves screenshots. Reading
// that output is the check; the probe asserts nothing itself.
//
//   bash scripts/sandbox-x-env.sh && bash scripts/sandbox-audio-engine.sh        (once per container)
//   source ~/.local/bin/x-env.sh && source ~/.local/bin/ae-env.sh
//   node shell/gates/probe-routine-real.mjs [--take N] [--scheduled] [--model standard|turbo]
//                                           [--wait-hours H] [--fresh-engine-data] [--no-build]
//
//   --take N        the first N stories of each section (absent: whole sections, as the user's routine)
//   --scheduled     leave it to the scheduler (the slot passed two minutes ago) instead of pressing r
//   --model M       the engine's ChatterboxModel setting, written before WireCopy starts the engine
//   --wait-hours H  how long to wait for the run to end (default 1)
//   --worker fake   the real engine, but its narration worker is the engine's own stand-in
//                   (tests/AudioEngine.Tests/Fixtures/fake_chatterbox_worker.py: a short tone per
//                   chunk, 2.5 s each) - for checks about the engine PROCESS (restart, outage),
//                   not about the audio. Always on scratch engine data, never the real cache.
//   --case stale-engine [--from-commit SHA]
//                   an engine is already running when the run starts, and its code has changed since
//                   it started (a file's time is bumped; with --from-commit the engine is first built
//                   and started at that older commit in a second clone, then the clone is moved to the
//                   newest commit - the state of a machine whose engine has run for days)
//                   --busy: the engine is in the middle of someone else's narration at that moment
//                   --broken-code: the newer code does not build (a file that is not C# is added
//                   to a scratch copy of the checkout) - the running engine must be kept
//   --case orphan [--stale]
//                   the engine still holds unfinished jobs of earlier runs of this routine (one
//                   narrating, one waiting; one marked as this build marks them, one as an older
//                   build sent it) and one of another routine, which must be left alone and waited
//                   for. --stale: only this routine's leftovers, and the engine's code has changed
//                   since it started - the state of the Mac in logs/audio-engine.log
//   --case behind   the engine is in the middle of someone else's narration when the run starts;
//                   with --stall-seconds N shorter than that narration, the run must not be
//                   stopped for standing still while the job ahead advances
//                   --freeze-other: once this run's job is waiting, the job ahead stops getting
//                   anywhere (its worker is frozen) - the run must then be stopped for standing still
//   --stall-seconds N  WIRECOPY_ROUTINE_STALL_SECONDS for the app
//   --first-part-seconds N  (with --worker fake) the first narrated part takes N seconds, as when
//                   the narrator is still loading its model
//   --case engine-restart
//                   the engine is stopped in the middle of narration and nothing starts it again
//                   except WireCopy itself
//   --case engine-gone
//                   the engine is stopped in the middle of narration and cannot be started again
// Narration on this CPU: standard about 0.32 s per character, turbo about 0.16 s; a whole
// day's paper is hours. The engine's data (settings, narrated chunks) lives in
// $AE_SANDBOX_DIR/engine-data and is kept between runs, so a repeat narrates only what changed.
//
// Environment: PROBE_DISPLAY, PROBE_CDP, ENGINE_PORT (default 8097), PROBE_DLL (a frozen build).
// The app's working directory is the run directory, so its logs never mix with <repo>/logs;
// the engine's own output goes to <run directory>/app/logs/audio-engine.log.
import fs from 'node:fs'
import path from 'node:path'
import { spawn, execFileSync } from 'node:child_process'
import { Env, Cdp, attach, findTarget, pollTermText, sleep, buildTui, ROOT } from './lib.mjs'

const argv = process.argv.slice(2)
const opt = (name, dflt) => { const i = argv.indexOf(name); return i >= 0 ? argv[i + 1] : dflt }
const TAKE = opt('--take', null) ? Number(opt('--take')) : null
const SCHEDULED = argv.includes('--scheduled')
const MODEL = opt('--model', null)
const WAIT_S = Math.round(Number(opt('--wait-hours', '1')) * 3600)
const CASE = opt('--case', 'plain')
const FAKE_WORKER = opt('--worker', 'real') === 'fake'
const FROM_COMMIT = opt('--from-commit', null)
const STALL_S = opt('--stall-seconds', null)
const FIRST_PART_S = opt('--first-part-seconds', null)
const SITE = 'https://www.nytimes.com/section/todayspaper'
const SECTIONS = ['Highlights', 'The Front Page', 'Business Day']

let AE_HOME = process.env.AUDIO_ENGINE_HOME
const AE_DIR = process.env.AE_SANDBOX_DIR
if (!AE_HOME || !AE_DIR || !fs.existsSync(path.join(AE_HOME, 'src', 'AudioEngine.Server'))) {
  console.error('no audio-engine checkout: run scripts/sandbox-audio-engine.sh and source ~/.local/bin/ae-env.sh'); process.exit(2)
}
let NEWEST = null
if (CASE === 'stale-engine' && FROM_COMMIT) {
  // A second clone that can be moved between commits without touching the main one.
  const old = path.join(AE_DIR, 'audio-engine-old')
  NEWEST = execFileSync('git', ['-C', AE_HOME, 'rev-parse', 'HEAD']).toString().trim()
  if (!fs.existsSync(old)) {
    execFileSync('git', ['clone', '--quiet', AE_HOME, old])
    fs.mkdirSync(path.join(old, '.dotnet'), { recursive: true })
    for (const d of fs.readdirSync(path.join(AE_HOME, '.dotnet'))) fs.symlinkSync(fs.realpathSync(path.join(AE_HOME, '.dotnet', d)), path.join(old, '.dotnet', d))
  }
  execFileSync('git', ['-C', old, 'checkout', '--quiet', '--force', FROM_COMMIT])
  AE_HOME = old
}
let brokenFile = null
if (CASE === 'stale-engine' && argv.includes('--broken-code')) {
  // A scratch copy of the checkout: the file that does not compile never touches the real one.
  const copy = path.join(AE_DIR, 'audio-engine-scratch')
  fs.rmSync(copy, { recursive: true, force: true })
  execFileSync('cp', ['-a', AE_HOME, copy])
  AE_HOME = copy
  brokenFile = path.join(copy, 'src', 'AudioEngine.Server', 'ProbeDoesNotCompile.cs')
}
if (!argv.includes('--no-build') && !process.env.PROBE_DLL) buildTui('Release')

const EPORT = Number(process.env.ENGINE_PORT || 8097)
const EURL = `http://127.0.0.1:${EPORT}`
const env = new Env({ display: process.env.PROBE_DISPLAY || ':96', cdpPort: Number(process.env.PROBE_CDP || 9316), screen: '1500x950x24' })
const xdg = env.extraEnv.XDG_DATA_HOME
const base = path.dirname(xdg)
const dataDir = path.join(xdg, 'WireCopy')
const appCwd = path.join(base, 'app')
const engineData = (argv.includes('--fresh-engine-data') || FROM_COMMIT || FAKE_WORKER) ? path.join(base, 'engine-data') : path.join(AE_DIR, 'engine-data')
const engineVoices = path.join(AE_DIR, 'engine-voices')
for (const d of [dataDir, appCwd, engineData, engineVoices, path.join(dataDir, 'hierarchy')]) fs.mkdirSync(d, { recursive: true })
console.log(`[real] run directory ${base}; engine ${AE_HOME} (${git('log', '-1', '--format=%h %ad', '--date=short')}) on ${EURL}, data ${engineData}`)

function git (...a) { try { return execFileSync('git', ['-C', AE_HOME, ...a]).toString().trim() } catch { return '?' } }
const standIn = path.join(process.env.AUDIO_ENGINE_HOME, 'tests', 'AudioEngine.Tests', 'Fixtures', 'fake_chatterbox_worker.py')
// --first-part-seconds: the stand-in's first part takes that long (a narrator that is still
// loading its model). A wrapper in the run directory, so the engine's own files stay as they are.
const slowFirst = path.join(base, 'slow-first-part-worker.py')
if (FIRST_PART_S) {
  fs.writeFileSync(slowFirst, `import os, runpy, time
_sleep = time.sleep
_first = [True]
def sleep(seconds):
    if _first[0] and seconds > 0:
        _first[0] = False
        _sleep(float(os.environ['PROBE_FIRST_PART_SECONDS']))
    else:
        _sleep(seconds)
time.sleep = sleep
runpy.run_path(os.environ['PROBE_STAND_IN_WORKER'], run_name='__main__')
`)
}
const engineEnv = {
  AudioEngine__DataDir: engineData,
  AudioEngine__VoicesDir: engineVoices,
  ...(FAKE_WORKER
    ? { Chatterbox__UvPath: 'python3', Chatterbox__UvArgs: ' ', Chatterbox__WorkerRelativePath: FIRST_PART_S ? slowFirst : standIn, FAKE_CB_SLOW_MS: '2500', PROBE_FIRST_PART_SECONDS: FIRST_PART_S || '0', PROBE_STAND_IN_WORKER: standIn }
    : {})
}
const http = async (method, p, body) => {
  const r = await fetch(EURL + p, { method, headers: body ? { 'content-type': 'application/json' } : {}, body: body ? JSON.stringify(body) : undefined, signal: AbortSignal.timeout(8000) })
  const t = await r.text()
  try { return { status: r.status, json: JSON.parse(t) } } catch { return { status: r.status, text: t.slice(0, 300) } }
}
const engineJobsBrief = async () => {
  try { return ((await http('GET', '/v1/jobs?limit=10')).json?.items || []).map(j => `${j.title} = ${j.status} ${j.chunksDone}/${j.chunksTotal}`) } catch (e) { return [String(e.message).slice(0, 80)] }
}
const engineUp = async () => { try { return (await http('GET', '/healthz')).status === 200 } catch { return false } }
const stopEngine = () => { try { return execFileSync(path.join(AE_HOME, 'run'), ['stop', String(EPORT)], { env: process.env }).toString().trim() } catch (e) { return `stop failed: ${String(e.stderr || e.message).split('\n')[0]}` } }

const enginePids = () => { try { return execFileSync('pgrep', ['-f', 'AudioEngine.Serve[r]']).toString().trim().split('\n').filter(Boolean).join(',') || '(none)' } catch { return '(none)' } }
const startEngineByHand = () => spawn(path.join(AE_HOME, 'dotnet'), ['run', '--project', 'src/AudioEngine.Server', '-c', 'Release'],
  { cwd: AE_HOME, stdio: 'ignore', detached: true, env: { ...process.env, ...engineEnv, ASPNETCORE_URLS: EURL, AUTH_DISABLED: '1' } })
if (await engineUp()) { console.log(`[real] something already answers on ${EURL}: ${stopEngine()}`) }
const ROUTINE_ID = 'b2000000-0000-4000-8000-0000000000aa'
const filler = (n, what) => Array.from({ length: n }, (_, i) => `Sentence number ${i + 1} of ${what}.`).join(' ')
if (CASE === 'stale-engine' || CASE === 'orphan' || CASE === 'behind') {
  // Someone started the engine earlier; its code moves on while it keeps running.
  startEngineByHand().unref()
  let up = false
  for (let i = 0; i < 400 && !(up = await engineUp()); i++) await sleep(1000)
  if (!up) { console.error('[real] the engine did not come up'); process.exit(1) }
  if (MODEL) console.log(`[real] engine setting ChatterboxModel=${MODEL}: HTTP ${(await http('PUT', '/v1/settings', { ChatterboxModel: MODEL })).status}`)
  console.log(`[real] an engine is running before the app starts: pid ${enginePids()} at ${git('log', '-1', '--format=%h %ad', '--date=short')}; POST /v1/shutdown exists: ${(await http('GET', '/v1/storage')).status !== 404}`)
  if (argv.includes('--busy') || CASE === 'behind') {
    // Someone else's narration is in progress on that engine when the run starts.
    const text = filler(120, 'a narration that another program asked this engine for before the routine began')
    const sub = await http('POST', '/v1/jobs', { title: 'Probe: somebody else\'s narration', text })
    console.log(`[real] another job was submitted first: HTTP ${sub.status} ${sub.json?.id || sub.text || ''}`)
  }
  if (CASE === 'orphan') {
    // What runs of this routine that were cut off (the app died) leave in the engine: their jobs,
    // one being narrated and one waiting. The first is marked the way this build marks its jobs,
    // the second is what an older build sent (a reading list id and the episode's title, no more).
    for (const [title, metadata] of [
      ['WireCopy Podcast: NYT daily · Thu 19 Nov', { readingListId: 'a1000000-0000-4000-8000-000000000001', wirecopySeries: `routine:${ROUTINE_ID}` }],
      ['WireCopy Podcast: NYT daily · Fri 20 Nov', { readingListId: 'a1000000-0000-4000-8000-000000000002' }],
      ...(argv.includes('--stale') ? [] : [['WireCopy Podcast: Long reads · Fri 20 Nov', { readingListId: 'a1000000-0000-4000-8000-000000000003', wirecopySeries: 'routine:another-routine' }]])
    ]) {
      const sub = await http('POST', '/v1/jobs', { title, metadata, author: 'WireCopy', text: filler(title.includes('Long reads') ? 24 : 400, `an earlier episode, ${title}`) })
      console.log(`[real] left in the engine by an earlier run: "${title}" HTTP ${sub.status} ${sub.json?.id || sub.text || ''}`)
    }
  }
  await sleep(2500)
  if (CASE !== 'stale-engine') console.log(`[real] the engine's jobs before the app starts: ${JSON.stringify((await engineJobsBrief()))}`)
  if (CASE === 'stale-engine' && FROM_COMMIT) {
    execFileSync('git', ['-C', AE_HOME, 'checkout', '--quiet', '--force', NEWEST])
    console.log(`[real] its checkout is now at ${git('log', '-1', '--format=%h %ad', '--date=short')} (the process is still the old build)`)
  } else if (brokenFile) {
    fs.writeFileSync(brokenFile, '// Written by probe-routine-real.mjs --broken-code: newer code that does not build.\nthis is not C#\n')
    console.log(`[real] added ${brokenFile}, which does not compile (the process started before that)`)
  } else if (CASE === 'stale-engine' || argv.includes('--stale')) {
    const f = path.join(AE_HOME, 'src', 'AudioEngine.Server', 'Program.cs')
    const now = new Date(); fs.utimesSync(f, now, now)
    console.log(`[real] bumped the time of ${f} (the process started before that)`)
  }
} else if (MODEL) {
  // The model is an engine setting (the user picks it in the engine's own Settings page). Write it
  // the same way, then stop the engine again so that WireCopy starts it itself, as on the Mac.
  const pre = spawn(path.join(AE_HOME, 'dotnet'), ['run', '--project', 'src/AudioEngine.Server', '-c', 'Release'],
    { cwd: AE_HOME, stdio: 'ignore', env: { ...process.env, ...engineEnv, ASPNETCORE_URLS: EURL, AUTH_DISABLED: '1' } })
  let up = false
  for (let i = 0; i < 300 && !(up = await engineUp()); i++) await sleep(1000)
  if (!up) { console.error('[real] the engine did not come up to take the model setting'); pre.kill(); process.exit(1) }
  const put = await http('PUT', '/v1/settings', { ChatterboxModel: MODEL })
  console.log(`[real] engine setting ChatterboxModel=${MODEL}: HTTP ${put.status}; ${stopEngine()}`)
  for (let i = 0; i < 30 && await engineUp(); i++) await sleep(500)
}

const wrapper = path.join(base, 'dotnet-here.sh')
fs.writeFileSync(wrapper, '#!/bin/sh\ncd "$WC_PROBE_CWD" || exit 1\nexec "$WC_PROBE_REAL_DOTNET" "$@"\n', { mode: 0o755 })
Object.assign(env.extraEnv, {
  WIRECOPY_SHELL_DOTNET: wrapper, WC_PROBE_CWD: appCwd, WC_PROBE_REAL_DOTNET: path.join(ROOT, 'dotnet'),
  ...(process.env.PROBE_DLL ? { WIRECOPY_SHELL_TUI_DLL: process.env.PROBE_DLL } : {}),
  AUDIO_ENGINE_URL: EURL, AUDIO_ENGINE_HOME: AE_HOME, ...engineEnv,
  ...(STALL_S ? { WIRECOPY_ROUTINE_STALL_SECONDS: STALL_S } : {})
})

// What the app writes when a section of an unconfigured page is added to a routine
// (RoutinePickModeHandler: a name-only "auto-grouped" layout) and the routine itself.
const pattern = '^https?://(www\\.)?nytimes\\.com/section/todayspaper(?=[/?#]|$)'
fs.writeFileSync(path.join(dataDir, 'hierarchy', 'nytimes.com.json'), JSON.stringify([{
  domain: 'nytimes.com', urlPattern: pattern, kind: 1, version: 2, strategy: 'document-order', structuralSignature: 'doc-order:325',
  modelVersion: 'auto-grouped', createdAt: new Date().toISOString(), needsReanalyze: false,
  excludeSelectors: [], excludeUrlPatterns: [], excludeSectionTitles: [], moreSelectors: [], moreUrlPatterns: [],
  userLabels: [], userInstructions: [], userSectionNames: [],
  sections: SECTIONS.map((name, i) => ({ name, sortOrder: i, parentSelectors: [], urlPatterns: [], startCollapsed: false }))
}], null, 2))
fs.writeFileSync(path.join(dataDir, 'bookmarks.json'), JSON.stringify({ version: 1, bookmarks: [{ name: "NYT Today's Paper", url: SITE }] }, null, 2))
const ALL = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']
const hm = d => `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
const slot = SCHEDULED ? hm(new Date(Date.now() - 2 * 60000)) : hm(new Date(Date.now() + 6 * 3600e3))
fs.writeFileSync(path.join(dataDir, 'schedules.json'), JSON.stringify({
  version: 1,
  recipes: [{
    id: ROUTINE_ID, name: 'NYT daily', enabled: true, days: ALL, localTime: slot,
    steps: SECTIONS.map((sectionName, i) => ({
      sourceUrl: SITE, domain: 'nytimes.com', configUrlPattern: pattern, sectionName, scope: 'PinnedSection',
      sortOrderFallback: i, headingAliases: [], takeMode: TAKE ? 'TopN' : 'WholeSection', ...(TAKE ? { takeCount: TAKE } : {}), positions: [], required: false
    })),
    outputCollectionName: 'NYT daily', version: 1, lastStatus: 'Never',
    ...(SCHEDULED ? {} : { scheduleArmedAtUtc: new Date().toISOString() })
  }]
}, null, 2))

if (process.env.XVFB_PRELOAD) {
  env.up = async function () {
    this.xvfb = spawn('Xvfb', [this.display, '-screen', '0', this.screen, '-xkbdir', process.env.XVFB_XKBDIR],
      { stdio: 'ignore', env: { ...process.env, LD_PRELOAD: process.env.XVFB_PRELOAD } })
    this.procs.push(this.xvfb); await sleep(1200)
    this.wm = spawn('openbox', [], { stdio: 'ignore', env: { ...process.env, DISPLAY: this.display } })
    this.procs.push(this.wm); await sleep(700)
  }
}

const dbPath = path.join(dataDir, 'wirecopy.db')
const table = name => {
  if (!fs.existsSync(dbPath)) return []
  try {
    return JSON.parse(execFileSync('python3', ['-I', '-c', `
import sqlite3,sys,json
c=sqlite3.connect('file:'+sys.argv[1]+'?mode=ro',uri=True); c.row_factory=sqlite3.Row
print(json.dumps([dict(r) for r in c.execute('select * from '+sys.argv[2])]))`, dbPath, name]).toString())
  } catch { return [] }
}
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
  return fs.readdirSync(dir).filter(f => /^wirecopy-\d+\.log$/.test(f)).sort().map(f => fs.readFileSync(path.join(dir, f), 'utf8')).join('')
}
const count = (text, needle) => text.split(needle).length - 1
// The engine's own view of its jobs: status, chunks, what it is doing, skipped chapters.
const engineJobs = async () => {
  try {
    const r = await http('GET', '/v1/jobs?limit=8')
    const items = (r.json && (r.json.items || r.json.jobs || r.json)) || []
    return (Array.isArray(items) ? items : []).map(j => ({
      id: j.id, status: j.status, title: j.title, engine: j.engine, chunks: `${j.progress?.chunksDone ?? j.chunksDone ?? '?'}/${j.progress?.chunksTotal ?? j.chunksTotal ?? '?'}`,
      activity: j.activity, error: j.error, skipped: j.skippedChapters, seconds: j.durationSeconds ?? j.audio?.durationSeconds, publication: j.publication
    }))
  } catch (e) { return [{ err: String(e.message).slice(0, 120) }] }
}

let wid = null
let shots = 0
let movedDotnet = false
const frozen = []
// This engine's narration worker processes: they carry the engine's data folder in their environment.
const workerPids = () => {
  try {
    return execFileSync('pgrep', ['-f', 'chatterbox_worker']).toString().trim().split('\n').filter(Boolean).map(Number)
      .filter(pid => { try { return fs.readFileSync(`/proc/${pid}/environ`, 'utf8').includes(engineData) } catch { return false } })
  } catch { return [] }
}
const tag = (CASE === 'plain' ? '' : CASE + '-') + (TAKE ? `take${TAKE}` : 'whole')
const shot = name => { try { env.shotWindow(`real-${tag}-${String(++shots).padStart(2, '0')}-${name}`, wid) } catch (e) { console.log('      shot failed:', String(e.message).split('\n')[0]) } }
await env.up()
const shell = env.launchShell()
let finishing = false
shell.on('exit', code => {
  if (finishing) return
  console.log(`PROBE ERROR: the shell exited (code ${code}) before the run was over`)
  console.log((shell.stderrText || '').slice(-1500))
  try { console.log('[real] ' + stopEngine()); env.down() } catch {}
  process.exit(1)
})
const t0 = Date.now()
const since = () => { const s = Math.round((Date.now() - t0) / 1000); return s < 600 ? `t+${s}s` : `t+${Math.floor(s / 60)}m` }
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
  const brief = v => { const s = (v && v.state) || {}; return JSON.stringify({ screen: s.screen, title: s.title, tagline: s.tagline, slots: (s.slots || []).map(x => `${x.title}${x.hint ? ' — ' + x.hint : ''}${x.alert ? ' !' + x.alert : ''}`), err: v && v.err }).slice(0, 1100) }
  const k = async (key, wait = 400) => { env.key(key); await sleep(wait) }
  console.log(`[real] launcher painted: ${(await pollTermText(term, 'Go to URL', 60000)).ok} (${since()}); engine pid ${enginePids()}`)
  wid = env.activate()
  await sleep(2500)
  if (!SCHEDULED) {
    env.type(':routines'); await sleep(300); await k('Return', 2000)
    await k('1', 600)
    console.log(`[real] routine screen before the run: ${brief(await pane())}`)
    shot('routine')
    await k('r', 1500)
    console.log(`[real] pressed r (${since()})`)
  } else {
    console.log(`[real] waiting for the scheduler to pick up the ${slot} slot (it looks every 45 s)`)
  }

  let rows = []
  let lastLine = ''
  let lastEngine = ''
  let shotRunning = false
  let acted = false
  let actedAt = 0
  let shotOutage = false
  for (let waited = 0; waited < WAIT_S; waited += 15) {
    await sleep(15000)
    rows = runRows()
    const line = rows.map(r => `${r.occurrence}:${r.status}`).join(' | ')
    if (line !== lastLine) { console.log(`[real] ${since()} runs: ${line || '(none yet)'}`); lastLine = line }
    if (waited % 600 === 585 || (rows.length && !shotRunning && waited >= 90)) {
      const ej = JSON.stringify(await engineJobs()).slice(0, 500)
      if (ej !== lastEngine) { console.log(`[real] ${since()} engine: ${ej}`); lastEngine = ej }
    }
    if (rows.length && !shotRunning && waited >= 90) {
      shotRunning = true
      if (SCHEDULED) { env.type(':routines'); await sleep(300); await k('Return', 2000); await k('1', 600); await k('Return', 1500) }
      console.log(`[real] ${since()} while running, pane: ${brief(await pane())}`)
      shot('running')
    }
    if (waited % 3600 === 3585) shot('hourly')
    if ((CASE === 'engine-restart' || CASE === 'engine-gone') && !acted && rows.length) {
      const jobs = await engineJobs()
      const done = Number(String(jobs[0]?.chunks || '0/0').split('/')[0])
      if (jobs[0]?.status === 'running' && done >= 2) {
        acted = true
        if (CASE === 'engine-gone') { fs.renameSync(path.join(AE_HOME, 'dotnet'), path.join(AE_HOME, 'dotnet.off')); movedDotnet = true }
        console.log(`[real] ${since()} narration at ${jobs[0].chunks}; stopping the engine (pid ${enginePids()}): ${stopEngine()}${movedDotnet ? ' - and it cannot be started again' : ''}`)
        actedAt = Date.now()
      }
    }
    if (argv.includes('--freeze-other') && !acted && rows.length) {
      // The job ahead stops getting anywhere: its narration worker is frozen (SIGSTOP), as a hung model would be.
      const jobs = await engineJobs()
      if (jobs.some(j => j.status === 'pending' && /NYT daily/.test(j.title || ''))) {
        acted = true; actedAt = Date.now()
        for (const pid of workerPids()) { try { process.kill(pid, 'SIGSTOP'); frozen.push(pid) } catch {} }
        console.log(`[real] ${since()} this run's job is waiting; froze the narration worker of the job ahead (pid ${frozen.join(',') || 'none found'}): ${JSON.stringify(await engineJobsBrief())}`)
      }
    }
    if (acted && !shotOutage && Date.now() - actedAt > 20000) {
      shotOutage = true
      console.log(`[real] ${since()} 20 s after ${frozen.length ? 'the job ahead froze' : 'the engine stopped'}, pane: ${brief(await pane())}; engine pid ${enginePids()}`)
      shot(frozen.length ? 'job-ahead-frozen' : 'engine-down')
    }
    if (rows.length && rows.every(r => !isRunning(r.status))) break
  }
  await sleep(5000)
  console.log(`[real] ${since()} at the end, engine pid ${enginePids()}, pane: ${brief(await pane())}`)
  console.log(`[real] ${since()} at the end, terminal: ${(await termText()).slice(0, 900)}`)
  shot('finished')

  const log = appLog()
  console.log('\n===== ScheduledRuns')
  for (const r of runRows()) console.log('  ', JSON.stringify(r))
  console.log('===== PodcastJobs')
  for (const j of table('PodcastJobs')) console.log('  ', JSON.stringify({ status: named(JOB_STATUS, j.Status), title: j.CollectionTitle, errorClass: j.ErrorClass, error: j.ErrorMessage, started: j.StartedAtUtc, lastProgress: j.LastProgressAtUtc }))
  console.log('===== app log, counted')
  for (const needle of ['Successfully loaded page via browser', 'Successfully loaded page via HTTP', 'Extracted readable content', 'Skipping', 'timed out',
    'Bot challenge', 'falling back to HTTP', 'database is locked', "the browser isn't available", '[WRN]', '[ERR]', 'Routine run finished'])
    console.log(`   ${String(count(log, needle)).padStart(3)}  × "${needle}"`)
  console.log('===== app log, the run')
  for (const l of log.split('\n')) {
    if (/Routine run|Scheduler:|Extracted |earlier episode|waits its turn|for its turn|does not build|Building audio-engine|Generating podcast|Starting podcast|Loading http|: (Resolved|Recovered|LoadFailed|Blocked|ZeroMatch|SectionNotFound)|Semantic recovery|Skipping|timed out|Bot challenge|fall|audio-engine|Narration|narrat|[Pp]ublish|Saved|\[WRN\]|\[ERR\]|progress for|awake|sleep|links while|Couldn't load/i.test(l) &&
        !/^\s+at |No XML encryptor|Successfully loaded page/.test(l)) console.log('   ' + l.slice(0, 330))
  }
  console.log('===== engine: its jobs')
  for (const j of await engineJobs()) console.log('  ', JSON.stringify(j).slice(0, 900))
  console.log('===== engine log, counted')
  let elog = ''
  try { elog = fs.readFileSync(path.join(appCwd, 'logs', 'audio-engine.log'), 'utf8') } catch {}
  for (const needle of ['started job', 'Spawning worker', 'Recycling worker', 'chunk-', 'warn:', 'fail:', 'Traceback', 'Requeued'])
    console.log(`   ${String(count(elog, needle)).padStart(4)}  × "${needle}"`)
  for (const l of elog.split('\n')) if (/^(warn|fail|crit): /.test(l) || /Traceback|Error:/.test(l)) console.log('   ' + l.slice(0, 200))
  console.log('===== audio files')
  const out = path.join(dataDir, 'output')
  const files = fs.existsSync(out) ? fs.readdirSync(out).map(f => path.join(out, f)) : []
  if (!files.length) console.log('   (none)')
  for (const f of files) {
    console.log(`   ${fs.statSync(f).size} bytes  ${f}`)
    try {
      const p = JSON.parse(execFileSync('ffprobe', ['-v', 'error', '-show_format', '-show_chapters', '-of', 'json', f]).toString())
      console.log(`   ffprobe: format ${p.format?.format_name}, ${Math.round(Number(p.format?.duration))} s, ${p.chapters?.length ?? 0} chapter(s), title "${p.format?.tags?.title ?? ''}"`)
      for (const c of p.chapters || []) console.log(`      ${String(Math.round(Number(c.start_time))).padStart(5)} s  ${c.tags?.title ?? ''}`)
    } catch (e) { console.log('   ffprobe failed: ' + String(e.message).split('\n')[0]) }
  }
} catch (e) {
  console.log('PROBE ERROR', e.stack || e)
  console.log((shell.stderrText || '').slice(-2000))
} finally {
  finishing = true
  env.down()
  if (movedDotnet) { try { fs.renameSync(path.join(AE_HOME, 'dotnet.off'), path.join(AE_HOME, 'dotnet')) } catch {} }
  for (const pid of frozen) { try { process.kill(pid, 'SIGKILL') } catch {} }
  if (brokenFile) fs.rmSync(brokenFile, { force: true })
  console.log('[real] ' + stopEngine())
  console.log(`[real] run directory left in place: ${base}`)
  process.exit(0)
}
