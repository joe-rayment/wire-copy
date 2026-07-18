// workspace-k60z — the macOS application menu, factored out as a PURE function so the
// darwin code path is verified in-env (gate-d8-menu.mjs) instead of parked on a Mac
// eyeball (see bd memory never-park-beads-on-user-mac).
//
// macOS keeps a REAL app menu: an App menu (About/Quit), an Edit menu whose clipboard
// ROLES are what make Cmd+C/V reach a focused page input, and a Window menu. Without an
// Edit menu the default menu says "Electron" and Cmd+C/V never reach page inputs. On
// Linux/Windows there is no menu bar at all — the term pane fills the content — so the
// template is null.
//
// The App menu TITLE (shown as the bold first menu, "WireCopy" not "Electron") comes
// from the packaged app's CFBundleName, which electron-builder sets from build.productName
// ("WireCopy" in package.json). gate-d8-menu.mjs asserts both the template roles and that
// productName, which is the whole of what is verifiable off a real Mac; the native
// rendering of a correct role template is an Electron contract.
function buildAppMenuTemplate (platform) {
  if (platform !== 'darwin') return null
  return [
    { role: 'appMenu' },
    { role: 'editMenu' },
    { role: 'windowMenu' }
  ]
}

module.exports = { buildAppMenuTemplate }
