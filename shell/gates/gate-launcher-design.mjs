// Launcher.dc.html design gate (workspace-pn5f): the launcher renders as ONE
// centred column — masthead, URL bar, grid, footer on the SAME axis — at the
// design's 18px body type, with the centred tagline and card insets. Also
// drives the REAL 'o' → type flow so the relocated URL-bar input position is
// proven by where typed characters actually land, not by row math alone.
// Headful under Xvfb (never headless). Run from shell/: node gates/gate-launcher-design.mjs
import { Env, attach, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

buildTui('Release')

const env = new Env({ display: ':96', cdpPort: 9280 })
await env.up()
env.launchShell()

const stripLine = s => s // __termText already yields plain text

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
  check('L0 TUI booted to launcher', (await pollTermText(term, 'Go to URL', 30000)).ok)
  await pollTermText(term, 'READING LIST', 15000)
  await sleep(800)

  // ---- Type scale: the design's 18px body is the default ----
  const font = await term.eval('window.__font()')
  check('L1 default type scale is 18px (Launcher.dc.html body)', font === 18, `font=${font}`)

  // ---- One shared axis: masthead, URL bar, grid, footer ----
  const txt = await term.eval('window.__termText()')
  const lines = txt.split('\n').map(stripLine)
  const centerOf = (line, left, right) => {
    const a = line.indexOf(left); const b = line.lastIndexOf(right)
    return (a >= 0 && b > a) ? (a + b) / 2 : NaN
  }
  const mastheadTop = lines.find(l => l.includes('╭') && l.includes('╮'))
  const urlBarTop = lines.filter(l => l.includes('╭') && l.includes('╮'))[1]
  const footerRule = lines.filter(l => /─{20,}/.test(l) && !l.includes('┼') && !l.includes('╭') && !l.includes('╰')).pop()
  const mastheadCenter = centerOf(mastheadTop || '', '╭', '╮')
  const urlBarCenter = centerOf(urlBarTop || '', '╭', '╮')
  check('L2 masthead and URL bar share one centring axis (±1 cell)',
    Number.isFinite(mastheadCenter) && Number.isFinite(urlBarCenter) && Math.abs(mastheadCenter - urlBarCenter) <= 1,
    `masthead=${mastheadCenter} urlBar=${urlBarCenter}`)
  const ruleCenter = footerRule ? (footerRule.indexOf('─') + footerRule.lastIndexOf('─')) / 2 : NaN
  check('L3 footer rule centres on the same axis (±1 cell)',
    Number.isFinite(ruleCenter) && Math.abs(ruleCenter - mastheadCenter) <= 1.5,
    `rule=${ruleCenter} masthead=${mastheadCenter}`)

  // ---- Masthead content: centred tagline with visible version, no period ----
  const tagLine = lines.find(l => l.includes('All copy, no nonsense'))
  check('L4 tagline reads "All copy, no nonsense · v{ver}"', !!tagLine && /All copy, no nonsense · v\d+\.\d+/.test(tagLine), tagLine?.trim())
  check('L5 tagline carries no trailing period', !!tagLine && !tagLine.includes('nonsense.'))
  const wordmarkLine = lines.find(l => l.includes('██╗    ██╗██╗'))
  check('L6 wordmark is the exact Launcher.dc.html art', !!wordmarkLine)

  // ---- Card insets: title starts 3 cells into its cell, badge inset from the edge ----
  const titleLine = lines.find(l => l.includes('THE DAILY GAZETTE'))
  const railIdx = titleLine ? titleLine.indexOf('▌') : -1
  const nameIdx = titleLine ? titleLine.indexOf('THE DAILY GAZETTE') : -1
  check('L7 selected card title inset 3 cells from the rail', railIdx >= 0 && nameIdx - railIdx === 3,
    `rail@${railIdx} title@${nameIdx}`)
  check('L8 badge sits inset from the cell edge', !!titleLine && / \[1\] {2}│/.test(titleLine), titleLine?.trim())

  env.shot('launcher-design-home')

  // ---- Drive the REAL 'o' flow: typed characters must land INSIDE the bar ----
  env.activate()
  await sleep(300)
  env.key('o')
  await sleep(900)
  env.type('example.com')
  await sleep(1200)
  const txt2 = await term.eval('window.__termText()')
  const lines2 = txt2.split('\n')
  const inputRow = lines2.findIndex(l => l.includes('example.com'))
  const barTop = lines2.findIndex(l => l.includes('╭') && lines2[l.length ? inputRow : inputRow] !== undefined && l.indexOf('╭') > 0)
  // The typed text must sit on a row that is enclosed by the URL-bar box:
  // the rows directly above and below it must carry the box borders.
  const above = inputRow > 0 ? lines2[inputRow - 1] : ''
  const below = inputRow >= 0 && inputRow + 1 < lines2.length ? lines2[inputRow + 1] : ''
  check('L9 typed URL landed inside the URL-bar box (borders above and below)',
    inputRow > 0 && above.includes('╭') && below.includes('╰'),
    `row=${inputRow} above="${above.trim().slice(0, 30)}" below="${below.trim().slice(0, 30)}"`)
  const typedCol = inputRow >= 0 ? lines2[inputRow].indexOf('example.com') : -1
  const boxLeft = above ? above.indexOf('╭') : -1
  check('L10 typed text starts just inside the box border',
    typedCol >= 0 && boxLeft >= 0 && typedCol - boxLeft === 2,
    `typed@${typedCol} boxLeft@${boxLeft}`)
  env.shot('launcher-design-url-typing')

  // ---- Selection movement: digit-jump [2] moves the fill + rail to card 2 ----
  // Escape cancels the URL input but leaves the BAR focused (index -1), where
  // any printable key seeds a new URL entry — so step down onto the grid
  // first, then digit-jump.
  env.key('Escape')
  await sleep(900)
  env.key('Down')
  await sleep(700)
  env.key('2')
  await sleep(900)
  const txt3 = await term.eval('window.__termText()')
  const lines3 = txt3.split('\n')
  const rlTitle = lines3.find(l => l.includes('READING LIST'))
  const divider = rlTitle ? rlTitle.indexOf('│') : -1
  const rail3 = rlTitle ? rlTitle.indexOf('▌') : -1
  check('L11 selection moved: rail now on the RIGHT card (after the divider)',
    rlTitle != null && divider >= 0 && rail3 === divider + 1,
    `divider@${divider} rail@${rail3} "${rlTitle?.trim().slice(0, 60)}"`)
  const gazette = lines3.find(l => l.includes('THE DAILY GAZETTE'))
  const gazDivider = gazette ? gazette.indexOf('│') : -1
  check('L12 previous card dropped its rail (no ▌ left of the divider)',
    !!gazette && gazDivider >= 0 && !gazette.slice(0, gazDivider).includes('▌'),
    gazette?.trim().slice(0, 50))
  env.shot('launcher-design-selection-moved')
} finally {
  env.down()
}
process.exit(summary())
