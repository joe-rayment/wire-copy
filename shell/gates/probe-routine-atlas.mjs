// Routines atlas: drive every Routines screen in realistic states and shoot each (no assertions).
// Run from shell/: source ~/.local/bin/x-env.sh && node gates/probe-routine-atlas.mjs --no-build
// Shots: gates/out/atlas-NN-<name>.png. Env: PROBE_DISPLAY (:96), PROBE_CDP (9316), FIXTURE_PORT (8124).
import fs from 'node:fs'
import path from 'node:path'
import { spawn, execFileSync } from 'node:child_process'
import { Env, attach, Cdp, findTarget, pollTermText, sleep, buildTui, ROOT } from './lib.mjs'

const argv = process.argv.slice(2)
// --tag <t> names the shots; --only 12,13 runs just those steps (the shell boots on the launcher).
const tag = argv.includes('--tag') ? argv[argv.indexOf('--tag') + 1] : 'atlas'
const only = argv.includes('--only') ? argv[argv.indexOf('--only') + 1].split(',') : null
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
      [step('World'), step('Science', 'TopN', 3)]),
    recipe('a1000000-0000-4000-8000-000000000002', 'Weekend long reads', true,
      ['Saturday', 'Sunday'], '09:30', [step('Arts')]),
    recipe('a1000000-0000-4000-8000-000000000003', 'Disaster watch', false, ALL, '18:00', [step('Disasters')]),
    recipe('a1000000-0000-4000-8000-000000000004', 'Empty one', true, ALL, '05:00', [])
  ]
}, null, 2))

const fixture = spawn('python3', [path.join(ROOT, 'scripts', 'routine_fixture_site.py'),
  '--port', String(FPORT), '--write-config', xdg], { stdio: 'ignore' })
let fake = null

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

  console.log('launcher painted:', (await pollTermText(term, 'Go to URL', 40000)).ok)
  wid = env.activate()
  await sleep(2000)

  await T('01', async () => { await snap('launcher-boot', 500) })
  await T('02', async () => { await openHome(); await snap('home', 500) })
  await T('03', async () => {
    await k('Right'); await snap('home-right')
    await k('Down'); await snap('home-down')   // now on Disaster watch (idx 3)
  })
  await T('04', async () => {
    await k('Return', 2000)      // selection is Morning brief (cards sort A-Z: New, Disaster, Empty, Morning, Weekend)
    await snap('editor-morning')
  })
  await T('05', async () => {
    await k('Down'); await k('Return'); await snap('freq-card')
    for (let i = 0; i < 3; i++) await k('Down')
    await k('Return'); await snap('freq-days-card')
    await k('Escape'); await sleep(500)
    await snap('freq-after-esc', 500)
    await k('Return'); await sleep(500); await k('Return'); await snap('freq-every-day-timeedit')
    await k('Escape'); await snap('freq-after-timeedit-esc', 500)
  })
  await T('06', async () => {
    // Rows: Name, Frequency, Podcast, Enabled, Last run, site, section(World)...
    const t = await termText()
    for (let i = 0; i < 6; i++) await k('Down', 300)
    await snap('editor-section-row')
    await ch('x'); await snap('remove-confirm')
    await ch('n'); await snap('remove-cancelled', 800)
  })
  await T('07b', async () => {
    // Deterministic: leave editor and re-enter so cursor is on Name, then Up wraps to "+ Add a site".
    await k('Escape', 1500); await k('Return', 2000)  // home selection is Morning brief
    await snap('editor-reentered', 300)
    await k('Up', 500); await snap('editor-add-site-row', 300)
    await k('Return', 2000); await snap('site-picker')
    await k('Return', 1000)
    const ok = await waitTree(); console.log('  linktree items:', ok)
    await snap('pick-mode', 1500)
    await k('Down'); await k('Down'); await snap('pick-down2', 600)
    await k('space', 1000); await snap('pick-space', 300)
    await k('Escape', 2500); await snap('editor-after-pick', 300)
  })
  await T('08', async () => {
    const before = await termText()
    await ch('r', 100)
    await snap('run-now-2s', 2000)
    const t1 = Date.now()
    let last = ''
    while (Date.now() - t1 < 90000) {
      await sleep(1000)
      last = await termText()
      if (/LAST RUN/.test(last)) break
    }
    console.log(`  settled after ${Math.round((Date.now() - t1) / 1000)}s`)
    await snap('run-now-settled', 500)
    await k('Down'); await k('Down'); await snap('details-down2', 500)
    await k('Next'); await snap('details-pgdn', 500)
    await k('Escape', 1500); await snap('editor-after-run', 300)
    await k('Escape', 1500); await snap('home-after-run', 300)
  })
  await T('09', async () => {
    await ch('e', 1500); await snap('home-details-e', 300)
    await k('Escape', 1200)
  })
  await T('10', async () => {
    await snap('home-before-select', 200)
    await k('Up')   // Morning (r1c1) -> Disaster watch (r0c1); await snap('home-disaster-selected', 300)
    await ch('d', 800); await snap('delete-confirm', 300)
    await ch('n', 800)
    await k('space', 800); await snap('disaster-toggled', 300)
    await k('space', 800); await snap('disaster-toggled-back', 300)
  })
  await T('11', async () => {
    await ch('?', 800); await snap('help', 300)
    await k('Escape', 1000)
  })
  await T('12', async () => {
    await k('Escape', 1500); await snap('launcher-after-routines', 300)
    await ch('1', 1000)                       // a digit only moves the selection to tile [1]…
    await k('Return', 1000)                   // …Enter opens it
    console.log('  linktree items:', await waitTree())
    await snap('fixture-linktree', 1500)
    await k('Down'); await k('Down')          // onto a story inside the first section
    await ch('g', 300); await ch('s', 1500); await snap('add-to-routine-card', 300)
    await k('Down'); await snap('add-to-routine-card-down', 300)
    await k('Up'); await k('Return', 2500); await snap('add-to-routine-after-enter', 300)
    await k('space', 1000); await snap('add-to-routine-space', 300)
    await k('Escape', 2500); await snap('add-to-routine-done', 300)
    await toLauncher()
    await snap('launcher-end', 500)
  })
  await T('13', async () => {
    if (Date.now() - t0 > 600000) { console.log('  skipped (time)'); return }
    killOurEngines(); await sleep(1500)       // free 8080 from the engine the app started in step 08
    fake = spawn('python3', [path.join(ROOT, 'scripts', 'fake_audio_engine.py'), '--port', '8080'], { stdio: 'ignore' })
    await sleep(2000)
    await openHome()
    // Home opens on "+ New routine" (idx 0); two Downs in the 2-column grid land on Weekend long reads (idx 4).
    await snap('home-fake-engine', 500)
    await k('Down'); await k('Down'); await snap('weekend-selected', 300)
    await ch('r', 100)
    await snap('weekend-run-2s', 2000)
    await snap('weekend-run-6s', 4000)
    await snap('weekend-run-15s', 9000)
    const t1 = Date.now()
    while (Date.now() - t1 < 150000) {
      await sleep(2000)
      if (!/running…/.test(await termText())) break
    }
    console.log(`  settled after ${Math.round((Date.now() - t1) / 1000)}s`)
    await snap('weekend-run-settled', 500)
    await ch('e', 1500); await snap('weekend-details', 300)
    await k('Next'); await snap('weekend-details-pgdn', 500)
    await k('Escape', 1200)
  })
} catch (e) {
  console.error('PROBE ERROR:', e)
  console.error(shell.stderrText.slice(-2000))
} finally {
  try { if (fake) fake.kill() } catch {}
  env.down(); try { fixture.kill() } catch {}
  killOurEngines()
  console.log(`total runtime ${Math.round((Date.now() - t0) / 1000)}s`)
}
process.exit(0)
