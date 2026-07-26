// workspace-ytdm3: does a key pressed DURING the decrypt reveal cut it short?
//
// The reveal plays 8 frames x 50ms synchronously ON the input loop, so it is ~350ms of
// dead keys on every navigation. DecryptRevealAnimation.Play takes a shouldAbort
// predicate; the question is whether the predicate now in use — IInputHandler.HasPendingKey
// — actually fires, and whether it costs anything.
//
// Timing is the whole difficulty. On a FIRST visit the reveal plays at the end of a
// multi-second page load, so a key sent 40ms after Return lands nowhere near it. This
// drives a CACHED revisit instead: the build cache hits instantly, so the reveal follows
// Return immediately and a key 40ms later lands inside the sweep.
//
// Compares two cached opens of the same story in one boot — one silent, one with a key —
// and reads the app's own [loop-timing] line for each.
//
// Headful under Xvfb. Run from shell/: node gates/probe-reveal-abort.mjs
import http from 'node:http'
import fs from 'node:fs'
import path from 'node:path'
import { Env, attach, pollTermText, sleep, buildTui, ROOT } from './lib.mjs'

const FPORT = 8169
const fixture = http.createServer((req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  if (/^\/story1\.html/.test(req.url)) {
    res.end('<!doctype html><meta charset="utf-8"><title>Reveal Story</title><body><article>' +
      '<h1>Reveal story: the desk rehearses its decrypt sweep</h1>' +
      Array.from({ length: 25 }, (_, i) =>
        `<p>Body paragraph ${i + 1}: the desk kept careful notes before press time.</p>`).join('\n') +
      '</article>')
    return
  }
  res.end('<!doctype html><meta charset="utf-8"><title>Reveal Wire</title><body><h1>Reveal Wire</h1>' +
    '<section><h2>Front Desk</h2><ul>' +
    '<li><a href="/story1.html">REVEAL STORY ONE — a headline long enough to wrap in a card</a></li>' +
    '</ul></section>')
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))

buildTui('Release')
const env = new Env({ display: ':91', cdpPort: 9311, screen: '1500x950x24' })
const dataDir = path.join(env.extraEnv.XDG_DATA_HOME, 'WireCopy')
fs.mkdirSync(dataDir, { recursive: true })
fs.writeFileSync(path.join(dataDir, 'bookmarks.json'), JSON.stringify({
  version: 1,
  bookmarks: [{ name: 'REVEAL WIRE', url: `http://127.0.0.1:${FPORT}/` }]
}, null, 2))

const logDir = path.join(ROOT, 'logs')
const logFile = () => {
  const f = fs.readdirSync(logDir).filter(x => /^wirecopy-.*\.log$/.test(x)).sort()
  return f.length ? path.join(logDir, f[f.length - 1]) : null
}
const readFrom = (f, off) => {
  const size = fs.statSync(f).size
  if (size <= off) return ''
  const fd = fs.openSync(f, 'r')
  const buf = Buffer.alloc(size - off)
  fs.readSync(fd, buf, 0, buf.length, off)
  fs.closeSync(fd)
  return buf.toString('utf8')
}
const branchTimes = text => [...text.matchAll(/branch held the loop (\d+)ms \(input=true\)/g)].map(m => Number(m[1]))

await env.up()
const shell = env.launchShell()

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal')
  await pollTermText(term, 'Go to URL', 40000)
  env.activate()
  await sleep(1200)

  // 1. Open the list, then the story once so the build cache holds it.
  env.key('Return'); await sleep(600)
  console.log('list:', (await pollTermText(term, 'REVEAL STORY ONE', 45000)).ok)
  await sleep(2500)
  env.key('Return')
  console.log('first (uncached) open:', (await pollTermText(term, 'rehearses its decrypt sweep', 45000)).ok)
  await sleep(3000)
  env.key('Escape'); await sleep(2500)

  const f = logFile()
  const measure = async (label, sendKey) => {
    const off = fs.statSync(f).size
    env.key('Return')
    if (sendKey) { await sleep(40); env.key('j') }
    await pollTermText(term, 'rehearses its decrypt sweep', 30000)
    await sleep(2500)
    const times = branchTimes(readFrom(f, off))
    // Read the header WHILE the story is still up. An aborted sweep must jump to the
    // finished frame, never strand the noise characters it was cycling through — and
    // that has to be checked here, not after Escape has taken the story off screen.
    const screen = await term.eval('window.__termText ? window.__termText() : ""')
    const titleOk = /rehearses its decrypt sweep/i.test(screen)
    console.log(`  ${label}: [loop-timing] ${JSON.stringify(times)}ms   header intact: ${titleOk ? 'YES' : 'NO'}`)
    env.key('Escape'); await sleep(2500)
    return { ms: times.length ? Math.max(...times) : null, titleOk }
  }

  console.log('\n--- cached opens ---')
  const silent = await measure('silent   ', false)
  const keyed = await measure('key at 40ms', true)

  console.log(`\n  silent cached open : ${silent.ms}ms   header intact: ${silent.titleOk}`)
  console.log(`  keyed  cached open : ${keyed.ms}ms   header intact: ${keyed.titleOk}`)
  console.log(`  DELTA              : ${silent.ms != null && keyed.ms != null ? (silent.ms - keyed.ms) + 'ms saved' : 'n/a'}`)
} catch (e) {
  console.error('PROBE ERROR:', e)
  console.error(shell.stderrText.slice(-2000))
} finally { env.down(); try { fixture.close() } catch {} }
process.exit(0)
