// L3+L5 launcher pane gate (workspace-wak1s / workspace-e2qz3): proves main.js hosts a
// display-only launcherView driven by the `launcherState` channel method + live zoom over
// wc:font. Headful under Xvfb (never headless). Run from shell/: node gates/gate-launcher-pane.mjs
import net from 'node:net'
import { readdirSync, statSync, existsSync } from 'node:fs'
import path from 'node:path'
import { Env, attach, findTarget, Cdp, pollTermText, check, summary, buildTui, sleep, SHELL_DIR } from './lib.mjs'

buildTui('Release')

const env = new Env({ display: ":99", cdpPort: 9285 })
await env.up()
const bootAt = Date.now()
const shell = env.launchShell()

// One JSON object per line over the shell's Unix control socket (channel.js). Multiple
// clients are supported, so this extra client rides alongside the TUI child.
function channelCall (sockPath, method, params) {
  return new Promise((resolve, reject) => {
    const c = net.connect(sockPath)
    let buf = ''
    const to = setTimeout(() => { c.destroy(); reject(new Error('channel timeout')) }, 5000)
    c.on('connect', () => c.write(JSON.stringify({ id: 1, method, params }) + '\n'))
    c.on('data', d => {
      buf += d.toString('utf8')
      const nl = buf.indexOf('\n')
      if (nl >= 0) { clearTimeout(to); c.end(); resolve(JSON.parse(buf.slice(0, nl))) }
    })
    c.on('error', e => { clearTimeout(to); reject(e) })
  })
}

const LAUNCHER_STATE = {
  slots: [], selectedIndex: 0, urlTyping: false, setupHint: null,
  paging: { offset: 0, visible: 6, total: 0 }, bookmarkCount: 0,
  footer: {}, tagline: 't', version: 'v'
}

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  // (a) Shell boots clean to the launcher; a missing template must not crash it.
  check('L0 shell booted to launcher screen', (await pollTermText(term, 'Go to URL', 30000)).ok)
  await sleep(600)

  // Find THIS shell's control socket (newest wirecopy-shell-*.sock created after boot).
  const socks = readdirSync('/tmp')
    .filter(f => /^wirecopy-shell-\d+\.sock$/.test(f))
    .map(f => path.join('/tmp', f))
    .filter(p => { try { return statSync(p).ctimeMs >= bootAt - 2000 } catch { return false } })
    .sort((a, b) => statSync(b).ctimeMs - statSync(a).ctimeMs)
  check('S0 control socket present', socks.length > 0, socks[0] || '(none)')
  const sock = socks[0]

  const templateExists = existsSync(path.join(SHELL_DIR, 'launcher', 'launcher.html'))
  console.log(`      launcher.html present at verify time: ${templateExists}`)

  // (b) Drive launcherState visible:true — reply ok, and the pane turns visible.
  const r1 = await channelCall(sock, 'launcherState', { visible: true, state: LAUNCHER_STATE })
  check('S1 launcherState(visible:true) → ok', r1 && r1.ok === true, JSON.stringify(r1))
  await sleep(400)
  const st1 = await term.eval('window.wc.state()')
  check('S2 launcherView became visible', st1.launcherShown === true, `launcherShown=${st1.launcherShown}`)
  const cs = st1.contentSize
  check('S3 launcher pane spans full content area',
    st1.launcherBounds && st1.launcherBounds.x === 0 && st1.launcherBounds.y === 0 &&
    st1.launcherBounds.width === cs[0] && st1.launcherBounds.height === cs[1],
    JSON.stringify(st1.launcherBounds) + ' vs content ' + JSON.stringify(cs))
  env.shot('wire-launcher-visible')

  // Verify the payload was FORWARDED to the pane. wclau.onState is exposed by the preload
  // even when the template 404s, so a CDP-injected listener proves the send arrives.
  let lc = null
  try {
    const lt = await findTarget(env.cdpPort, t => t.url.includes('launcher.html'), 8000, 'launcher pane')
    lc = await Cdp.connect(lt.webSocketDebuggerUrl)
    await lc.eval('window.__st=[]; window.wclau.onState(m=>window.__st.push(m)); window.__fp=[]; window.wclau.onFont(px=>window.__fp.push(px)); true')
    // push again so our freshly-attached listener captures it
    await channelCall(sock, 'launcherState', { visible: true, state: { ...LAUNCHER_STATE, tagline: 'probe-xyz' } })
    await sleep(300)
    const got = await lc.eval('JSON.stringify(window.__st[window.__st.length-1]||null)')
    const parsed = JSON.parse(got || 'null')
    check('S4 pane received forwarded launcherState payload',
      parsed && parsed.visible === true && parsed.state && parsed.state.tagline === 'probe-xyz',
      got ? got.slice(0, 80) : '(null)')
    if (templateExists) {
      const ls = await lc.eval('typeof window.__launcherState==="function" ? JSON.stringify(window.__launcherState()) : "NO_FN"')
      check('S4b template exposes __launcherState() reflecting the push',
        ls !== 'NO_FN' && ls.includes('probe-xyz'), ls === 'NO_FN' ? 'template has no __launcherState()' : ls.slice(0, 80))
    }
  } catch (e) {
    check('S4 pane received forwarded launcherState payload', false, 'could not attach launcher target: ' + e.message)
  }

  // (b cont.) Hide it again — the state change is where bugs hide.
  const r2 = await channelCall(sock, 'launcherState', { visible: false, state: LAUNCHER_STATE })
  check('S5 launcherState(visible:false) → ok', r2 && r2.ok === true)
  await sleep(400)
  const st2 = await term.eval('window.wc.state()')
  check('S6 launcherView hid on visible:false', st2.launcherShown === false, `launcherShown=${st2.launcherShown}`)
  env.shot('wire-launcher-hidden')

  // Show again, then flip to browser mode: reader-gated visibility must drop it.
  await channelCall(sock, 'launcherState', { visible: true, state: LAUNCHER_STATE })
  await sleep(200)
  env.activate(); await sleep(200)
  await term.eval('window.wc.setMode("browser")')
  await sleep(400)
  const st3 = await term.eval('window.wc.state()')
  check('S7 launcher hidden in browser mode despite visible:true',
    st3.mode === 'browser' && st3.launcherShown === false, `mode=${st3.mode} launcherShown=${st3.launcherShown}`)
  await term.eval('window.wc.setMode("reader")')
  await sleep(400)
  const st3b = await term.eval('window.wc.state()')
  check('S8 launcher re-shown on return to reader mode',
    st3b.mode === 'reader' && st3b.launcherShown === true, `mode=${st3b.mode} launcherShown=${st3b.launcherShown}`)

  // (c) L5 live zoom: Ctrl+= bumps termFontSize AND pushes wc:font to the launcher pane.
  const fontBefore = st3b.termFontSize
  env.activate(); await sleep(200)
  env.key('ctrl+equal')
  await sleep(500)
  const st4 = await term.eval('window.wc.state()')
  check('S9 Ctrl+= increased termFontSize', st4.termFontSize === fontBefore + 1, `${fontBefore} → ${st4.termFontSize}`)
  if (lc) {
    const fp = await lc.eval('JSON.stringify(window.__fp)')
    check('S10 launcher pane received wc:font over Ctrl+=', JSON.parse(fp).includes(st4.termFontSize),
      `fontProbe=${fp} expected ${st4.termFontSize}`)
    lc.close()
  } else {
    check('S10 launcher pane received wc:font over Ctrl+=', false, 'no launcher CDP target to probe')
  }

  // (a) No crash / clean main log. The guarded loadURL failure logs but must not throw.
  const crashed = /Uncaught|TypeError|ReferenceError|A JavaScript error occurred in the main process/.test(shell.stderrText)
  check('L-clean no main-process crash in stderr', !crashed,
    crashed ? shell.stderrText.split('\n').filter(l => /error|Error/.test(l)).slice(-3).join(' | ') : '')
  const guardLogged = /launcher pane load failed/.test(shell.stderrText)
  console.log(`      guarded load-fail log present (expected when template absent): ${guardLogged}`)
} finally {
  env.down()
}
process.exit(summary())
