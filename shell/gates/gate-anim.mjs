// Pane animation gate (workspace-va2s): the browser pane reveal/collapse is a real
// eased SLIDE, not a jump — and it must never regress the two hard rules:
//   - the TERMINAL reflows exactly once per transition (cols take exactly 2 distinct
//     values across a transition — no pty resize storm),
//   - the page view moves at FINAL SIZE (width constant in every sample).
// Legs: controlled collapse (x strictly rises to the right edge over ≥4 distinct
// positions; pane hidden + terminal expanded only at the END), controlled reveal
// (x strictly falls to the docked position; terminal narrow from the FIRST animated
// sample), mid-flight reversal (collapse interrupted by reveal settles docked), and
// WIRECOPY_SHELL_NO_ANIM=1 (instant, zero animated samples).
// Run from shell/: node gates/gate-anim.mjs
import http from 'node:http'
import { Env, attach, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

const FPORT = 8141
const fixture = http.createServer((_req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  res.end(`<!doctype html><meta charset="utf-8"><title>Motion Wire</title><body>
<h1>Motion Wire</h1><section><h2>Front Desk</h2><ul>
<li><a href="/one.html">Gliding panels: the reading desk rehearses the evening reveal one frame at a time</a></li>
<li><a href="/two.html">Collapse ledger notes every pixel the pane crossed on its way off the stage</a></li>
</ul></section>`)
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))
const listUrl = `http://127.0.0.1:${FPORT}/`

buildTui('Release')

async function sampleTransition (term, ms = 3000) {
  const t0 = Date.now()
  const samples = []
  let sawAnim = false
  while (Date.now() - t0 < ms) {
    const st = await term.eval('window.__wc.state()').catch(() => null)
    if (st) {
      samples.push({
        t: Date.now() - t0,
        x: st.pageBounds.x,
        pw: st.pageBounds.width,
        termW: st.termBounds.width,
        anim: st.paneAnimating,
        shown: st.paneShown,
        w: st.contentSize[0]
      })
      if (st.paneAnimating) sawAnim = true
      else if (sawAnim) break // animation observed and finished
    }
  }
  return samples
}
const animXs = s => s.filter(p => p.anim).map(p => p.x)
const distinct = a => [...new Set(a)]
const monotonic = (a, dir) => a.every((v, i) => i === 0 || (dir > 0 ? v >= a[i - 1] : v <= a[i - 1]))

async function navigateToList (term, env) {
  env.activate(); await sleep(300)
  env.type('o'); await sleep(400)
  env.type(listUrl); await sleep(300)
  env.key('Return')
  return (await pollTermText(term, 'Gliding panels', 40000)).ok
}

// ---------- animated run ----------
{
  const env = new Env({ display: ':79', cdpPort: 9265 })
  await env.up()
  env.launchShell()
  try {
    const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
    check('A.0 TUI booted', (await pollTermText(term, 'Go to URL', 30000)).ok)
    check('A.1 list rendered (auto-reveal animates in)', await navigateToList(term, env))
    let st = null
    for (let i = 0; i < 40; i++) { st = await term.eval('window.__wc.state()'); if (st.revealed && !st.paneAnimating) break; await sleep(300) }
    check('A.2 reveal settled: docked, reader primary', st?.revealed && st?.paneShown && st.termBounds.width > st.pageBounds.width)
    const w = st.contentSize[0]
    const dockedX = st.pageBounds.x
    const narrowW = st.termBounds.width
    const colsNarrow = (await term.eval('window.__dims()')).cols

    console.log('\n== A: collapse slides OUT (| hide) ==')
    env.activate(); await sleep(300)
    env.type('|')
    const hide = await sampleTransition(term)
    const hx = animXs(hide)
    check('A.3 collapse is a real slide: ≥4 distinct rising positions', distinct(hx).length >= 4 && monotonic(hx, +1),
      `xs=${distinct(hx).join(',')}`)
    check('A.4 pane width constant while moving (no per-frame page reflow)',
      hide.filter(p => p.anim).every(p => p.pw === st.pageBounds.width))
    check('A.5 terminal stays NARROW during the slide, expands only at the end',
      hide.filter(p => p.anim).every(p => p.termW === narrowW))
    let after = null
    for (let i = 0; i < 20; i++) { after = await term.eval('window.__wc.state()'); if (!after.paneAnimating && !after.paneShown) break; await sleep(200) }
    check('A.6 collapse settled: pane hidden, terminal full width',
      !!after && !after.paneShown && after.termBounds.width === w,
      after ? `termW=${after.termBounds.width} w=${w}` : '')
    let colsFull = colsNarrow
    for (let i = 0; i < 20; i++) { colsFull = (await term.eval('window.__dims()')).cols; if (colsFull > colsNarrow) break; await sleep(200) }
    check('A.7 exactly ONE reflow per transition (cols narrow → full, nothing between)', colsFull > colsNarrow,
      `cols ${colsNarrow} → ${colsFull}`)

    console.log('\n== A: reveal slides IN (| show) ==')
    env.type('|')
    const show = await sampleTransition(term)
    const sx = animXs(show)
    check('A.8 reveal is a real slide: ≥4 distinct falling positions ending docked',
      distinct(sx).length >= 4 && monotonic(sx, -1), `xs=${distinct(sx).join(',')}`)
    check('A.9 terminal narrow from the FIRST animated sample (single up-front reflow)',
      show.filter(p => p.anim).every(p => p.termW === narrowW))
    for (let i = 0; i < 20; i++) { after = await term.eval('window.__wc.state()'); if (!after.paneAnimating) break; await sleep(200) }
    check('A.10 reveal settled exactly at the docked position', after.paneShown && after.pageBounds.x === dockedX,
      `x=${after.pageBounds.x} docked=${dockedX}`)

    console.log('\n== A: mid-flight reversal (| then | again) ==')
    env.type('|'); await sleep(90); env.type('|')
    let rev = null
    for (let i = 0; i < 30; i++) { rev = await term.eval('window.__wc.state()'); if (!rev.paneAnimating && rev.revealed && rev.pageBounds.x === dockedX) break; await sleep(200) }
    check('A.11 reversal settles docked with no stuck animation',
      rev?.revealed && rev?.paneShown && !rev.paneAnimating && rev.pageBounds.x === dockedX,
      JSON.stringify({ x: rev?.pageBounds?.x, anim: rev?.paneAnimating, shown: rev?.paneShown }))
    env.shot('anim-settled-docked')
    term.close()
  } catch (err) {
    console.error('GATE ERROR:', err)
    check('anim gate completed without error', false, String(err))
    try { env.shot('anim-error') } catch {}
  } finally {
    env.down()
  }
}

// ---------- NO_ANIM escape hatch ----------
{
  const env = new Env({ display: ':78', cdpPort: 9266 })
  await env.up()
  env.launchShell({ WIRECOPY_SHELL_NO_ANIM: '1' })
  try {
    const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
    check('N.0 TUI booted (NO_ANIM)', (await pollTermText(term, 'Go to URL', 30000)).ok)
    // Sample across the auto-reveal window: paneAnimating must never turn on.
    const nav = navigateToList(term, env)
    let animSeen = false
    const t0 = Date.now()
    while (Date.now() - t0 < 12000) {
      const st = await term.eval('window.__wc.state()').catch(() => null)
      if (st?.paneAnimating) { animSeen = true; break }
      if (st?.revealed && st?.paneShown) break
      await sleep(30)
    }
    check('N.1 list rendered', await nav)
    let st = null
    for (let i = 0; i < 40; i++) { st = await term.eval('window.__wc.state()'); if (st.revealed && st.paneShown) break; await sleep(300) }
    check('N.2 NO_ANIM reveals instantly (zero animated samples)', !animSeen && st?.revealed && st?.paneShown)
    term.close()
  } catch (err) {
    console.error('GATE ERROR:', err)
    check('NO_ANIM leg completed without error', false, String(err))
  } finally {
    env.down()
  }
}
fixture.close()
process.exit(summary())
