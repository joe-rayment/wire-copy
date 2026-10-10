// Saved-layout specificity gate (workspace-zh1o1): proves, in the REAL shell, that (1) a layout
// saved for a section (/news) is used on that section even though a homepage layout was saved
// EARLIER, and (2) ':layout clear' on the section removes ONLY the section's layout — the
// homepage's survives. Before the fix the homepage config's root pattern matched every URL on
// the site and the store returned the FIRST match in save order, so the /news layout was never
// used (the homepage layout does not fit /news, so its fallback brought /news's ad back); and
// clearing /news removed every config whose pattern matched it, homepage included.
// The user's saved state is PRE-SEEDED as the layouts file the app itself writes (two configs in
// save order: homepage, then /news — same shape as a label-mode save, see gate-tree-exclusions).
// It is seeded rather than taught through 'g l' because the wizard currently stalls when its
// page screenshot is slow (filed separately). Everything after that is what the user does: open
// /news, ':layout clear', open the front page. Tree checks read the link-tree pane's pushed
// items; the layouts file is read back from the gate's isolated data dir.
// STATUS (2026-10-10): all of L0-L9 PASS on main (928e7ba1 + the store fix 2d39cb72). Pre-fix RED
// (HierarchyConfigStore/DocumentOrderStrategy/ScheduleConfigResolution at 2d39cb72~1): L2, L6, L8
// FAIL (the /news ad is listed; ':layout clear' wipes the homepage layout too; the front page's ad
// comes back); L0, L1, L3, L5, L7, L9 pass there; L4 also passes pre-fix, so it does not
// discriminate. The earlier L5/L6 failure was a command-line bug, not the store: HandleCommandLineInput
// dispatched on the first word only, so ':layout clear' opened the chooser — fixed in 928e7ba1.
// Headful under Xvfb ONLY (never headless). Run from shell/: node gates/gate-layout-specific.mjs
import http from 'node:http'
import fs from 'node:fs'
import path from 'node:path'
import { Env, attach, Cdp, findTarget, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

buildTui('Release')

const FPORT = 8178
const HOME = [
  ['Harbour commission approves the northern breakwater rebuild', '/local/2026/10/breakwater-rebuild'],
  ['City council votes to extend late-night bus routes', '/local/2026/10/late-night-buses'],
  ['Regional orchestra announces a free winter concert series', '/arts/2026/10/winter-concerts'],
  ['Port authority delays the new ferry terminal opening', '/local/2026/10/ferry-terminal-delay'],
  ['School board debates a four-day week for next autumn', '/education/2026/10/four-day-week'],
  ['Fishing fleet returns early as storm warnings widen', '/world/2026/10/fleet-returns-early'],
  ['Library announces extended weekend opening hours', '/local/2026/10/library-hours'],
  ['Hospital trust reports a drop in emergency wait times', '/health/2026/10/waiting-times-drop'],
]
const NEWS = [
  ['Mayor unveils a ten year plan for the waterfront district', '/news/2026/10/waterfront-plan'],
  ['Coastal rail line to close for three weekends of upgrades', '/news/2026/10/rail-closures'],
  ['Teachers union accepts the revised pay offer from the council', '/news/2026/10/teachers-pay-offer'],
  ['Flood barrier test completed ahead of the winter surge season', '/news/2026/10/flood-barrier-test'],
  ['New bakery opens in the old customs house on the quay', '/news/2026/10/customs-house-bakery'],
  ['Coastguard rescues two sailors off the southern headland', '/news/2026/10/coastguard-rescue'],
  ['Planning committee rejects the proposed hotel tower', '/news/2026/10/hotel-tower-rejected'],
  ['University opens a marine research station on the estuary', '/news/2026/10/marine-station'],
]
// Story-shaped ads inside the content area, so only the user's exclusion keeps them out.
const AD_Y = ['Zenith watches: spring clearance, up to 60% off', '/partner/zenith-watches-offer']
const AD_X = ['Acme chef knives: half price this week only', '/partner/acme-knives-offer']

const li = ([t, h]) => `<li><a href="${h}">${t}</a> <time datetime="2026-10-10T08:00:00Z">Oct 10</time></li>`
const card = ([t, h]) => `<article class="card"><h3><a href="${h}">${t}</a></h3><p>Oct 10</p></article>`
const fixture = http.createServer((req, res) => {
  const p = new URL(req.url, 'http://x').pathname
  res.setHeader('content-type', 'text/html; charset=utf-8')
  if (p === '/') {
    res.end(`<!doctype html><meta charset="utf-8"><title>Harbour Gazette</title><body><h1>Harbour Gazette</h1>
<main><ul class="river">${HOME.map(li).join('\n')}</ul><div class="promo-y"><a href="${AD_Y[1]}">${AD_Y[0]}</a></div></main>`)
  } else if (p === '/news') {
    // A DIFFERENT markup from the front page, so the homepage layout genuinely does not fit here.
    res.end(`<!doctype html><meta charset="utf-8"><title>Harbour Gazette news</title><body><h1>Harbour Gazette: news</h1>
<div class="feed-wrap"><div class="feed">${NEWS.map(card).join('\n')}</div><div class="promo-x"><a href="${AD_X[1]}">${AD_X[0]}</a></div></div>`)
  } else {
    res.statusCode = 404; res.end()
  }
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))
const base = `http://127.0.0.1:${FPORT}`

// The layouts file exactly as a label-mode save writes it (field set copied from a real save).
const host = `127\\.0\\.0\\.1:${FPORT}`
const layout = (urlPattern, parentSelector, exclude, createdAt) => ({
  domain: `127.0.0.1:${FPORT}`, urlPattern,
  sections: [{ name: 'Stories', sortOrder: 0, parentSelectors: [parentSelector], urlPatterns: [], startCollapsed: false, maxLinks: null }],
  createdAt, modelVersion: 'label-first+labels', kind: 3, rssFeedUrl: null, structuralSignature: null, version: 3,
  strategy: 'AiCurated', aiResult: null, excludeSelectors: [exclude], excludeUrlPatterns: [], excludeSectionTitles: [],
  moreSelectors: [], moreUrlPatterns: [], userLabels: [], userInstructions: [], userSectionNames: [], needsReanalyze: false,
})
const HOME_PATTERN = `^https?://(www\\.)?${host}/?`
const NEWS_PATTERN = `^https?://(www\\.)?${host}/news(?=[/?#]|$)`

const env = new Env({ display: ':89', cdpPort: 9299 })
const savedFile = path.join(env.extraEnv.XDG_DATA_HOME, 'WireCopy', 'hierarchy', `127.0.0.1_${FPORT}.json`)
fs.mkdirSync(path.dirname(savedFile), { recursive: true })
fs.writeFileSync(savedFile, JSON.stringify([
  layout(HOME_PATTERN, 'ul.river', 'div.promo-y', '2026-10-01T09:00:00Z'),   // saved first
  layout(NEWS_PATTERN, 'div.feed', 'div.promo-x', '2026-10-02T09:00:00Z'),   // saved later
], null, 2))
const readSaved = () => { try { return JSON.parse(fs.readFileSync(savedFile, 'utf8')) } catch { return [] } }

await env.up()
const shell = env.launchShell()

let lt = null
try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('L0 shell booted to launcher', (await pollTermText(term, 'Go to URL', 30000)).ok)
  const wid = env.activate(); await sleep(500)
  const screenText = async () => (await pollTermText(term, '\u0000never', 300)).txt

  // Opening a URL the way the user does; keystrokes can drop under load, so it is retried.
  // From the launcher 'o' opens the URL prompt; from a loaded page 'o' means something else, so
  // there the URL goes through the ':open' command line, as a user on a page would type it.
  const openUrl = async (suffix, needle, fromPage = false) => {
    let r = { ok: false }
    for (let attempt = 0; attempt < 3 && !r.ok; attempt++) {
      if (attempt) { env.key('Escape'); await sleep(600); env.key('Escape'); await sleep(600) }
      if (fromPage) { env.type(':open '); await sleep(900) } else { env.key('o'); await sleep(900) }
      env.type('http:/'); await sleep(200); env.type(base.slice('http:/'.length) + suffix); await sleep(500)
      env.key('Return')
      r = await pollTermText(term, needle, 30000)
    }
    return r
  }
  const attachTree = async () => {
    if (lt) { try { lt.close() } catch {} }
    const t0 = await findTarget(env.cdpPort, t => t.url.includes('linktree.html'), 20000, 'link-tree pane')
    lt = await Cdp.connect(t0.webSocketDebuggerUrl)
    for (let i = 0; i < 40 && !(await lt.eval('!!window.__linkTreeReady').catch(() => false)); i++) await sleep(250)
    await sleep(1500)
  }
  // The pane shows one page of stories: walk the selection down and collect every page.
  const readTree = async () => {
    const once = async () => JSON.parse(await lt.eval(`JSON.stringify((() => {
      const st = (window.__linkTreeState() || {}).state || {}
      return { items: (st.items || []).filter(i => i.kind !== 'section').map(i => i.title || ''),
               total: st.totalStories, more: !!st.moreBelow, sel: st.selectedIndex }
    })())`))
    const seen = []
    let r = await once(); for (const t of r.items) if (!seen.includes(t)) seen.push(t)
    for (let i = 0; i < 16; i++) {
      env.key('Down'); await sleep(400)
      const next = await once(); for (const t of next.items) if (!seen.includes(t)) seen.push(t)
      if (!next.more && next.sel === r.sel) break
      r = next
    }
    return { items: seen, total: r.total }
  }
  const listed = (tree, t) => tree.items.some(i => i.includes(t))

  // ---- 1. The section the user set up AFTER the homepage: its own layout must apply ----
  check('L1 /news rendered', (await openUrl('/news?v=1', 'Mayor unveils')).ok)
  await attachTree(); await sleep(1500)
  const newsScreen = await screenText()
  const news = await readTree()
  check('L2 /news: the section layout applies — its ad X is NOT in the story list',
    !listed(news, 'Acme chef knives'), `items=${JSON.stringify(news.items)}`)
  check('L3 /news: every /news story is listed', NEWS.every(([t]) => listed(news, t)), `total=${news.total}`)
  check('L4 /news: no "Saved layout no longer matches" warning (the RIGHT layout was picked)',
    !/no longer matches/.test(newsScreen), newsScreen.replace(/\s+/g, ' ').slice(-200))
  env.shotWindow('layout-specific-news', wid)

  // ---- 2. ':layout clear' on /news removes only the /news layout ----
  // Open the command line first and confirm it is up before typing the command, so a dropped
  // ':' can never turn the command's letters into page shortcuts.
  let cleared = { ok: false, txt: '' }
  for (let attempt = 0; attempt < 3 && !cleared.ok; attempt++) {
    if (attempt) { env.key('Escape'); await sleep(800) }
    env.type(':'); await sleep(700)
    const lines = (await screenText()).split('\n').map(l => l.trim()).filter(Boolean)
    // The prompt line reads ':' followed by its own hint, e.g. ": (:help)".
    if (!/^:(\s|$)/.test(lines[lines.length - 1] || '')) continue
    env.type('layout clear'); await sleep(600); env.key('Return')
    cleared = await pollTermText(term, 'Layout config cleared', 10000)
  }
  check('L5 ":layout clear" on /news reports the layout cleared', cleared.ok,
    cleared.ok ? '' : cleared.txt.replace(/\s+/g, ' ').slice(-200))
  await sleep(1500)
  const after = readSaved()
  check('L6 the layouts file still holds the HOMEPAGE layout, and only it',
    after.length === 1 && after[0].urlPattern === HOME_PATTERN, `patterns=${JSON.stringify(after.map(c => c.urlPattern))}`)

  // ---- 3. The front page still uses its layout ----
  check('L7 front page rendered', (await openUrl('/?v=2', 'breakwater', true)).ok)
  await attachTree()
  const home = await readTree()
  check('L8 front page: the homepage layout survived the clear — ad Y is still NOT listed',
    !listed(home, 'Zenith watches'), `items=${JSON.stringify(home.items)}`)
  check('L9 front page: every front-page story is listed', HOME.every(([t]) => listed(home, t)), `total=${home.total}`)
  env.shotWindow('layout-specific-home-after-clear', wid)
} catch (e) {
  console.log('GATE ERROR:', e)
  check('gate completed without error', false, String(e))
  try { env.shot('layout-specific-error') } catch {}
} finally {
  if (process.env.GATE_DEBUG) console.log(shell.stderrText.slice(-4000))
  try { lt?.close?.() } catch {}
  env.down()
  fixture.close()
}
process.exit(summary() ? 1 : 0)
