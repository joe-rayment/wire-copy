// Stretch gate: same shell, terminal pane swapped from xterm.js to ghostty-web.
import { execFileSync } from 'node:child_process'
import { mkdirSync } from 'node:fs'

const PORT = process.env.SPIKE_CDP_PORT || '9334'
const DISPLAY = process.env.SPIKE_DISPLAY || ':78'
const OUT = new URL('./out/', import.meta.url).pathname
mkdirSync(OUT, { recursive: true })
const sleep = ms => new Promise(r => setTimeout(r, ms))

async function findTarget (pred, timeoutMs = 25000) {
  const t0 = Date.now()
  while (Date.now() - t0 < timeoutMs) {
    const list = await fetch(`http://127.0.0.1:${PORT}/json`).then(r => r.json()).catch(() => [])
    const hit = list.find(t => t.type === 'page' && pred(t))
    if (hit) return hit
    await sleep(300)
  }
  throw new Error('target not found')
}
async function connect (wsUrl) {
  const ws = new WebSocket(wsUrl)
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej })
  let id = 0; const pending = new Map()
  ws.onmessage = ev => {
    const m = JSON.parse(ev.data)
    if (m.id && pending.has(m.id)) { const { res } = pending.get(m.id); pending.delete(m.id); res(m.result) }
  }
  return {
    eval: async expr => {
      const my = ++id
      ws.send(JSON.stringify({ id: my, method: 'Runtime.evaluate', params: { expression: expr, returnByValue: true, awaitPromise: true } }))
      const r = await new Promise(res => pending.set(my, { res }))
      return r?.result?.value
    },
    close: () => ws.close()
  }
}
const x = (...a) => execFileSync('xdotool', a, { env: { ...process.env, DISPLAY } }).toString()

const t = await findTarget(t => t.url.includes('term-ghostty'))
const c = await connect(t.webSocketDebuggerUrl)
let ok = false, txt = ''
for (let i = 0; i < 40; i++) {
  txt = String(await c.eval('window.__termText()'))
  if (txt.includes('Go to URL')) { ok = true; break }
  await sleep(500)
}
console.log(`${ok ? 'PASS' : 'FAIL'}  GH1 launcher renders via ghostty-web (libghostty VT core)${ok ? '' : ' — ' + txt.slice(0, 200)}`)

x('search', '--sync', '--name', 'WC-SPIKE')
const wid = x('search', '--name', 'WC-SPIKE').trim().split('\n')[0]
x('windowactivate', '--sync', wid)
await sleep(300)
x('key', 'Up')
await sleep(300)
x('type', '--delay', '95', 'gh7')
await sleep(800)
const t2 = String(await c.eval('window.__termText()'))
const typed = t2.includes('gh7')
console.log(`${typed ? 'PASS' : 'FAIL'}  GH2 typing round-trips through ghostty-web pane`)
execFileSync('import', ['-display', DISPLAY, '-window', 'root', `${OUT}h-ghostty.png`])
console.log('      shot: out/h-ghostty.png')
c.close()
process.exit(ok && typed ? 0 : 1)
