// Routine page ACTIONS: drive the routine editor rendered as launcher tiles; shoot each step and print launcher state.
// Run from shell/: source ~/.local/bin/x-env.sh && node gates/probe-routine-page.mjs
// Shots: gates/out/page-NN-<name>.png. Env: PROBE_DISPLAY (:96), PROBE_CDP (9316), FIXTURE_PORT (8124).
import fs from 'node:fs'
import path from 'node:path'
import { spawn, execFileSync } from 'node:child_process'
import { Env, attach, Cdp, findTarget, pollTermText, sleep, buildTui, ROOT } from './lib.mjs'

const argv = process.argv.slice(2)
// --tag <t> names the shots; --only 12,13 runs just those steps (the shell boots on the launcher).
const tag = 'gs'
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
const writeSched = () => fs.writeFileSync(path.join(dataDir, 'schedules.json'), JSON.stringify({ version: 1, recipes: [recipe('b2000000-0000-4000-8000-000000000001', 'Morning brief', true, ALL, '05:00', [step('World')])].map(r => ({ ...r, scheduleArmedAtUtc: new Date().toISOString() })) }, null, 2))
writeSched()

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

  const T2 = async (name, fn) => { console.log('--- ' + name); try { await fn() } catch (e) { console.log(`  STEP FAILED ${name}: ${e.message}`); try { shot(name + '-failed') } catch {} } }

  const goUnused = 0

  console.log('launcher painted:', (await pollTermText(term, 'Go to URL', 40000)).ok)
  wid = env.activate()
  await sleep(2000)
  const JS = id => { const r = JSON.parse(fs.readFileSync(path.join(dataDir, 'schedules.json'), 'utf8')).recipes; console.log(`[${id}] json ${r.length}: ` + r.map(x => `${x.name}[${x.steps.map(s => s.sectionName).join(',')}]`).join(' ; ')) }
  const lt = async () => (await paneState('linktree.html', '__linkTreeState')) || {}
  const lau = async () => (await paneState('launcher.html', '__launcherState')) || {}
  const short = o => JSON.stringify(o)?.slice(0, 700)
  const sel = async id => { const s = (await lt()).state || {}; const it = (s.items || [])[s.selectedIndex ?? s.selected ?? -1]; console.log(`[${id}] sel=${s.selectedIndex ?? s.selected} item=${short(it)} chooser=${short(s.chooser)}`); return s }
  const chooser = async id => { const r = await lt(); const s = r.state || {}; const l = await lau(); console.log(`[${id}] lt.visible=${r.visible} launcher.visible=${l.visible} chooser=${short(s.chooser)}`); return s }
  const pickLog = async id => { const r = await lt(); const s = r.state || {}; const rest = { ...s }; delete rest.items; console.log(`[${id}] pick lt.visible=${r.visible} state(no items)=${short(rest)}`); console.log(`[${id}] term: ${(await termText()).slice(0, 400)}`) }
  const gs = async () => { await ch('g', 500); await ch('s', 1200) }
  await T2('1', async () => {
    await k('Return', 2500); await waitTree(); const r = await lt(); console.log('[1] keys', Object.keys(r.state || {}).join(',')); await sel('1'); await sleep(500); shot('tree')
  })
  await T2('2', async () => {
    for (let i = 0; i < 40; i++) { const s = (await lt()).state || {}; const it = (s.items || [])[s.selectedIndex ?? s.selected ?? -1]; if (it && /science/i.test(JSON.stringify(it)) && !/^\W*science\W*$/i.test(it.text || it.title || '')) break; await k('Down', 250) }
    await sel('2'); shot('science-story')
  })
  await T2('3', async () => { await gs(); const s = await chooser('3'); await sleep(500); shot('chooser') })
  await T2('4', async () => { await k('Escape', 800); await chooser('4'); JS('4'); shot('esc') })
  await T2('5', async () => {
    await gs(); await chooser('5-open'); await k('Down', 500); await chooser('5-down'); await k('Return', 1200)
    const s = (await lt()).state || {}; console.log('[5] lt state(no items)=' + short({ ...s, items: undefined })); console.log('[5] term: ' + (await termText()).slice(0, 400)); shot('nameprompt')
    await k('Escape', 1200); await chooser('5-esc'); JS('5')
  })
  await T2('6', async () => {
    await gs(); await chooser('6-open'); await k('Down', 500); await chooser('6-down'); await k('Return', 1000)
    await ch('Science desk', 800); await k('Return', 2500); await pickLog('6-pick'); shot('pick'); JS('6')
    await ch('?', 1200); await pickLog('6-help'); shot('help'); await k('Escape', 1000)
    await pickLog('6-helpclosed'); await k('Escape', 2500); const l = await lau(); console.log(`[6] after esc: launcher visible=${l.visible} screen=${l.state?.screen} title=${JSON.stringify(l.state?.title)}`); const r = await lt(); console.log(`[6] lt visible=${r.visible}`); shot('after-pick'); JS('6-final')
  })
  await T2('7', async () => {
    for (let i = 0; i < 6; i++) { const l = await lau(); if (l.visible && l.state?.screen && !(await lt()).visible) break; await k('Escape', 800) }
    let l = await lau(); console.log(`[7] launcher visible=${l.visible} screen=${l.state?.screen}`)
    await k('Return', 2500); await waitTree()
    for (let i = 0; i < 60; i++) { const s = (await lt()).state || {}; const it = (s.items || [])[s.selectedIndex ?? s.selected ?? -1]; if (it && /arts/i.test(JSON.stringify(it)) && !/^\W*arts\W*$/i.test(it.text || it.title || '')) break; await k('Down', 200) }
    await sel('7'); await gs(); await chooser('7-open'); shot('chooser2')
    await k('Return', 2500); await pickLog('7-pick'); shot('pick2'); await k('Escape', 2500); JS('7')
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
