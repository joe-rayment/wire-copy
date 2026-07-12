// P3 gate (workspace-cnyl + workspace-4b14 acceptance): BrowserSession attach mode.
// Drives the REAL user flow on the REAL app inside the shell: navigate to a live-served
// link list, | reveals the pane (lens adopted, spotlight overlaps the SELECTED story and
// MOVES with selection), Enter opens the reader, | hides/shows sticky. Asserts the child
// ATTACHED over CDP and that NO second browser (Playwright Chromium) ever spawned.
// Run from shell/: node gates/gate-p3.mjs
import http from 'node:http'
import { readFileSync, existsSync } from 'node:fs'
import path from 'node:path'
import { execFileSync } from 'node:child_process'
import { Env, attach, Cdp, pollTermText, check, summary, buildTui, sleep, targets, ROOT } from './lib.mjs'

// ---------- fixture site (real HTTP server, real pages) ----------
const FPORT = 8123
const OPENERS = ['Around', 'Beneath', 'Countless', 'During', 'Every', 'Frequent', 'Gradual', 'Hardly']
function article (title, marker) {
  const paras = OPENERS.map((op, i) =>
    `<p>${op} the ${title.toLowerCase()} desk, reporters filed segment ${i + 1} of the day, noting that ` +
    'the assignment ran longer than planned while editors compared drafts, checked names against the record, ' +
    'and held the piece until the numbers settled. The copy desk trimmed a clause, restored a paragraph, and ' +
    'sent the pages back with questions in the margin about sourcing, sequence, and the missing timeline.</p>').join('\n')
  return `<!doctype html><meta charset="utf-8"><title>${title}</title><body>
<article><h1>${title}</h1>\n${paras}\n<p>Signed off at the desk: ${marker} closed the file.</p></article>`
}
const STORIES = [
  { slug: 'story1', title: 'Quiet Turbine Passes First Field Trial', marker: 'the quiet turbine editor' },
  { slug: 'story2', title: 'Harbor Census Finds Record Seal Count', marker: 'the harbor census editor' },
  { slug: 'story3', title: 'Night Market Reopens After Two Years', marker: 'the night market editor' },
  { slug: 'story4', title: 'Glass Bridge Survives Load Testing', marker: 'the glass bridge editor' }
]
const fixture = http.createServer((req, res) => {
  const url = new URL(req.url, `http://127.0.0.1:${FPORT}`)
  const story = STORIES.find(s => url.pathname === `/${s.slug}.html`)
  res.setHeader('content-type', 'text/html; charset=utf-8')
  if (story) { res.end(article(story.title, story.marker)); return }
  res.end(`<!doctype html><meta charset="utf-8"><title>Gate Wire</title><body>
<h1>Gate Wire</h1><ul>
${STORIES.map(s => `<li><a href="/${s.slug}.html">${s.title}</a></li>`).join('\n')}
</ul>`)
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))

buildTui('Release')
const env = new Env({ display: ':83', cdpPort: 9245 })
await env.up()
env.launchShell()

const listUrl = `http://127.0.0.1:${FPORT}/`
function playwrightChromiumRunning () {
  try {
    return execFileSync('bash', ['-c', 'pgrep -fc "[m]s-playwright.*chrom" || true']).toString().trim() !== '0'
  } catch { return false }
}
// The hidden fetch page can sit at the same URL as the visible lens; both report
// visibilityState 'visible'. The honest discriminator: the lens renders at the PANE's
// real width, the fetch page at the emulated 1280.
async function lensCdp (paneWidth, timeoutMs = 30000) {
  const t0 = Date.now()
  while (Date.now() - t0 < timeoutMs) {
    const list = await targets(env.cdpPort).catch(() => [])
    for (const t of list.filter(t => t.type === 'page' && t.url.includes(`127.0.0.1:${FPORT}`))) {
      const c = await Cdp.connect(t.webSocketDebuggerUrl).catch(() => null)
      if (!c) continue
      const w = await c.eval('innerWidth').catch(() => -1)
      if (Math.abs(w - paneWidth) <= 2) return c
      c.close()
    }
    await sleep(500)
  }
  throw new Error('lens pane target not found by width')
}
function todayLog () {
  const d = new Date()
  return path.join(ROOT, 'logs',
    `wirecopy-${d.getFullYear()}${String(d.getMonth() + 1).padStart(2, '0')}${String(d.getDate()).padStart(2, '0')}.log`)
}
const logFile = todayLog()
const logBase = existsSync(logFile) ? readFileSync(logFile, 'utf8').length : 0

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  const boot = await pollTermText(term, 'Go to URL', 30000)
  check('P3.0 TUI booted', boot.ok)
  env.activate()
  await sleep(300)

  console.log('\n== P3: navigate the real app to the fixture list ==')
  env.type('o') // the launcher's real "go to url" flow — deterministic edit mode, ':' stays literal
  await sleep(400)
  env.type(listUrl)
  await sleep(300)
  env.key('Return')
  const list = await pollTermText(term, 'Quiet Turbine', 40000)
  check('P3.1 link list rendered in the reader', list.ok, list.ok ? '' : list.txt.slice(-300))

  console.log('\n== P3: | reveals the pane; lens adopted; attach proven ==')
  // Settle: | during the sub-second window before the app swaps its current page off
  // the launcher hits a non-summonable URL and no-ops. A user would just press | again.
  await sleep(2500)
  env.type('|')
  let st = null
  for (let i = 0; i < 40; i++) {
    st = await term.eval('window.__wc.state()')
    if (st.revealed) break
    if (i === 20) env.type('|') // user-realistic retry
    await sleep(500)
  }
  check('P3.2 | revealed the pane (reader stays primary)',
    st?.revealed && st.termBounds.width > st.pageBounds.width,
    st ? `term=${st.termBounds.width} page=${st.pageBounds.width}` : 'no state')

  const attachLine = `Attached to the desktop shell browser at http://127.0.0.1:${env.cdpPort}`
  let attached = false
  for (let i = 0; i < 30 && !attached; i++) {
    const tail = existsSync(logFile) ? readFileSync(logFile, 'utf8').slice(logBase) : ''
    attached = tail.includes(attachLine)
    if (!attached) await sleep(500)
  }
  check('P3.3 child log proves CDP attach to THIS shell (no launch path)', attached, attachLine)
  check('P3.4 NO second browser: no Playwright Chromium process', !playwrightChromiumRunning())

  const lens = await lensCdp(st.pageBounds.width)
  // Patchright runs its scripts in an ISOLATED world — window.__wcSpotlight is invisible
  // to a main-world eval. The DOM is shared: read the overlay ELEMENT and compute which
  // story anchor it covers.
  const spotHrefExpr = `(() => {
    const o = document.getElementById('__wirecopy-spotlight')
    if (!o) return null
    const or = o.getBoundingClientRect()
    if (or.width === 0 || or.height === 0) return null
    let best = null, bestArea = 0
    for (const a of document.querySelectorAll('a[href]')) {
      const ar = a.getBoundingClientRect()
      const w = Math.min(or.right, ar.right) - Math.max(or.left, ar.left)
      const h = Math.min(or.bottom, ar.bottom) - Math.max(or.top, ar.top)
      const area = w > 0 && h > 0 ? w * h : 0
      if (area > bestArea) { bestArea = area; best = a.getAttribute('href') }
    }
    return best
  })()`
  let spot = null
  for (let i = 0; i < 60; i++) {
    spot = await lens.eval(spotHrefExpr).catch(() => null)
    if (spot) break
    await sleep(500)
  }
  check('P3.5 spotlight overlay covers the SELECTED (first) story on the live page',
    spot === '/story1.html', String(spot))
  env.shot('p3-split-list')

  console.log('\n== P3: selection moves → spotlight moves ==')
  env.type('j')
  let spot2 = null
  for (let i = 0; i < 25; i++) {
    spot2 = await lens.eval(spotHrefExpr).catch(() => null)
    if (spot2 && spot2 !== '/story1.html') break
    await sleep(400)
  }
  check('P3.6 spotlight overlay MOVED with the selection (off story 1)',
    !!spot2 && spot2 !== '/story1.html', String(spot2))

  console.log('\n== P3: Enter opens the reader; lens follows the SAME story ==')
  env.key('Return')
  // Contract, not grid semantics: Enter opens WHATEVER is selected — identify the opened
  // story from the reader body (lowercased-title phrase appears in every paragraph),
  // then require the pane to follow to that SAME story.
  let opened = null
  const t0 = Date.now()
  while (Date.now() - t0 < 40000 && !opened) {
    const txt = (await term.eval('window.__termText()').catch(() => '')).replace(/\s+/g, ' ')
    opened = STORIES.find(s => txt.includes(`the ${s.title.toLowerCase()} desk`)) || null
    if (!opened) await sleep(500)
  }
  check('P3.7 reader opened the selected story (body on screen)', !!opened, opened ? opened.slug : 'none')
  let lensUrl = ''
  if (opened) {
    for (let i = 0; i < 40; i++) {
      const w = await lens.eval('location.pathname').catch(() => '')
      if (w === `/${opened.slug}.html`) { lensUrl = w; break }
      await sleep(500)
    }
  }
  check('P3.8 lens followed to the SAME opened story', !!opened && lensUrl === `/${opened.slug}.html`, lensUrl || 'no-follow')
  await sleep(600)
  env.shot('p3-reader')

  console.log('\n== P3: | hide / show (sticky parity) ==')
  env.type('|')
  await sleep(1200)
  const stHidden = await term.eval('window.__wc.state()')
  check('P3.9 | hides the pane', stHidden.revealed === false)
  env.type('|')
  let stBack = null
  for (let i = 0; i < 15; i++) {
    stBack = await term.eval('window.__wc.state()')
    if (stBack.revealed) break
    await sleep(400)
  }
  check('P3.10 | shows it again (sticky)', !!stBack?.revealed)

  const wins = env.x('search', '--onlyvisible', '--name', '.').trim().split('\n').filter(Boolean)
  check('P3.11 exactly one OS window on the display (no popped browser windows)', wins.length === 1, `windows=${wins.length}`)

  lens.close()
  term.close()
} catch (err) {
  console.error('GATE ERROR:', err)
  check('P3 gate completed without error', false, String(err))
  try { env.shot('p3-error') } catch {}
} finally {
  env.down()
  fixture.close()
}
process.exit(summary())
