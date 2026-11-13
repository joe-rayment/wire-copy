// Routine page ACTIONS: drive the routine editor rendered as launcher tiles; shoot each step and print launcher state.
// Run from shell/: source ~/.local/bin/x-env.sh && node gates/probe-routine-page.mjs
// Shots: gates/out/page-NN-<name>.png. Env: PROBE_DISPLAY (:96), PROBE_CDP (9316), FIXTURE_PORT (8124).
import fs from 'node:fs'
import path from 'node:path'
import { spawn, execFileSync } from 'node:child_process'
import { Env, attach, Cdp, findTarget, pollTermText, sleep, buildTui, ROOT } from './lib.mjs'

const argv = process.argv.slice(2)
// --tag <t> names the shots; --only 12,13 runs just those steps (the shell boots on the launcher).
const tag = 'empty'
const only = null
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
    { name: 'Routine Fixture Gazette', url: `http://127.0.0.1:${FPORT}/` }
  ]
}, null, 2))

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

let wid = null
let n = 0
const t0 = Date.now()
const shot = name => { env.shotWindow(`${tag}-${String(++n).padStart(2, '0')}-${name}`, wid) }

await env.up()
const shell = env.launchShell()
try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal')
  const termText = async () => (await term.eval('window.__termText()').catch(() => '')).replace(/\s+/g, ' ')
  const paneState = async (file, fn) => {
    try {
      const t = await findTarget(env.cdpPort, x => x.url.includes(file), 500, file)
      const c = await Cdp.connect(t.webSocketDebuggerUrl)
      const v = JSON.parse(await c.eval(`JSON.stringify(window.${fn} ? (window.${fn}()||{}) : {})`))
      c.close()
      return v
    } catch { return null }
  }
  // log + shoot; the log line's file name is printed so the report can map shot -> text
  const snap = async (label, wait = 1500) => {
    await sleep(wait)
    const t = await termText()
    console.log(`[${label}] shot#${String(n + 1).padStart(2, '0')} term: ${t.slice(0, 300)}`)
    shot(label)
    return t
  }
  const k = async (key, wait = 400) => { env.key(key); await sleep(wait) }
  const ch = async (c, wait = 400) => { env.type(c); await sleep(wait) }
  const waitTree = async () => {
    for (let i = 0; i < 40; i++) {
      const st = await paneState('linktree.html', '__linkTreeState')
      if (st && st.state && (st.state.items || []).length) return true
      await sleep(500)
    }
    return false
  }
  const toLauncher = async () => { for (let i = 0; i < 5; i++) { await k('Escape', 500) } }
  const openHome = async () => { env.type(':routines'); await sleep(300); await k('Return', 2000) }
  const T = async (name, fn) => {
    if (only && !only.includes(name)) return
    console.log(`--- ${name}`)
    try { await fn() } catch (e) {
      console.log(`  STEP FAILED ${name}: ${e.message}`)
      try { shot(`${name}-failed`) } catch {}
    }
  }

  const ls = async () => {
    try {
      const t = await findTarget(env.cdpPort, x => x.url.includes('launcher.html'), 1500, 'launcher.html')
      const c = await Cdp.connect(t.webSocketDebuggerUrl)
      const v = JSON.parse(await c.eval('JSON.stringify(window.__launcherState())'))
      const active = await c.eval("document.activeElement ? (document.activeElement.tagName + '#' + document.activeElement.id + '.' + document.activeElement.className) : ''").catch(() => '')
      c.close()
      return v.state ? { ...v.state, visible: v.visible, active } : v
    } catch (e) { return { err: e.message } }
  }
  const line = async id => {
    const s = await ls()
    console.log(`[${id}] screen=${s.screen} selectedIndex=${s.selectedIndex} title=${JSON.stringify(s.slots?.[s.selectedIndex]?.title)}${s.err ? ' ERR ' + s.err : ''}`)
    return s
  }
  const sched = () => { const r = JSON.parse(fs.readFileSync(path.join(dataDir, 'schedules.json'), 'utf8')).recipes.find(x => x.id.endsWith('0001')); return `json name=${r.name} enabled=${r.enabled} steps=${r.steps.map(x => x.domain + '/' + x.sectionName).join(', ')}` }
  const full = async (id, name, wait = 1000) => {
    await sleep(wait); const s = await ls()
    console.log(`[${id}] screen=${s.screen} sel=${s.selectedIndex} slot=${JSON.stringify(s.slots?.[s.selectedIndex]?.title)} title=${JSON.stringify(s.title)} urlTyping=${JSON.stringify(s.urlTyping)} prompt=${JSON.stringify(s.promptLine)} hint=${JSON.stringify(s.setupHint)}${s.err ? ' ERR ' + s.err : ''}`)
    if (name) shot(name); return s
  }
  const J = id => console.log(`[${id}] ${sched()}`)
  const typeSlow = async t => { for (let i = 0; i < t.length; i += 3) { env.type(t.slice(i, i + 3)); await sleep(350) } }
  const step2 = async (id, name, wait = 1200) => { await sleep(wait); const s = await line(id); shot(name); return s }

  console.log('launcher painted:', (await pollTermText(term, 'Go to URL', 40000)).ok)
  wid = env.activate()
  await sleep(2000)

  console.log('launcher painted:', (await pollTermText(term, 'Go to URL', 40000)).ok)
  wid = env.activate()
  await sleep(2000)
  const P = async (id, shotName, wait = 1200) => {
    await sleep(wait); const s = await ls()
    const sl = s.slots || []
    console.log(`[${id}] screen=${s.screen} sel=${s.selectedIndex} slot=${JSON.stringify(sl[s.selectedIndex]?.title)} title=${JSON.stringify(s.title)} eyebrow=${JSON.stringify(s.eyebrow)} count=${JSON.stringify(s.countLabel)} setupHint=${JSON.stringify(s.setupHint)} urlTyping=${JSON.stringify(s.urlTyping)} prompt=${JSON.stringify(s.promptLine)}${s.err ? ' ERR ' + s.err : ''}`)
    console.log('   slots: ' + sl.map(x => `${x.title}|${x.hint}|${x.detail}`).join('  ;;  '))
    if (shotName) shot(shotName); return s
  }
  const JS = id => { const r = JSON.parse(fs.readFileSync(path.join(dataDir, 'schedules.json'), 'utf8')).recipes; console.log(`[${id}] json ${r.length}: ` + r.map(x => `${x.name}(${x.steps.length})`).join(', ')) }
  const pickState = async id => { const st = await paneState('linktree.html', '__linkTreeState'); const x = st?.state || {}; console.log(`[${id}] linktree title=${JSON.stringify(x.title)} items=${(x.items || []).length}`) }
  const goTo = async title => { for (let i = 0; i < 16; i++) { const s = await ls(); if (s.slots?.[s.selectedIndex]?.title?.toLowerCase() === title.toLowerCase()) return true; await k('Down', 300) } return false }
  const T2 = async (name, fn) => { console.log('--- ' + name); try { await fn() } catch (e) { console.log(`  STEP FAILED ${name}: ${e.message}`); try { shot(name + '-failed') } catch {} } }

  const OV = async id => { try { const t = await findTarget(env.cdpPort, x => x.url.includes('launcher.html'), 1500, 'launcher.html'); const c = await Cdp.connect(t.webSocketDebuggerUrl); const v = await c.eval('document.documentElement.scrollHeight + " vs inner " + innerHeight'); c.close(); console.log(`[${id}] overflow scrollHeight ${v}`) } catch (e) { console.log(`[${id}] overflow ERR ${e.message}`) } }
  const Q = async (id, shotName, wait = 1200) => { await sleep(wait); const s = await ls(); const sl = s.slots || []
    console.log(`[${id}] ${s.screen} | ${JSON.stringify(s.title)} | eyebrow=${JSON.stringify(s.eyebrow)} | sel=${s.selectedIndex} (${JSON.stringify(sl[s.selectedIndex]?.title)}) | buf=${JSON.stringify(s.urlTyping?.buffer)} | hint=${JSON.stringify(s.setupHint)} | slots=${JSON.stringify(sl.map(x => x.title))}${s.err ? ' ERR ' + s.err : ''}`)
    if (shotName) shot(shotName); return s }
  const upToBar = async () => { for (let i = 0; i < 12; i++) { const s = await ls(); if (s.selectedIndex === -1) break; await k('Up', 300) } }
  const pick = async id => { await sleep(1500); for (let q = 0; q < 3 && !(await waitTree()); q++); await pickState(id) }

  const dbRows = () => { const db = path.join(dataDir, 'wirecopy.db'); if (!fs.existsSync(db)) return []; try { return JSON.parse(execFileSync('python3', ['-I', '-c', `
import sqlite3,sys,json
c=sqlite3.connect('file:'+sys.argv[1]+'?mode=ro',uri=True)
print(json.dumps(c.execute('select RecipeName,OccurrenceKey,Status,ErrorClass from ScheduledRuns order by StartedAtUtc').fetchall()))`, db]).toString()) } catch (e) { return ['DBERR ' + e.message.slice(0, 100)] } }
  const J2 = id => { const f = path.join(dataDir, 'schedules.json'); if (!fs.existsSync(f)) { console.log(`[${id}] schedules.json absent`); return }
    for (const r of JSON.parse(fs.readFileSync(f, 'utf8')).recipes) console.log(`[${id}] json name=${r.name} enabled=${r.enabled} days=${(r.days || []).join('/')} localTime=${r.localTime} steps=${r.steps.length}`) }
  const L = async (id, shotName, wait = 1200) => { await sleep(wait); const s = await ls(); const sl = s.slots || []
    console.log(`[${id}] screen=${s.screen} eyebrow=${JSON.stringify(s.eyebrow)} title=${JSON.stringify(s.title)} tagline=${JSON.stringify(s.tagline)} count=${JSON.stringify(s.countLabel)} sel=${s.selectedIndex} buf=${JSON.stringify(s.urlTyping?.buffer)} setupHint=${JSON.stringify(s.setupHint)} hints=${JSON.stringify((s.hints || []).map(h => h.key + ':' + h.label))} footer=${JSON.stringify(s.footer)}${s.err ? ' ERR ' + s.err : ''}`)
    console.log('   slots: ' + JSON.stringify(sl.map(x => ({ title: x.title, hint: x.hint, detail: x.detail, alert: x.alert, disabled: x.disabled, kind: x.kind }))))
    if (shotName) shot(shotName); return s }
  await T2('1', async () => { await openHome(); await L('1-home-empty', 'home-empty', 1500); J2('1') })
  await T2('2', async () => {
    await typeSlow('Empty'); await L('2-typing', 'typing')
    await k('Return', 1500); await L('2-addsite')
    await k('Escape', 2000); await L('2-empty-page', 'empty-page'); J2('2')
  })
  await T2('3', async () => {
    const s = await ls(); const i = (s.slots || []).findIndex(x => /schedule/i.test(x.title || ''))
    console.log('[3] schedule idx ' + i)
    await ch(String(i + 1), 600); await k('Return', 1500); await L('3-schedule')
    const d = new Date(Date.now() + 120000); const hhmm = String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0')
    globalThis.slotAt = new Date(d); globalThis.slotAt.setSeconds(0, 0)
    console.log('[3] target time ' + hhmm + ' now ' + new Date().toString())
    await ch('t', 600); await typeSlow(hhmm); await k('Return', 1500); await L('3-time-set')
    await k('Escape', 1500); await L('3-routine-again'); J2('3')
    await k('Escape', 1500); await L('3-home', 'home-with-empty')
  })
  await T2('4', async () => {
    const until = globalThis.slotAt.getTime() + 100000
    console.log('[4] waiting ' + Math.round((until - Date.now()) / 1000) + 's')
    while (Date.now() < until) await sleep(5000)
    console.log('[4] dbRows ' + JSON.stringify(dbRows()))
    await L('4-after-slot', 'after-slot'); J2('4')
  })
  await T2('5', async () => {
    await k('Escape', 1500); const s = await L('5-launcher', 'launcher')
    console.log('[5] routines slot ' + JSON.stringify((s.slots || []).filter(x => x.kind === 'routines' || /routines/i.test(x.title || ''))))
  })
} catch (e) {
  console.error('PROBE ERROR:', e)
  console.error(shell.stderrText.slice(-2000))
} finally {
  env.down(); try { fixture.kill() } catch {}
  killOurEngines()
  console.log(`total runtime ${Math.round((Date.now() - t0) / 1000)}s`)
}
process.exit(0)
