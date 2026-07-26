// workspace-u1is6: does ReadyToRun buy anything on the .NET boot? MEASURE, do not assume.
//
// The app refuses to run outside the shell, so the phase this bead is about — spawn to
// the TUI actually running — can only be timed with a real shell around it. This boots
// the shell N times per variant with WIRECOPY_SHELL_TUI_DLL pointed at a publish
// directory, and reads the boot marks the shell and Program.cs already emit.
//
// Publish the two variants first (framework-dependent, so only the app's own assemblies
// differ):
//   ./dotnet publish src/WireCopy.API/WireCopy.API.csproj -c Release -r linux-arm64 \
//       --self-contained false -o <dir>/pub-base
//   …same plus -p:PublishReadyToRun=true -o <dir>/pub-r2r
//
// Run from shell/: node gates/probe-r2r.mjs <pub-base> <pub-r2r> [runs]
import path from 'node:path'
import fs from 'node:fs'
import { Env, sleep } from './lib.mjs'

const [baseDir, r2rDir, runsArg] = process.argv.slice(2)
if (!baseDir || !r2rDir) {
  console.error('usage: node gates/probe-r2r.mjs <pub-base> <pub-r2r> [runs]')
  process.exit(2)
}
const RUNS = Number(runsArg) || 5

// Newest child-stderr log written since `since` — one per shell launch.
const LOG_DIR = path.join(path.resolve(path.dirname(new URL(import.meta.url).pathname), '../..'), 'logs')
function childStderr (since) {
  try {
    const files = fs.readdirSync(LOG_DIR)
      .filter(f => /^shell-child-stderr-.*\.log$/.test(f))
      .map(f => ({ f, m: fs.statSync(path.join(LOG_DIR, f)).mtimeMs }))
      .filter(x => x.m >= since - 2000)
      .sort((a, b) => b.m - a.m)
    return files.length ? fs.readFileSync(path.join(LOG_DIR, files[0].f), 'utf8') : ''
  } catch { return '' }
}

const median = a => {
  const s = [...a].sort((x, y) => x - y)
  return s.length % 2 ? s[(s.length - 1) / 2] : (s[s.length / 2 - 1] + s[s.length / 2]) / 2
}

async function boot (label, dll, i) {
  const env = new Env({ display: ':99', cdpPort: 9310 + (i % 3), screen: '1200x800x24' })
  await env.up()
  const t0 = Date.now() / 1000
  const since = Date.now()
  const shell = env.launchShell({
    WIRECOPY_BOOT_TIMELINE: '1',
    WIRECOPY_BOOT_T0: String(t0),
    WIRECOPY_SHELL_TUI_DLL: dll
  })
  // The shell's own marks come from its stderr. The .NET child's (dotnet-main, host-build,
  // host-started, db-ready, tui-run) do NOT: node-pty hands the child one stream for
  // stdout and stderr, so main.js routes fd2 to logs/shell-child-stderr-<ts>.log before
  // exec, precisely so diagnostics cannot paint over the TUI's frames. That file is where
  // the .NET half of the boot timeline actually lands.
  const marks = {}
  const scrape = text => {
    for (const m of text.matchAll(/\[boot\] (\S+)\s+([\d.]+)s/g)) {
      if (marks[m[1]] == null) marks[m[1]] = Math.round(parseFloat(m[2]) * 1000)
    }
  }
  for (let k = 0; k < 200; k++) {
    scrape(shell.stderrText)
    scrape(childStderr(since))
    if (marks['tui-run'] != null && marks['tui-first-paint'] != null) break
    await sleep(120)
  }
  scrape(shell.stderrText)
  scrape(childStderr(since))
  env.down()
  await sleep(600)
  console.log(`  ${label} run ${i + 1}: ` +
    Object.entries(marks).map(([k, v]) => `${k}=${v}`).join(' '))
  return marks
}

// INTERLEAVED, not one variant then the other. Running all of A before all of B hands B
// a warmer page cache and a warmer machine, and the effect being measured is the same
// size as that bias: the first uncontrolled attempt at this put BASE first and its very
// first boot was 300ms slower than its own median.
const out = { BASE: [], 'R2R ': [] }
console.log(`\n=== interleaved, ${RUNS} boots each ===`)
for (let i = 0; i < RUNS; i++) {
  for (const [label, dir] of [['BASE', baseDir], ['R2R ', r2rDir]]) {
    out[label].push(await boot(label, path.resolve(dir, 'WireCopy.API.dll'), i))
  }
}

console.log('\n=== SUMMARY (medians, ms) ===')
// spawn-tui -> tui-run is the whole .NET boot as seen by the shell; dotnet-main -> tui-run
// excludes process creation, which ReadyToRun cannot help with either way.
// spawn-tui -> tui-first-paint is the whole .NET boot as the shell sees it: process
// creation, host build, DI, EF init, and the TUI's first frame. The .NET side's own marks
// (dotnet-main, host-build, tui-run) go to the CHILD's stderr, which is the pty — they
// land on the terminal screen, not in the shell's stderr, so they are not readable here.
for (const phase of [['spawn-tui', 'tui-first-paint'], ['spawn-tui', 'tui-run'],
  ['dotnet-main', 'tui-run'], ['host-build', 'host-started'], ['electron-main', 'term-ready']]) {
  const val = label => median(out[label]
    .map(m => (m[phase[1]] != null && m[phase[0]] != null ? m[phase[1]] - m[phase[0]] : null))
    .filter(v => v != null))
  const b = val('BASE')
  const r = val('R2R ')
  console.log(`  ${phase[0]} -> ${phase[1]}:  base ${b}ms   r2r ${r}ms   delta ${(r - b).toFixed(0)}ms`)
}
console.log('\n  ADOPT ONLY IF the median improves by >= 100ms (this bead\'s own rule), and')
console.log('  the number that decides it is the one measured on the USER\'S MAC, not here.')
process.exit(0)
