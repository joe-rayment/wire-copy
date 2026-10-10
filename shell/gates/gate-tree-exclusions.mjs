// Stale-layout exclusions gate (workspace-dhddt): proves a link the user dropped with 'x' stays
// dropped even after the site redesigns and the saved layout goes STALE, in the REAL shell.
// Before the fix NavigationTreeBuilder's stale fallback (sections cover < 10% of the page)
// rebuilt the tree from the RAW links, so the exact ad the user had removed came straight
// back at the moment the layout was flagged "no longer matches this page". The gate does what
// the user does: g l -> "Mark the links yourself" -> a on a story, x on the ad, Enter, s
// (no OpenAI key needed; the cursor is walked onto the ad row and checked before x), then the
// fixture "redesigns" (stories move to a new container, the ad keeps its href) and the page is
// opened again under a new query string so the extraction cache cannot serve page A's links. Every check reads what the link-tree pane
// shows (its pushed items), plus the TUI's stale-layout status line.
// Headful under Xvfb ONLY (never headless). Run from shell/: node gates/gate-tree-exclusions.mjs
import http from 'node:http'
import fs from 'node:fs'
import path from 'node:path'
import { Env, attach, Cdp, findTarget, pollTermText, check, summary, buildTui, sleep, OUT } from './lib.mjs'

buildTui('Release')

const FPORT = 8174
const STORIES = [
  ['Harbour commission approves the northern breakwater rebuild', '/local/2026/10/breakwater-rebuild'],
  ['City council votes to extend late-night bus routes', '/local/2026/10/late-night-buses'],
  ['Regional orchestra announces a free winter concert series', '/arts/2026/10/winter-concerts'],
  ['Port authority delays the new ferry terminal opening', '/local/2026/10/ferry-terminal-delay'],
  ['School board debates a four-day week for next autumn', '/education/2026/10/four-day-week'],
  ['Fishing fleet returns early as storm warnings widen', '/world/2026/10/fleet-returns-early'],
  ['Library announces extended weekend opening hours', '/local/2026/10/library-hours'],
  ['Hospital trust reports a drop in emergency wait times', '/health/2026/10/waiting-times-drop'],
  ['Tidal energy trial passes its first winter test', '/science/2026/10/tidal-trial-passes'],
  ['Farmers market adds a second riverside site', '/food/2026/10/riverside-market'],
  ['Cycle lane extension approved after months of dispute', '/transport/2026/10/cycle-lane-extension'],
  ['Lighthouse museum reopens following a major restoration', '/arts/2026/10/lighthouse-museum-reopens']
]
// The ad the user will drop with 'x'. Story-shaped text on purpose: the extractor must not
// pre-filter it, so only the user's exclusion can keep it out of the tree.
const AD = ['Acme chef knives: half price this week only', '/partner/acme-knives-offer']

let layout = 'A'
const a = ([t, h]) => `<li><a href="${h}">${t}</a> <time datetime="2026-10-10T08:00:00Z">Oct 10</time></li>`
const card = ([t, h]) => `<article class="card"><h3><a href="${h}">${t}</a></h3><p>Oct 10</p></article>`
const fixture = http.createServer((req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  // The ad sits INSIDE the content area (not an <aside>/<nav>: the extractor files those as
  // Navigation, which label mode doesn't list and the story tree never shows). It must be a
  // Content-typed link, or the user could never 'x' it and the stale fallback could never leak it.
  const ad = `<div class="partner-box"><a href="${AD[1]}">${AD[0]}</a></div>`
  res.end(layout === 'A'
    // Page A: stories in ul.river (this is what the label derivation learns), ad after them.
    ? `<!doctype html><meta charset="utf-8"><title>Harbour Gazette</title><body>
<h1>Harbour Gazette</h1>
<main><ul class="river">${STORIES.map(a).join('\n')}</ul>${ad}</main>`
    // Page B ("redesign"): same stories, brand-new container classes; the ad is untouched.
    : `<!doctype html><meta charset="utf-8"><title>Harbour Gazette</title><body>
<h1>Harbour Gazette</h1>
<div class="feed-wrap"><div class="feed">${STORIES.map(card).join('\n')}</div>${ad}</div>`)
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))
const base = `http://127.0.0.1:${FPORT}/`

const env = new Env({ display: ':88', cdpPort: 9298 })
await env.up()
const shell = env.launchShell()

let lt = null
try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('T0 shell booted to launcher', (await pollTermText(term, 'Go to URL', 30000)).ok)
  const wid = env.activate(); await sleep(500)

  // Open the fixture URL the way the user does; keystrokes can drop under load, so retry.
  const openFixture = async (needle, suffix = '') => {
    let opened = { ok: false }
    for (let attempt = 0; attempt < 3 && !opened.ok; attempt++) {
      if (attempt) { env.key('Escape'); await sleep(600); env.key('Escape'); await sleep(600) }
      env.key('o'); await sleep(900)
      env.type('http:/'); await sleep(200); env.type(base.slice('http:/'.length) + suffix); await sleep(500)
      env.key('Return')
      opened = await pollTermText(term, needle, 30000)
    }
    return opened
  }

  // Read the link-tree pane: walk the selection down so every page of stories is collected.
  const readTree = async () => {
    const read = async () => JSON.parse(await lt.eval(`JSON.stringify((() => {
      const st = (window.__linkTreeState() || {}).state || {}
      return {
        items: (st.items || []).filter(i => i.kind !== 'section').map(i => ({ title: i.title, url: i.url || i.href || '' })),
        total: st.totalStories, more: !!st.moreBelow, sel: st.selectedIndex
      }
    })())`))
    const seen = []
    const merge = r => { for (const it of r.items) if (!seen.some(s => s.title === it.title)) seen.push(it) }
    let r = await read(); merge(r)
    for (let i = 0; i < 20; i++) {
      env.key('Down'); await sleep(400)
      const next = await read(); merge(next)
      if (!next.more && next.sel === r.sel) break
      r = next
    }
    return seen
  }
  const attachTree = async () => {
    const t0 = await findTarget(env.cdpPort, t => t.url.includes('linktree.html'), 20000, 'link-tree pane')
    lt = await Cdp.connect(t0.webSocketDebuggerUrl)
    for (let i = 0; i < 40 && !(await lt.eval('!!window.__linkTreeReady').catch(() => false)); i++) await sleep(250)
    await sleep(1500)
  }

  // ---- Page A: open, then the user teaches the layout by hand ----
  check('T1 TUI rendered fixture page A', (await openFixture('breakwater')).ok)
  await attachTree()

  // g l = re-run layout setup. Option 1 ("Mark the links yourself") is the default when
  // there is no AI key. Keystrokes can drop under load, so the chord is retried until the
  // chooser is really up (Escape first so a half-eaten chord can't leave a stray 'g' pending).
  let chooser = { ok: false }
  for (let attempt = 0; attempt < 3 && !chooser.ok; attempt++) {
    if (attempt) { env.key('Escape'); await sleep(600) }
    env.key('g'); await sleep(400); env.key('l')
    chooser = await pollTermText(term, 'Mark the links yourself', 8000)
  }
  check('T2 layout chooser offers "Mark the links yourself"', chooser.ok)
  env.key('Return')
  // Label mode: the cursor lands on the FIRST story row. 'a' ranks it, then Down walks past
  // the other 11 stories to the ad (row 13, listed last because it follows the river in page order).
  check('T2a label mode is up', (await pollTermText(term, 'Label the links', 15000)).ok)
  await sleep(1000)
  env.key('a'); await sleep(500)
  // Walk Down until the highlighted row (the one with the cursor marker) IS the ad: never count rows.
  const cursorRow = async () => ((await term.eval('window.__termText()')).split('\n').find(l => l.includes('\u25b8') && /\[|\u2022/.test(l))) || ''
  let onAd = false
  for (let i = 0; i < STORIES.length + 3 && !onAd; i++) {
    env.key('Down'); await sleep(300)
    onAd = (await cursorRow()).includes('Acme chef')
  }
  const lab = await term.eval('window.__termText()')
  fs.writeFileSync(path.join(OUT, 'tree-exclusions-label.txt'), lab)
  check('T2b label-mode cursor is ON the ad row before x is pressed', onAd, `cursorRow=${(await cursorRow()).slice(0, 80)}`)
  env.key('x')
  check('T2c the ad is marked: footer reads 1 article(s) · 1 ad(s)',
    (await pollTermText(term, '1 article(s) · 1 ad(s)', 8000)).ok)
  env.shotWindow('tree-exclusions-label', wid)
  env.key('Return'); await sleep(2500) // -> preview card
  env.key('s')                         // save exactly what is previewed
  const saved = path.join(env.extraEnv.XDG_DATA_HOME, 'WireCopy', 'hierarchy', '127.0.0.1_8174.json')
  for (let i = 0; i < 40 && !fs.existsSync(saved); i++) await sleep(500)
  await sleep(1500)
  // The durable rule the whole gate rests on: the saved layout must carry an exclusion for the ad.
  let cfg = {}
  try { cfg = JSON.parse(fs.readFileSync(saved, 'utf8'))[0] || {} } catch {}
  const rules = [...(cfg.excludeSelectors || []), ...(cfg.excludeUrlPatterns || [])]
  check('T2d saved layout carries an exclusion rule for the ad', rules.some(r => /partner|acme/i.test(r)),
    `sections=${JSON.stringify((cfg.sections || []).map(x => x.parentSelectors))} rules=${JSON.stringify(rules)} labels=${(cfg.userLabels || []).map(l => l.kind + ':' + l.text.slice(0, 12))}`)

  // Baseline on page A: with the layout saved, the ad is not in the story list.
  const onA = await readTree()
  check('T3 page A: the x-dropped ad is absent after save', !onA.some(i => i.title.includes('Acme chef knives')),
    `items=${JSON.stringify(onA.map(i => i.title))}`)
  check('T3b page A: the stories are present', STORIES.every(([t]) => onA.some(i => i.title.includes(t))),
    `collected=${onA.length}`)

  // ---- Page B: the site redesigns; the saved layout goes stale ----
  layout = 'B'
  env.key('Escape'); await sleep(800)
  check('T4 TUI rendered redesigned page B', (await openFixture('breakwater', '?v=2')).ok)
  const stale = await pollTermText(term, 'Saved layout no longer matches this page', 20000)
  check('T5 the stale-layout status is shown', stale.ok)
  if (lt) { try { lt.close() } catch {} }
  await attachTree()
  const onB = await readTree()
  check('T6 page B (stale): the x-dropped ad stays OUT of the story list', !onB.some(i => i.title.includes('Acme chef knives')),
    `items=${JSON.stringify(onB.map(i => i.title))}`)
  const totalB = await lt.eval(`((window.__linkTreeState() || {}).state || {}).totalStories`)
  check('T6b page B (stale): the tree counts 12 stories, not 13', totalB === STORIES.length, `totalStories=${totalB}`)
  check('T7 page B (stale): the stories still show (document-order fallback)',
    STORIES.every(([t]) => onB.some(i => i.title.includes(t))), `collected=${onB.length}`)
  env.shotWindow('tree-exclusions', wid)
} catch (e) {
  console.log('GATE ERROR:', e)
  check('gate completed without error', false, String(e))
  try { env.shot('tree-exclusions-error') } catch {}
} finally {
  if (process.env.GATE_DEBUG) console.log(shell.stderrText.slice(-4000))
  try { lt?.close?.() } catch {}
  env.down()
  fixture.close()
}
process.exit(summary() ? 1 : 0)
