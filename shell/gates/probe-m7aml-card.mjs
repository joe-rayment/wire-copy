// Scratch probe (workspace-m7aml): open Settings -> Browser extension in the real app and screenshot.
import { Env, buildTui, sleep, attach, pollTermText } from './lib.mjs'
import { mkdtempSync, mkdirSync, writeFileSync, existsSync, readdirSync } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
const cfg = mkdtempSync(path.join(os.tmpdir(), 'm7aml-cfg-'))
mkdirSync(path.join(cfg, 'vivaldi'), { recursive: true })
mkdirSync(path.join(cfg, 'chromium'), { recursive: true })
if (!process.env.SKIP_BUILD) buildTui('Release')
const env = new Env({ display: ':76', cdpPort: 9476, env: { XDG_CONFIG_HOME: cfg } })
await env.up()
env.launchShell()
const term = await attach(9476, 'term.html', 'terminal pane')
await pollTermText(term, 'Reading List', 40000)
await sleep(2500)
const data = env.extraEnv.XDG_DATA_HOME
console.log('data', data, 'cfg', cfg)
console.log('manifests', ['vivaldi', 'chromium'].map(b => existsSync(path.join(cfg, b, 'NativeMessagingHosts', 'com.wirecopy.reader.json'))))
console.log('ext dir', existsSync(path.join(data, 'WireCopy', 'browser-extension')) && readdirSync(path.join(data, 'WireCopy', 'browser-extension')))
env.activate(); await sleep(800)
let r = { ok: false }
for (let i = 0; i < 4 && !r.ok; i++) { env.key('c'); r = await pollTermText(term, 'Browser extension', 6000) }
console.log('settings open', r.ok)
env.shot('m7aml-settings')
// "Browser extension" is the LAST Settings row and MoveUp wraps, so one Up from the first row lands on it.
env.key('Up')
await sleep(1200)
const lastRowText = await term.eval('window.__termText()')
const marked = lastRowText.split('\n').find(l => l.includes('▌') && l.includes('Browser extension'))
console.log('marker on Browser extension row:', Boolean(marked))
env.shot('m7aml-settings-lastrow')
if (!marked) { console.log('ABORT: selection is not on Browser extension'); env.down(); process.exit(1) }
env.key('Return')
r = await pollTermText(term, 'Save articles from your browser', 8000)
console.log('card open', r.ok)
await sleep(1500)
console.log('CARD TEXT:', (await term.eval('window.__termText()')).replace(/\n\s*\n/g, '\n').slice(0, 1800))
env.shot('m7aml-card-ready')
mkdirSync(path.join(data, 'WireCopy', 'browser-extension'), { recursive: true })
writeFileSync(path.join(data, 'WireCopy', 'browser-extension', 'connections.json'),
  JSON.stringify({ vivaldi: { lastSeenUtc: new Date().toISOString(), extensionVersion: '1.0.0' } }))
r = await pollTermText(term, 'connected', 6000)
console.log('flipped', r.ok)
env.shot('m7aml-card-connected')
env.key('Escape'); await sleep(1500)
env.shot('m7aml-settings-after')
env.down()
process.exit(0)
