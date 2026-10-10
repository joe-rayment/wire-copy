// Link false-positive gate (workspace-vk4nf): proves real headlines that merely CONTAIN an
// ad/login word reach the user's story list in the REAL shell. Before the fix the link
// extractor matched ad labels and URL markers as bare substrings, so "Week ahead: Fed
// decision" (contains "ad:") was dropped outright, "State-sponsored hackers…" was flagged
// as an ad, and /tech/…/login-with-passkeys-explained was filed as site navigation. Every
// check asserts what the link-tree pane actually shows (its pushed items + painted cards).
// Headful under Xvfb ONLY (never headless). Run from shell/: node gates/gate-links-falsepos.mjs
import http from 'node:http'
import { Env, attach, Cdp, findTarget, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

buildTui('Release')

const FPORT = 8172
// Headlines that must survive as stories, with the href each one points at.
const KEEP = [
  ['Week ahead: Fed decision', '/markets/2026/10/week-ahead-fed-decision'],
  ['State-sponsored hackers breach a regional water utility', '/world/2026/10/state-sponsored-hackers-water'],
  ['Senator promoted to committee chair after a close vote', '/politics/2026/10/senator-promoted-chair'],
  ['Log in with passkeys: the end of passwords explained', '/tech/2026/10/login-with-passkeys-explained'],
  ['Subscription fatigue hits the streaming business hard', '/business/2026/10/subscription-fatigue-hits-streaming'],
]
// Ordinary stories so the page reads as a news front.
const PLAIN = [
  ['Harbour commission approves the northern breakwater rebuild', '/local/2026/10/breakwater-rebuild'],
  ['City council votes to extend late-night bus routes', '/local/2026/10/late-night-buses'],
  ['Regional orchestra announces a free winter concert series', '/arts/2026/10/winter-concerts'],
]
// The real thing must still be caught: an ad label leading the text.
const AD = ['Sponsored: Upgrade your kitchen with Acme chef knives today', '/promo/acme-knives']

const li = ([t, h]) => `<li><a href="${h}">${t}</a> <time datetime="2026-10-10T08:00:00Z">Oct 10</time></li>`
const fixture = http.createServer((req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  res.end(`<!doctype html><meta charset="utf-8"><title>Harbour Gazette</title><body>
<h1>Harbour Gazette</h1>
<section><h2>Top stories</h2><ul>${[...KEEP, ...PLAIN, AD].map(li).join('\n')}</ul></section>`)
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))
const base = `http://127.0.0.1:${FPORT}`

const env = new Env({ display: ':86', cdpPort: 9296 })
await env.up()
const shell = env.launchShell()

let lt = null
try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('F0 shell booted to launcher', (await pollTermText(term, 'Go to URL', 30000)).ok)
  const wid = env.activate(); await sleep(500)
  // Keystrokes can be lost while the machine is busy (a dropped 'o' leaves the launcher
  // up; a dropped '/' mangles the URL), so the open is retried — the CHECK is that the
  // page rendered, not that the first attempt did.
  let opened = { ok: false }
  for (let attempt = 0; attempt < 3 && !opened.ok; attempt++) {
    if (attempt) { env.key('Escape'); await sleep(600); env.key('Escape'); await sleep(600) }
    env.key('o'); await sleep(900)
    // Two strokes: one xdotool run can drop the second of two identical keys ("//").
    env.type('http:/'); await sleep(200); env.type(base.slice('http:/'.length) + '/'); await sleep(500)
    env.key('Return')
    opened = await pollTermText(term, 'breakwater', 30000)
  }
  check('F1 TUI rendered the fixture front page', opened.ok)

  const t0 = await findTarget(env.cdpPort, t => t.url.includes('linktree.html'), 20000, 'link-tree pane')
  lt = await Cdp.connect(t0.webSocketDebuggerUrl)
  for (let i = 0; i < 40 && !(await lt.eval('!!window.__linkTreeReady').catch(() => false)); i++) await sleep(250)
  await sleep(1500)

  // What the pane was handed and what it painted. The pane shows one PAGE of stories, so
  // walk the selection down the whole list (as the user would) and collect every page.
  const read = async () => JSON.parse(await lt.eval(`JSON.stringify((() => {
    const st = (window.__linkTreeState() || {}).state || {}
    return {
      items: (st.items || []).filter(i => i.kind !== 'section').map(i => ({ title: i.title, linkType: i.linkType })),
      painted: [...document.querySelectorAll('#grid .card-title')].map(e => e.textContent),
      total: st.totalStories, more: !!st.moreBelow, sel: st.selectedIndex
    }
  })())`))
  const seen = { items: [], painted: [], total: 0 }
  const merge = r => {
    for (const it of r.items) if (!seen.items.some(s => s.title === it.title)) seen.items.push(it)
    for (const p of r.painted) if (!seen.painted.includes(p)) seen.painted.push(p)
    seen.total = r.total
  }
  let r = await read(); merge(r)
  for (let i = 0; i < 16; i++) {
    env.key('Down'); await sleep(400)
    const next = await read(); merge(next)
    if (!next.more && next.sel === r.sel) break
    r = next
  }
  check('F1b walked the whole story list', seen.items.length >= seen.total && seen.total > 0,
    `collected=${seen.items.length} totalStories=${seen.total}`)
  const item = t => seen.items.find(i => (i.title || '').includes(t))
  for (const [t] of KEEP) {
    const it = item(t)
    check(`F2 "${t}" is in the story list as a story`, !!it && it.linkType === 'content',
      it ? `linkType=${it.linkType}` : `missing; items=${JSON.stringify(seen.items.map(i => i.title))}`)
  }
  check('F3 the painted cards include the short "Week ahead: Fed decision" headline',
    seen.painted.some(p => p.includes('Week ahead: Fed decision')), `painted=${JSON.stringify(seen.painted)}`)
  // An ad isn't hidden — it is flagged and DEMOTED below the real stories. So the outcome
  // to see is ORDER: the genuine "Sponsored: …" sits below every full-length headline,
  // and the headlines that merely contain an ad word are NOT demoted alongside it.
  const pos = t => seen.painted.findIndex(p => p.includes(t))
  const adAt = pos('Acme chef knives')
  const longReal = [...KEEP, ...PLAIN].map(([t]) => t).filter(t => t.length >= 25)
  const above = longReal.filter(t => pos(t) >= 0 && pos(t) < adAt)
  check('F4 the genuine "Sponsored: …" link is still demoted below every full-length real headline',
    adAt >= 0 && above.length === longReal.length,
    `adAt=${adAt} order=${JSON.stringify(seen.painted)}`)
  env.shotWindow('links-falsepos', wid)
} catch (e) {
  console.log('GATE ERROR:', e)
  check('gate completed without error', false, String(e))
  try { env.shot('links-falsepos-error') } catch {}
} finally {
  if (process.env.GATE_DEBUG) console.log(shell.stderrText.slice(-4000))
  try { lt?.close?.() } catch {}
  env.down()
  fixture.close()
}
process.exit(summary() ? 1 : 0)
