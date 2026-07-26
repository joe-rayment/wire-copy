// Reproduction probe #4: once a redirect has latched _divergedFromTarget
// (DockSpotlight.cs:382-392), what recovers and what stays stuck?
// Drives a redirecting site: open story -> back to list -> open ANOTHER story ->
// back to list -> out to the launcher -> back into the site. Reports what the visible
// pane shows at every step. Headful under Xvfb. Run from shell/: node gates/probe-diverge-persist.mjs
import http from 'node:http'
import { mkdirSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { Env, attach, pollTermText, sleep, buildTui } from './lib.mjs'

const PORT = 8271
const FILLER = Array.from({ length: 10 }, (_, i) => `<p>Body paragraph ${i + 1}: dispatches and corrections.</p>`).join('\n')
const srv = http.createServer((req, res) => {
  const m = req.url.match(/^\/story(\d+)\.html/)
  if (m) { res.writeHead(302, { location: `/articles/${m[1]}` }); res.end(); return }
  const art = req.url.match(/^\/articles\/(\d+)/)
  res.setHeader('content-type', 'text/html; charset=utf-8')
  if (art) { res.end(`<!doctype html><meta charset="utf-8"><title>REDIR Story ${art[1]}</title><body><article><h1>REDIR Story ${art[1]} headline</h1>${FILLER}</article>`); return }
  const stories = Array.from({ length: 6 }, (_, i) =>
    `<li><a href="/story${i + 1}.html">REDIR STORY ${i + 1} — a headline long enough to wrap in a card</a></li>`).join('\n')
  res.end(`<!doctype html><meta charset="utf-8"><title>REDIR Front</title><body><h1>REDIR Front Page</h1><section><h2>Front Desk</h2><ul>${stories}</ul></section>`)
})
await new Promise(r => srv.listen(PORT, '127.0.0.1', r))

buildTui('Release')
const env = new Env({ display: ':95', cdpPort: 9305, screen: '1800x1100x24' })
const dataDir = path.join(env.extraEnv.XDG_DATA_HOME, 'WireCopy')
mkdirSync(dataDir, { recursive: true })
writeFileSync(path.join(dataDir, 'bookmarks.json'), JSON.stringify({
  version: 1, bookmarks: [{ name: 'REDIR WIRE', url: `http://127.0.0.1:${PORT}/` }]
}, null, 2))
await env.up()
const shell = env.launchShell()

const LENS_JS = `(function(){const o=document.getElementById('__wirecopy-spotlight');
  return {url:location.href,title:document.title,spot:o?Math.round(o.getBoundingClientRect().top):null};})()`
let n = 0
async function look (term, label) {
  const l = await term.eval(`wc.lensEval(${JSON.stringify(LENS_JS)})`)
  console.log(`[${String(++n).padStart(2)}] ${label}\n       PANE=${l && l.title}  (${l && l.url})   SPOT=${l && l.spot != null ? 'top=' + l.spot : 'NONE'}`)
  return l
}
const key = async (k, ms = 1500) => { env.key(k); await sleep(ms) }

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal')
  for (let i = 0; i < 120; i++) { const t = await term.eval('window.__termText?window.__termText():""').catch(() => ''); if (t.trim().length > 20) break; await sleep(400) }
  const wid = env.activate()
  await sleep(1200)

  await key('Return', 500)
  console.log('list:', (await pollTermText(term, 'REDIR STORY', 45000)).ok)
  await sleep(5000)
  await look(term, 'front page, fresh')

  await key('Return', 500); await pollTermText(term, 'Story 1 headline', 30000); await sleep(4500)
  await look(term, 'opened story 1 (redirects to /articles/1)')

  await key('Escape', 4000)
  await look(term, 'BACK on the list — should show the FRONT PAGE + spotlight')
  env.shotWindow('dp-1-back-after-redirect', wid)

  await key('Down', 3000)
  await look(term, 'moved the selection on the list — spotlight should track')

  await key('Return', 500); await pollTermText(term, 'headline', 30000); await sleep(4500)
  await look(term, 'opened ANOTHER story — does the pane move?')
  env.shotWindow('dp-2-second-story', wid)

  await key('Escape', 4000)
  await look(term, 'BACK on the list again')

  await key('Escape', 3500)
  await look(term, 'out to the LAUNCHER')
  await key('1', 1200); await key('Return', 500)
  console.log('re-entered:', (await pollTermText(term, 'REDIR STORY', 45000)).ok)
  await sleep(5000)
  await look(term, 're-entered the site from the launcher — does it recover?')
  env.shotWindow('dp-3-reentered', wid)

  await key('Down', 3000)
  await look(term, 'selection moved after re-entry — spotlight back?')
  env.shotWindow('dp-4-final', wid)
} catch (e) {
  console.error('PROBE ERROR:', e); console.error(shell.stderrText.slice(-4000))
} finally { env.down(); srv.close() }
process.exit(0)
