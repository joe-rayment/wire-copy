// Reader-view state-class gate (workspace-79zhv): proves an article whose <body> carries a
// modal/consent STATE class (Bootstrap's `modal-open`, set while a banner or modal is up —
// exactly the state a page is captured in) still opens as a readable article in the REAL
// shell, and that a genuine cookie banner inside it is still stripped. Before the fix the
// boilerplate pass matched `contains(@class,'modal')` on <body> itself, removed the whole
// document body, and the reader came back empty.
// Headful under Xvfb ONLY (never headless). Run from shell/: node gates/gate-reader-stateclass.mjs
import http from 'node:http'
import { Env, attach, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

buildTui('Release')

const FPORT = 8171
const LEAD = 'The harbour commission voted on Tuesday to rebuild the northern breakwater'
const LATE = 'Engineers expect the first concrete caissons to be floated into place by spring'
const BANNER = 'We use cookies to improve your experience'
const para = s => `<p>${s}, according to officials who spoke after the meeting at the town hall.</p>`
// Two shapes: an <article> page, and the common div-only page with no semantic content
// container (the extractor's whole-body fallback — where a removed <body> bit hardest).
const page = wrap => `<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Breakwater rebuild approved</title></head>
<body class="article-page modal-open">
<div class="cookie-banner">${BANNER}. Accept all cookies?</div>
<${wrap}><h1>Breakwater rebuild approved</h1>
${para(LEAD)}
${para('Residents had campaigned for the repair since the winter storms breached the old wall twice')}
${para('The project will be funded jointly by the regional government and the port authority')}
${para(LATE)}
</${wrap.split(' ')[0]}></body></html>`
const fixture = http.createServer((req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  res.end(page(req.url.startsWith('/div') ? 'div class="story-body"' : 'article'))
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))
const base = `http://127.0.0.1:${FPORT}`

const env = new Env({ display: ':85', cdpPort: 9295 })
await env.up()
const shell = env.launchShell()

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('R0 shell booted to launcher', (await pollTermText(term, 'Go to URL', 30000)).ok)
  const wid = env.activate(); await sleep(500)
  for (const [tag, path] of [['article', '/article/breakwater'], ['div-only', '/div/breakwater']]) {
    env.key('o'); await sleep(700)
    env.type(base + path); await sleep(500)
    env.key('Return')
    // Articles may open in the link view first; 'r' is the reader view either way.
    await pollTermText(term, 'Breakwater rebuild approved', 40000)
    await sleep(1500)
    env.key('r'); await sleep(1500)

    const lead = await pollTermText(term, LEAD, 20000)
    check(`R1 [${tag}] reader shows the article body despite <body class="modal-open">`, lead.ok,
      lead.ok ? '' : `term text: ${lead.txt.replace(/\s+/g, ' ').slice(0, 300)}`)
    const late = await pollTermText(term, LATE, 5000)
    check(`R2 [${tag}] reader shows the LAST paragraph too (whole body survived, not a fragment)`, late.ok)
    const norm = (late.txt || lead.txt).replace(/\s+/g, ' ')
    check(`R3 [${tag}] the real cookie banner inside the body is still stripped`, !norm.includes(BANNER))
    env.shotWindow(`reader-stateclass-${tag}`, wid)
    env.key('Escape'); await sleep(800); env.key('Escape'); await sleep(800)
  }
} catch (e) {
  console.log('GATE ERROR:', e)
  check('gate completed without error', false, String(e))
  try { env.shot('reader-stateclass-error') } catch {}
} finally {
  if (process.env.GATE_DEBUG) console.log(shell.stderrText.slice(-4000))
  env.down()
  fixture.close()
}
process.exit(summary() ? 1 : 0)
