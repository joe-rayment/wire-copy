// Routine page ACTIONS: drive the routine editor rendered as launcher tiles; shoot each step and print launcher state.
// Run from shell/: source ~/.local/bin/x-env.sh && node gates/probe-routine-page.mjs
// Shots: gates/out/page-NN-<name>.png. Env: PROBE_DISPLAY (:96), PROBE_CDP (9316), FIXTURE_PORT (8124).
import fs from 'node:fs'
import path from 'node:path'
import { spawn, execFileSync } from 'node:child_process'
import { Env, attach, Cdp, findTarget, pollTermText, sleep, buildTui, ROOT } from './lib.mjs'

const argv = process.argv.slice(2)
// --tag <t> names the shots; --only 12,13 runs just those steps (the shell boots on the launcher).
const tag = 'act'
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
    { name: 'Routine Fixture Gazette', url: `http://127.0.0.1:${FPORT}/` },
    { name: 'The Guardian', url: 'https://www.theguardian.com/international' }
  ]
}, null, 2))

const ALL = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']
const step = (sectionName, takeMode = 'WholeSection', takeCount = 0) => ({
  sourceUrl: `http://127.0.0.1:${FPORT}/`, domain: `127.0.0.1:${FPORT}`,
  configUrlPattern: `^https?://(www\\.)?127\\.0\\.0\\.1:${FPORT}/?`,
  sectionName, scope: 'PinnedSection', sortOrderFallback: 0, headingAliases: [],
  takeMode, takeCount, positions: [], required: false
})
const recipe = (id, name, enabled, days, localTime, steps) => ({
  id, name, enabled, days, localTime, steps, outputCollectionName: name, version: 1, lastStatus: 'Never'
})
fs.writeFileSync(path.join(dataDir, 'schedules.json'), JSON.stringify({
  version: 1,
  recipes: [
    recipe('a1000000-0000-4000-8000-000000000001', 'Morning brief', true,
      ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday'], '07:00',
      [step('World'), step('Science', 'TopN', 3), { ...step('Headlines'), sourceUrl: 'https://www.theguardian.com/international', domain: 'www.theguardian.com', configUrlPattern: '^https?://(www\\.)?theguardian\\.com/international/?' }]),
    recipe('a1000000-0000-4000-8000-000000000002', 'Weekend long reads', true,
      ['Saturday', 'Sunday'], '09:30', [step('Arts')]),
    recipe('a1000000-0000-4000-8000-000000000003', 'Disaster watch', false, ALL, '18:00', [step('Disasters')]),
    recipe('a1000000-0000-4000-8000-000000000004', 'Empty one', true, ALL, '05:00', [])
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
  await T('A', async () => {
    await openHome()
    await ch('2', 600); await k('Return', 2000)
    const s = await full('A', 'page'); console.log('  slots:', JSON.stringify((s.slots || []).map(x => x.title)), 'hr=', JSON.stringify(s.headerRight || s.status))
    J('A')
  })
  await T('B', async () => {
    await ch('2'); await full('B1')
    await k('Return', 800); await full('B1b'); for (let i = 0; i < 16; i++) await k('BackSpace', 80); await typeSlow('AM brief'); await full('B2', 'typing')
    await k('Return', 1500); await full('B3', 'renamed'); J('B3')
  })
  await T('C', async () => {
    await ch('4'); await full('C0')
    await ch('K'); await full('C1', 'moved'); J('C1')
    await ch('J'); await full('C2'); J('C2')
    await ch('K'); await full('C3'); J('C3')
  })
  await T('D', async () => {
    await ch('d'); await full('D1', 'confirm'); J('D1')
    await ch('n'); await full('D2'); J('D2')
    await ch('d'); await ch('y', 1200); await full('D3', 'removed'); J('D3')
  })
  await T('E', async () => {
    for (let i = 0; i < 12; i++) { const s = await ls(); if (s.selectedIndex === -1) break; await k('Up', 300) }
    await full('E0', 'bar')
    await typeSlow('127.0.0.1:8124/'); await full('E1')
    await k('Return', 2500); for (let q = 0; q < 4 && !(await waitTree()); q++); await full('E2', 'pick', 1000)
    await k('Escape', 2500); await full('E3')
  })
  await T('F', async () => {
    await k('Down', 500); await full('F0'); await k('space', 1200); await full('F1', 'off'); J('F1')
    await k('space', 1200); await full('F2'); J('F2')
  })
  await T('G', async () => {
    await k('Escape', 2000); await full('G', 'home')
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
