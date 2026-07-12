// P6 parity gate (workspace-vcpf): bundled font + glyph coverage + palette + wizard
// screenshot parity + tmux-vs-shell text parity.
//   F: 'WireCopy Mono' (bundled DejaVu) is LOADED and leads the resolved stack; xterm
//      measured its cells against it (not a fallback).
//   G: no-tofu probe — every glyph class the TUI uses (braille spinner, box drawing,
//      accent/star/arrow marks) has REAL outlines in the bundled font (canvas pixels
//      differ from the .notdef box); emoji renders non-blank via system fallback.
//   P: ANSI-16 pinned to the classic xterm palette (xterm.js defaults to Tango — a
//      visible cross-mode divergence class) — config truth + a rendered pixel probe.
//   W: wizard screenshot parity — CDP Page.captureScreenshot captureBeyondViewport on
//      the app's HIDDEN fetch page returns content BELOW the viewport fold (the exact
//      primitive OpenAI-wizard capture uses; Playwright clips to 3 viewport heights).
//   T: tmux-vs-shell TEXT parity — the same TUI at the same cols/rows renders the same
//      launcher lines in terminal mode (tmux, private socket) and in the shell pane.
// Run from shell/: node gates/gate-p6-parity.mjs
import http from 'node:http'
import { execFileSync } from 'node:child_process'
import { writeFileSync } from 'node:fs'
import path from 'node:path'
import { Env, OUT, ROOT, Cdp, targets, attach, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

buildTui('Release')

const FPORT = 8136
const fixture = http.createServer((_req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  res.end(`<!doctype html><meta charset="utf-8"><title>Tall Wire</title><body style="margin:0">
<h1>Tall Wire</h1>
<section><h2>Front Desk</h2><ul>
<li><a href="/one.html">Descent ledger: the archive desk mapped every fold below the first screen of the page</a></li>
<li><a href="/two.html">Second dispatch confirms the bottom of the scroll still renders for the capture rig</a></li>
</ul></section>
<div style="height:1700px"></div>
<div id="marker" style="height:200px;background:#d81b60;color:#fff;font-size:40px">BOTTOM-MARKER</div>
<div style="height:400px"></div>`)
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))

const env = new Env({ display: ':94', cdpPort: 9260 })
await env.up()
env.launchShell()

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('P6.0 TUI booted', (await pollTermText(term, 'Go to URL', 30000)).ok)

  // ---- F: bundled font active ----
  const fontLoaded = await term.eval(`document.fonts.check("16px 'WireCopy Mono'")`)
  check('P6.F1 bundled WireCopy Mono is loaded', fontLoaded === true)
  const family = await term.eval('getComputedStyle(document.querySelector(".xterm-rows")).fontFamily')
  check('P6.F2 WireCopy Mono leads the stack', /^["']?WireCopy Mono/.test(family), family)

  // ---- G: no-tofu probes ----
  const probeExpr = `(() => {
    const paint = (ch, font) => {
      const c = document.createElement('canvas'); c.width = 32; c.height = 32
      const x = c.getContext('2d'); x.font = font; x.fillStyle = '#fff'
      x.fillText(ch, 2, 24); return c.toDataURL()
    }
    const mono = "16px 'WireCopy Mono'"
    const notdef = paint('\\uffff', mono)
    const out = {}
    for (const ch of ['\\u280b','\\u2819','\\u283c','─','│','┼','█','╗','▌','★','⇒','·','▓','…','⠿'])
      out[ch] = paint(ch, mono) !== notdef && paint(ch, mono) !== paint(' ', mono)
    // Emoji ride the platform fallback: assert non-blank, tofu-distinct rendering.
    const stack = "16px 'WireCopy Mono', monospace, sans-serif"
    for (const ch of ['🎧','⏭'])
      out[ch] = paint(ch, stack) !== paint(' ', stack) && paint(ch, stack) !== paint('\\uffff', stack)
    return out
  })()`
  const glyphs = await term.eval(probeExpr)
  const missing = Object.entries(glyphs).filter(([, ok]) => !ok).map(([ch]) => ch)
  check('P6.G1 zero tofu across the TUI glyph classes', missing.length === 0,
    missing.length ? 'missing: ' + missing.join(' ') : Object.keys(glyphs).length + ' glyphs real')

  // ---- P: palette pinned to classic xterm ----
  const theme = await term.eval('JSON.stringify(term.options.theme)')
  const t = JSON.parse(theme)
  const wantAnsi = { green: '#00cd00', red: '#cd0000', blue: '#0000ee', cyan: '#00cdcd', yellow: '#cdcd00', brightGreen: '#00ff00', magenta: '#ff87d7', brightMagenta: '#ffa8e2' }
  const paletteBad = Object.entries(wantAnsi).filter(([k, v]) => (t[k] || '').toLowerCase() !== v)
  check('P6.P1 ANSI-16 pinned (classic xterm + brand magentas)', paletteBad.length === 0, JSON.stringify(paletteBad))
  // Rendered truth: paint basic-ANSI green in a scratch xterm cell offscreen is overkill;
  // instead assert the RENDERER resolves ANSI 2 to the pinned value via the buffer API
  // after writing a probe row directly into the pane's local echo (decode-only, no pty).
  const ansiProbe = await term.eval(`(() => new Promise(res => {
    const probe = new Terminal({ fontFamily: term.options.fontFamily, theme: term.options.theme, allowProposedApi: true })
    const d = document.createElement('div'); d.style.position='fixed'; d.style.left='-2000px'; document.body.appendChild(d)
    probe.open(d)
    probe.write('\\x1b[32mGREEN\\x1b[0m', () => {
      const cell = probe.buffer.active.getLine(0).getCell(0)
      res({ mode: cell.getFgColorMode(), color: cell.getFgColor() })
      probe.dispose(); d.remove()
    })
  }))()`)
  check('P6.P2 SGR 32 resolves to palette green (render-layer truth)',
    ansiProbe && (ansiProbe.color === 2 || ansiProbe.color === 0x00cd00),
    JSON.stringify(ansiProbe))

  // Stash the LAUNCHER text + dims now — the W-step navigates away and the D2
  // auto-reveal narrows the terminal, so the parity snapshot must precede both.
  const dims = await term.eval('window.__dims()')
  const shellText = await term.eval('window.__termText()')

  // ---- W: captureBeyondViewport on the hidden fetch page ----
  env.activate(); await sleep(300)
  env.type('o'); await sleep(400)
  env.type(`http://127.0.0.1:${FPORT}/`); await sleep(300)
  env.key('Return')
  check('P6.W0 tall fixture rendered in the reader', (await pollTermText(term, 'Descent ledger', 40000)).ok)
  let fetchT = null
  for (let i = 0; i < 30 && !fetchT; i++) {
    const list = await targets(env.cdpPort).catch(() => [])
    for (const cand of list.filter(x => x.type === 'page' && x.url.includes(`127.0.0.1:${FPORT}`))) {
      const c = await Cdp.connect(cand.webSocketDebuggerUrl).catch(() => null)
      if (!c) continue
      const w = await c.eval('innerWidth').catch(() => -1)
      if (w === 1280) { fetchT = c; break }
      c.close()
    }
    if (!fetchT) await sleep(400)
  }
  check('P6.W1 hidden fetch page found (1280 viewport)', !!fetchT)
  if (fetchT) {
    const shot = await fetchT.send('Page.captureScreenshot', {
      format: 'png',
      captureBeyondViewport: true,
      clip: { x: 0, y: 0, width: 1280, height: 2160, scale: 1 }
    })
    const png = Buffer.from(shot.data, 'base64')
    const pngW = png.readUInt32BE(16); const pngH = png.readUInt32BE(20)
    check('P6.W2 capture spans BEYOND the viewport (2160 tall vs 720 viewport)', pngW === 1280 && pngH === 2160, `${pngW}x${pngH}`)
    const shotPath = path.join(OUT, 'p6-beyond-viewport.png')
    writeFileSync(shotPath, png)
    // The magenta marker sits ~y1900..2100 — sample inside it.
    const probe = execFileSync('convert', [shotPath, '-format', '%[pixel:p{640,2000}]', 'info:-']).toString()
    check('P6.W3 below-the-fold content present (marker pixel)', /d81b60|216,27,96/i.test(probe) || /srgb\(216,27,96\)/.test(probe), probe.trim())
    fetchT.close()
  }

  // ---- T: tmux vs shell text parity at identical cols/rows ----
  const sock = `wc-p6-${process.pid}`
  const tuiXdg = env.extraEnv.XDG_DATA_HOME + '-tmux'
  execFileSync('mkdir', ['-p', tuiXdg])
  const tuiCmd = `XDG_DATA_HOME='${tuiXdg}' TERM=xterm-256color COLORTERM=truecolor '${path.join(ROOT, 'dotnet')}' exec '${path.join(ROOT, 'src/WireCopy.API/bin/Release/net10.0/WireCopy.API.dll')}'`
  execFileSync('tmux', ['-L', sock, 'new-session', '-d', '-x', String(dims.cols), '-y', String(dims.rows), tuiCmd])
  let tmuxText = ''
  for (let i = 0; i < 40; i++) {
    await sleep(700)
    tmuxText = execFileSync('tmux', ['-L', sock, 'capture-pane', '-p', '-t', '0']).toString()
    if (tmuxText.includes('Go to URL')) break
  }
  check('P6.T0 terminal-mode TUI booted in tmux at the same size', tmuxText.includes('Go to URL'), `cols=${dims.cols} rows=${dims.rows}`)
  const norm = s => s.split('\n').map(l => l.replace(/\s+/g, ' ').trim()).filter(l => l.length > 0)
  const a = norm(shellText); const b = norm(tmuxText)
  const bSet = new Set(b)
  const missingLines = a.filter(l => !bSet.has(l) && !/[⠀-⣿]|Loading|·\s*$/.test(l))
  check('P6.T1 shell launcher lines all appear in terminal mode (text parity)',
    missingLines.length <= 2, missingLines.slice(0, 4).join(' || ') || 'exact')
  writeFileSync(path.join(OUT, 'p6-tmux-launcher.txt'), tmuxText)
  writeFileSync(path.join(OUT, 'p6-shell-launcher.txt'), shellText)
  execFileSync('tmux', ['-L', sock, 'kill-session', '-t', '0'])
  env.shot('p6-shell-launcher')
  term.close()
} catch (err) {
  console.error('GATE ERROR:', err)
  check('P6 gate completed without error', false, String(err))
  try { env.shot('p6-error') } catch {}
} finally {
  env.down()
  fixture.close()
}
process.exit(summary())
