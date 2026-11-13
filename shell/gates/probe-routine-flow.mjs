// Walk the routine-creation flow the way a user does and shoot every screen:
// :routines -> home (tiles) -> n, type the name, Return -> "Add a site" -> Return picks the first
// saved site (the fixture) -> pick mode -> Space on a section header -> Esc to the routine page
// (every change saves at once) -> Esc to the home, where the new routine is a tile.
// Headful under Xvfb; real X keys via xdotool. Shots land in gates/out/<tag>-NN-*.png —
// open them, they are the evidence.
//
// Run from shell/: source ~/.local/bin/x-env.sh && node gates/probe-routine-flow.mjs [--tag rf] [--no-build]
// Env: PROBE_DISPLAY (default :95), PROBE_CDP (default 9315), FIXTURE_PORT (default 8123).
import fs from 'node:fs'
import path from 'node:path'
import { spawn } from 'node:child_process'
import { Env, attach, Cdp, findTarget, pollTermText, sleep, buildTui, ROOT } from './lib.mjs'

const argv = process.argv.slice(2)
const tag = argv.includes('--tag') ? argv[argv.indexOf('--tag') + 1] : 'rf'
const FPORT = Number(process.env.FIXTURE_PORT || 8123)
if (!argv.includes('--no-build')) buildTui('Release')

const env = new Env({
  display: process.env.PROBE_DISPLAY || ':95',
  cdpPort: Number(process.env.PROBE_CDP || 9315),
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

// Fixture news site with four named sections (World, Disasters, Science, Arts).
const fixture = spawn('python3', [path.join(ROOT, 'scripts', 'routine_fixture_site.py'),
  '--port', String(FPORT), '--write-config', xdg], { stdio: 'ignore' })

if (process.env.XVFB_PRELOAD) {
  // Bare container: plain Xvfb dies without the xkbcomp shim (bd memory xvfb-needs-shim).
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

let n = 0
let wid = null
const shot = name => env.shotWindow(`${tag}-${String(++n).padStart(2, '0')}-${name}`, wid)

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
  const say = async label => console.log(`  [${label}] term: ${(await termText()).slice(0, 200)}`)

  console.log('launcher painted:', (await pollTermText(term, 'Go to URL', 40000)).ok)
  wid = env.activate()
  await sleep(2000)
  shot('launcher')

  env.type(':routines'); env.key('Return'); await sleep(2000)
  await say('home'); shot('home')

  const ls = async () => {
    const v = await paneState('launcher.html', '__launcherState')
    return (v && v.state) || {}
  }
  const line = async label => {
    const st = await ls()
    console.log(`  [${label}] screen=${st.screen} sel=${st.selectedIndex} title=${JSON.stringify(st.title)} slots=${JSON.stringify((st.slots || []).map(x => x.title))}`)
  }
  const schedules = () => fs.readFileSync(path.join(dataDir, 'schedules.json'), 'utf8').replace(/\s+/g, ' ')

  await line('home'); 
  if ((await ls()).selectedIndex !== -1) { env.type('n'); await sleep(800) }   // with no routines the bar is already lit; 'n' there would be typed into the name                          // the name bar lights (selectedIndex -1)
  await line('name-bar'); shot('name-bar')
  env.type('Morning brief'); await sleep(500)
  await line('name-typed'); shot('name-typed')
  env.key('Return'); await sleep(1800)                      // -> "Add a site"
  await line('add-site'); shot('add-site')

  env.key('Return'); await sleep(1000)                      // first saved site = the fixture
  for (let i = 0; i < 40; i++) {
    const st = await paneState('linktree.html', '__linkTreeState')
    if (st && st.state && (st.state.items || []).length) break
    await sleep(500)
  }
  await sleep(1500)
  await say('pick'); shot('pick-mode')

  env.key('space'); await sleep(1200)                       // include the World section under the cursor
  await say('picked'); shot('pick-space')

  env.key('Escape'); await sleep(2500)                      // pick mode done -> routine page
  await line('routine-page'); shot('routine-page')
  console.log('SCHEDULES (routine page)', schedules())

  env.key('Escape'); await sleep(2000)                      // routine page -> home
  await line('home-after'); shot('home-after')
  console.log('SCHEDULES (final)', schedules())
} catch (e) {
  console.error('PROBE ERROR:', e)
  console.error(shell.stderrText.slice(-2000))
} finally { env.down(); try { fixture.kill() } catch {} }
process.exit(0)
