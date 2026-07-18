// D8 menu gate (workspace-k60z): the macOS app-menu logic, verified in-env instead of
// parked on a Mac eyeball (bd memory never-park-beads-on-user-mac). What is verifiable
// off a real Mac is (1) the darwin menu TEMPLATE — an App menu, an Edit menu (whose roles
// are what make Cmd+C/V reach a focused page input), and a Window menu, and null off
// darwin — and (2) that the packaged App-menu TITLE is "WireCopy" (build.productName ->
// CFBundleName), not "Electron". The native rendering of a correct role template is an
// Electron contract. Run from shell/: node gates/gate-d8-menu.mjs
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import path from 'node:path'
import { createRequire } from 'node:module'
import { check, summary } from './lib.mjs'

const require = createRequire(import.meta.url)
const here = path.dirname(fileURLToPath(import.meta.url))
const { buildAppMenuTemplate } = require(path.join(here, '..', 'menu.js'))

// (1) darwin template: exactly [appMenu, editMenu, windowMenu], in order.
const mac = buildAppMenuTemplate('darwin')
const roles = Array.isArray(mac) ? mac.map(m => m.role) : mac
check('D8.1 darwin builds the real app menu [appMenu, editMenu, windowMenu]',
  JSON.stringify(roles) === JSON.stringify(['appMenu', 'editMenu', 'windowMenu']), JSON.stringify(roles))

// The Edit menu is the load-bearing item: its role is what wires Cmd+C/V/X/SelectAll to
// the focused page input on macOS. Without it the default menu says "Electron".
check('D8.2 darwin menu includes the Edit menu (the Cmd+C/V clipboard provider)',
  Array.isArray(mac) && mac.some(m => m.role === 'editMenu'))

// (2) off darwin there is NO menu bar (term pane fills content).
check('D8.3 linux has no menu bar (null template)', buildAppMenuTemplate('linux') === null)
check('D8.4 win32 has no menu bar (null template)', buildAppMenuTemplate('win32') === null)

// (3) the packaged App-menu TITLE = build.productName -> CFBundleName = "WireCopy",
// which is the "app menu, not 'Electron'" half of the visual.
const pkg = JSON.parse(readFileSync(path.join(here, '..', 'package.json'), 'utf8'))
check('D8.5 packaged app-menu title is "WireCopy" (build.productName -> CFBundleName)',
  pkg.build?.productName === 'WireCopy', String(pkg.build?.productName))

process.exit(summary())
