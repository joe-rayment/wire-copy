// Reproduction probe (not a gate): drives the EXACT user actions behind the report
//   "the live page is unresponsive; follow-along on the link list no longer works;
//    when I switch to another article or site the same page stays up".
// Asserts USER-VISIBLE OUTCOMES on the REAL visible sidecar (wc.lensEval runs inside
// state.pageView, the pane the user looks at):
//   A) the spotlight rect exists AND MOVES when the selection moves, onto the newly
//      selected story's anchor;
//   B) Enter on a story navigates the visible pane to THAT story's URL;
//   C) going back and opening a DIFFERENT story re-navigates it;
//   D) opening a DIFFERENT SITE from the launcher re-navigates it.
// Headful under Xvfb. Run from shell/: node gates/probe-followalong.mjs
import http from 'node:http'
import { mkdirSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { Env, attach, pollTermText, sleep, buildTui } from './lib.mjs'

const PORT_A = 8241
const PORT_B = 8242
const FILLER = Array.from({ length: 10 }, (_, i) =>
  `<p>Body paragraph ${i + 1}: the desk kept careful notes on the day's dispatches and filed corrections before press time.</p>`).join('\n')

function site (name, port) {
  const stories = Array.from({ length: 6 }, (_, i) =>
    `<li><a href="/story${i + 1}.html">${name} STORY ${i + 1} — a headline long enough to wrap in a card</a></li>`).join('\n')
  return http.createServer((req, res) => {
    res.setHeader('content-type', 'text/html; charset=utf-8')
    const m = req.url.match(/^\/story(\d+)\.html/)
    if (m) {
      res.end(`<!doctype html><meta charset="utf-8"><title>${name} Story ${m[1]}</title><body><article><h1>${name} Story ${m[1]} headline</h1>${FILLER}</article>`)
      return
    }
    res.end(`<!doctype html><meta charset="utf-8"><title>${name} Front</title><body><h1>${name} Front Page</h1><section><h2>Front Desk</h2><ul>${stories}</ul></section>`)
  })
}
const a = site('ALPHA', PORT_A); const b = site('BETA', PORT_B)
await new Promise(r => a.listen(PORT_A, '127.0.0.1', r))
await new Promise(r => b.listen(PORT_B, '127.0.0.1', r))

buildTui('Release')
const env = new Env({ display: ':92', cdpPort: 9302, screen: '1800x1100x24' })
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

// Read the VISIBLE sidecar: its URL, and the spotlight overlay's rect + the anchor it sits on.
const LENS_JS = `(function(){
  const o=document.getElementById('__wirecopy-spotlight');
  let on=null;
  if(o){
    const ob=o.getBoundingClientRect();
    // Which anchor does the overlay sit on? Compare document-space rects.
    let best=null,bestOverlap=0;
    for(const el of document.querySelectorAll('a[href]')){
      const r=el.getBoundingClientRect();
      const ix=Math.max(0,Math.min(ob.right,r.right)-Math.max(ob.left,r.left));
      const iy=Math.max(0,Math.min(ob.bottom,r.bottom)-Math.max(ob.top,r.top));
      const ov=ix*iy;
      if(ov>bestOverlap){bestOverlap=ov;best=el;}
    }
    on=best?{href:best.getAttribute('href'),text:(best.textContent||'').trim().slice(0,50)}:null;
  }
  return {url:location.href, title:document.title,
    spotlight:o?{left:Math.round(o.getBoundingClientRect().left),top:Math.round(o.getBoundingClientRect().top),
      w:Math.round(o.getBoundingClientRect().width),h:Math.round(o.getBoundingClientRect().height)}:null,
    onAnchor:on, anchors:document.querySelectorAll('a[href]').length,
    h1:(document.querySelector('h1')||{}).textContent||''};
})()`

let step = 0
async function look (term, label) {
  const lens = await term.eval(`wc.lensEval(${JSON.stringify(LENS_JS)})`)
  const st = await term.eval('(await wc.state())').catch(() => null)
  console.log(`\n[${++step}] ${label}`)
  console.log(`     lens url    : ${lens && lens.url}`)
  console.log(`     lens title  : ${lens && lens.title}   h1="${(lens && lens.h1 || '').slice(0, 40)}"  anchors=${lens && lens.anchors}`)
  console.log(`     spotlight   : ${lens && lens.spotlight ? JSON.stringify(lens.spotlight) : 'NONE'}`)
  console.log(`     sits on     : ${lens && lens.onAnchor ? lens.onAnchor.href + '  "' + lens.onAnchor.text + '"' : 'n/a'}`)
  if (st) console.log(`     shell       : revealed=${st.revealed} pageBounds=${JSON.stringify(st.pageBounds)} mode=${st.mode}`)
  return lens
}

function termTail (txt, n = 6) {
  return txt.split('\n').map(l => l.replace(/\s+$/, '')).filter(l => l.trim()).slice(-n).join('\n       ')
}

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal')
  for (let i = 0; i < 120; i++) { const t = await term.eval('window.__termText?window.__termText():""').catch(() => ''); if (t.trim().length > 20) break; await sleep(400) }
  const wid = env.activate()
  await sleep(1200)

  // --- open ALPHA's front page -> link list, sidecar docks
  env.key('Return')
  const got = await pollTermText(term, 'ALPHA STORY', 45000)
  console.log('link list reached:', got.ok)
  await sleep(4000)
  const s0 = await look(term, 'link list up, selection on story 1')
  env.shotWindow('fa-1-story1', wid)

  // --- A) MOVE THE SELECTION: follow-along must move the spotlight
  env.key('Down'); await sleep(900)
  env.key('Down'); await sleep(3000)
  const s1 = await look(term, 'after Down x2 — spotlight must be on story 3')
  env.shotWindow('fa-2-after-down', wid)
  console.log(`     >>> A) spotlight MOVED: ${JSON.stringify(s0 && s0.spotlight) !== JSON.stringify(s1 && s1.spotlight)}`)
  console.log(`     >>> A) sits on story 3: ${!!(s1 && s1.onAnchor && /story3/.test(s1.onAnchor.href))}`)

  // --- B) Enter on story 3 -> the visible pane must navigate to story3
  env.key('Return')
  await pollTermText(term, 'Story 3', 30000)
  await sleep(4000)
  const s2 = await look(term, 'after Enter on story 3 — lens must be story3.html')
  env.shotWindow('fa-3-story3-open', wid)
  console.log(`     >>> B) lens on story3: ${!!(s2 && /story3/.test(s2.url))}`)

  // --- C) back, move to story 5, Enter -> the visible pane must re-navigate
  env.key('Escape'); await sleep(2500)
  env.key('Down'); await sleep(700)
  env.key('Down'); await sleep(2500)
  const s3 = await look(term, 'back on the list, selection moved to story 5')
  env.key('Return')
  await pollTermText(term, 'Story 5', 30000)
  await sleep(4000)
  const s4 = await look(term, 'after Enter on story 5 — lens must be story5.html')
  env.shotWindow('fa-4-story5-open', wid)
  console.log(`     >>> C) lens moved to story5: ${!!(s4 && /story5/.test(s4.url))}`)

  // --- D) back to the launcher, open a DIFFERENT SITE
  env.key('Escape'); await sleep(1500)
  env.key('Escape'); await sleep(2500)
  const txt = await term.eval('window.__termText()')
  console.log('\n     terminal tail:\n       ' + termTail(txt))
  env.key('2'); await sleep(1500)
  const gotB = await pollTermText(term, 'BETA STORY', 45000)
  console.log('\nBETA reached in TUI:', gotB.ok)
  await sleep(4500)
  const s5 = await look(term, 'after opening BETA WIRE — lens must be the BETA front page')
  env.shotWindow('fa-5-beta', wid)
  console.log(`     >>> D) lens moved to BETA (port ${PORT_B}): ${!!(s5 && s5.url && s5.url.includes(String(PORT_B)))}`)

  console.log('\n--- shell stderr tail ---')
  console.log(shell.stderrText.split('\n').filter(l => /spotlight|lens|dock|pane|Spotlight/i.test(l)).slice(-25).join('\n'))
} catch (e) {
  console.error('PROBE ERROR:', e)
  console.error(shell.stderrText.slice(-4000))
} finally { env.down(); a.close(); b.close() }
process.exit(0)
