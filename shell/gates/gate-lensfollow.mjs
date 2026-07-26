// Live-pane follow-along gate (workspace-hr3wr, proving workspace-ox6sj).
//
// Nothing in the gate suite asserted that the VISIBLE live pane follows the link-list
// selection, which is why the pane could freeze on a stale article on every redirecting
// site and still ship green: gate-columns' one "spotlight" check reads the LAUNCHER's
// selected column out of the terminal buffer, gate-o-pane only drives 'o' on a site that
// never redirects, and no gate read #__wirecopy-spotlight at all.
//
// Everything here is asserted as an OUTCOME inside the real page the user is looking at
// (wc.lensEval runs in state.pageView): where the spotlight box actually SITS, which
// anchor it sits ON, what the pane's URL actually IS. Presence is never the assertion.
//
// Four arms:
//   R  REDIRECTING site (/storyN.html 302s to /articles/N — i.e. every real news site:
//      trailing slash, https, m., /amp/, consent hop). This is the arm that failed
//      before the fix: after Back the pane stayed on the article forever.
//   P  PLAIN control site with no redirects, so a "fix" that just disables the takeover
//      feature does not pass.
//   C  CACHED revisit — cache hits never navigate a visible page, so the pane depends
//      ENTIRELY on follow-navigation there.
//   T  A GENUINE user takeover: the lens is driven to an unrelated URL from inside the
//      page. Follow-navigation must SUSPEND (the workspace-mctt behaviour), which is the
//      thing the redirect bug was a false positive of.
//
// Headful under Xvfb ONLY (never headless). Run from shell/: node gates/gate-lensfollow.mjs
import http from 'node:http'
import { mkdirSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { Env, attach, pollTermText, check, summary, sleep, buildTui, Cdp, findTarget } from './lib.mjs'

const PORT_R = 8271 // redirecting
const PORT_P = 8272 // plain (control)
const PORT_X = 8273 // the unrelated site a user takeover lands on
const FILLER = Array.from({ length: 12 }, (_, i) =>
  `<p>Body paragraph ${i + 1}: the desk kept careful notes on the day's dispatches.</p>`).join('\n')
const STORIES = n => Array.from({ length: n }, (_, i) =>
  `<li><a href="/story${i + 1}.html">STORY ${i + 1} — a headline long enough to wrap in a card</a></li>`).join('\n')

// /storyN.html --302--> /articles/N. The 302 is the whole point: it makes the URL the
// spotlight ASKED for differ from the one the lens LANDS on.
const redirecting = http.createServer((req, res) => {
  const m = req.url.match(/^\/story(\d+)\.html/)
  if (m) { res.writeHead(302, { location: `/articles/${m[1]}` }); res.end(); return }
  res.setHeader('content-type', 'text/html; charset=utf-8')
  const art = req.url.match(/^\/articles\/(\d+)/)
  if (art) { res.end(`<!doctype html><meta charset="utf-8"><title>REDIR Story ${art[1]}</title><body><article><h1>REDIR Story ${art[1]} headline</h1>${FILLER}</article>`); return }
  res.end(`<!doctype html><meta charset="utf-8"><title>REDIR Front</title><body><h1>REDIR Front Page</h1><section><h2>Front Desk</h2><ul>${STORIES(6)}</ul></section>`)
})
const plain = http.createServer((req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  const m = req.url.match(/^\/story(\d+)\.html/)
  if (m) { res.end(`<!doctype html><meta charset="utf-8"><title>PLAIN Story ${m[1]}</title><body><article><h1>PLAIN Story ${m[1]} headline</h1>${FILLER}</article>`); return }
  res.end(`<!doctype html><meta charset="utf-8"><title>PLAIN Front</title><body><h1>PLAIN Front Page</h1><section><h2>Front Desk</h2><ul>${STORIES(6)}</ul></section>`)
})
const unrelated = http.createServer((_req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  res.end('<!doctype html><meta charset="utf-8"><title>USER TAKEOVER PAGE</title><body><h1>Somewhere the app never sent us</h1>')
})
await new Promise(r => redirecting.listen(PORT_R, '127.0.0.1', r))
await new Promise(r => plain.listen(PORT_P, '127.0.0.1', r))
await new Promise(r => unrelated.listen(PORT_X, '127.0.0.1', r))

buildTui('Release')
const env = new Env({ display: ':95', cdpPort: 9305, screen: '1800x1100x24' })
const dataDir = path.join(env.extraEnv.XDG_DATA_HOME, 'WireCopy')
mkdirSync(dataDir, { recursive: true })
writeFileSync(path.join(dataDir, 'bookmarks.json'), JSON.stringify({
  version: 1,
  bookmarks: [
    { name: 'REDIR WIRE', url: `http://127.0.0.1:${PORT_R}/` },
    { name: 'PLAIN WIRE', url: `http://127.0.0.1:${PORT_P}/` }
  ]
}, null, 2))
await env.up()
const shell = env.launchShell()

// Read the spotlight's real geometry AND the anchor it actually covers. `onAnchor` is
// the anchor with the largest intersection area with the box — the element a user would
// say the box is "on" — plus how much of that anchor the box covers, so a box that
// merely grazes a neighbour cannot pass as being on it.
const LENS_JS = `(function(){
  const o=document.getElementById('__wirecopy-spotlight');
  let on=null, frac=0, rect=null;
  if(o){
    const ob=o.getBoundingClientRect();
    rect={x:Math.round(ob.left),y:Math.round(ob.top),w:Math.round(ob.width),h:Math.round(ob.height)};
    let best=null,bo=0;
    for(const el of document.querySelectorAll('a[href]')){
      const r=el.getBoundingClientRect();
      const ov=Math.max(0,Math.min(ob.right,r.right)-Math.max(ob.left,r.left))*
               Math.max(0,Math.min(ob.bottom,r.bottom)-Math.max(ob.top,r.top));
      if(ov>bo){bo=ov;best=el;}
    }
    if(best){const br=best.getBoundingClientRect();
      on=best.getAttribute('href'); frac=br.width*br.height?bo/(br.width*br.height):0;}
  }
  return {url:location.href, title:document.title, rect, onAnchor:on, cover:Math.round(frac*100)/100};
})()`

let lt = null
const key = async (k, ms = 1500) => { env.key(k); await sleep(ms) }

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  for (let i = 0; i < 120; i++) {
    const t = await term.eval('window.__termText?window.__termText():""').catch(() => '')
    if (t.trim().length > 20) break
    await sleep(400)
  }
  const wid = env.activate()
  await sleep(1200)
  const lens = () => term.eval(`wc.lensEval(${JSON.stringify(LENS_JS)})`)

  // The link-tree pane is the source of truth for WHICH story the user has selected.
  // Asserting the spotlight against it — rather than against "some anchor" — is what
  // makes this a check on the right target rather than on mere presence.
  const ltT = await findTarget(env.cdpPort, t => t.url.includes('linktree.html'), 25000, 'link-tree pane')
  lt = await Cdp.connect(ltT.webSocketDebuggerUrl)
  const selectedStory = async () => {
    const raw = await lt.eval('JSON.stringify(window.__linkTreeState()||{})')
    const st = (JSON.parse(raw) || {}).state || {}
    const items = st.items || []
    return items[st.selectedIndex] || null
  }
  // The pane sends story URLs absolute; the page's anchors are relative hrefs. Compare
  // on the path, which is what both actually carry.
  const pathOf = u => { try { return new URL(u, 'http://127.0.0.1').pathname } catch { return u } }

  // ── One arm: open a site, prove the box tracks the selection, prove Enter opens the
  //    selected story, prove Back restores the front page AND the box. ────────────────
  async function arm (tag, badge, listMarker, port) {
    console.log(`\n──── arm ${tag} (${listMarker}) ────`)
    await key(badge, 1200)
    await key('Return', 500)
    const reached = await pollTermText(term, listMarker, 45000)
    check(`${tag}0 the story list opened`, reached.ok)
    await sleep(4500)

    // A section header can hold the selection on entry; there is no anchor to light up
    // for one, so step onto a real story first. This is setup, never an assertion.
    for (let i = 0; i < 4; i++) {
      const s = await selectedStory()
      if (s && s.kind === 'story' && s.url) break
      await key('Down', 2000)
    }
    const sel0 = await selectedStory()
    const l0 = await lens()
    check(`${tag}1 the spotlight sits ON the SELECTED story's anchor (not merely present)`,
      !!(l0 && l0.rect && sel0 && l0.onAnchor && pathOf(l0.onAnchor) === pathOf(sel0.url) && l0.cover >= 0.5),
      `selected="${sel0 && sel0.title}" (${sel0 && sel0.url}) box=${JSON.stringify(l0 && l0.rect)} on=${l0 && l0.onAnchor} cover=${l0 && l0.cover}`)

    await key('Down', 3000)
    const sel1 = await selectedStory()
    const l1 = await lens()
    check(`${tag}2 moving the selection MOVES the box, and it lands on the NEW story`,
      !!(l1 && l1.rect && l0 && l0.rect && sel1 && (l1.rect.y !== l0.rect.y || l1.rect.x !== l0.rect.x) &&
         pathOf(l1.onAnchor) === pathOf(sel1.url) && l1.cover >= 0.5),
      `box ${JSON.stringify(l0 && l0.rect)} → ${JSON.stringify(l1 && l1.rect)} on=${l1 && l1.onAnchor} selected=${sel1 && sel1.url}`)
    env.shotWindow(`lf-${tag}-list`, wid)

    // The PRIMARY action: Enter on the selected story. Assert the VISIBLE pane's URL is
    // that story's — after any redirect, so compare on the story number the site serves.
    const opened = sel1 || sel0
    await key('Return', 500)
    await pollTermText(term, 'headline', 30000)
    await sleep(4500)
    const l2 = await lens()
    // The URL is compared on its PATH, and on the redirecting arm the story the user
    // picked is served from a different path than the one they clicked — /story3.html
    // becomes /articles/3 — so the identity that must survive is the story NUMBER.
    const wantN = (pathOf(opened.url).match(/(\d+)/) || [])[1]
    check(`${tag}3 Enter opened THAT story in the live pane (url + rendered title)`,
      !!(l2 && new RegExp(`Story ${wantN}\\b`).test(l2.title) &&
         l2.url.startsWith(`http://127.0.0.1:${port}/`) &&
         new RegExp(`(story|articles/)${wantN}\\b`).test(pathOf(l2.url))),
      `selected=${opened.url} → pane "${l2 && l2.title}" (${l2 && l2.url})`)
    env.shotWindow(`lf-${tag}-story`, wid)

    // THE CHECK THAT FAILED BEFORE workspace-ox6sj: Back must bring the pane home AND
    // bring the box back. On a redirecting site the pane used to stay on the article
    // with no box, forever, and no amount of moving the selection recovered it.
    await key('Escape', 4500)
    const sel3 = await selectedStory()
    const l3 = await lens()
    check(`${tag}4 Back returned the live pane to the FRONT PAGE`,
      !!(l3 && /Front/i.test(l3.title) && pathOf(l3.url) === '/'),
      `pane "${l3 && l3.title}" (${l3 && l3.url})`)
    check(`${tag}5 …and the spotlight came BACK, on the selected story`,
      !!(l3 && l3.rect && sel3 && pathOf(l3.onAnchor) === pathOf(sel3.url) && l3.cover >= 0.5),
      `box=${JSON.stringify(l3 && l3.rect)} on=${l3 && l3.onAnchor} selected=${sel3 && sel3.url}`)

    // And it must still TRACK after the round trip — a box that is merely re-drawn once
    // is not the same as follow-along being alive again.
    await key('Right', 3000)
    const sel4 = await selectedStory()
    const l4 = await lens()
    check(`${tag}6 …and follow-along is ALIVE again: the box moves with the selection`,
      !!(l4 && l4.rect && l3 && l3.rect && sel4 && (l4.rect.x !== l3.rect.x || l4.rect.y !== l3.rect.y) &&
         pathOf(l4.onAnchor) === pathOf(sel4.url)),
      `box ${JSON.stringify(l3 && l3.rect)} → ${JSON.stringify(l4 && l4.rect)} on=${l4 && l4.onAnchor} selected=${sel4 && sel4.url}`)
    env.shotWindow(`lf-${tag}-back`, wid)
    await key('Escape', 3000) // back to the launcher for the next arm
  }

  await arm('P', '3', 'STORY 1', PORT_P)   // control: no redirects anywhere
  await arm('R', '1', 'STORY 1', PORT_R)   // the real-world case: every story 302s

  // ── C: CACHED revisit. A cache hit never navigates a visible page, so what the user
  //    sees here comes from follow-navigation alone. Re-enter the REDIRECTING site. ────
  console.log('\n──── arm C (cached revisit of the redirecting site) ────')
  await key('1', 1200)
  await key('Return', 500)
  check('C0 the cached list re-opened', (await pollTermText(term, 'STORY 1', 45000)).ok)
  await sleep(5000)
  const selC = await selectedStory()
  const lC = await lens()
  check('C1 on a CACHED revisit the pane is back on that site\'s front page with the box on the selection',
    !!(lC && /REDIR Front/.test(lC.title) && pathOf(lC.url) === '/' && lC.rect &&
       selC && pathOf(lC.onAnchor) === pathOf(selC.url)),
    `pane "${lC && lC.title}" (${lC && lC.url}) on=${lC && lC.onAnchor} selected=${selC && selC.url}`)
  env.shotWindow('lf-C-cached', wid)

  // ── T: a GENUINE takeover. Drive the lens from inside the page to a site the app
  //    never sent it to, then move the selection: follow-navigation must SUSPEND and the
  //    user's page must survive. This is workspace-mctt's behaviour, and the reason the
  //    redirect bug existed at all — so a fix that simply deletes it must fail here. ───
  console.log('\n──── arm T (genuine user takeover) ────')
  await term.eval(`wc.lensEval("location.href='http://127.0.0.1:${PORT_X}/'; 1")`)
  await sleep(3500)
  const lT0 = await lens()
  check('T0 the lens really is on the unrelated page the "user" drove it to',
    !!(lT0 && /USER TAKEOVER/.test(lT0.title)), `pane "${lT0 && lT0.title}" (${lT0 && lT0.url})`)
  await key('Down', 3500)
  const lT1 = await lens()
  check('T1 follow-navigation SUSPENDS — the user\'s page is not yanked away',
    !!(lT1 && /USER TAKEOVER/.test(lT1.title) && lT1.url.startsWith(`http://127.0.0.1:${PORT_X}/`)),
    `pane "${lT1 && lT1.title}" (${lT1 && lT1.url})`)
  const tookOver = await pollTermText(term, 'Browser page changed', 8000)
  check('T2 …and the app OFFERS to adopt it rather than silently doing nothing',
    tookOver.ok, 'status line: "Browser page changed · y: read it here"')
  env.shotWindow('lf-T-takeover', wid)

  // Leaving the site and coming back must break the suspension — the app moving on wins.
  await key('Escape', 3000)
  await key('3', 1200)
  await key('Return', 500)
  check('T3 opening a DIFFERENT site ends the suspension (the app moving on wins)',
    (await pollTermText(term, 'STORY 1', 45000)).ok)
  await sleep(5000)
  const lT2 = await lens()
  check('T4 …and the live pane really followed to the new site',
    !!(lT2 && /PLAIN Front/.test(lT2.title)), `pane "${lT2 && lT2.title}" (${lT2 && lT2.url})`)
  env.shotWindow('lf-T-recovered', wid)

  const crashed = /A JavaScript error occurred in the main process/.test(shell.stderrText)
  check('Z0 no main-process crash in stderr', !crashed)
} catch (err) {
  console.error('GATE ERROR:', err)
  check('gate completed without error', false, String(err))
  console.error(shell.stderrText.slice(-3000))
} finally {
  if (lt) lt.close()
  env.down()
  try { redirecting.close(); plain.close(); unrelated.close() } catch {}
}
process.exit(summary() > 0 ? 1 : 0)
