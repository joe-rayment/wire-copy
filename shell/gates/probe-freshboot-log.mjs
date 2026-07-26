// workspace-xpr5t: does a FIRST run — no wirecopy.db anywhere — reach the launcher
// without logging SQLite errors and stack traces?
//
// The gate harness already gives us a fresh install for free: it points XDG_DATA_HOME at
// a throwaway directory, and .NET's LocalApplicationData follows XDG_DATA_HOME on Linux,
// so the database the app opens has never existed. Boots headful, waits for the launcher
// to actually paint, then reports every ERR/WRN line and every SQLite mention the .NET
// side wrote to logs/wirecopy-<date>.log during THIS boot (measured from the file's size
// before launch, so an earlier run's lines cannot be mistaken for this one's).
//
// Run from shell/: node gates/probe-freshboot-log.mjs
import { statSync, openSync, readSync, closeSync, existsSync, readdirSync } from 'node:fs'
import path from 'node:path'
import { Env, attach, pollTermText, sleep, buildTui, ROOT } from './lib.mjs'

buildTui('Release')

const logDir = path.join(ROOT, 'logs')
const logFile = () => {
  if (!existsSync(logDir)) return null
  const files = readdirSync(logDir).filter(f => /^wirecopy-.*\.log$/.test(f)).sort()
  return files.length ? path.join(logDir, files[files.length - 1]) : null
}
const sizeOf = f => { try { return statSync(f).size } catch { return 0 } }
const readFrom = (f, offset) => {
  const size = sizeOf(f)
  if (!f || size <= offset) return ''
  const fd = openSync(f, 'r')
  const buf = Buffer.alloc(size - offset)
  readSync(fd, buf, 0, buf.length, offset)
  closeSync(fd)
  return buf.toString('utf8')
}

const before = logFile()
const offset = before ? sizeOf(before) : 0

// A second boot REUSING the first boot's data dir is the other half of the contract:
// the schema exists there, so the sweeps must actually run rather than being skipped.
const reuse = process.argv.includes('--reuse') ? process.argv[process.argv.indexOf('--reuse') + 1] : null
const env = new Env({
  display: ':96',
  cdpPort: 9306,
  screen: '1600x1000x24',
  isolateData: !reuse,
  env: reuse ? { XDG_DATA_HOME: reuse, WIRECOPY_SHELL_USERDATA: path.join(reuse, '..', 'electron') } : {}
})
console.log(reuse ? 'REUSED XDG_DATA_HOME:' : 'fresh XDG_DATA_HOME:', env.extraEnv.XDG_DATA_HOME)
console.log('db exists before boot:',
  existsSync(path.join(env.extraEnv.XDG_DATA_HOME, 'WireCopy', 'wirecopy.db')))
await env.up()
const shell = env.launchShell()

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal')
  const painted = await pollTermText(term, 'Go to URL', 40000)
  console.log('launcher painted:', painted.ok)
  // Give the orphan sweeps their whole window: they are dispatched at host start and,
  // with the fix, wait for the migration before touching a table.
  await sleep(6000)

  const f = logFile()
  const fresh = readFrom(f, f === before ? offset : 0)
  const lines = fresh.split('\n')
  const errs = lines.filter(l => /\[(ERR|FTL)\]/.test(l))
  const warns = lines.filter(l => /\[WRN\]/.test(l))
  const sqlite = lines.filter(l => /SqliteException|no such table|SQLite Error/i.test(l))
  const traces = lines.filter(l => /^\s+at Microsoft\.Data\.Sqlite|^\s+at Microsoft\.EntityFrameworkCore/.test(l))
  const sweeps = lines.filter(l => /orphan sweep|Interrupted/i.test(l))

  console.log(`\nlog: ${f}  (+${fresh.length} bytes this boot, ${lines.length} lines)`)
  console.log(`ERR/FTL lines : ${errs.length}`)
  errs.slice(0, 10).forEach(l => console.log('   ' + l.slice(0, 200)))
  console.log(`WRN lines     : ${warns.length}`)
  warns.slice(0, 10).forEach(l => console.log('   ' + l.slice(0, 200)))
  console.log(`SQLite errors : ${sqlite.length}`)
  sqlite.slice(0, 10).forEach(l => console.log('   ' + l.slice(0, 200)))
  console.log(`EF/SQLite stack-trace frames : ${traces.length}`)
  console.log(`orphan-sweep mentions        : ${sweeps.length}`)
  sweeps.slice(0, 10).forEach(l => console.log('   ' + l.slice(0, 200)))
} catch (e) {
  console.error('PROBE ERROR:', e)
  console.error(shell.stderrText.slice(-3000))
} finally { env.down() }
process.exit(0)
