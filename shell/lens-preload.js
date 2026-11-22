// Live page pane: show the site zoomed out (workspace-sez21).
//
// The pane is a slice of the window, far narrower than a desktop page. At its own width
// every responsive site lays itself out for a phone: one tall column, one or two stories
// to a screen, so moving through the link list keeps throwing the next story off screen.
// Zoomed out, the page keeps its desktop layout and a whole run of stories is in view.
//
// This runs at the start of every document in the pane, before any of the page's own
// scripts, so a site never measures a phone-width window first. The factor comes from
// the shell (it owns the pane's size) and is sent again whenever the pane is resized.
//
// It has to be done from in here: webFrame's zoom belongs to this one page view. The
// main-process webContents.setZoomFactor is remembered per SITE for the whole browser
// partition, which would also shrink the hidden pages the app reads stories from.
const { webFrame, ipcRenderer } = require('electron')

function apply (factor) {
  if (typeof factor === 'number' && factor > 0) webFrame.setZoomFactor(factor)
}

apply(ipcRenderer.sendSync('wclens:zoom'))
ipcRenderer.on('wclens:zoom', (_e, factor) => apply(factor))

// Where scrollbars are drawn beside the page (Linux, Windows) they take a strip of the
// pane the page cannot use, and they do not shrink with the zoom — so a page fitted to
// the pane's full width comes out a little wider than what is left, and gets a sideways
// scrollbar. Tell the shell how thick they are, in screen pixels, so it fits the page to
// the room there really is. Where they float over the page (macOS) this reports 0.
// Measured on the pane's own blank page only: a real site's DOM is never touched.
function reportScrollbar () {
  const probe = document.createElement('div')
  probe.style.cssText = 'position:fixed;top:-200px;left:0;width:100px;height:100px;overflow:scroll;visibility:hidden'
  document.documentElement.appendChild(probe)
  const thickness = (probe.offsetWidth - probe.clientWidth) * webFrame.getZoomFactor()
  probe.remove()
  ipcRenderer.send('wclens:scrollbar', thickness)
}
if (location.protocol === 'about:') {
  if (document.documentElement) reportScrollbar()
  else document.addEventListener('DOMContentLoaded', reportScrollbar, { once: true })
}
