// Pane transition gate (workspace-tj1z, replaces the workspace-va2s slide gate): the
// reveal/collapse is now a compositor-driven OVERLAY animation over capturePage
// snapshots — the REAL views never animate. Hard invariants:
//   - pageBounds.x takes ONLY the two legal values (parked w / docked) — never an
//     intermediate position (the inverse of the old monotonic-slide asserts),
//   - the overlay card is what moves: __anim().cardX shows ≥6 distinct monotonic
//     positions during the slide, at constant pane width,
//   - the overlay drops within 700ms of a transition start,
//   - the terminal reflows exactly once (cols take exactly 2 distinct values),
//   - seamDirty === 0 at drop in a quiet fixture (no pty bytes after the settle capture),
//   - keystroke-never-lost: a key typed mid-transition reaches the pty,
//   - | spam / 90ms reversal settle at final parity with no stuck overlay,
//   - WIRECOPY_SHELL_NO_ANIM=1 stays instant and never shows the overlay.
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

const distinct = a => [...new Set(a)]
const monotonic = (a, dir) => a.every((v, i) => i === 0 || (dir > 0 ? v >= a[i - 1] : v <= a[i - 1]))

// Sample one transition episode: poll shell state + overlay introspection until the
// transition has been observed AND finished (or the cap elapses).
async function sampleTransition (term, ov, ms = 3000) {
  const t0 = Date.now()
  const samples = []
  let sawTrans = false
  while (Date.now() - t0 < ms) {
    const st = await term.eval('window.__wc.state()').catch(() => null)
    const an = ov ? await ov.eval('window.__anim()').catch(() => null) : null
    const dims = await term.eval('window.__dims()').catch(() => null)
    if (st) {
      samples.push({
        t: Date.now() - t0,
        x: st.pageBounds.x,
        pw: st.pageBounds.width,
        termW: st.termBounds.width,
        anim: st.paneAnimating,
        overlay: st.overlayShown,
        phase: st.transPhase,
        shown: st.paneShown,
        seamDirty: st.seamDirty,
        cols: dims ? dims.cols : null,
        cardX: an ? an.cardX : null,
        ovPhase: an ? an.phase : null
      })
      if (st.paneAnimating) sawTrans = true
      else if (sawTrans) break
    }
  }
  return samples
}

function checkEpisode (label, s, { w, dockedX, pageW, finalX, cardDir }) {
  const inTrans = s.filter(p => p.anim)
  // Real views never animate: x only ever parked (w) or docked.
  const xs = distinct(s.map(p => p.x))
  check(`${label}: pageBounds.x takes ONLY legal values (parked/docked)`,
    xs.every(x => x === w || x === dockedX), `xs=${xs.join(',')} legal=${dockedX},${w}`)
  check(`${label}: pane width constant in every sample`, s.every(p => p.pw === pageW),
    `pws=${distinct(s.map(p => p.pw)).join(',')}`)
  // The overlay card is what moves.
  const cardXs = distinct(s.filter(p => p.ovPhase === 'slide' && Number.isFinite(p.cardX)).map(p => p.cardX))
  check(`${label}: overlay card slides — ≥6 distinct monotonic positions`,
    cardXs.length >= 6 && monotonic(cardXs, cardDir), `cardXs=${cardXs.join(',')}`)
  if (cardXs.length) {
    const last = cardXs[cardXs.length - 1]
    check(`${label}: card lands exactly at ${finalX}`, Math.abs(last - finalX) <= 1, `last=${last}`)
  }
  // Overlay lifetime: from first shown sample to last shown sample. The settle is
  // anchored on the TUI's deterministic resize-rendered ack; with the workspace-7htl
  // fix (the input loop races the resize channel with a non-consuming waiter, so a
  // prompt session can no longer eat the reflow event) the ack lands ~150ms after
  // the snap (renderer RO->IPC ~40ms + ≤100ms detector poll + a ~0ms rewrap) and the
  // drop tracks slide end: measured 323ms collapse / 346ms reveal on this box.
  // 700ms = the design bound, ~2x the measured worst case for slower machines.
  const shownT = s.filter(p => p.overlay).map(p => p.t)
  if (shownT.length) console.log(`  ${label}: overlay lifetime ${shownT[shownT.length - 1] - shownT[0]}ms`)
  check(`${label}: overlay drops within 700ms`,
    shownT.length > 0 && (shownT[shownT.length - 1] - shownT[0]) <= 700,
    shownT.length ? `up=${shownT[shownT.length - 1] - shownT[0]}ms` : 'overlay never sampled shown')
  // Exactly one pty reflow across the transition: never a third (intermediate) cols
  // value. (<=2, not ===2: the sampler can miss the pre-snap value when xdotool's
  // synchronous spawn blocks the loop across the leading edge.)
  const cols = distinct(s.map(p => p.cols).filter(c => c != null))
  check(`${label}: cols never take an intermediate value (≤2 distinct)`,
    cols.length >= 1 && cols.length <= 2, `cols=${cols.join(',')}`)
}

// ---------- animated run ----------
{
  const env = new Env({ display: ':79', cdpPort: 9265 })
  await env.up()
  env.launchShell()
  try {
    const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
    check('A.0 TUI booted', (await pollTermText(term, 'Go to URL', 30000)).ok)
    env.activate(); await sleep(300)
    env.type('o'); await sleep(400)
    env.type(listUrl); await sleep(300)
    env.key('Return')
    check('A.1 list rendered (auto-reveal)', (await pollTermText(term, 'Gliding panels', 40000)).ok)
    let st = null
    for (let i = 0; i < 40; i++) { st = await term.eval('window.__wc.state()'); if (st.revealed && !st.paneAnimating) break; await sleep(300) }
    check('A.2 reveal settled: docked, reader primary', st?.revealed && st?.paneShown && st.termBounds.width > st.pageBounds.width)
    const ov = await attach(env.cdpPort, 'overlay.html', 'overlay pane')
    const w = st.contentSize[0]
    const pageW = st.pageBounds.width
    const dockedX = st.pageBounds.x
    check('A.2b docked position equals layout math', dockedX === w - Math.round(w * 0.42), `x=${dockedX} w=${w}`)
    // Quiet fixture: let prefetch finish so no stray pty bytes dirty the seam.
    await pollTermText(term, 'all 2 cached', 20000)
    await sleep(500)

    console.log('\n== A: collapse (| hide) plays on the overlay ==')
    env.activate(); await sleep(300)
    const collapseP = sampleTransition(term, ov)
    env.type('|')
    const hide = await collapseP
    checkEpisode('A.3 collapse', hide, { w, dockedX, pageW, finalX: w, cardDir: +1 })
    let after = null
    for (let i = 0; i < 20; i++) { after = await term.eval('window.__wc.state()'); if (!after.paneAnimating && !after.paneShown) break; await sleep(200) }
    check('A.4 collapse settled: pane PARKED at x=w (never hidden), terminal full width',
      !!after && !after.paneShown && after.pageBounds.x === w && after.termBounds.width === w,
      after ? `x=${after.pageBounds.x} termW=${after.termBounds.width} w=${w}` : '')
    check('A.5 seamDirty === 0 at drop (quiet fixture)', after?.seamDirty === 0, `seamDirty=${after?.seamDirty}`)

    console.log('\n== A: reveal (| show) plays on the overlay ==')
    const revealP = sampleTransition(term, ov)
    env.type('|')
    const show = await revealP
    checkEpisode('A.6 reveal', show, { w, dockedX, pageW, finalX: dockedX, cardDir: -1 })
    for (let i = 0; i < 20; i++) { after = await term.eval('window.__wc.state()'); if (!after.paneAnimating) break; await sleep(200) }
    check('A.7 reveal settled exactly at the docked position', after.paneShown && after.pageBounds.x === dockedX,
      `x=${after.pageBounds.x} docked=${dockedX}`)
    check('A.8 seamDirty === 0 at drop (quiet fixture)', after?.seamDirty === 0, `seamDirty=${after?.seamDirty}`)
    env.shot('anim-after-drop')

    console.log('\n== A: keystroke mid-transition reaches the pty (keystroke-never-lost) ==')
    env.type('|') // collapse begins; overlay covers the window
    await sleep(120)
    env.type('?') // lands while the overlay is up — must reach the TUI
    check('A.9 key typed during the transition opened the help popup',
      (await pollTermText(term, 'open link', 8000)).ok)
    env.key('Escape'); await sleep(400)

    console.log('\n== B: 90ms reversal + | spam settle at parity, no stuck overlay ==')
    // From collapsed: | (reveal) then 90ms later | (collapse) — parity: collapsed.
    env.type('|'); await sleep(90); env.type('|')
    let rev = null
    for (let i = 0; i < 30; i++) {
      rev = await term.eval('window.__wc.state()')
      if (!rev.paneAnimating && !rev.overlayShown && !rev.revealed && rev.pageBounds.x === w) break
      await sleep(200)
    }
    check('B.1 90ms reversal settles collapsed (parity), overlay dropped',
      rev && !rev.revealed && !rev.paneShown && !rev.paneAnimating && !rev.overlayShown && rev.pageBounds.x === w,
      JSON.stringify({ x: rev?.pageBounds?.x, anim: rev?.paneAnimating, ov: rev?.overlayShown, shown: rev?.paneShown }))
    // | spam: 3 rapid presses — odd parity → revealed/docked.
    const spamP = sampleTransition(term, ov, 4000)
    env.type('|'); await sleep(45); env.type('|'); await sleep(45); env.type('|')
    const spam = await spamP
    for (let i = 0; i < 30; i++) {
      rev = await term.eval('window.__wc.state()')
      if (!rev.paneAnimating && !rev.overlayShown && rev.revealed && rev.pageBounds.x === dockedX) break
      await sleep(200)
    }
    check('B.2 | spam (3x) settles docked (parity), overlay dropped',
      rev && rev.revealed && rev.paneShown && !rev.paneAnimating && !rev.overlayShown && rev.pageBounds.x === dockedX,
      JSON.stringify({ x: rev?.pageBounds?.x, anim: rev?.paneAnimating, ov: rev?.overlayShown }))
    const spamCols = distinct(spam.map(p => p.cols).filter(c => c != null))
    check('B.3 cols across the spam episode ⊆ {narrow, full} (2 values max)',
      spamCols.length <= 2, `cols=${spamCols.join(',')}`)
    env.shot('anim-spam-settled')

    console.log('\n== C: window resize mid-flight aborts to an instant snap ==')
    // From docked: start a collapse, then resize the window while the overlay is up.
    env.type('|')
    await sleep(120) // inside the slide window
    const widC = env.activate()
    env.x('windowsize', widC, '1200', '760')
    let snap = null
    for (let i = 0; i < 30; i++) {
      snap = await term.eval('window.__wc.state()')
      if (!snap.paneAnimating && !snap.overlayShown) break
      await sleep(200)
    }
    const wC = snap.contentSize[0]
    const legalC = [wC, wC - Math.round(wC * 0.42)]
    check('C.1 resize mid-flight: overlay dropped, no stuck transition',
      snap && !snap.paneAnimating && !snap.overlayShown,
      JSON.stringify({ anim: snap?.paneAnimating, ov: snap?.overlayShown }))
    check('C.2 resize mid-flight: pane snapped to layout() math at the NEW size',
      snap && legalC.includes(snap.pageBounds.x) && snap.pageBounds.width === Math.round(wC * 0.42),
      `x=${snap?.pageBounds?.x} pw=${snap?.pageBounds?.width} legal=${legalC.join(',')}`)
    env.shot('anim-resize-abort')
    term.close(); ov.close()
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
    // Sample across the auto-reveal window: neither the transition machine nor the
    // overlay may ever engage.
    env.activate(); await sleep(300)
    env.type('o'); await sleep(400)
    env.type(listUrl); await sleep(300)
    env.key('Return')
    let animSeen = false
    let overlaySeen = false
    const t0 = Date.now()
    while (Date.now() - t0 < 12000) {
      const st = await term.eval('window.__wc.state()').catch(() => null)
      if (st?.paneAnimating) animSeen = true
      if (st?.overlayShown) overlaySeen = true
      if (st?.revealed && st?.paneShown) break
      await sleep(30)
    }
    check('N.1 list rendered', (await pollTermText(term, 'Gliding panels', 40000)).ok)
    let st = null
    for (let i = 0; i < 40; i++) { st = await term.eval('window.__wc.state()'); if (st.revealed && st.paneShown) break; await sleep(300) }
    check('N.2 NO_ANIM reveals instantly (zero animated samples)', !animSeen && st?.revealed && st?.paneShown)
    check('N.3 overlay never shown under NO_ANIM', !overlaySeen)
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
