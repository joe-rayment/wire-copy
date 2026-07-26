// Calibration probe (workspace-981sf): measure the REAL link-tree pane's chrome and
// card stride off the live DOM, so LinkTreeRenderer's admission budget is calibrated
// against measurement instead of summed from linktree.css's clamp()s. #grid is the
// only shrinkable child of #column (flex:1 1 auto), so the grid's own height IS the
// budget and chrome == column height - grid height, exactly.
// Headful under Xvfb ONLY (never headless). Run from shell/: node gates/probe-linktree-budget.mjs
import http from 'node:http'
import fs from 'node:fs'
import path from 'node:path'
import { Env, attach, Cdp, findTarget, pollTermText, buildTui, sleep } from './lib.mjs'

buildTui('Release')

const FPORT = 8171
const story = (i, w) =>
  `<li><a href="/a${i}.html">Dispatch ${w}: a headline sized for the responsive grid probe number ${i}</a>` +
  ` <span class="byline">Ada Lovelace</span> <time datetime="2026-07-19T08:00:00Z">Jul 19</time></li>`
const WORDS = Array.from({ length: 24 }, (_, i) => `w${i + 1}`)
const fixture = http.createServer((req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  res.end(`<!doctype html><meta charset="utf-8"><title>Column Wire</title><body>
<h1>Column Wire</h1>
<section><h2>Dispatches</h2><ul>${WORDS.map((w, i) => story(i + 1, w)).join('\n')}</ul></section>`)
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))

const env = new Env({ display: ':88', cdpPort: 9298, screen: '2600x1500x24' })
fs.writeFileSync(path.join(env.extraEnv.WIRECOPY_SHELL_USERDATA, 'window-bounds.json'),
  JSON.stringify({ x: 0, y: 0, width: 1360, height: 900 }))
await env.up()
env.launchShell()

const GEO = `(function(){
  const col=document.getElementById('column'), g=document.getElementById('grid');
  const cr=col.getBoundingClientRect(), gr=g.getBoundingClientRect();
  const cards=[...g.querySelectorAll('.card')];
  const secs=[...g.querySelectorAll('.section')];
  const tops=[...new Set(cards.map(c=>Math.round(c.getBoundingClientRect().top)))].sort((a,b)=>a-b);
  const st=window.__linkTreeState?window.__linkTreeState():null;
  return {winW:window.innerWidth, winH:window.innerHeight,
    colH:Math.round(cr.height), gridTop:Math.round(gr.top), gridH:Math.round(gr.height),
    chrome:Math.round(cr.height-gr.height),
    cardH:cards.length?Math.round(cards[0].getBoundingClientRect().height):null,
    rowStride:tops.length>1?tops[1]-tops[0]:null,
    secH:secs.length?Math.round(secs[0].getBoundingClientRect().height):null,
    rows:tops.length, cards:cards.length, sections:secs.length,
    cols:getComputedStyle(g).gridTemplateColumns.split(' ').length,
    stateCols:st&&st.state?st.state.columns:null,
    shown:(document.querySelector('#header-right .hr-shown')||{}).textContent||'',
    // How much of the grid's height the admitted window actually uses, and how
    // much room is left over — the dead band the user sees.
    usedH:cards.length?Math.round(Math.max(...cards.map(c=>c.getBoundingClientRect().bottom))-gr.top):0};
})()`

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  await pollTermText(term, 'Go to URL', 30000)
  env.activate(); await sleep(500)
  env.key('o'); await sleep(700)
  env.type(`http://127.0.0.1:${FPORT}/`); await sleep(500)
  env.key('Return')
  await pollTermText(term, 'Dispatch one', 40000)
  const t0 = await findTarget(env.cdpPort, t => t.url.includes('linktree.html'), 20000, 'link-tree pane')
  const lt = await Cdp.connect(t0.webSocketDebuggerUrl)
  for (let i = 0; i < 40 && !(await lt.eval('!!window.__linkTreeReady').catch(() => false)); i++) await sleep(250)

  const wid = env.activate()
  let docked = true // the pane boots with the sidecar docked
  for (const [w, h] of [[1360, 900], [1512, 982], [1680, 1000], [1680, 1300], [1100, 800]]) {
    env.x('windowsize', wid, String(w), String(h))
    // Nudge the selection so the TUI re-renders and re-pushes state at the new size.
    await sleep(1500); env.key('Down'); await sleep(900); env.key('Up'); await sleep(1200)
    for (const want of [true, false]) {
      if (docked !== want) { env.key('bar'); await sleep(2200); docked = want }
      const g = await lt.eval(GEO)
      console.log(`win ${w}x${h} ${want ? 'DOCKED  ' : 'UNDOCKED'} ` + JSON.stringify(g))
    }
    // leave it docked for the next size
    if (!docked) { env.key('bar'); await sleep(2200); docked = true }
  }
} finally {
  env.down()
  fixture.close()
}
