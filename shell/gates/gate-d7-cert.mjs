// D7 gate (workspace-83tf): cert-error parity for the shell fetch path.
// Terminal mode launches Playwright with IgnoreHTTPSErrors=true (BrowserSession.cs:1629);
// the shell's persist:wirecopy partition now mirrors that via setCertificateVerifyProc.
// A local https fixture with a SELF-SIGNED cert must load through the app's real fetch
// path (hidden fetch page in the wirecopy partition), while a CONTROL load on the
// DEFAULT session must still REJECT the same URL — proving the tolerance is scoped.
// Run from shell/: node gates/gate-d7-cert.mjs
import https from 'node:https'
import { execFileSync } from 'node:child_process'
import { mkdtempSync, readFileSync } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { Env, attach, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

const FPORT = 8133

// Self-signed cert for 127.0.0.1 (SAN required — Chromium ignores CN-only certs).
const certDir = mkdtempSync(path.join(os.tmpdir(), 'wc-gate-d7-'))
execFileSync('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes',
  '-keyout', path.join(certDir, 'key.pem'), '-out', path.join(certDir, 'cert.pem'),
  '-days', '2', '-subj', '/CN=127.0.0.1',
  '-addext', 'subjectAltName=IP:127.0.0.1'], { stdio: 'ignore' })

const fixture = https.createServer({
  key: readFileSync(path.join(certDir, 'key.pem')),
  cert: readFileSync(path.join(certDir, 'cert.pem'))
}, (_req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  res.end(`<!doctype html><meta charset="utf-8"><title>Cert Wire</title><body>
<h1>Cert Wire</h1>
<section><h2>Front Desk</h2><ul>
<li><a href="/one.html">Expired seals: the registry kept serving the archive long after the certificate lapsed</a></li>
<li><a href="/two.html">Tolerance ledger: how the reading desk decided which broken padlocks were worth ignoring</a></li>
</ul></section>`)
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))
const url = `https://127.0.0.1:${FPORT}/`

buildTui('Release')
const env = new Env({ display: ':89', cdpPort: 9255 })
await env.up()
env.launchShell()

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('D7.0 TUI booted', (await pollTermText(term, 'Go to URL', 30000)).ok)

  // Drive the REAL user action: go-to-URL against the self-signed https fixture.
  env.activate()
  await sleep(300)
  env.type('o'); await sleep(400)
  env.type(url); await sleep(300)
  env.key('Return')
  const rendered = await pollTermText(term, 'Expired seals', 40000)
  check('D7.1 self-signed https loads through the app fetch path (wirecopy partition)',
    rendered.ok, rendered.ok ? '' : rendered.txt.slice(-300))
  env.shot('d7-selfsigned-rendered')

  // Control: the DEFAULT session must still reject the exact same URL.
  const control = await term.eval(`window.__wc.probeCertDefaultSession(${JSON.stringify(url)})`)
  check('D7.2 default session still REJECTS the self-signed cert (tolerance is scoped)',
    /ERR_CERT|CERT_/.test(String(control)) && control !== 'loaded', String(control))
  term.close()
} catch (err) {
  console.error('GATE ERROR:', err)
  check('D7 gate completed without error', false, String(err))
  try { env.shot('d7-error') } catch {}
} finally {
  env.down()
  fixture.close()
}
process.exit(summary())
