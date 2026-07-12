// P4 gate (workspace-xjce): focus doctrine on the REAL app under deliberate assault.
// The lens shows a HOSTILE page (autofocus + focus() every 300ms). Reader mode: every
// key keeps driving the TUI (spotlight moves), the page logs ZERO keys. Click into the
// pane = browser mode: typing reaches the page input, the TUI hint teaches Esc. Esc:
// reader again, keys drive the TUI, nothing leaked. Then typing keeps working across
// a navigation churn (Enter → story + lens follow; b → back to the list).
// Run from shell/: node gates/gate-p4.mjs
import http from 'node:http'
import { Env, attach, Cdp, pollTermText, check, summary, buildTui, sleep, targets } from './lib.mjs'

const FPORT = 8125
const OPENERS = ['Around', 'Beneath', 'Countless', 'During', 'Every', 'Frequent', 'Gradual', 'Hardly']
const STORIES = [
  { slug: 'story1', title: 'Quiet Turbine Passes First Field Trial' },
  { slug: 'story2', title: 'Harbor Census Finds Record Seal Count' },
  { slug: 'story3', title: 'Night Market Reopens After Two Years' },
  { slug: 'story4', title: 'Glass Bridge Survives Load Testing' }
]
function article (title) {
  // Mirrors gate-p3's fixture: ≥540 words — the reader's extraction floor is 500.
  const paras = OPENERS.map((op, i) =>
    `<p>${op} the ${title.toLowerCase()} desk, reporters filed segment ${i + 1} of the day, noting that ` +
    'the assignment ran longer than planned while editors compared drafts, checked names against the record, ' +
    'and held the piece until the numbers settled. The copy desk trimmed a clause, restored a paragraph, and ' +
    'sent the pages back with questions in the margin about sourcing, sequence, and the missing timeline.</p>').join('\n')
  return `<!doctype html><meta charset="utf-8"><title>${title}</title><body>
<article><h1>${title}</h1>\n${paras}\n<p>Signed off at the desk: the ${title.toLowerCase()} editor closed the file.</p></article>`
}
const fixture = http.createServer((req, res) => {
  const url = new URL(req.url, `http://127.0.0.1:${FPORT}`)
  const story = STORIES.find(s => url.pathname === `/${s.slug}.html`)
  res.setHeader('content-type', 'text/html; charset=utf-8')
  if (story) { res.end(article(story.title)); return }
  // The link list IS the focus thief.
  res.end(`<!doctype html><meta charset="utf-8"><title>Thief Wire</title><body style="background:#20304a;color:#fff">
<h1>Thief Wire</h1>
<input id="inp" autofocus placeholder="I steal focus" style="font-size:16px;padding:6px;width:60%">
<div id="log">keys: 0</div>
<ul>${STORIES.map(s => `<li><a href="/${s.slug}.html">${s.title}</a></li>`).join('')}</ul>
<script>
  window.__keys = []
  addEventListener('keydown', e => { window.__keys.push(e.key); document.getElementById('log').textContent = 'keys: ' + window.__keys.length })
  setInterval(() => { window.focus(); document.getElementById('inp').focus() }, 300)
</script>`)
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))

buildTui('Release')
const env = new Env({ display: ':85', cdpPort: 9249 })
await env.up()
const shell = env.launchShell()

const listUrl = `http://127.0.0.1:${FPORT}/`
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

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('P4.0 TUI booted', (await pollTermText(term, 'Go to URL', 30000)).ok)
  const wid = env.activate()
  await sleep(300)

  console.log('\n== P4: open the hostile list, reveal the pane ==')
  env.type('o'); await sleep(400)
  env.type(listUrl); await sleep(300)
  env.key('Return')
  check('P4.1 hostile list rendered in the reader', (await pollTermText(term, 'Quiet Turbine', 40000)).ok)
  env.type('|')
  let st = null
  for (let i = 0; i < 30; i++) { st = await term.eval('window.__wc.state()'); if (st.revealed) break; await sleep(500) }
  check('P4.2 pane revealed with the thief page live', !!st?.revealed)
  const lens = await lensCdp(st.pageBounds.width)
  let spot0 = null
  for (let i = 0; i < 40; i++) { spot0 = await lens.eval(spotHrefExpr).catch(() => null); if (spot0) break; await sleep(500) }
  check('P4.3 spotlight up on the thief page', !!spot0, String(spot0))
  await lens.eval('window.__keys.length = 0, window.__keys.splice(0), 0').catch(() => {})

  console.log('\n== P4: reader integrity under 300ms focus assault ==')
  let moves = 0
  let prev = spot0
  for (const keyPress of ['j', 'k', 'j']) { // j/k pairs move in any tile-grid semantics
    env.type(keyPress)
    let cur = prev
    for (let i = 0; i < 20; i++) {
      cur = await lens.eval(spotHrefExpr).catch(() => null)
      if (cur && cur !== prev) break
      await sleep(400)
    }
    if (cur && cur !== prev) { moves++; prev = cur }
    await sleep(500) // let the thief's interval fire between keys
  }
  check('P4.4 three moves under assault: spotlight tracked every one', moves === 3, `moves=${moves}`)
  const stolen = await lens.eval('window.__keys.length')
  check('P4.5 thief page saw ZERO keys in reader mode', stolen === 0, `keys=${stolen}`)

  console.log('\n== P4: click into the pane = deliberate browser mode ==')
  const clickX = Math.round(st.termBounds.width + st.pageBounds.width / 2)
  env.x('mousemove', '--window', wid, String(clickX), '300')
  env.x('click', '1')
  let mode = ''
  let pageFocused = false
  for (let i = 0; i < 15; i++) {
    const s2 = await term.eval('window.__wc.state()')
    mode = s2.mode
    pageFocused = s2.pageFocused
    if (mode === 'browser' && pageFocused) break
    await sleep(400)
  }
  check('P4.6 click into the pane entered browser mode AND the pane holds focus',
    mode === 'browser' && pageFocused, `mode=${mode} pageFocused=${pageFocused}`)
  // GATE HYGIENE: repeated CDP evals against the TERM target pull Chromium input focus
  // to the terminal view (observed empirically; production never evals the term target —
  // only gates do). So: read the hint FIRST, then deterministically re-click into the
  // pane before typing/Esc so the probe can't invalidate what it measures.
  const hint = await pollTermText(term, 'Esc returns to the reader', 8000)
  check('P4.7 TUI status hint teaches the Esc handback', hint.ok)
  env.x('mousemove', '--window', wid, String(clickX), '300')
  env.x('click', '1')
  await sleep(400)
  await lens.eval('document.getElementById("inp").focus(), document.getElementById("inp").value = ""').catch(() => {})
  await sleep(300)
  env.type('secret7')
  await sleep(600)
  const val = await lens.eval('document.getElementById("inp").value')
  check('P4.8 browser mode: typing reaches the page input', val === 'secret7', `value="${val}"`)
  env.shot('p4-browser-mode')

  console.log('\n== P4: Esc hands back; nothing leaks either way ==')
  env.key('Escape')
  for (let i = 0; i < 15; i++) { mode = (await term.eval('window.__wc.state()')).mode; if (mode === 'reader') break; await sleep(400) }
  check('P4.9 Esc returned to reader mode', mode === 'reader')
  if (mode !== 'reader') console.log('      shell stderr tail:\n' + shell.stderrText.split('\n').filter(l => l.includes('[shell]')).slice(-25).join('\n'))
  const before = prev
  env.type('k')
  let after = before
  for (let i = 0; i < 20; i++) { after = await lens.eval(spotHrefExpr).catch(() => null); if (after && after !== before) break; await sleep(400) }
  check('P4.10 post-Esc keys drive the TUI again (spotlight moved)', !!after && after !== before, `${before} → ${after}`)
  const valAfter = await lens.eval('document.getElementById("inp").value')
  check('P4.11 post-Esc keys did NOT leak into the page input', valAfter === 'secret7', `value="${valAfter}"`)

  console.log('\n== P4: typing keeps working across navigation churn ==')
  env.key('Return')
  let opened = null
  const t0 = Date.now()
  while (Date.now() - t0 < 40000 && !opened) {
    const txt = (await term.eval('window.__termText()').catch(() => '')).replace(/\s+/g, ' ')
    opened = STORIES.find(s => txt.includes(`the ${s.title.toLowerCase()} desk`)) || null
    if (!opened) await sleep(500)
  }
  check('P4.12 Enter opened a story (reader body on screen)', !!opened, opened ? opened.slug : 'none')
  env.type('b')
  check('P4.13 b returned to the list — keys alive through the lens navigation churn',
    (await pollTermText(term, 'Thief Wire', 25000)).ok)
  env.shot('p4-back-on-list')

  lens.close()
  term.close()
} catch (err) {
  console.error('GATE ERROR:', err)
  check('P4 gate completed without error', false, String(err))
  try { env.shot('p4-error') } catch {}
} finally {
  env.down()
  fixture.close()
}
process.exit(summary())
