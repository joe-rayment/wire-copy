// Reproduction probe #2: the CACHED-revisit path, where the visible sidecar depends
// entirely on the spotlight's follow-navigation (cache hits and HTTP fetches never
// navigate a visible page — DockSpotlight.cs:23-25).
// Drives: fresh site -> story -> back -> OTHER site -> back -> FIRST site again (cached)
// -> move the selection -> open a cached story. After every step it reports what the
// USER-VISIBLE pane actually shows (wc.lensEval runs inside state.pageView) and whether
// the pane is docked.
// Headful under Xvfb. Run from shell/: node gates/probe-lensfollow.mjs
import http from 'node:http'
import { mkdirSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { Env, attach, pollTermText, sleep, buildTui } from './lib.mjs'

const PORT_A = 8251
const PORT_B = 8252
const FILLER = Array.from({ length: 10 }, (_, i) =>
  `<p>Body paragraph ${i + 1}: the desk kept careful notes on the day's dispatches and filed corrections before press time.</p>`).join('\n')

function site (name, port) {
  const stories = Array.from({ length: 6 }, (_, i) =>
    `<li><a href="/story${i + 1}.html">${name} STORY ${i + 1} — a headline long enough to wrap in a card</a></li>`).join('\n')
  return http.createServer((req, res) => {
    res.setHeader('content-type', 'text/html; charset=utf-8')
    const m = req.url.match(/^\/story(\d+)\.html/)
    if (m) { res.end(`<!doctype html><meta charset="utf-8"><title>${name} Story ${m[1]}</title><body><article><h1>${name} Story ${m[1]} headline</h1>${FILLER}</article>`); return }
    res.end(`<!doctype html><meta charset="utf-8"><title>${name} Front</title><body><h1>${name} Front Page</h1><section><h2>Front Desk</h2><ul>${stories}</ul></section>`)
  })
}
const a = site('ALPHA', PORT_A); const b = site('BETA', PORT_B)
await new Promise(r => a.listen(PORT_A, '127.0.0.1', r))
await new Promise(r => b.listen(PORT_B, '127.0.0.1', r))

buildTui('Release')
const env = new Env({ display: ':93', cdpPort: 9303, screen: '1800x1100x24' })
const dataDir = path.join(env.extraEnv.XDG_DATA_HOME, 'WireCopy')
mkdirSync(dataDir, { recursive: true })
writeFileSync(path.join(dataDir, 'bookmarks.json'), JSON.stringify({
  version: 1,
  bookmarks: [
    { name: 'ALPHA WIRE', url: `http://127.0.0.1:${PORT_A}/` },
    { name: 'BETA WIRE', url: `http://127.0.0.1:${PORT_B}/` }
  ]
}, null, 2))
await env.up()
const shell = env.launchShell()

const LENS_JS = `(function(){
  const o=document.getElementById('__wirecopy-spotlight');
  let on=null;
  if(o){
    const ob=o.getBoundingClientRect(); let best=null,bo=0;
    for(const el of document.querySelectorAll('a[href]')){
      const r=el.getBoundingClientRect();
      const ov=Math.max(0,Math.min(ob.right,r.right)-Math.max(ob.left,r.left))*Math.max(0,Math.min(ob.bottom,r.bottom)-Math.max(ob.top,r.top));
      if(ov>bo){bo=ov;best=el;}
    }
    on=best?best.getAttribute('href'):null;
  }
  return {url:location.href, title:document.title, spot:o?Math.round(o.getBoundingClientRect().top):null, onAnchor:on};
})()`
const STATE_JS = `(async()=>{const s=await wc.state();return {revealed:s.revealed,mode:s.mode,pw:s.pageBounds.width,px:s.pageBounds.x,cw:s.contentBounds.width};})()`

let n = 0
async function look (term, label) {
  const lens = await term.eval(`wc.lensEval(${JSON.stringify(LENS_JS)})`)
  const st = await term.eval(STATE_JS).catch(e => ({ err: String(e) }))
  const docked = st && st.px != null ? st.px < st.cw : null
  console.log(`\n[${++n}] ${label}`)
  console.log(`     PANE SHOWS : ${lens && lens.title}   (${lens && lens.url})`)
  console.log(`     spotlight  : ${lens && lens.spot != null ? 'top=' + lens.spot + ' on ' + lens.onAnchor : 'NONE'}`)
  console.log(`     pane docked: ${docked}  revealed=${st && st.revealed}  pageBounds.x=${st && st.px} of ${st && st.cw}`)
  return { lens, st, docked }
}

const key = async (k, ms = 1200) => { env.key(k); await sleep(ms) }

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal')
  for (let i = 0; i < 120; i++) { const t = await term.eval('window.__termText?window.__termText():""').catch(() => ''); if (t.trim().length > 20) break; await sleep(400) }
  const wid = env.activate()
  await sleep(1200)

  // 1. ALPHA, first (uncached) visit
  await key('Return', 500)
  console.log('ALPHA list:', (await pollTermText(term, 'ALPHA STORY', 45000)).ok)
  await sleep(4000)
  await look(term, 'ALPHA front, FIRST visit (uncached)')
  env.shotWindow('lf-1-alpha-fresh', wid)

  // 2. open story 1 (uncached)
  await key('Return', 500)
  console.log('story1:', (await pollTermText(term, 'Story 1 headline', 30000)).ok)
  await sleep(3500)
  await look(term, 'ALPHA story 1 open (uncached)')

  // 3. back to the list, back to the launcher
  await key('Escape', 2500)
  await look(term, 'back on the ALPHA list')
  await key('Escape', 3000)
  await look(term, 'back on the LAUNCHER — panel should be collapsed')
  env.shotWindow('lf-2-launcher', wid)

  // 4. open BETA (a DIFFERENT site) — bookmark badge [3] (Reading List is [2])
  await key('3', 1200)
  await key('Return', 500)
  console.log('BETA list:', (await pollTermText(term, 'BETA STORY', 45000)).ok)
  await sleep(4500)
  await look(term, 'BETA front — the pane MUST have switched sites')
  env.shotWindow('lf-3-beta', wid)

  // 5. back to the launcher, re-open ALPHA — now CACHED
  await key('Escape', 3000)
  await key('1', 1200)
  await key('Return', 500)
  console.log('ALPHA list again:', (await pollTermText(term, 'ALPHA STORY', 45000)).ok)
  await sleep(5000)
  await look(term, 'ALPHA front, SECOND visit (CACHED) — pane must be back on ALPHA')
  env.shotWindow('lf-4-alpha-cached', wid)

  // 6. move the selection on the cached list — follow-along must track
  await key('Down', 2500)
  await look(term, 'after Down on the cached list — spotlight must move a row')
  await key('Right', 2500)
  await look(term, 'after Right — spotlight must move a column')
  env.shotWindow('lf-5-cached-moved', wid)

  // 7. open a story from the cached list
  await key('Return', 500)
  console.log('story open:', (await pollTermText(term, 'headline', 30000)).ok)
  await sleep(4000)
  await look(term, 'story opened from the CACHED list — pane must show that story')
  env.shotWindow('lf-6-cached-story', wid)

  const err = shell.stderrText.split('\n').filter(l => /spotlight|lens|dock|Diverge|error|Error|warn/i.test(l))
  console.log('\n--- shell stderr (filtered, last 30) ---\n' + err.slice(-30).join('\n'))
} catch (e) {
  console.error('PROBE ERROR:', e); console.error(shell.stderrText.slice(-4000))
} finally { env.down(); a.close(); b.close() }
process.exit(0)
