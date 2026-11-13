// Renders PROPOSED routines screens with the app's REAL launcher pane (shell/launcher/launcher.html):
// each scenario is nothing but a state pushed through window.__renderLauncher.
// Headed Electron under Xvfb; window content size = the shell's (1275x808).
// Run from the repo root (source ~/.local/bin/x-env.sh first; needs an X display, never headless):
//   shell/deps/<platform>/node_modules/.bin/electron docs/design/routines-v3/mock/main.js --no-sandbox \
//     --disable-dev-shm-usage "$PWD/shell/launcher/launcher.html" "$PWD/docs/design/routines-v3/shots"
// Writes shots/proposed-<name>.png.
const { app, BrowserWindow } = require('electron')
const fs = require('fs'); const path = require('path')
const [launcher, outDir] = process.argv.slice(-2)
const scenarios = require('./scenarios.js')
app.whenReady().then(async () => {
  const win = new BrowserWindow({ width: 1275, height: 808, useContentSize: true, show: true, backgroundColor: '#000' })
  await win.loadFile(launcher)
  await new Promise(r => setTimeout(r, 1200))
  for (const sc of scenarios) {
    await win.webContents.executeJavaScript(`(() => {
      const sc = ${JSON.stringify(sc)}
      window.__renderLauncher({ visible: true, state: sc.state })
      document.querySelectorAll('*').forEach(el => { el.style.animation = 'none' })
      return document.documentElement.scrollHeight + 'x' + document.documentElement.scrollWidth + ' cards=' + document.querySelectorAll('.card').length
    })()`).then(v => console.log(sc.name, v))
    await new Promise(r => setTimeout(r, 500))
    const img = await win.webContents.capturePage()
    fs.writeFileSync(path.join(outDir, `proposed-${sc.name}.png`), img.toPNG())
  }
  app.quit()
})
