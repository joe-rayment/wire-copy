// Walk the routine-creation flow the way a user does and shoot every screen:
// :routines -> home -> "+ New routine" -> name -> editor -> "+ Add a site" -> choose the
// fixture site -> pick mode -> Space on a section header -> Esc back to the editor.
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

const LEGACY = argv.includes('--legacy')
if (LEGACY) {
  // Seed a pre-existing routine whose World step is the old "top 3" (TakeMode TopN).
  fs.writeFileSync(path.join(dataDir, 'schedules.json'), JSON.stringify({
    version: 1,
    recipes: [{
      id: '6f1c2a10-1111-4222-8333-444455556677', name: 'Legacy brief', enabled: true,
      days: ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'],
      localTime: '07:00',
      steps: [{
        sourceUrl: `http://127.0.0.1:${FPORT}/`, domain: `127.0.0.1:${FPORT}`,
        configUrlPattern: `^https?://(www\\.)?127\\.0\\.0\\.1:${FPORT}/?`,
        sectionName: 'World', scope: 'PinnedSection', sortOrderFallback: 0, headingAliases: [],
        takeMode: 'TopN', takeCount: 3, positions: [], required: false
      }],
      outputCollectionName: 'Legacy brief', version: 1, lastStatus: 'Never'
    }]
  }, null, 2))
}

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

  if (LEGACY) {
  env.key('Right'); await sleep(500); shot('home-legacy-selected')
  env.key('Return'); await sleep(2000)
  await say('editor'); shot('editor-legacy')
  if (process.env.LEGACY_STOP) throw new Error('stop')
  for (let i = 0; i < 4; i++) { env.key('Down'); await sleep(300) }
  await sleep(300)
  await say('site-row'); shot('editor-site-row')
  env.key('Return'); await sleep(1000)
  for (let i = 0; i < 40; i++) {
    const st = await paneState('linktree.html', '__linkTreeState')
    if (st && st.state && (st.state.items || []).length) break
    await sleep(500)
  }
  await sleep(1500)
  await say('pick'); shot('legacy-pick-mode')
  if (process.env.LEGACY_TOUCH !== '0') {
    for (const k of (process.env.LEGACY_KEYS || 'Down,Down').split(',').filter(Boolean)) { env.key(k); await sleep(400) }
    await sleep(600); shot('legacy-moved')
    env.key('space'); await sleep(1000); await say('story'); shot('legacy-space-story')
  }
  env.key('Escape'); await sleep(2500)
  await say('back'); shot('legacy-editor-after')
  console.log('SCHEDULES', fs.readFileSync(path.join(dataDir, 'schedules.json'), 'utf8').replace(/\s+/g, ' '))
  } else {
  env.key('Return'); await sleep(1500)                     // "+ New routine"
  await say('new'); shot('new-routine')

  env.type('Morning brief'); env.key('Return'); await sleep(1500)
  await say('editor'); shot('editor')

  env.key('Up')                                             // rows wrap: Up from Name = "+ Add a site"
  await sleep(500)
  env.key('Return'); await sleep(2000)
  await say('add-site'); shot('add-site')

  env.key('Return'); await sleep(1000)                      // first site = the fixture
  for (let i = 0; i < 40; i++) {
    const st = await paneState('linktree.html', '__linkTreeState')
    if (st && st.state && (st.state.items || []).length) break
    await sleep(500)
  }
  await sleep(1500)
  await say('pick'); shot('pick-mode')

  env.key('space'); await sleep(1200)                       // Space on the World header
  await say('world-all'); shot('space-world-header')
  const downs = Number(process.env.DOWNS || 0)
  for (let i = 0; i < downs; i++) { env.key('Down'); await sleep(400) }
  await sleep(600); await say('moved'); shot('moved')
  if (downs) {
    env.key('space'); await sleep(1000); await say('story'); shot('space-story')
    env.key('shift+t'); await sleep(1000); await say('shift-t'); shot('shift-t')
  }
  env.key('Escape'); await sleep(2500)
  await say('back'); shot('editor-after')
  }
} catch (e) {
  console.error('PROBE ERROR:', e)
  console.error(shell.stderrText.slice(-2000))
} finally { env.down(); try { fixture.kill() } catch {} }
process.exit(0)
